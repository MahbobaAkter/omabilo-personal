using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

/// <summary>
/// The API in Production behind a simulated hosting proxy: the proxy ends HTTPS and forwards
/// plain HTTP with X-Forwarded-Proto. Sign-in uses a test-only signing key in place of a real
/// identity provider, so a complete signed-in request can run without any outside service.
/// </summary>
public sealed class ProxyFactory(bool trustAllProxies, string[]? knownNetworks = null) : WebApplicationFactory<Program>
{
    public const string Issuer = "https://issuer.test/";
    public const string Audience = "api://omabilo-test";
    public const string WebsiteOrigin = "https://omabilo-test.vercel.app";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-signing-key-0123456789abcdef"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Storage:Provider", "InMemory");
        builder.UseSetting("Auth:Authority", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
        builder.UseSetting("Cors:AllowedOrigins:0", WebsiteOrigin);
        builder.UseSetting("https_port", "443");
        builder.UseSetting("Proxy:TrustAllProxies", trustAllProxies ? "true" : "false");

        for (var i = 0; i < (knownNetworks?.Length ?? 0); i++)
        {
            builder.UseSetting($"Proxy:KnownNetworks:{i}", knownNetworks![i]);
        }

        builder.ConfigureTestServices(services =>
        {
            // Test-only: validate tokens with a local key instead of fetching the provider's keys
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = null;
                o.Configuration = null;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    IssuerSigningKey = Key,
                };
            });

            // The in-memory server reports 127.0.0.1, which is trusted by default; requests here come
            // from the hosting proxy's private address instead, as they would in a real deployment
            services.AddTransient<IStartupFilter>(_ => new ProxyAddressFilter(ProxyIp));
        });
    }

    /// <summary>The address the simulated hosting proxy connects from.</summary>
    public const string ProxyIp = "10.1.2.3";

    private sealed class ProxyAddressFilter(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    public static string Token(string subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer,
        Audience = Audience,
        Subject = new ClaimsIdentity([new Claim("sub", subject)]),
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
    });

    /// <summary>A client that shows redirects instead of following them.</summary>
    public HttpClient Raw() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

public class ProxyTests
{
    private static HttpRequestMessage ViaProxy(HttpMethod method, string path, string? token = null)
    {
        var r = new HttpRequestMessage(method, path);
        r.Headers.Add("X-Forwarded-Proto", "https");
        r.Headers.Add("X-Forwarded-For", "203.0.113.7");
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    [Fact]
    public async Task A_signed_in_request_through_a_trusted_proxy_succeeds()
    {
        using var f = new ProxyFactory(trustAllProxies: true);
        var res = await f.Raw().SendAsync(ViaProxy(HttpMethod.Get, "/api/finance/overview", ProxyFactory.Token("user-123")));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("balance").GetDecimal() > 0);
    }

    [Fact]
    public async Task Through_the_proxy_without_a_token_the_api_still_requires_sign_in()
    {
        using var f = new ProxyFactory(trustAllProxies: true);
        var raw = f.Raw();

        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(ViaProxy(HttpMethod.Get, "/api/finance/overview"))).StatusCode);

        // The development-only demo header is never accepted in Production
        var demo = ViaProxy(HttpMethod.Get, "/api/invoices");
        demo.Headers.Add("X-Demo-User", "demo-maija");
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(demo)).StatusCode);

        // A token signed with another key is refused
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ProxyFactory.Issuer, Audience = ProxyFactory.Audience, Subject = new ClaimsIdentity([new Claim("sub", "x")]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-different-key-0123456789abcdefgh")), SecurityAlgorithms.HmacSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.SendAsync(ViaProxy(HttpMethod.Get, "/api/finance/overview", forged))).StatusCode);
    }

    [Fact]
    public async Task Plain_http_without_the_proxy_header_is_redirected_to_https()
    {
        using var f = new ProxyFactory(trustAllProxies: true);
        var res = await f.Raw().GetAsync("/api/finance/overview");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
        Assert.Equal("https", res.Headers.Location?.Scheme);
    }

    [Fact]
    public async Task A_proxy_header_from_an_untrusted_source_is_ignored()
    {
        // Default settings trust no outside proxy, so a faked "https" changes nothing
        using var f = new ProxyFactory(trustAllProxies: false);
        var res = await f.Raw().SendAsync(ViaProxy(HttpMethod.Get, "/api/finance/overview", ProxyFactory.Token("user-123")));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
    }

    [Fact]
    public async Task Health_checks_over_plain_http_are_not_redirected()
    {
        using var f = new ProxyFactory(trustAllProxies: true);
        var res = await f.Raw().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"ok\"", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browser_preflight_through_the_proxy_allows_only_the_website()
    {
        using var f = new ProxyFactory(trustAllProxies: true);
        var raw = f.Raw();

        HttpRequestMessage Preflight(string origin)
        {
            var r = ViaProxy(HttpMethod.Options, "/api/finance/overview");
            r.Headers.Add("Origin", origin);
            r.Headers.Add("Access-Control-Request-Method", "GET");
            r.Headers.Add("Access-Control-Request-Headers", "authorization");
            return r;
        }

        var allowed = await raw.SendAsync(Preflight(ProxyFactory.WebsiteOrigin));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(ProxyFactory.WebsiteOrigin, allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var other = await raw.SendAsync(Preflight("https://evil.example"));
        Assert.NotEqual(HttpStatusCode.TemporaryRedirect, other.StatusCode);
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Https_responses_through_the_proxy_carry_hsts()
    {
        using var f = new ProxyFactory(trustAllProxies: true);
        var r = ViaProxy(HttpMethod.Get, "/health");
        r.Headers.Host = "api.omabilo.example";   // HSTS is never sent for localhost

        var res = await f.Raw().SendAsync(r);
        Assert.True(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public void A_malformed_trusted_network_stops_startup()
    {
        using var f = new ProxyFactory(trustAllProxies: false, knownNetworks: ["not-a-network"]);
        var ex = Assert.ThrowsAny<Exception>(() => f.Raw());
        Assert.Contains("Proxy:KnownNetworks", ex.ToString());
    }

    [Fact]
    public async Task A_proxy_inside_a_listed_trusted_network_is_believed()
    {
        using var f = new ProxyFactory(trustAllProxies: false, knownNetworks: ["10.0.0.0/8"]);
        var res = await f.Raw().SendAsync(ViaProxy(HttpMethod.Get, "/api/finance/overview", ProxyFactory.Token("user-123")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task A_proxy_outside_the_listed_networks_is_not_believed()
    {
        using var f = new ProxyFactory(trustAllProxies: false, knownNetworks: ["172.16.0.0/12"]);
        var res = await f.Raw().SendAsync(ViaProxy(HttpMethod.Get, "/api/finance/overview", ProxyFactory.Token("user-123")));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, res.StatusCode);
    }
}
