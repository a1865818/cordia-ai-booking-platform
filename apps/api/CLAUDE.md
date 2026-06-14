# apps/api — Cordia .NET API

C# .NET 10 REST API, clean architecture.

**Before writing or editing code here, read the canonical patterns:**
[`../../docs/dotnet-api-patterns.md`](../../docs/dotnet-api-patterns.md).

It defines: layering rules (Domain ← Application/Infrastructure ← Api), CQRS
handlers with MediatR, FluentValidation, thin controllers, typed exceptions +
global error middleware, EF Core conventions (snake_case, migrations, pg-enums),
generated shared types (read-only), xUnit/Alba testing, DI registration, and a
**minimal-code checklist**. Keep changes minimal and consistent with that doc.

## Layout

```
src/
  Cordia.Domain/          entities, enums, interfaces, Generated/SharedTypes.cs
  Cordia.Application/     MediatR handlers, validators (AddApplication)
  Cordia.Infrastructure/  ApplicationDbContext, repositories (AddInfrastructure)
  Cordia.Api/             controllers, Program.cs, Swagger, Serilog, CORS
test/
  Cordia.Tests/           UnitTests/ (Category=Unit) + IntegrationTests/ (Category=Integration)
```

## Commands

```bash
bun nx serve api            # dotnet watch, https://localhost:8001
bun nx build api            # dotnet build Cordia.sln
bun nx test api             # unit tests (Category=Unit)
bun nx test-integration api # integration tests (Alba)
bun nx lint api             # dotnet format --verify-no-changes
```

New `.cs` files must be saved **UTF-8 with BOM** (`.editorconfig` `[*.cs]`), or
`bun nx lint api` fails.
