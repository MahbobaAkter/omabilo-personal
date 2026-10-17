using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Omabilo.Api.Auth;

/// <summary>
/// DEVELOPMENT-ONLY sign-in: the caller names a fictional user in the X-Demo-User header
/// (for example "demo-alice"). There is no password because these users are not real.
/// Program.cs registers this only when the environment is Development AND Auth:Mode is
/// "Demo"; any other environment must use a real identity provider (JWT bearer).
/// </summary>
public sealed partial class DemoUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DemoUser";
    public const string HeaderName = "X-Demo-User";

    // Only obviously fictional ids, so this can never be mistaken for a real account
    [GeneratedRegex("^demo-[a-z0-9-]{1,40}$")]
    private static partial Regex DemoId();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var id = values.ToString().Trim();

        if (!DemoId().IsMatch(id))
        {
            return Task.FromResult(AuthenticateResult.Fail("Demo user ids look like demo-alice."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id), new Claim("demo", "true")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class CurrentUser
{
    /// <summary>The signed-in owner id, taken from the validated identity on the server,
    /// never from the request body or the URL.</summary>
    public static string OwnerId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")
        ?? throw new InvalidOperationException("Authenticated user has no subject id.");
}
