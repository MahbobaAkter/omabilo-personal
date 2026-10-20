// Free and Premium. Premium is a €2.99 monthly subscription paid on the payment provider's own page
// (Mollie, through IKON payments): Omabilo never sees card or bank details, and IKON keeps the payment
// state. The app owner's test accounts can be given Premium without paying by listing their emails in
// the secret OMABILO_PREMIUM_EMAILS. Until payments go live, user testers unlock Premium with an access
// code (Membership.DefaultAccessCode, or the secret OMABILO_ACCESS_CODE); nothing is charged and they can
// return to the free plan. What each plan holds is in Companion/Membership.cs.

public partial class OmabiloApp
{
    private enum PaymentsSetup { Unknown, Ready, NotSetUp }

    private readonly Reactive<PaymentsSetup> _paymentsSetup = new(PaymentsSetup.Unknown);
    private readonly ClientReactive<PaymentSubscription?> _premiumSub = new((PaymentSubscription?)null);
    private readonly ClientReactive<bool> _premiumBusy = new(false);
    private readonly ClientReactive<bool> _confirmCancel = new(false);
    private int _paymentsChecking;
    private readonly ClientReactive<string> _accessCode = new("");
    private readonly ClientReactive<string?> _accessCodeError = new((string?)null);
    private readonly ClientReactive<bool> _confirmLeaveCode = new(false);

    // Wrong access codes per user: after a few, that person waits before trying again
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Tries, DateTime Until)> AccessCodeTries = new();

    private bool HasAccessCodePremium() => _my.Value is { } s && Membership.HasAccessCode(s.Settings);

    private bool IsComplimentaryPremium() => Membership.IsComplimentary(_me.Value.Email, SecretOrNull("OMABILO_PREMIUM_EMAILS"));

    /// <summary>True for a signed-in person with an active Premium subscription (or complimentary Premium).</summary>
    private bool IsPremium()
    {
        if (MyUserId == null)
        {
            return false;
        }

        if (IsComplimentaryPremium() || HasAccessCodePremium())
        {
            return true;
        }

        try
        {
            return app.Payments.IsEntitled(Membership.PremiumOfferId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Gmail can be used now: on Premium, or on the free plan before or during the 30-day trial.</summary>
    private bool GmailAvailable() =>
        IsPremium() || (_my.Value is { } s && Membership.GmailFreeNow(s.Settings, s.Connection, DateTime.UtcNow));

    /// <summary>Free plan with Gmail: how long the trial lasts, or that it ended. Nothing on Premium.</summary>
    private void GmailTrialNote(UIView view, CompanionSnapshot snap)
    {
        if (IsPremium() || Membership.GmailTrialEnds(snap.Settings, snap.Connection) is not { } end)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (now < end)
        {
            var days = Math.Max(1, (int)Math.Ceiling((end - now).TotalDays));
            view.Alert($"Gmail is free until {Day(end)}", tone: SemanticTone.Info, key: "gmail-trial",
                description: $"{days} day{(days == 1 ? "" : "s")} left of your free Gmail trial. After that, Gmail syncing is part of Premium ({Membership.PremiumPrice}). Everything Omabilo found stays yours either way.");
            return;
        }

        view.Box([Card.Default, "p-5 flex flex-col gap-3"], key: "gmail-trial-ended", content: v =>
        {
            v.Alert($"Your free Gmail trial ended on {Day(end)}", tone: SemanticTone.Warning,
                description: "Omabilo has stopped reading your Gmail. The bills it already found stay here. Get Premium to start syncing again, or disconnect Gmail in Plan & privacy.");
            UpgradeButton(v);
        });
    }

    private void SubscribePaymentEvents()
    {
        try
        {
            // Entitlements refresh by themselves; the event is only logged (type and id, never amounts or people)
            app.Payments.PaymentEventReceived += async evt =>
            {
                Log.Instance.Info($"payment event {evt.Type?.ToString() ?? "unknown"} {evt.EventId}");
                await Task.CompletedTask;
            };
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"payment events not available: {ex.GetType().Name}");
        }
    }

    /// <summary>Premium can be bought only once a payment provider is enabled and the "premium" offer exists.</summary>
    private async Task CheckPaymentsAsync()
    {
        if (_paymentsSetup.Value == PaymentsSetup.Ready || Interlocked.Exchange(ref _paymentsChecking, 1) == 1)
        {
            return;
        }

        try
        {
            var offers = await app.Payments.ListOffersAsync();
            _paymentsSetup.Value = offers.Any(o => o.OfferId == Membership.PremiumOfferId) ? PaymentsSetup.Ready : PaymentsSetup.NotSetUp;
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"payments check: {ex.GetType().Name}");
            _paymentsSetup.Value = PaymentsSetup.NotSetUp;
        }
        finally
        {
            Interlocked.Exchange(ref _paymentsChecking, 0);
        }
    }

    private async Task LoadPremiumAsync()
    {
        await CheckPaymentsAsync();

        if (MyUserId == null || _paymentsSetup.Value != PaymentsSetup.Ready)
        {
            _premiumSub.Value = null;
            return;
        }

        try
        {
            var subs = await app.Payments.ListSubscriptionsAsync();
            _premiumSub.Value = subs
                .Where(s => s.OfferId == Membership.PremiumOfferId && s.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)
                .OrderByDescending(s => s.CurrentPeriodEnd)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"subscription lookup skipped: {ex.GetType().Name}");
        }
    }

    private async Task StartPremiumAsync()
    {
        if (MyUserId == null)
        {
            await SignInAsync("Sign in to start Omabilo Premium");
            return;
        }

        if (_premiumBusy.Value || _paymentsSetup.Value != PaymentsSetup.Ready)
        {
            return;
        }

        _premiumBusy.Value = true;

        try
        {
            // The provider's page takes the payment details; it returns here afterwards
            var link = await app.Payments.CreatePaymentLinkAsync(Membership.PremiumOfferId, email: _me.Value.Email);

            if (!await ClientFunctions.NavigateToAsync(link.Url))
            {
                _toasts.Error("Your browser didn't open the payment page. Please try again.");
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"premium checkout failed: {ex.GetType().Name}");
            _toasts.Error("The payment page couldn't be opened just now. Nothing was charged. Please try again.");
        }
        finally
        {
            _premiumBusy.Value = false;
        }
    }

    private async Task ChangePremiumAsync(bool cancel)
    {
        if (_premiumSub.Value is not { } sub || _premiumBusy.Value)
        {
            return;
        }

        _confirmCancel.Value = false;
        _premiumBusy.Value = true;

        try
        {
            if (cancel)
            {
                await app.Payments.CancelSubscriptionAsync(sub.Id, idempotencyKey: $"cancel-{sub.Id}");
            }
            else
            {
                await app.Payments.ResumeSubscriptionAsync(sub.Id);
            }

            await LoadPremiumAsync();
            _toasts.Success(cancel
                ? $"Premium is cancelled. You keep it through {PaidThrough(sub)}, and you won't be charged again."
                : "Premium continues. Nothing changes in your billing date.");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"premium change failed: {ex.GetType().Name}");
            _toasts.Error("That didn't work. Your subscription is unchanged. Please try again.");
        }
        finally
        {
            _premiumBusy.Value = false;
        }
    }

    /// <summary>Deleting the account ends a paid subscription at once, so the card is never charged again.
    /// False when there is one and it couldn't be stopped.</summary>
    private async Task<bool> EndPremiumForDeletionAsync()
    {
        await LoadPremiumAsync();

        if (_premiumSub.Value is not { } sub)
        {
            return true;
        }

        try
        {
            await app.Payments.CancelSubscriptionAsync(sub.Id, immediate: true, idempotencyKey: $"delete-{sub.Id}");
            _premiumSub.Value = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"premium end for deletion failed: {ex.GetType().Name}");
            return false;
        }
    }

    private static string PaidThrough(PaymentSubscription sub) =>
        sub.CurrentPeriodEnd is { } end ? Day(Membership.LastPaidDay(end.UtcDateTime)) : "the end of the month you paid for";

    private static string RenewsOn(PaymentSubscription sub) =>
        sub.CurrentPeriodEnd is { } end ? Day(end.UtcDateTime) : "the start of the next month";

    private async Task RedeemAccessCodeAsync()
    {
        if (MyUserId is not { } uid)
        {
            await SignInAsync("Sign in to use your access code");
            return;
        }

        if (_premiumBusy.Value)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (AccessCodeTries.TryGetValue(uid, out var t) && t.Tries >= Membership.AccessCodeMaxTries && now < t.Until)
        {
            _accessCodeError.Value = $"Too many wrong codes. Please try again in {Math.Max(1, (int)Math.Ceiling((t.Until - now).TotalMinutes))} minutes.";
            return;
        }

        if (!Membership.IsAccessCode(_accessCode.Value, SecretOrNull("OMABILO_ACCESS_CODE")))
        {
            var tries = (t.Until > now ? t.Tries : 0) + 1;
            AccessCodeTries[uid] = (tries, now.Add(Membership.AccessCodeLockout));
            Log.Instance.Info($"access code rejected for {uid} ({tries})");
            _accessCodeError.Value = _accessCode.Value.Trim().Length == 0 ? "Enter your access code." : "That code isn't right. Check it and try again.";
            return;
        }

        _premiumBusy.Value = true;

        try
        {
            await Companion.SetAccessCodeAsync(uid, true, CancellationToken.None);
            AccessCodeTries.TryRemove(uid, out _);
            Log.Instance.Info($"access code redeemed for {uid}");
            _accessCode.Value = "";
            _accessCodeError.Value = null;
            await LoadMyAsync();
            _toasts.Success("Premium is unlocked. Nothing is charged while Omabilo is in user testing.");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"access code redeem failed: {ex.GetType().Name}");
            _accessCodeError.Value = "That didn't save just now. Please try again.";
        }
        finally
        {
            _premiumBusy.Value = false;
        }
    }

    private async Task LeaveAccessCodeAsync()
    {
        _confirmLeaveCode.Value = false;

        if (MyUserId is not { } uid || _premiumBusy.Value)
        {
            return;
        }

        _premiumBusy.Value = true;

        try
        {
            await Companion.SetAccessCodeAsync(uid, false, CancellationToken.None);
            Log.Instance.Info($"access code removed for {uid}");
            await LoadMyAsync();
            _toasts.Success("You're on the free plan. Your bills and records stay, and you can enter the code again anytime.");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"access code remove failed: {ex.GetType().Name}");
            _toasts.Error("That didn't work. Your plan is unchanged. Please try again.");
        }
        finally
        {
            _premiumBusy.Value = false;
        }
    }

    // ── Pieces of UI ────────────────────────────────────────────────────────

    private void PlanBadge(UIView view)
    {
        if (MyUserId == null)
        {
            return;
        }

        view.Badge(IsPremium() ? "Premium" : "Free plan", tone: IsPremium() ? SemanticTone.Success : SemanticTone.Neutral, size: BadgeSize.Sm);
    }

    private void UpgradeButton(UIView view, bool large = false)
    {
        var setup = _paymentsSetup.Value;

        view.Column(["gap-1.5 items-start"], content: col =>
        {
            col.Button([large ? Button.PrimaryLg : Button.PrimaryMd], text: _premiumBusy.Value ? "Opening…" : $"Get Premium · {Membership.PremiumPrice}", icon: "sparkles",
                disabled: setup != PaymentsSetup.Ready || _premiumBusy.Value, onClick: StartPremiumAsync);

            if (setup != PaymentsSetup.Ready)
            {
                col.Text([Text.Caption, "text-muted-foreground"], setup == PaymentsSetup.Unknown
                    ? "Checking whether Premium can be bought…"
                    : "Payments open later. During user testing, use your access code below instead: nothing is charged.");
            }
            else
            {
                col.Text([Text.Caption, "text-muted-foreground"], "Paid securely on Mollie's page. Renews automatically each month until you cancel. After cancelling you keep Premium until the month you paid for ends.");
            }

            AccessCodeForm(col);
        });
    }

    /// <summary>User testing: an access code unlocks Premium without paying.</summary>
    private void AccessCodeForm(UIView view)
    {
        view.Column(["gap-2 w-full max-w-sm pt-2"], content: form =>
        {
            form.Text([Text.BodySm, "text-foreground"], "Testing Omabilo? Enter your access code to use Premium free.");

            if (MyUserId == null)
            {
                form.Button([Button.OutlineMd, "self-start"], text: "Sign in to use an access code", icon: "key-round",
                    onClick: async () => await SignInAsync("Sign in to use your access code"));
                return;
            }

            form.Row(["gap-2 items-start w-full"], content: row =>
            {
                row.Box(["flex-1 min-w-0"], content: b => b.TextField(bind: _accessCode, placeholder: "Access code", ariaLabel: "Access code",
                    onSubmit: async _ => await RedeemAccessCodeAsync()));
                row.Button([Button.SecondaryMd, "shrink-0"], text: _premiumBusy.Value ? "Checking…" : "Unlock", icon: "key-round",
                    disabled: _premiumBusy.Value, onClick: RedeemAccessCodeAsync);
            });

            if (_accessCodeError.Value is { } error)
            {
                form.Text([Text.Caption, "text-error-700"], error);
            }
        });
    }

    private static void FeatureList(UIView view, IEnumerable<string> lines, string icon = "check")
    {
        view.Column(["gap-2"], content: list =>
        {
            foreach (var line in lines)
            {
                list.Row(["items-start gap-2"], content: row =>
                {
                    row.Icon([Icon.Sm, "text-brand-secondary mt-0.5 shrink-0"], name: icon);
                    row.Text([Text.BodySm, "text-foreground/90"], line);
                });
            }
        });
    }

    /// <summary>Shown where a Premium feature would be: what it does, and how to get it.</summary>
    private void PremiumLock(UIView view, PremiumFeature feature, string why)
    {
        view.Box([Card.Elevated, "p-6 sm:p-8 flex flex-col gap-4"], key: $"lock-{feature}", content: v =>
        {
            v.Row(["items-center gap-3"], content: r =>
            {
                IconTile(r, "sparkles", "brand", "w-12 h-12");
                r.Column(["gap-0.5 min-w-0"], content: c =>
                {
                    c.Text([Text.Overline, "text-brand-secondary"], "Premium");
                    c.Heading(Membership.Describe(feature), style: [Text.H5, "m-0 text-foreground"], level: 2);
                });
            });
            v.Text([Text.Body, "text-foreground/90"], why);
            UpgradeButton(v);
        });
    }

    /// <summary>Home, free plan: one quiet card saying what Premium adds.</summary>
    private void PremiumTeaser(UIView view)
    {
        view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], key: "premium-teaser", content: v =>
        {
            v.Row(["items-center gap-3"], content: r =>
            {
                IconTile(r, "sparkles", "brand");
                r.Column(["gap-0 min-w-0"], content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground"], "Let Omabilo find your bills for you");
                    c.Text([Text.Caption, "text-muted-foreground"], $"Premium · {Membership.PremiumPrice}");
                });
            });
            FeatureList(v, Membership.PremiumAdds.Select(Membership.Describe), "plus");
            UpgradeButton(v);
        });
    }

    /// <summary>Plan & privacy: the current plan, with upgrade or cancel.</summary>
    private void RenderPlanCard(UIView view)
    {
        var premium = IsPremium();
        var sub = _premiumSub.Value;

        view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-4"], key: "plan-card", content: v =>
        {
            v.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
            {
                r.Column(["gap-0.5"], content: c =>
                {
                    c.Text([Text.Overline, "text-muted-foreground"], "Your plan");
                    c.Heading(premium ? "Premium" : "Free", style: [Text.H4, "m-0 text-foreground"], level: 2);
                });
            });

            if (!premium)
            {
                v.Text([Text.BodySm, "text-muted-foreground"], "Included now:");
                FeatureList(v, Membership.FreeIncludes);
                v.Text([Text.BodySm, "text-muted-foreground"], $"Premium adds, for {Membership.PremiumPrice}:");
                FeatureList(v, Membership.PremiumAdds.Select(Membership.Describe), "plus");
                UpgradeButton(v);
                return;
            }

            FeatureList(v, Membership.PremiumAdds.Select(Membership.Describe));

            if (sub == null && !IsComplimentaryPremium() && HasAccessCodePremium())
            {
                v.Text([Text.BodySm, "text-foreground/90"], "Unlocked with your user-testing access code. Nothing is charged, and no payment details are needed.");
                v.Button([Button.OutlineMd, "self-start"], text: _premiumBusy.Value ? "Working…" : "Return to the free plan", disabled: _premiumBusy.Value,
                    onClick: () => _confirmLeaveCode.Value = true);
                v.AlertDialog(open: _confirmLeaveCode.Value, onOpenChange: async open => _confirmLeaveCode.Value = open,
                    title: "Return to the free plan?",
                    description: "Premium features lock again. Your bills and records stay, and you can enter the access code again anytime to unlock Premium.",
                    content: d => d.Row(["gap-2 justify-end flex-wrap"], content: r =>
                    {
                        r.Button([Button.OutlineMd], text: "Keep Premium", onClick: () => _confirmLeaveCode.Value = false);
                        r.Button([Button.PrimaryMd], text: "Return to free plan", onClick: LeaveAccessCodeAsync);
                    }));
                return;
            }

            if (sub == null)
            {
                v.Text([Text.Caption, "text-muted-foreground"], IsComplimentaryPremium()
                    ? "Premium is included for this account at no charge."
                    : "Your Premium subscription is active.");
                return;
            }

            v.Text([Text.BodySm, "text-foreground/90"], sub.CancelAtPeriodEnd
                ? $"Cancelled: you keep Premium through {PaidThrough(sub)}, and you won't be charged again."
                : sub.Status == SubscriptionStatus.PastDue
                    ? "The last payment didn't go through. Mollie will try again; check your payment method there."
                    : $"{Membership.PremiumPrice}. Paid through {PaidThrough(sub)}. It renews automatically on {RenewsOn(sub)}, when your card is charged again, unless you cancel before then.");

            if (sub.CancelAtPeriodEnd)
            {
                v.Button([Button.OutlineMd, "self-start"], text: _premiumBusy.Value ? "Working…" : "Keep Premium", disabled: _premiumBusy.Value,
                    onClick: async () => await ChangePremiumAsync(cancel: false));
            }
            else
            {
                v.Button([Button.OutlineErrorMd, "self-start"], text: "Cancel Premium", disabled: _premiumBusy.Value, onClick: () => _confirmCancel.Value = true);
            }
        });

        view.AlertDialog(open: _confirmCancel.Value, onOpenChange: async open => _confirmCancel.Value = open,
            title: "Cancel Premium?",
            description: $"You won't be charged again. You keep Premium through {(sub != null ? PaidThrough(sub) : "the end of the month you paid for")}, then your account moves to the free plan. Your bills and records stay. Gmail stops syncing unless you're still in your free {Membership.GmailTrialDays} days.",
            content: d => d.Row(["gap-2 justify-end flex-wrap"], content: r =>
            {
                r.Button([Button.OutlineMd], text: "Keep Premium", onClick: () => _confirmCancel.Value = false);
                r.Button([Button.ErrorMd], text: "Cancel Premium", onClick: async () => await ChangePremiumAsync(cancel: true));
            }));
    }
}
