using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Console;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.SetCustomerNotes;

/// <summary>
/// Upserts operator-written notes and tags for a customer. Operator-gated (owner satisfies).
/// Omitted fields keep their stored value; a first write with both omitted stores empty notes.
/// </summary>
public sealed record SetCustomerNotesCommand(
    string TenantId,
    string CustomerRef,
    string? Note,
    IReadOnlyList<string>? Tags,
    string Actor,
    string ActorRole,
    string ReasonCode) : IRequest<CustomerSummary>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Operator;

    public string ActionName => "customer.notes_updated";

    public string TargetType => "customer";

    public string TargetId => CustomerRef;
}

public sealed class SetCustomerNotesValidator : AbstractValidator<SetCustomerNotesCommand>
{
    public const int MaxRefLength = 128;
    public const int MaxNoteLength = 8000;
    public const int MaxTagCount = 30;
    public const int MaxTagLength = 64;

    public SetCustomerNotesValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CustomerRef).NotEmpty().MaximumLength(MaxRefLength);
        RuleFor(x => x.Note)
            .MaximumLength(MaxNoteLength)
            .When(x => x.Note is not null);
        RuleFor(x => x.Tags)
            .Must(t => t!.Count <= MaxTagCount)
            .When(x => x.Tags is not null)
            .WithMessage($"tags must not exceed {MaxTagCount} items.");
        RuleForEach(x => x.Tags)
            .NotEmpty()
            .MaximumLength(MaxTagLength)
            .When(x => x.Tags is not null);
    }
}

public sealed class SetCustomerNotesCommandHandler(
    ICustomerOperatorNotesRepository notes,
    IConsoleReadModel reads,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<SetCustomerNotesCommand, CustomerSummary>
{
    public async Task<CustomerSummary> Handle(SetCustomerNotesCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var existing = await notes.GetAsync(command.TenantId, command.CustomerRef, ct2);
            var nextNote = command.Note ?? existing?.Note ?? string.Empty;
            var nextTags = command.Tags is not null
                ? JsonSerializer.Serialize(command.Tags)
                : existing?.TagsJson ?? "[]";

            await notes.UpsertAsync(
                new CustomerOperatorNotes
                {
                    TenantId = command.TenantId,
                    CustomerRef = command.CustomerRef,
                    Note = nextNote,
                    TagsJson = nextTags,
                    UpdatedAt = now,
                },
                ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: StaffRole.IsKnown(command.ActorRole) ? command.ActorRole : "operator",
                    ActorId: string.IsNullOrWhiteSpace(command.Actor) ? "unknown" : command.Actor,
                    Action: "customer.notes_updated",
                    TargetType: "customer",
                    TargetId: command.CustomerRef,
                    Decision: "allow",
                    DataClasses: ["operations"],
                    ReasonCode: string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode,
                    CorrelationId: command.CustomerRef,
                    OccurredAt: now),
                ct2);
        }, ct);

        return await reads.GetCustomerAsync(command.TenantId, command.CustomerRef, ct)
               ?? new CustomerSummary(
                   command.CustomerRef,
                   0, null, null, null, null,
                   Origin: null,
                   NoShowCount: 0,
                   Tags: command.Tags ?? [],
                   Note: string.IsNullOrEmpty(command.Note) ? null : command.Note);
    }
}
