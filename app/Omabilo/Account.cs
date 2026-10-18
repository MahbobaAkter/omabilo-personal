// Who is using Omabilo right now. Anyone can sign in with any account the platform offers (Google,
// Apple, Microsoft, Signicat, an email code or a passkey) or look around as a guest. Every screen
// shows which of the two you are, with Sign in or Sign out one press away. Data is always keyed by
// the platform's user id; this file only reads the profile to name the person.

public partial class OmabiloApp
{
    private ClientProfiles Profiles { get; } = new(app);

    /// <summary>The person behind this tab: a guest, or a signed-in account with a display name.</summary>
    private sealed record Me(bool SignedIn, string Name, string? Email)
    {
        public string Initials => SignedIn
            ? string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])))
            : "G";
    }

    private readonly ClientReactive<Me> _me = new(new Me(false, "Guest", null));
    private readonly ClientReactive<bool> _signingIn = new(false);
    private readonly ClientReactive<bool> _accountOpen = new(false);

    /// <summary>Reads the profile once per tab; a failed lookup still leaves a usable signed-in state.</summary>
    private async Task LoadMeAsync(Context ctx)
    {
        if (ctx.IsAnonymous)
        {
            _me.Value = new Me(false, "Guest", null);
            return;
        }

        string name = "Signed in";
        string? email = null;

        try
        {
            if (await Profiles.GetProfileAsync(ctx) is { } profile)
            {
                email = profile.Email;
                name = profile.VisibleName is { Length: > 0 } visible ? visible
                    : profile.Name is { Length: > 0 } full ? full
                    : email is { Length: > 0 } ? email.Split('@')[0]
                    : name;
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"profile lookup skipped: {ex.GetType().Name}");
        }

        _me.Value = new Me(true, name, email);
    }

    private async Task SignInAsync(string reason)
    {
        if (_signingIn.Value)
        {
            return;
        }

        _signingIn.Value = true;

        try
        {
            // A successful sign-in reconnects the page as the new account, so this tab ends here
            if (!await ClientFunctions.LoginShowAsync(reason))
            {
                _toasts.Info("Sign-in wasn't completed. You can keep using Omabilo as a guest.");
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"sign-in screen failed: {ex.GetType().Name}");
            _toasts.Error("The sign-in screen didn't open. Please reload the page and try again.");
        }
        finally
        {
            _signingIn.Value = false;
        }
    }

    private async Task SignOutAsync()
    {
        _accountOpen.Value = false;

        try
        {
            if (!await ClientFunctions.LogoutAsync())
            {
                _toasts.Error("Signing out didn't work. Please reload the page and try again.");
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"sign-out failed: {ex.GetType().Name}");
            _toasts.Error("Signing out didn't work. Please reload the page and try again.");
        }
    }

    private void Avatar(UIView view, Me me, string size = "w-10 h-10")
    {
        view.Box([$"{size} rounded-full flex items-center justify-center shrink-0 text-sm font-semibold",
            me.SignedIn ? "bg-brand-solid text-primary-on-brand" : "bg-muted text-muted-foreground"], content: b =>
        {
            if (me.SignedIn)
            {
                b.Text([], me.Initials);
            }
            else
            {
                b.Icon([Icon.Sm], name: "user-round");
            }
        });
    }

    // The sidebar's foot: who you are, and the one action that changes it
    private void RenderAccountPanel(UIView view)
    {
        var me = _me.Value;

        view.Box([Card.Default, "p-3 flex flex-col gap-3"], content: card =>
        {
            card.Row(["items-center gap-3 min-w-0"], content: r =>
            {
                Avatar(r, me);
                r.Column(["gap-0 min-w-0 flex-1"], content: c =>
                {
                    c.Text([Text.Label, "truncate text-foreground"], me.SignedIn ? me.Name : "Guest");
                    c.Text([Text.Caption, "truncate text-muted-foreground"], me.SignedIn ? me.Email ?? "Signed in" : "Not signed in");
                });
            });

            if (me.SignedIn)
            {
                card.Row(["gap-2"], content: r =>
                {
                    r.Button([Button.OutlineSm, "flex-1"], text: "Account", icon: "settings", onClick: () => Go("my-settings"));
                    r.Button([Button.GhostSm, "flex-1"], text: "Sign out", icon: "log-out", onClick: SignOutAsync);
                });
            }
            else
            {
                card.Text([Text.Caption, "text-muted-foreground"], "As a guest, your demo changes stay in this browser only. Sign in to keep your own money data on any device.");
                card.Button([Button.PrimarySm, "w-full"], text: _signingIn.Value ? "Opening…" : "Sign in", icon: "log-in", disabled: _signingIn.Value,
                    onClick: async () => await SignInAsync("Sign in to keep your own Omabilo data"));
            }
        });
    }

    // Phones: a compact account button in the top bar, with a sheet for the details
    private void RenderAccountButton(UIView view)
    {
        var me = _me.Value;

        if (!me.SignedIn)
        {
            view.Button([Button.PrimarySm, "md:hidden h-10"], text: _signingIn.Value ? "Opening…" : "Sign in", disabled: _signingIn.Value,
                onClick: async () => await SignInAsync("Sign in to keep your own Omabilo data"));
            return;
        }

        view.Button(["md:hidden h-10 w-10 rounded-full flex items-center justify-center"], ariaLabel: $"Account: {me.Name}",
            onClick: () => _accountOpen.Value = true,
            content: v => Avatar(v, me));
    }

    private void RenderAccountDialog(UIView view)
    {
        var me = _me.Value;

        view.Dialog(open: _accountOpen.Value && me.SignedIn, onOpenChange: async open => _accountOpen.Value = open,
            title: "Your account",
            content: d => d.Column(["gap-4"], content: col =>
            {
                col.Row(["items-center gap-3 min-w-0"], content: r =>
                {
                    Avatar(r, me, "w-12 h-12");
                    r.Column(["gap-0 min-w-0"], content: c =>
                    {
                        c.Text([Text.H5, "truncate text-foreground"], me.Name);

                        if (me.Email is { } email)
                        {
                            c.Text([Text.BodySm, "truncate text-muted-foreground"], email);
                        }
                    });
                });
                col.Text([Text.BodySm, "text-muted-foreground"], "Your records belong to this account only. No one else who signs in can see them.");
                col.Row(["gap-2 flex-wrap"], content: r =>
                {
                    r.Button([Button.OutlineMd], text: "Connections & privacy", icon: "shield-check", onClick: () =>
                    {
                        _accountOpen.Value = false;
                        Go("my-settings");
                    });
                    r.Button([Button.GhostMd], text: "Sign out", icon: "log-out", onClick: SignOutAsync);
                });
            }));
    }

    // Shown on every screen while an account is scheduled for deletion, so it can't go unnoticed
    private void RenderRemovalBanner(UIView view)
    {
        if (_removalDate.Value is not { } date)
        {
            return;
        }

        view.Alert("Your account is scheduled for deletion", tone: SemanticTone.Warning,
            description: $"Your account and all its Omabilo data will be deleted on {date:d MMMM yyyy}. You can change your mind until then.",
            action: a => a.Button([Button.OutlineSm], text: "Keep my account", onClick: CancelRemovalAsync));
    }

    private async Task CancelRemovalAsync()
    {
        try
        {
            if (await ClientFunctions.CancelAccountRemovalAsync() is { Succeeded: true })
            {
                _removalDate.Value = null;
                _toasts.Success("Your account will be kept.");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"cancel removal failed: {ex.GetType().Name}");
        }

        _toasts.Error("That didn't work. Please try again.");
    }

    private string Greeting() => $"Welcome, {_me.Value.Name.Split(' ')[0]}";
}
