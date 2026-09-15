using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cordia.Infrastructure.EntityFramework;

/// <summary>EF Core context for the Cordia API.</summary>
public class ApplicationDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Spec 14 / Inv-12 — every audit row's metadata is sanitized at save
        // time regardless of which write path produced it. Registered here
        // (not in DI) so test fixtures and design-time contexts get it too.
        optionsBuilder.AddInterceptors(AuditEventSanitizationInterceptor.Instance);
        base.OnConfiguring(optionsBuilder);
    }

    public DbSet<CapacitySlot> CapacitySlots => Set<CapacitySlot>();

    public DbSet<CapacityHold> CapacityHolds => Set<CapacityHold>();

    public DbSet<ConversationSession> ConversationSessions => Set<ConversationSession>();

    public DbSet<BookingIntent> BookingIntents => Set<BookingIntent>();

    public DbSet<BookingIntentRevision> BookingIntentRevisions => Set<BookingIntentRevision>();

    public DbSet<IntentConfirmation> IntentConfirmations => Set<IntentConfirmation>();

    public DbSet<BookingOperation> BookingOperations => Set<BookingOperation>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();

    public DbSet<WorkflowAttempt> WorkflowAttempts => Set<WorkflowAttempt>();

    public DbSet<OperationWorkItem> OperationWorkItems => Set<OperationWorkItem>();

    public DbSet<HandoffWorkItem> HandoffWorkItems => Set<HandoffWorkItem>();

    public DbSet<TenantResource> TenantResources => Set<TenantResource>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ConsumerInbox> ConsumerInbox => Set<ConsumerInbox>();

    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();

    public DbSet<DeadLetterMessage> DeadLetterMessages => Set<DeadLetterMessage>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<VerificationGrant> VerificationGrants => Set<VerificationGrant>();

    public DbSet<VerificationChallenge> VerificationChallenges => Set<VerificationChallenge>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<VendorCatalog> VendorCatalogs => Set<VendorCatalog>();

    public DbSet<VendorCatalogItem> VendorCatalogItems => Set<VendorCatalogItem>();

    public DbSet<DepositSession> DepositSessions => Set<DepositSession>();

    public DbSet<WaitlistEnrollment> WaitlistEnrollments => Set<WaitlistEnrollment>();

    public DbSet<WaitlistOffer> WaitlistOffers => Set<WaitlistOffer>();

    public DbSet<PreorderSaga> PreorderSagas => Set<PreorderSaga>();

    public DbSet<TenantChannelEndpoint> TenantChannelEndpoints => Set<TenantChannelEndpoint>();

    public DbSet<TenantDisplayName> TenantDisplayNames => Set<TenantDisplayName>();

    public DbSet<Operator> Operators => Set<Operator>();

    public DbSet<OperatorSession> OperatorSessions => Set<OperatorSession>();

    public DbSet<TenantProfile> TenantProfiles => Set<TenantProfile>();

    public DbSet<TenantDepositPolicy> TenantDepositPolicies => Set<TenantDepositPolicy>();

    public DbSet<TenantCapacityPrimitive> TenantCapacityPrimitives => Set<TenantCapacityPrimitive>();

    public DbSet<AgentGuardrails> AgentGuardrails => Set<AgentGuardrails>();

    public DbSet<AgentKnowledgeEntry> AgentKnowledgeEntries => Set<AgentKnowledgeEntry>();

    public DbSet<StaffResource> StaffResources => Set<StaffResource>();

    public DbSet<CustomerOperatorNotes> CustomerOperatorNotes => Set<CustomerOperatorNotes>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    /// <summary>
    /// Guest contact held out of the immutable canonical payload so it can actually be erased
    /// (spec 12/14 retention ruling, owner decision 2026-08-06).
    /// </summary>
    public DbSet<GuestContact> GuestContacts => Set<GuestContact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        ApplyTenantFilters(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType et in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(et.ClrType))
                continue;

            ApplyTenantFilter(modelBuilder, et.ClrType);
        }
    }

    private void ApplyTenantFilter(ModelBuilder modelBuilder, Type entityType)
    {
        var method = typeof(ApplicationDbContext)
            .GetMethod(nameof(SetTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(entityType);

        method.Invoke(this, [modelBuilder]);
    }

    private void SetTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
    }
}
