using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.AgentKnowledge;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.AgentKnowledge.UpdateAgentKnowledge;

/// <summary>
/// Updates an existing knowledge-base entry (docs/backend-gaps.md G9). Owner-gated.
/// Returns <c>null</c> when the id is unknown (controller maps to 404).
/// </summary>
public sealed record UpdateAgentKnowledgeCommand(
    string TenantId,
    string EntryId,
    string? Answer,
    string? Status,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<AgentKnowledgeEntryResult?>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "agent.knowledge_updated";

    public string TargetType => "agent_knowledge";

    public string TargetId => EntryId;
}

public sealed class UpdateAgentKnowledgeValidator : AbstractValidator<UpdateAgentKnowledgeCommand>
{
    public UpdateAgentKnowledgeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Status)
            .Must(s => s is null || AgentKnowledgeStatusWire.Allowed.Contains(s))
            .WithMessage("status must be needs-answer or answered.");
    }
}

public sealed class UpdateAgentKnowledgeCommandHandler(
    IAgentKnowledgeRepository repo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<UpdateAgentKnowledgeCommand, AgentKnowledgeEntryResult?>
{
    public async Task<AgentKnowledgeEntryResult?> Handle(UpdateAgentKnowledgeCommand request, CancellationToken ct)
    {
        var existing = await repo.GetAsync(request.TenantId, request.EntryId, ct);
        if (existing is null)
            return null;

        if (request.Answer is not null)
        {
            existing.Answer = string.IsNullOrWhiteSpace(request.Answer) ? null : request.Answer.Trim();
            if (request.Status is null)
                existing.Status = AgentKnowledgeStatusWire.FromAnswer(existing.Answer);
        }

        if (request.Status is not null)
            existing.Status = AgentKnowledgeStatusWire.Parse(request.Status);

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await repo.UpdateAsync(existing, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "agent.knowledge_updated",
                    TargetType: "agent_knowledge",
                    TargetId: request.EntryId,
                    Decision: "allow",
                    DataClasses: ["agent_knowledge"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.TenantId),
                ct2);
        }, ct);

        return AgentKnowledgeStatusWire.ToResult(existing);
    }
}
