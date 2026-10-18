
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Omabilo.Api.Auth;
using Omabilo.Api.Endpoints;
using Omabilo.Api.Finance;
using Omabilo.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// ── Storage ─────────────────────────────────────────────────────────────────
var storage = config["Storage:Provider"] ?? "InMemory";

if (storage.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = config.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres is required when Storage:Provider=Postgres.");

    builder.Services.AddSingleton<IInvoiceStore>(
        _ => new PostgresInvoiceStore(connectionString));

    builder.Services.AddSingleton<IFinanceStore>(
        sp => new PostgresFinanceStore(
            connectionString,
            sp.GetRequiredService<TimeProvider>()));
}
else if (storage.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var path = config["Storage:SqlitePath"]
        ?? Path.Combine("App_Data", "omabilo-dev.db");

    builder.Services.AddSingleton<IInvoiceStore>(
        _ => new SqliteInvoiceStore(path));

    builder.Services.AddSingleton<IFinanceStore>(
        sp => new SqliteFinanceStore(path, sp.GetRequiredService<TimeProvider>()));
}
else if (storage.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
    builder.Services.AddSingleton<IFinanceStore, InMemoryFinanceStore>();
}
else
{
    throw new InvalidOperationException(
        $"Unknown Storage:Provider '{storage}'. Use Postgres, Sqlite or InMemory.");
}

// ── Authentication ──────────────────────────────────────────────────────────
var authMode = config["Auth:Mode"] ?? "";

if (authMode.Equals("Demo", StringComparison.OrdinalIgnoreCase))
{
    if (!env.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Auth:Mode=Demo is allowed only in the Development environment.");
    }

    builder.Services.AddAuthentication(DemoUserAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DemoUserAuthenticationHandler>(
            DemoUserAuthenticationHandler.SchemeName, _ => { });
}
else if (authMode.Equals("Staging", StringComparison.OrdinalIgnoreCase))
{
    // No authentication scheme is configured in Staging.
    // The staging gate below rejects all paths except /health before
    // authentication or authorization middleware can run.
    builder.Services.AddAuthentication();
}
else
{
    var authority = config["Auth:Authority"];
    var audience = config["Auth:Audience"];

    if (string.IsNullOrWhiteSpace(authority) ||
        string.IsNullOrWhiteSpace(audience))
    {
        throw new InvalidOperationException(
            "Sign-in is not configured. Set Auth:Authority and Auth:Audience for your identity provider " +
            "(environment variables Auth__Authority and Auth__Audience), or run in Development with Auth:Mode=Demo.");
    }

    builder.Services.AddAuthentication().AddJwtBearer(o =>
    {
        o.Authority = authority;
        o.Audience = audience;
        o.RequireHttpsMetadata = !env.IsDevelopment();
    });
}

builder.Services.AddAuthorization();

// ── Browser access ───────────────────────────────────────────────────────────
var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length > 0)
    {
        p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    }
}));

// ── Behind a hosting proxy ───────────────────────────────────────────────────
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;

    if (config.GetValue<bool>("Proxy:TrustAllProxies"))
    {
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }

    foreach (var cidr in config.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
    {
        if (!System.Net.IPNetwork.TryParse(cidr, out var network))
        {
            throw new InvalidOperationException(
                $"Proxy:KnownNetworks entry '{cidr}' is not a network like 10.0.0.0/8.");
        }

        o.KnownIPNetworks.Add(network);
    }
});

var app = builder.Build();

// ── Initialize PostgreSQL before accepting requests ──────────────────────────
if (storage.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = config.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres is required.");

    await PostgresDatabaseInitializer.InitializeAsync(connectionString);
}

// ── Middleware ───────────────────────────────────────────────────────────────
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();

    // Hosting health checks may arrive over HTTP.
    app.UseWhen(
        ctx => !ctx.Request.Path.Equals(
            "/health", StringComparison.OrdinalIgnoreCase),
        branch => branch.UseHttpsRedirection());
}

app.UseCors();

// In staging, allow only /health. Reject every other path, including
// invoice, finance, demo, and unknown endpoints before authentication
// and authorization middleware can challenge an unconfigured scheme.
if (authMode.Equals("Staging", StringComparison.OrdinalIgnoreCase))
{
    app.UseWhen(
        ctx => !ctx.Request.Path.Equals(
            "/health", StringComparison.OrdinalIgnoreCase),
        branch => branch.Run(async context =>
        {
            context.Response.StatusCode =
                StatusCodes.Status503ServiceUnavailable;

            await context.Response.WriteAsJsonAsync(new
            {
                error = "staging_restricted",
                message = "This API is restricted during staging. Authentication is not configured."
            });
        }));
}

app.UseAuthentication();
app.UseAuthorization();

// ── Health check ─────────────────────────────────────────────────────────────
app.MapGet(
    "/health",
    () => Results.Ok(new
    {
        status = "ok",
        storage,
        auth = authMode.Length > 0 ? authMode : "Jwt"
    }))
    .AllowAnonymous();

// ── API endpoints ────────────────────────────────────────────────────────────
app.MapInvoiceEndpoints();
app.MapDemoEndpoints();
app.MapFinanceEndpoints();

app.Run();

/// <summary>Lets the integration tests start the app in memory.</summary>
public partial class Program;
