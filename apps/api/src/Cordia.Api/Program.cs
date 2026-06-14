using Cordia.Application.Extensions;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ReadFrom.Configuration(context.Configuration));

// --- Layers ---
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// --- Web ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

const string CorsPolicy = "CordiaCors";
builder.Services.AddCors(options =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    options.AddPolicy(CorsPolicy, policy =>
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// --- Database bootstrap (opt-in via config) ---
if (app.Configuration.GetValue("DatabaseBootstrap:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
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
