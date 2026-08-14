using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.AgentPolicy;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.AgentPolicy.SetAgentGuardrails;

/// <summary>
/// Upserts agent guardrail toggles (docs/backend-gaps.md G8 / G10). Owner-gated.
/// Locked rails (g1, g4) are forced on regardless of the payload. When g5 is present,
/// <c>tenant_profiles.waitlist_auto_book</c> is upserted so the venue waitlist switch
/// stays consistent with the guardrail.
/// </summary>
public sealed record GuardrailToggle(string Id, bool On);

public sealed record SetAgentGuardrailsCommand(
    string TenantId,
    IReadOnlyList<GuardrailToggle> Guardrails,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<AgentGuardrailsResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "agent.guardrails_set";

    public string TargetType => "agent_guardrails";

    public string TargetId => TenantId;
}

public sealed class SetAgentGuardrailsValidator : AbstractValidator<SetAgentGuardrailsCommand>
{
    public SetAgentGuardrailsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Guardrails).NotNull();
        RuleForEach(x => x.Guardrails).ChildRules(g =>
        {
            g.RuleFor(x => x.Id).NotEmpty();
        });
    }
}

public sealed class SetAgentGuardrailsCommandHandler(
    IAgentGuardrailsRepository guardrails,
    ITenantProfileRepository profiles,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<SetAgentGuardrailsCommand, AgentGuardrailsResult>
{
    public async Task<AgentGuardrailsResult> Handle(SetAgentGuardrailsCommand request, CancellationToken ct)
    {
        var knownIds = AgentGuardrailCatalog.All.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingRow = await guardrails.GetAsync(request.TenantId, ct);
        var toggles = AgentGuardrailCatalog.ParseOrDefault(existingRow?.TogglesJson);

        foreach (var incoming in request.Guardrails)
        {
            if (!knownIds.Contains(incoming.Id))
                continue;
            toggles[incoming.Id] = incoming.On;
        }

        AgentGuardrailCatalog.ForceLockedOn(toggles);

        var g5Present = request.Guardrails.Any(g =>
            string.Equals(g.Id, AgentGuardrailCatalog.G5, StringComparison.OrdinalIgnoreCase));

        TenantProfile? profile = null;
        if (g5Present)
        {
            profile = await profiles.GetAsync(request.TenantId, ct)
                ?? new TenantProfile { TenantId = request.TenantId };
            profile.WaitlistAutoBook = toggles[AgentGuardrailCatalog.G5];
        }

        var stored = new AgentGuardrails
        {
            TenantId = request.TenantId,
            TogglesJson = AgentGuardrailCatalog.Serialize(toggles),
        };

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // agent_guardrails before tenant_profiles — a stable two-table order so a concurrent
            // profile PUT cannot deadlock with this write.
            await guardrails.UpsertAsync(stored, ct2);
            if (profile is not null)
                await profiles.UpsertAsync(profile, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "agent.guardrails_set",
                    TargetType: "agent_guardrails",
                    TargetId: request.TenantId,
                    Decision: "allow",
                    DataClasses: ["agent_policy"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.TenantId),
                ct2);
        }, ct);

        return new AgentGuardrailsResult(AgentGuardrailCatalog.ToItems(toggles));
    }
}
