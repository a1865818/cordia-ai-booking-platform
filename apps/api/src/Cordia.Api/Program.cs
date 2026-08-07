using Cordia.Application.Extensions;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Observability;
using Cordia.Api.Logging;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
// §25.1/§25.3 + spec 14 §24.4 (Inv-12): correlation enrichment on every line, two independent
// default-deny redaction passes before any sink, and a fenced-off model-trace lane.
//   • SerilogCorrelationEnricher tags every event with the ambient correlation ids (§25.1).
//   • LogScrubbingEnricher runs on EVERY event — credential-named properties and Luhn-valid PAN
//     shapes are masked in message-template scalars and destructured trees alike (spec 14 §24.4).
//     Redaction:SensitiveFields adds deployment/tenant-configured selectors (spec 14 §4.1).
//   • RedactingDestructuringPolicy is the structured-object backstop for the @-destructured path.
// The app (console) sink EXCLUDES events tagged ModelTrace; a separate sink INCLUDES ONLY them so
// model prompts/outputs are never co-mingled with app logs (Inv-12). MinimumLevel + per-source
// overrides flow in via ReadFrom.Configuration (appsettings "Serilog"). PROD swap point: the
// model-trace sub-logger writes to Console here for local visibility; point it at the
// stricter-access, shorter-retention store (§25.3) per environment by replacing its WriteTo.
builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.With(new SerilogCorrelationEnricher())
        .Enrich.With(new LogScrubbingEnricher(
            context.Configuration.GetSection("Redaction:SensitiveFields").Get<string[]>()))
        .Destructure.With(new RedactingDestructuringPolicy())
        .WriteTo.Logger(l => l
            .Filter.ByExcluding(e => e.Properties.ContainsKey(ModelTraceSink.MarkerProperty))
            .WriteTo.Console())
        .WriteTo.Logger(l => l
            .Filter.ByIncludingOnly(e => e.Properties.ContainsKey(ModelTraceSink.MarkerProperty))
            .WriteTo.Console(
                outputTemplate:
                "[MODEL-TRACE {Timestamp:HH:mm:ss}] {Message:lj}{NewLine}")));

// --- Layers ---
builder.Services.AddApplication();

// An asserted `X-Actor-Role` may authorize a reschedule only where asserted identity is already
// the accepted posture — the same places the `X-Tenant-Id` shim is enabled, and for the same
// reason: neither is proven, and outside Development both are unauthenticated access to another
// party's data (Inv-11/Inv-13). Default ON in Development, OFF elsewhere; an explicit
// `Authorization:AllowAssertedStaffRole` overrides. Superseded by signed actor context (spec 08/15).
builder.Services.PostConfigure<Cordia.Application.Common.Authorization.AssertedActorOptions>(o =>
    o.AllowAssertedStaffRole = builder.Configuration.GetValue(
        "Authorization:AllowAssertedStaffRole",
        builder.Environment.IsDevelopment()));
builder.Services.AddInfrastructure(builder.Configuration);

// --- Web ---
// Suppress ASP.NET's implicit [Required] on non-nullable reference-type properties.
// The quicktype-generated C# types (SubmitConfirmedIntentRequest, IntentRevision,
// BookingRequest, Confirmation) declare Pydantic-optional fields (e.g. ReplacesOperationId,
// SupersessionReason, LocationId, TargetBookingId, SpecialRequests) as non-nullable strings
// / collections because quicktype has no way to encode Pydantic's field(default=None) as
// nullable in C# without manual annotation.  With [ApiController] + NRT enabled, ASP.NET
// would treat every non-nullable reference-type property as implicitly [Required] and return
// HTTP 400 whenever the real Python agent omits those absent optionals.
// Requiredness for genuinely-required fields is enforced explicitly by FluentValidation
// (e.g. SubmitConfirmedIntentValidator), so disabling the implicit behaviour here is correct
// and consistent with the codebase's validation philosophy.
// TRACKED FOLLOW-UP: this is a mitigation for a type-generation gap — the proper fix is for the
// quicktype pipeline to emit `string?` for Pydantic-optional fields. Revisit if that lands.
builder.Services.AddControllers(options =>
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o =>
    {
        // The generated contract types (Cordia.Domain.Generated) rely on custom JsonConverters
        // (union types like DurationMinutes/StartsAt, enum converters, ISO datetime). They live in
        // Converter.Settings; the MVC pipeline must use them or request bodies fail to bind.
        foreach (var converter in Cordia.Domain.Generated.Converter.Settings.Converters)
            o.JsonSerializerOptions.Converters.Add(converter);
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // The generated union types (Cordia.Domain.Generated) serialize via custom converters to
    // either a scalar or a TriState sentinel string; Swashbuckle would otherwise reflect their
    // C# struct layout ({isNull, integer, enum}). Map them to their true wire schema.
    // Microsoft.OpenApi v2 API: Type is JsonSchemaType enum, Enum is IList<JsonNode>,
    // nullable is expressed by adding a JsonSchemaType.Null branch to the OneOf.
    c.MapType<Cordia.Domain.Generated.DurationMinutes>(() => new Microsoft.OpenApi.OpenApiSchema
    {
        OneOf = new List<Microsoft.OpenApi.IOpenApiSchema>
        {
            new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Integer, Format = "int64" },
            new Microsoft.OpenApi.OpenApiSchema
            {
                Type = Microsoft.OpenApi.JsonSchemaType.String,
                Enum = new List<System.Text.Json.Nodes.JsonNode>
                {
                    System.Text.Json.Nodes.JsonValue.Create("absent")!,
                    System.Text.Json.Nodes.JsonValue.Create("unknown")!,
                    System.Text.Json.Nodes.JsonValue.Create("declined")!,
                },
            },
            new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Null },
        },
    });
    c.MapType<Cordia.Domain.Generated.StartsAt>(() => new Microsoft.OpenApi.OpenApiSchema
    {
        OneOf = new List<Microsoft.OpenApi.IOpenApiSchema>
        {
            new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, Format = "date-time" },
            new Microsoft.OpenApi.OpenApiSchema
            {
                Type = Microsoft.OpenApi.JsonSchemaType.String,
                Enum = new List<System.Text.Json.Nodes.JsonNode>
                {
                    System.Text.Json.Nodes.JsonValue.Create("absent")!,
                    System.Text.Json.Nodes.JsonValue.Create("unknown")!,
                    System.Text.Json.Nodes.JsonValue.Create("declined")!,
                },
            },
            new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Null },
        },
    });

    // Dev-only: expose the X-Tenant-Id / X-Actor header shims (resolved by TenantResolutionMiddleware)
    // as Swagger "Authorize" fields so the Swagger UI can exercise the tenant-scoped endpoints. Type the
    // tenant once via the Authorize button and it is attached to every "Try it out" request. Not
    // registered outside Development, where a signed-context resolver (spec 08/15) replaces the shim.
    if (builder.Environment.IsDevelopment())
    {
        c.AddSecurityDefinition("TenantId", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Name = "X-Tenant-Id",
            Description = "Dev tenant id (e.g. 'demo'). Sent on every request after you click Authorize.",
        });
        c.AddSecurityDefinition("Actor", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Name = "X-Actor",
            Description = "Optional actor id recorded in audit events (e.g. 'owner1').",
        });
        // Microsoft.OpenApi 2.0 API: reference a defined scheme via OpenApiSecuritySchemeReference,
        // supplied through the AddSecurityRequirement(document => ...) overload (empty scope list).
        c.AddSecurityRequirement(document => new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("TenantId", document)] = [],
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Actor", document)] = [],
        });
    }
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<Cordia.Api.Errors.GlobalExceptionHandler>();

const string CorsPolicy = "CordiaCors";
builder.Services.AddCors(options =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    options.AddPolicy(CorsPolicy, policy =>
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Exception handler wraps the full pipeline — must be first middleware.
app.UseExceptionHandler();

// --- Database bootstrap (opt-in via config) ---
if (app.Configuration.GetValue("DatabaseBootstrap:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

// Mint/echo correlation id at the HTTP edge BEFORE tenant resolution so the ambient correlation
// context exists for all downstream work and logging (§25.1).
app.UseMiddleware<Cordia.Api.Middleware.CorrelationMiddleware>();

// Verify an inbound signed TenantContext (spec-15 Phase 4, Inv-13). Runs AFTER correlation (so the
// correlation id exists) and BEFORE the X-Tenant-Id shim so a verified context is AUTHORITATIVE — on
// a valid X-Tenant-Context it sets the request tenant + folds it into the ambient correlation before
// any handler runs; a bad context throws → 401. No header ⇒ pass through (endpoints fail closed at
// TenantGuard). This is the API-side mirror of the langgraph resolve_tenant node.
app.UseMiddleware<Cordia.Api.Middleware.TenantContextVerificationMiddleware>();

// Resolve tenant from the X-Tenant-Id header (DEV/TEST shim only). Gated so it NEVER overrides a
// context already verified above (Inv-13); superseded by signed-context resolution (spec 08/15).
//
// REGISTERED ONLY WHERE IT IS MEANT TO EXIST. `X-Tenant-Id` is trusted client input: wherever this
// runs and no signed context is present, any caller can name the tenant whose data they get. That is
// acceptable on a developer machine and in the test host, and is unauthenticated cross-tenant access
// anywhere else — so the shim is opt-in outside Development rather than always-on (Inv-13).
// Deployments that still need it set Tenancy:AllowHeaderShim explicitly; the default is off.
var allowTenantHeaderShim = app.Configuration.GetValue(
    "Tenancy:AllowHeaderShim",
    app.Environment.IsDevelopment());

if (allowTenantHeaderShim)
{
    app.UseMiddleware<Cordia.Api.Middleware.TenantResolutionMiddleware>();

    // Say so once at startup — and say it LOUDLY when it is dangerous.
    //
    // Only the disabled branch announced itself, so the one configuration that actually needs
    // announcing was the silent one: a deployment that switched `Tenancy:AllowHeaderShim` on
    // outside Development accepted a client-named tenant with nothing in the log to show for it.
    // "Disabled" is the safe state and barely worth a line; "enabled in Production" is
    // unauthenticated cross-tenant access (Inv-13) and is the thing an operator must be able to
    // find by grepping startup.
    if (app.Environment.IsDevelopment())
    {
        app.Logger.LogInformation(
            "X-Tenant-Id header shim is ENABLED ({Environment}); callers name their own tenant. "
            + "Expected on a developer machine and in the test host.",
            app.Environment.EnvironmentName);
    }
    else
    {
        app.Logger.LogWarning(
            "X-Tenant-Id header shim is ENABLED OUTSIDE DEVELOPMENT ({Environment}) because "
            + "Tenancy:AllowHeaderShim was set explicitly. Any caller can name the tenant whose "
            + "data they receive — this is unauthenticated cross-tenant access (Inv-13). Turn it "
            + "off unless a signed X-Tenant-Context issuer is genuinely unavailable.",
            app.Environment.EnvironmentName);
    }
}
else
{
    // A silently absent shim looks exactly like a broken tenant lookup.
    app.Logger.LogInformation(
        "X-Tenant-Id header shim is disabled ({Environment}); tenant must come from a verified X-Tenant-Context.",
        app.Environment.EnvironmentName);
}

// --- Pipeline ---
app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors(CorsPolicy);

// Serve the bundled SPA (present in the combined Docker image under wwwroot/).
// Harmless in dev where wwwroot is empty.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Client-side routes fall back to the SPA shell. Controller routes (/api/*) and
// Swagger are matched first, so only unmatched non-API paths hit index.html.
app.MapFallbackToFile("index.html");

await app.RunAsync();

// Exposed for the integration test host (Alba / WebApplicationFactory).
public partial class Program;
