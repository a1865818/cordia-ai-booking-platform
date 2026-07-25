// Disambiguation aliases (mirror of Cordia.Infrastructure/GlobalUsings.cs): the generated
// Cordia.Domain.Generated namespace now emits TenantChannel (a quicktype wrapper) and
// TenantContext (the spec-15 claim-set DTO), whose simple names collide with the hand-written
// pg-enum and the multi-tenancy spine service that the test fixtures use. Resolve the CS0104
// ambiguity project-wide; use Cordia.Domain.Generated.TenantContext explicitly when the DTO is meant.
global using TenantChannel = Cordia.Domain.Enums.TenantChannel;
global using TenantContext = Cordia.Infrastructure.Services.TenantContext;
