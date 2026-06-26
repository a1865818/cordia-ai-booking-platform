using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
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

    public DbSet<ConversationSession> ConversationSessions => Set<ConversationSession>();

    public DbSet<BookingIntent> BookingIntents => Set<BookingIntent>();

    public DbSet<BookingIntentRevision> BookingIntentRevisions => Set<BookingIntentRevision>();

    public DbSet<IntentConfirmation> IntentConfirmations => Set<IntentConfirmation>();

    public DbSet<BookingOperation> BookingOperations => Set<BookingOperation>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();

    public DbSet<WorkflowAttempt> WorkflowAttempts => Set<WorkflowAttempt>();

    public DbSet<OperationWorkItem> OperationWorkItems => Set<OperationWorkItem>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ConsumerInbox> ConsumerInbox => Set<ConsumerInbox>();

    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();

    public DbSet<DeadLetterMessage> DeadLetterMessages => Set<DeadLetterMessage>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<VerificationGrant> VerificationGrants => Set<VerificationGrant>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<VendorCatalog> VendorCatalogs => Set<VendorCatalog>();

    public DbSet<VendorCatalogItem> VendorCatalogItems => Set<VendorCatalogItem>();

    public DbSet<DepositSession> DepositSessions => Set<DepositSession>();

    public DbSet<WaitlistEnrollment> WaitlistEnrollments => Set<WaitlistEnrollment>();

    public DbSet<WaitlistOffer> WaitlistOffers => Set<WaitlistOffer>();

    public DbSet<PreorderSaga> PreorderSagas => Set<PreorderSaga>();

    public DbSet<TenantChannelEndpoint> TenantChannelEndpoints => Set<TenantChannelEndpoint>();

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
