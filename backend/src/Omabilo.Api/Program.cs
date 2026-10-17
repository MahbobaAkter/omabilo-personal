// Omabilo Personal standalone API. Runs without IKON.
//
// IKON service       → replacement here
// sign-in (IKON auth) → Development: fictional "demo-…" users via the X-Demo-User header
//                       Anything else: JWT bearer tokens from an identity provider you configure
// persistent state    → IInvoiceStore + IFinanceStore: InMemory (tests) or a local SQLite file (development)
// UI streaming        → none: this is a JSON API for a future frontend

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

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// ── Storage ─────────────────────────────────────────────────────────────────
var storage = config["Storage:Provider"] ?? "InMemory";

if (storage.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var path = config["Storage:SqlitePath"] ?? Path.Combine("App_Data", "omabilo-dev.db");
    builder.Services.AddSingleton<IInvoiceStore>(_ => new SqliteInvoiceStore(path));
    builder.Services.AddSingleton<IFinanceStore>(sp => new SqliteFinanceStore(path, sp.GetRequiredService<TimeProvider>()));
}
else if (storage.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
    builder.Services.AddSingleton<IFinanceStore, InMemoryFinanceStore>();
}
else
{
    throw new InvalidOperationException($"Unknown Storage:Provider '{storage}'. Use InMemory or Sqlite.");
}

// ── Sign-in: fail closed ────────────────────────────────────────────────────
var authMode = config["Auth:Mode"] ?? "";

if (authMode.Equals("Demo", StringComparison.OrdinalIgnoreCase))
{
    if (!env.IsDevelopment())
    {
        throw new InvalidOperationException("Auth:Mode=Demo is allowed only in the Development environment.");
    }

    builder.Services.AddAuthentication(DemoUserAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DemoUserAuthenticationHandler>(DemoUserAuthenticationHandler.SchemeName, _ => { });
}
else
{
    var authority = config["Auth:Authority"];
    var audience = config["Auth:Audience"];

    if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
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

// ── Browser access for a future frontend (none allowed unless configured) ──
var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length > 0)
    {
        p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    }
}));

// ── Behind a hosting proxy ───────────────────────────────────────────────────
// Hosts end HTTPS at their proxy and forward plain HTTP with X-Forwarded-Proto/-For. Those
// headers are believed ONLY from trusted proxies, or a client could fake "https" or its IP.
// Default: only this machine. A deployment opts in with Proxy:TrustAllProxies (when the
// container is reachable solely through the platform's proxy) or Proxy:KnownNetworks (CIDRs).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
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
            throw new InvalidOperationException($"Proxy:KnownNetworks entry '{cidr}' is not a network like 10.0.0.0/8.");
        }

        o.KnownIPNetworks.Add(network);
    }
});

var app = builder.Build();

// First, so every later step sees the browser's real scheme (https) and address
app.UseForwardedHeaders();

// Errors become plain problem responses: no stack traces or record contents leave the server
app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();

    // The host's health check calls the container directly over HTTP, so /health is not redirected
    app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", storage, auth = authMode.Length > 0 ? authMode : "Jwt" })).AllowAnonymous();
app.MapInvoiceEndpoints();
app.MapDemoEndpoints();
app.MapFinanceEndpoints();

app.Run();

/// <summary>Lets the integration tests start the app in memory.</summary>
public partial class Program;
