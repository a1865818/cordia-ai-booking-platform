// Disambiguation aliases for names that the generated Cordia.Domain.Generated namespace
// (Pydantic -> JSON Schema -> C#) now shadows within this project:
//   * TenantChannel  — the hand-written pg-enum used by EF configs/migrations
//                      (Cordia.Domain.Generated also emits a quicktype convenience wrapper class).
//   * TenantContext  — the multi-tenancy spine service (Cordia.Domain.Generated also emits the
//                      spec-15 claim-set DTO of the same simple name).
// These aliases resolve the CS0104 ambiguity across every Infrastructure file — including the
// EF migration Designer/snapshot files that import both namespaces — without hand-editing
// generated code or applied migrations. To use the generated DTO in this project, fully qualify it
// as Cordia.Domain.Generated.TenantContext.
global using TenantChannel = Cordia.Domain.Enums.TenantChannel;
global using TenantContext = Cordia.Infrastructure.Services.TenantContext;
