using Cordia.Application.Extensions;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Api.Logging;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
// The scrubbing enricher runs on EVERY event (spec 14 §24.4, Inv-12): credential-named
// properties and Luhn-valid PAN shapes never reach a sink. Sinks/levels stay config-driven.
// Redaction:SensitiveFields adds deployment/tenant-configured selectors (spec 14 §4.1).
builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.With(new LogScrubbingEnricher(
            context.Configuration.GetSection("Redaction:SensitiveFields").Get<string[]>())));

// --- Layers ---
builder.Services.AddApplication();
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

// Resolve tenant from X-Tenant-Id header (dev/test shim — replaced by signed-context resolution in spec 08/15).
app.UseMiddleware<Cordia.Api.Middleware.TenantResolutionMiddleware>();

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
