using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Interceptors;
using Cordia.Infrastructure.Menu;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Providers;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Retry;
using Cordia.Infrastructure.Security;
using Cordia.Infrastructure.Services;
using Cordia.Infrastructure.Services.Retention;
using Cordia.Infrastructure.Storage;
using Cordia.Infrastructure.TenantResolution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Extensions;

/// <summary>Registers the Infrastructure layer (EF Core, repositories).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">
    /// The host environment, when there is one. Only the menu-extraction startup gates read it
    /// (issue #94): a loopback <c>LangGraph:Host</c> and a missing shared secret are normal on a
    /// developer machine and are refused everywhere else.
    ///
    /// <para><b><c>null</c> reads as a deployment, not as dev.</b> A container assembled by hand —
    /// a unit test, a tool — has no <see cref="IHostEnvironment"/>, and defaulting that to
    /// "Development" would make every gate below vanish for anyone who simply did not pass this
    /// argument. A gate that disappears when nobody opts in is the silent misconfiguration these
    /// gates exist to prevent, so the fail-closed direction is the default and an offline caller
    /// says so explicitly.</para>
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var connectionString = configuration.GetConnectionString("CordiaDBConnection");
        var isDevelopment = environment?.IsDevelopment() ?? false;

        // Spec 13 T4 — correlation stamping interceptor (singleton: only depends on singleton ICorrelationContext).
        services.AddSingleton<CorrelationStampingInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options
                .AddInterceptors(sp.GetRequiredService<CorrelationStampingInterceptor>())
                .UseNpgsql(
                    connectionString,
                    o => o
                        .MapEnum<ConsentState>("consent_state")
                        .MapEnum<TenantChannel>("channel_enum")
                        .MapEnum<OperationTypeEnum>("operation_type_enum")
                        .MapEnum<OperationStatus>("operation_status_enum")
                        .MapEnum<BookingLifecycleStatus>("booking_lifecycle_status_enum")
                        .MapEnum<IdempotencyStatus>("idempotency_status_enum")
                        .MapEnum<VerificationLevel>("verification_level_enum")
                        .MapEnum<WorkflowStepStatus>("workflow_step_status_enum")
                        .MapEnum<AttemptOutcome>("attempt_outcome_enum")
                        .MapEnum<RetryClassification>("retry_classification_enum")
                        .MapEnum<InboxOutcome>("inbox_outcome_enum")
                        .MapEnum<ScheduledJobStatus>("scheduled_job_status_enum")
                        .MapEnum<DlqReplayState>("dlq_replay_state_enum")
                        .MapEnum<NotificationState>("notification_state_enum")
                        .MapEnum<NotificationEvent>("notification_event_enum")
                        .MapEnum<NotificationChannel>("notification_channel_enum")
                        .MapEnum<MessagePriority>("message_priority_enum")
                        .MapEnum<VerificationActionEnum>("verification_action_enum")
                        .MapEnum<VerificationRequestChannel>("verification_request_channel_enum")
                        .MapEnum<CatalogStatus>("catalog_status_enum")
                        .MapEnum<DepositStatus>("deposit_status_enum")
                        .MapEnum<WaitlistOfferStatus>("waitlist_offer_status_enum")
                        .MapEnum<PreorderSagaState>("preorder_saga_state_enum")
                        .MapEnum<SignalType>("signal_type_enum")
                        .MapEnum<EndpointStatus>("endpoint_status_enum")
                        .MapEnum<HoldStatus>("hold_status_enum")
                        .MapEnum<ReleaseReason>("release_reason_enum")
                        .MapEnum<AttributedSource>("attributed_source_enum")
                        .MapEnum<AttendanceStatus>("attendance_status_enum")
                        .MapEnum<CampaignStatus>("campaign_status_enum")
                        .MapEnum<KnowledgeStatus>("knowledge_status_enum"))
                .UseSnakeCaseNamingConvention();
        });

        // The DbContext consumes ITenantContext (read-only). A future ingress middleware
        // (tenant resolution, spec 15) resolves the concrete TenantContext from the same
        // scope and sets TenantId; both registrations return the SAME scoped instance so
        // the setter is observed by the DbContext's global query filter.
        services.AddScoped<ITenantContext>(_ => new TenantContext());
        services.AddScoped<TenantContext>(sp => (TenantContext)sp.GetRequiredService<ITenantContext>());

        services.AddScoped<IActorContext, ActorContext>();
        services.AddScoped<ActorContext>(sp => (ActorContext)sp.GetRequiredService<IActorContext>());

        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IBookingOperationRepository, BookingOperationRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IBookingReadRepository, BookingReadRepository>();
        services.AddScoped<IBookingIntentRepository, BookingIntentRepository>();
        services.AddScoped<IWorkflowStepRepository, WorkflowStepRepository>();
        services.AddScoped<IWorkflowAttemptRepository, WorkflowAttemptRepository>();
        services.AddScoped<IOperationWorkItemRepository, OperationWorkItemRepository>();
        services.AddScoped<IHandoffWorkItemRepository, HandoffWorkItemRepository>();
        services.AddScoped<ITenantResourceRepository, TenantResourceRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<IScheduledJobRepository, ScheduledJobRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IVerificationGrantRepository, VerificationGrantRepository>();
        services.AddScoped<IVerificationChallengeRepository, VerificationChallengeRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IDepositSessionRepository, DepositSessionRepository>();
        services.AddScoped<IWaitlistOfferRepository, WaitlistOfferRepository>();
        services.AddScoped<IWaitlistEnrollmentRepository, WaitlistEnrollmentRepository>();
        services.AddScoped<IVendorCatalogRepository, VendorCatalogRepository>();
        services.AddScoped<IGuestContactRepository, GuestContactRepository>();
        services.AddScoped<ITenantDisplayNameRepository, TenantDisplayNameRepository>();
        services.AddScoped<IOperatorRepository, OperatorRepository>();
        services.AddScoped<IOperatorSessionRepository, OperatorSessionRepository>();
        services.AddScoped<ITenantProfileRepository, TenantProfileRepository>();
        services.AddScoped<ITenantDepositPolicyRepository, TenantDepositPolicyRepository>();
        services.AddScoped<IAgentGuardrailsRepository, AgentGuardrailsRepository>();
        services.AddScoped<IAgentKnowledgeRepository, AgentKnowledgeRepository>();
        services.AddScoped<IStaffResourceRepository, StaffResourceRepository>();
        services.AddScoped<ICustomerOperatorNotesRepository, CustomerOperatorNotesRepository>();
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<ITenantChannelEndpointRepository, TenantChannelEndpointRepository>();
        services.AddScoped<IPreorderSagaRepository, PreorderSagaRepository>();

        // Spec 13 T6 — product-reporting projector (read-only funnel, Inv-16).
        services.AddScoped<IBookingFunnelReadModel, BookingFunnelReadModel>();
        services.AddScoped<IConsoleReadModel, ConsoleReadModel>();

        // Spec 07 T3 — capacity slot + hold repos + first-party reservation service.
        services.AddScoped<ICapacitySlotRepository, CapacitySlotRepository>();
        services.AddScoped<ICapacityHoldRepository, CapacityHoldRepository>();
        services.AddScoped<ICapacityLockOrder, CapacityLockOrder>();
        services.AddScoped<ICapacityReservation, FirstPartyCapacityReservation>();

        // Spec 07 T4 — primitive resolver + stub external adapter (spec 07 §13 open decision:
        // no real provider configured yet; resolver defaults every triple to FirstParty).
        // Scoped, not singleton: it now reads tenant_capacity_primitives through the scoped DbContext (#106).
        services.AddScoped<ITenantCapacityPrimitiveRepository, TenantCapacityPrimitiveRepository>();
        services.AddScoped<ICapacityPrimitiveResolver, CapacityPrimitiveResolver>();
        services.AddSingleton<IExternalInventoryAdapter, StubExternalInventoryAdapter>();

        // Spec 07 T5 — capacity options bound from "Capacity" config section.
        services.AddOptions<CapacityOptions>()
            .BindConfiguration(CapacityOptions.SectionName);

        services.AddScoped<RetentionSweepHandler>();
        // Spec 14 §24.3 — cross-tenant retention driver consumed by RetentionSweepHostedService.
        services.AddScoped<IRetentionSweeper, RetentionSweeper>();
        services.AddScoped<IMenuIngestionMonitor, Services.Menu.MenuIngestionMonitor>();

        // Spec 14 — consent vertical: session consent updates + jurisdiction policy.
        services.AddScoped<IConversationSessionRepository, ConversationSessionRepository>();
        services.AddSingleton<IJurisdictionPolicyProvider, JurisdictionPolicyProvider>();

        // Spec 14 §24.4 — server-side secret custody; values never serialized or logged.
        services.AddSingleton<ISecretStore, ConfigurationSecretStore>();

        // Spec 15 Phase 2 — HS256 compact-JWS TenantContext signer/verifier. Signing keys are
        // loaded once at construction from the ISecretStore custody seam
        // (Secrets:TenantContextSigningKeys); they are never logged or serialized.
        services.AddSingleton<ITenantContextSigner, JwsTenantContextSigner>();

        // G1 web-origin operator session — same signing keys as tenant context, distinct typ
        // (opctx+jws) and claim set. PasswordHasher is ASP.NET Identity's PBKDF2 hasher.
        services.AddSingleton<IOperatorSessionSigner, JwsOperatorSessionSigner>();
        services.AddSingleton<IPasswordHasher<Operator>, PasswordHasher<Operator>>();
        services.AddOptions<OperatorSessionOptions>()
            .BindConfiguration(OperatorSessionOptions.SectionName);

        // Spec 15 Phase 3 — TenantContext TTL, bound from the "TenantContext" config section.
        services.AddOptions<TenantContextOptions>()
            .BindConfiguration(TenantContextOptions.SectionName);

        // Injectable clock (matches the signer's TimeProvider seam) so the resolution handler's
        // issued_at/expires_at are pinnable in tests. Real wall clock in production.
        services.AddSingleton(TimeProvider.System);

        // Spec 14 §24.3/§24.4 — tenant-scoped encrypted blob custody (AES-256-GCM at rest,
        // key resolved lazily from the secret store; expired objects swept by RetentionSweeper).
        services.AddSingleton<IObjectStore, EncryptedFileObjectStore>();

        // Pure, stateless, thread-safe → Singleton (spec 04 §5).
        services.AddSingleton<ICanonicalRequestHasher, CanonicalRequestHasher>();

        // Spec 06 T2 reliability building blocks — all singletons (stateless or own thread-safe state).
        services.AddSingleton<RetryClassifier>();
        services.AddSingleton<IRetryPolicy, FullJitterRetryPolicy>();
        services.AddSingleton<IProviderCircuitBreaker, InMemoryProviderCircuitBreaker>();
        services.AddSingleton<IBookingProviderAdapter, StubBookingProviderAdapter>();

        // Spec 06 T7 deposit + verification seams — singletons (stateless stubs; spec 09 / spec 08 replace).
        services.AddScoped<IDepositPolicy, TenantDepositPolicyProvider>();
        services.AddSingleton<IVerificationService, NoUserActionRequiredService>();

        // Spec 11 — artifact scanning port (deterministic stub; real ClamAV swaps in later behind
        // this port). Stateless → singleton.
        services.AddSingleton<IArtifactScanner, StubArtifactScanner>();

        // Spec 11 / issue #87 — the menu-extraction seam.
        //
        // The shared secret is read through the SAME ISecretStore seam the client uses, not through
        // a second copy of the "Secrets:<name>" configuration path. It is constructed here rather
        // than resolved from the container because these are REGISTRATION-time closures: the
        // container does not exist yet. ConfigurationSecretStore is stateless over the same
        // IConfiguration the registered singleton gets, so this instance answers identically.
        // Nothing about the VALUE is ever put in a validation message (Inv-12) — only whether it is
        // present and whether it is representable on the wire.
        var extractionSecrets = new ConfigurationSecretStore(configuration);

        services.AddOptions<LangGraphOptions>()
            .BindConfiguration(LangGraphOptions.SectionName)
            .Validate(o => o.ExtractTimeoutSeconds > 0,
                "LangGraph:ExtractTimeoutSeconds must be greater than zero.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ExtractPath),
                "LangGraph:ExtractPath must be set.")
            // An ExtractPath that can name its own authority silently defeats the Host validation
            // below — HttpClient ignores BaseAddress whenever the request URI is absolute, and a
            // network-path reference ("//evil.example/x") replaces the host even though Uri calls it
            // relative. Either way the menu text and the X-Cordia-Internal-Key go somewhere `Host`
            // never authorised. See LangGraphOptions.HasRelativeExtractPath.
            .Validate(o => o.HasRelativeExtractPath,
                "LangGraph:ExtractPath must be a relative route path that cannot change the host.")
            // Only meaningful for the real client — the stub never dials out, so a Host-less
            // offline configuration must still start. Checked AT STARTUP rather than on the first
            // ingestion: a malformed host would otherwise surface hours later as an
            // ExtractFailed catalog with nothing pointing at the config as the cause.
            .Validate(o => o.UseStubExtractor || o.HasUsableHost,
                "LangGraph:Host must be an absolute http/https URI when the real menu extractor is used.")
            // A path (or query/fragment) on the host is DISCARDED by RFC 3986 resolution against
            // BaseAddress, so `https://gw.internal/langgraph` POSTs to
            // `https://gw.internal/internal/menu/extract` and returns a 404 the operator reads as
            // "the agent returned HTTP 404". Addressing the agent through a reverse-proxy route is
            // the obvious thing for a deployment to do, so this is refused rather than dropped.
            // Environment-independent: a path is wrong on a developer machine too.
            .Validate(o => o.UseStubExtractor || o.HostIsAuthorityOnly,
                "LangGraph:Host must carry scheme and authority only — a path on the host is "
                + "silently discarded when ExtractPath is resolved against it.")
            // A DEPLOYED API that dials a loopback address is calling itself. The old committed
            // default (`http://localhost:2024`) is an absolute http URI, so the rule above passed
            // it, nothing printed the resolved host at startup, and the first symptom was every
            // ingestion settling ExtractFailed. Dev is where a loopback host is CORRECT, so it is
            // the one environment exempted.
            .Validate(o => o.UseStubExtractor || isDevelopment || !o.HostIsLoopbackOrUnspecified,
                "LangGraph:Host must not be a loopback or unspecified address outside Development — "
                + "a deployed API pointed at localhost/127.0.0.1/::1/0.0.0.0 is calling itself.")
            // D-G, fail closed. The route is only protected when BOTH sides are configured, and
            // outside Development neither side may be left open: the Python route now refuses to
            // serve anonymously without an explicit opt-out, so an API with no key would 401 on
            // every ingestion. Checked here so a deploy that forgot the secret never starts.
            .Validate(
                o => o.UseStubExtractor || isDevelopment || HasExtractionKey(extractionSecrets),
                $"Secrets:{LangGraphMenuExtractorClient.InternalKeySecretName} must be configured "
                + "outside Development when the real menu extractor is used — the extraction route "
                + "requires the X-Cordia-Internal-Key header.")
            // A non-ASCII secret is a PERMANENT 401 with no diagnosis available from either side:
            // Starlette decodes header values as latin-1 while the agent reads its environment as
            // UTF-8, and the two encodings agree only on ASCII. Control characters are refused with
            // them because the client adds the header with TryAddWithoutValidation, so a CR/LF in
            // the secret would be request-header injection rather than a failed comparison.
            .Validate(
                o => o.UseStubExtractor || ExtractionKeyIsWireSafe(extractionSecrets),
                $"Secrets:{LangGraphMenuExtractorClient.InternalKeySecretName} must be printable "
                + "ASCII — the extraction route decodes the header as latin-1 and compares UTF-8, "
                + "so the two sides agree only on ASCII.")
            .ValidateOnStart();

        // #84 replaces the implementation, not the port: object-storage refs land behind this
        // same seam. Stateless → singleton.
        services.AddSingleton<IMenuArtifactTextResolver, InlineMenuArtifactTextResolver>();

        // D-E — explicit configuration, never environment sniffing. `false` is the default, so a
        // deployment that says nothing gets the REAL extractor; only an explicit opt-in downgrades
        // to the stub, and Program.cs says so loudly when that happens outside Development.
        // Read directly (rather than through IOptions) because the branch is a REGISTRATION
        // decision: it has to be made while the container is still being built.
        if (configuration.GetValue($"{LangGraphOptions.SectionName}:UseStubExtractor", false))
        {
            services.AddSingleton<IIsolatedMenuExtractor, StubIsolatedMenuExtractor>();
        }
        else
        {
            services.AddHttpClient<IIsolatedMenuExtractor, LangGraphMenuExtractorClient>((sp, client) =>
            {
                var langGraph = sp.GetRequiredService<IOptions<LangGraphOptions>>().Value;
                client.BaseAddress = new Uri(langGraph.Host, UriKind.Absolute);
                // Belt to the client's own linked CTS: two independent deadlines at the same value,
                // both of which surface as OperationCanceledException and are translated there.
                client.Timeout = TimeSpan.FromSeconds(langGraph.ExtractTimeoutSeconds);
            });
        }

        // Spec 11 — pre-order item-reservation port (deterministic stub; real POS adapter swaps in
        // later behind this port). Stateless → singleton. Payment is no longer a saga concern: the
        // whole booking (incl. spec-09 payment) is delegated to the spec-06 engine (§5.3, divergence #2).
        services.AddSingleton<IPreorderItemReservation, StubPreorderItemReservation>();

        // Spec 06 T11 validation policy seam — singleton (always-pass stub; spec 04 real engine replaces).
        services.AddSingleton<IValidationPolicy, AlwaysValidPolicy>();

        // Spec 10 T2 — notification provider adapters (one per channel; injected as IEnumerable<INotificationProviderAdapter>).
        services.AddScoped<INotificationProviderAdapter, SmsNotificationAdapter>();
        services.AddScoped<INotificationProviderAdapter, EmailNotificationAdapter>();
        services.AddScoped<INotificationProviderAdapter, ChannelCallbackAdapter>();

        // Spec 10 T2 — notification support seams (stubs; real store/consent/minimization is §31).
        services.AddScoped<INotificationTemplateResolver, NotificationTemplateResolver>();
        services.AddScoped<IConsentGate, ConsentGate>();
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddMessaging();

        // Spec 13 T2 — OpenTelemetry tracer/meter + metric instrument singletons.
        services.AddObservability(configuration);

        return services;
    }

    /// <summary>
    /// Whether the menu-extraction shared secret is configured at all. Whitespace-only counts as
    /// absent, matching both the client (which treats a blank secret as "send no header") and the
    /// route (which reads its expected value as <c>os.environ.get(...).strip()</c>).
    /// </summary>
    private static bool HasExtractionKey(ISecretStore secrets)
        => !string.IsNullOrWhiteSpace(
            secrets.GetSecret(LangGraphMenuExtractorClient.InternalKeySecretName));

    /// <summary>
    /// Whether the configured secret can survive the wire. Trimmed first, because that is what the
    /// client actually sends; an absent secret is vacuously safe, so this rule reports only the
    /// charset problem and the rule above reports absence.
    /// </summary>
    private static bool ExtractionKeyIsWireSafe(ISecretStore secrets)
    {
        var key = secrets.GetSecret(LangGraphMenuExtractorClient.InternalKeySecretName)?.Trim();
        return string.IsNullOrEmpty(key) || key.All(c => char.IsAscii(c) && !char.IsControl(c));
    }
}
