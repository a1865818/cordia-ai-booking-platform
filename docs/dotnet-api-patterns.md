# .NET API Patterns (Cordia)

Conventions for `apps/api`. Follow these so the codebase stays consistent and
high quality. Keep code **minimal** — the smallest change that satisfies the
pattern. No speculative abstractions, no unused parameters, no dead code.

## Layering

Clean architecture, four projects. Dependencies point inward only:

```
Cordia.Api ──> Cordia.Application ──> Cordia.Domain
        └────> Cordia.Infrastructure ──> Cordia.Domain
```

| Layer | Contains | Never contains |
|-------|----------|----------------|
| **Domain** | Entities, enums, domain interfaces, generated shared types | EF, HTTP, MediatR, DI |
| **Application** | MediatR handlers (CQRS), validators, DTOs, mapping | Controllers, DbContext queries inline |
| **Infrastructure** | `ApplicationDbContext`, repository/service impls, external clients | Business rules |
| **Api** | Controllers, `Program.cs`, middleware, Swagger, auth | Business logic, EF queries |

Rule: a controller's only job is to send a MediatR request and return the result.
Business logic lives in Application handlers; data access behind Domain interfaces
implemented in Infrastructure.

## CQRS handlers (Application)

One request + one handler per file, colocated under a feature folder beneath the
standard `Handlers/` bucket (`Application/Handlers/<Feature>/`). Commands mutate,
queries read. Records for messages.

**Application top-level folders are standard buckets, never feature names** — feature
names live *inside* `Handlers/`:

| Folder | Holds |
|--------|-------|
| `Handlers/<Feature>/` | what MediatR dispatches to — request + handler + validator + result |
| `Behaviours/` | `IPipelineBehavior<,>` middleware (validation, inbox dedup) |
| `Messaging/` | queue↔MediatR bridge: dispatcher, consumer registry, request markers |
| `Extensions/` | DI (`AddApplication()`) |

A controller and the queue dispatcher both call `mediator.Send` yet are *not* handlers:
"uses MediatR" is not the test for `Handlers/`; "is an `IRequestHandler<,>`" is.

```csharp
public record GetWidgetQuery(Guid Id) : IRequest<WidgetDto>;

public class GetWidgetQueryHandler(IWidgetRepository repo)
    : IRequestHandler<GetWidgetQuery, WidgetDto>
{
    public async Task<WidgetDto> Handle(GetWidgetQuery request, CancellationToken ct)
    {
        var widget = await repo.GetAsync(request.Id, ct)
            ?? throw new NotFoundException(nameof(Widget), request.Id);
        return widget.ToDto();
    }
}
```

- Handlers depend on **Domain interfaces**, never on `ApplicationDbContext` directly.
- Pass `CancellationToken` through to every async call.
- Register via `AddApplication()` (assembly scan) — don't hand-register handlers.

## Validation

FluentValidation, one validator per command/query that needs it, same folder.
Validators target the MediatR **command/query** type and run via a `ValidationPipelineBehavior`
(`IPipelineBehavior<,>`, registered in `AddApplication()`) before the handler; failures throw
`ValidationException`, which the global exception handler maps to HTTP 400. Validators bind to the
command, not the controller's request DTO, so this MediatR-pipeline seam — not an MVC action filter
— is the right place. (Note: `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` is on,
so the validators are the **sole** source of required-field enforcement — every required field needs
an explicit rule.)

```csharp
public class CreateWidgetValidator : AbstractValidator<CreateWidgetCommand>
{
    public CreateWidgetValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}
```

Don't manually validate in handlers or controllers.

## Controllers (Api)

Thin. `[ApiController]`, route `api/[controller]`, inject `IMediator`, return
`ActionResult<T>`. No try/catch (middleware handles it), no business logic.

```csharp
[ApiController]
[Route("api/[controller]")]
public class WidgetsController(IMediator mediator) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WidgetDto>> Get(Guid id) =>
        Ok(await mediator.Send(new GetWidgetQuery(id)));
}
```

## Error handling

Throw typed domain exceptions; a global exception-handling middleware maps them
to HTTP status codes + a consistent problem response. Never return raw 500s for
expected failures, never swallow exceptions.

## EF Core (Infrastructure)

Infrastructure top-level folders are standard buckets too, never feature names:
`EntityFramework/` (DbContext, `Configurations/`, migrations), `Repositories/`
(Domain-interface impls), `Services/` (other Domain-interface impls — canonical hashing,
tenant context, unit of work, `Retention/`), `Messaging/` (transport, hosted services,
dead-letter handling), `Extensions/` (DI, `AddInfrastructure()`).

- All DB access goes through `ApplicationDbContext`; expose it to handlers behind
  Domain repository interfaces.
- Snake_case naming convention is on (`UseSnakeCaseNamingConvention`); entity
  config via `IEntityTypeConfiguration<T>` classes, not inline in `OnModelCreating`
  once it grows.
- Read queries use `.AsNoTracking()`.
- Schema changes = a new migration (never edit applied migrations):

  ```bash
  cd apps/api/src/Cordia.Infrastructure
  dotnet ef migrations add <Name> --startup-project ../Cordia.Api \
    --context ApplicationDbContext --output-dir EntityFramework/Migrations
  ```

- pg-enum-mapped enums: add the enum to `tools/config/ef-enum-mappings.json`
  (quicktype suffixes the type name with `Enum`) so `[PgName]`/`[JsonStringEnumMemberName]`
  attributes are generated, then register the pg enum in the Npgsql data source.

## Transactions, locking, and state ownership

**Lock ordering.** When one transaction takes row locks on more than one table — or more than one
row of a table — the acquisition order must be **global and total**, or two transactions taking the
same rows in different orders will deadlock (`40P01`). Two rules, both learned the hard way here:

- Fix the **order**, never add a `40P01`/`40001` retry wrapper. A retry treats the symptom and hides
  the next inversion everywhere in the system.
- When a transaction touches several rows of one table, order them by a **total** key (ascending id),
  not by their role in the operation. "Previous then new" and "old then new" are not orders — two
  transactions can disagree about which row is which.

Acquire the whole set **up front**, before the first write, so later steps re-touch rows the
transaction already holds and can re-order nothing. Express the order in **one** place; a rule each
call site re-derives is a rule each call site can get wrong.

The capacity tables' concrete rule (**CLO**) is owned by
[`docs/specs/07-capacity-and-holds.md`](specs/07-capacity-and-holds.md) — holds before slots, each
set ascending by id, with a documented carve-out for inserts. `ICapacityLockOrder` implements it.

**Isolation** is READ COMMITTED (`UnitOfWork` opens with no isolation argument). Do not raise it to
work around a race: the conditional-write pattern (`UPDATE … WHERE <guard>`, then branch on
rows-affected) is the sanctioned mechanism and depends on those semantics.

**Constraint violations abort the whole transaction.** PostgreSQL does not let you catch a `23505`
and carry on — every subsequent command in that transaction fails with "current transaction is
aborted", including the COMMIT. So for "insert unless someone already did", use
`INSERT … ON CONFLICT DO NOTHING` and branch on rows-affected; **never** read-then-insert, and never
try to catch the violation. `CapacitySlotRepository.TryAddAsync`,
`IdempotencyRepository.ElectCreatorAsync` and `OperationWorkItemRepository.TryAddOpenAsync` are the
worked examples.

**State ownership.** A state machine in `Cordia.Domain` owns its states *and their classifications*.
Handlers, repositories and read models must **ask** it (`IsTerminal`, `IsAwaitingUser`,
`IsSlaEligible`, …) rather than keep a private set — a duplicated state set fails **silently** when
the enum moves. Where a materialised collection is genuinely required (EF Core cannot translate a
method call into SQL), **derive** it from the state machine rather than retyping the members.
`StateOwnershipArchitectureTests` enforces this by reflection and fails on any new unjustified
collection; see [`docs/specs/06-booking-workflow-and-fulfillment.md`](specs/06-booking-workflow-and-fulfillment.md)
§19.1 for the permitted exceptions.

## Generated shared types

Types in `Cordia.Domain.Generated` (from `libs/shared-schemas`) are **read-only** —
never hand-edit `SharedTypes.cs`. To change them, edit the Pydantic models in
`apps/langgraph/cordia_agent/` and run `bun nx run shared-schemas:generate-types`.

## Testing

xUnit v3; `[Trait("Category", "Unit")]` or `"Integration"`. Moq for unit isolation,
Alba (`AlbaHost.For<Program>`) for integration. Test folder mirrors source folder.

- **Unit** (`test/Cordia.Tests/UnitTests/`): handlers/services in isolation, mocked deps.
- **Integration** (`test/Cordia.Tests/IntegrationTests/`): full HTTP through the real pipeline.
- Test class names omit `Command`/`Query` suffixes (e.g. `GetWidgetTests`).
- Every new behavior gets a test; bug fixes get a regression test.

## DI registration

Each layer owns one extension: `AddApplication()`, `AddInfrastructure(config)`.
`Program.cs` composes them. Don't scatter `services.AddScoped` calls across the codebase.

## Minimal-code checklist (before committing)

- [ ] Smallest change that works — no speculative interfaces, options, or layers.
- [ ] Controller is thin; logic is in a handler; data access behind a Domain interface.
- [ ] `CancellationToken` threaded through async calls.
- [ ] Validator added for new commands/queries with input rules.
- [ ] Typed exception for failures; no swallowed errors.
- [ ] Tests added/updated; `bun nx test api` green.
- [ ] `bun nx lint api` green (new `.cs` files saved **UTF-8 with BOM** — `.editorconfig` enforces it).
- [ ] No edits to generated `SharedTypes.cs` or applied EF migrations.
