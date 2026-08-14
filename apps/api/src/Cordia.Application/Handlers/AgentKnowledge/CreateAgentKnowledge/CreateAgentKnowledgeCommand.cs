using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.AgentKnowledge;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.AgentKnowledge.CreateAgentKnowledge;

/// <summary>
/// Creates a manual knowledge-base entry (docs/backend-gaps.md G9). Owner-gated.
/// Empty <c>a</c> → status <c>needs-answer</c>; otherwise <c>answered</c>. Source is always
/// <c>manual</c> on this path.
/// </summary>
public sealed record CreateAgentKnowledgeCommand(
    string TenantId,
    string Question,
    string? Answer,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<AgentKnowledgeEntryResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "agent.knowledge_created";

    public string TargetType => "agent_knowledge";

    public string TargetId => TenantId;
}

public sealed class CreateAgentKnowledgeValidator : AbstractValidator<CreateAgentKnowledgeCommand>
{
    public CreateAgentKnowledgeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Question)
            .NotEmpty()
            .WithMessage("q is required.");
    }
}

public sealed class CreateAgentKnowledgeCommandHandler(
    IAgentKnowledgeRepository repo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<CreateAgentKnowledgeCommand, AgentKnowledgeEntryResult>
{
    public async Task<AgentKnowledgeEntryResult> Handle(CreateAgentKnowledgeCommand request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var answer = string.IsNullOrWhiteSpace(request.Answer) ? null : request.Answer.Trim();
        var entry = new AgentKnowledgeEntry
        {
            TenantId = request.TenantId,
            EntryId = $"knw_{Guid.NewGuid():N}",
            Question = request.Question.Trim(),
            Answer = answer,
            Status = AgentKnowledgeStatusWire.FromAnswer(answer),
            Source = "manual",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await repo.AddAsync(entry, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "agent.knowledge_created",
                    TargetType: "agent_knowledge",
                    TargetId: entry.EntryId,
                    Decision: "allow",
                    DataClasses: ["agent_knowledge"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.TenantId),
                ct2);
        }, ct);

        return AgentKnowledgeStatusWire.ToResult(entry);
    }
}
