// Omabilo Personal standalone API. Runs without IKON.
//
// IKON service       → replacement here
// sign-in (IKON auth) → Development: fictional "demo-…" users via the X-Demo-User header
//                       Anything else: JWT bearer tokens from an identity provider you configure
// persistent state    → IInvoiceStore: InMemory (tests) or a local SQLite file (development)
// UI streaming        → none: this is a JSON API for a future frontend

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Omabilo.Api.Auth;
using Omabilo.Api.Endpoints;
using Omabilo.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// ── Storage ─────────────────────────────────────────────────────────────────
var storage = config["Storage:Provider"] ?? "InMemory";

if (storage.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var path = config["Storage:SqlitePath"] ?? Path.Combine("App_Data", "omabilo-dev.db");
    builder.Services.AddSingleton<IInvoiceStore>(_ => new SqliteInvoiceStore(path));
}
else if (storage.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
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

var app = builder.Build();

// Errors become plain problem responses: no stack traces or record contents leave the server
app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", storage, auth = authMode.Length > 0 ? authMode : "Jwt" })).AllowAnonymous();
app.MapInvoiceEndpoints();
app.MapDemoEndpoints();

app.Run();

/// <summary>Lets the integration tests start the app in memory.</summary>
public partial class Program;
