// Screens for a signed-in person's own money: My money (overview, attention, cash flow), My inbox
// (records from Gmail with their sources, corrections and statuses) and Plan & privacy.
// Data is loaded in handlers, never while rendering. Every amount shows where it came from.

using Ikon.Common.Core.Protocol;

public partial class OmabiloApp
{
    private readonly ClientReactive<CompanionSnapshot?> _my = new((CompanionSnapshot?)null);
    private readonly ClientReactive<bool> _myLoading = new(false);
    private readonly ClientReactive<string?> _myError = new((string?)null);
    private readonly ClientReactive<string?> _myNotice = new((string?)null);
    private readonly ClientReactive<bool> _mySyncing = new(false);
    private readonly ClientReactive<bool> _awaitingGmail = new(false);
    private readonly ClientReactive<string?> _connectUrl = new((string?)null);
    private readonly ClientReactive<string> _myFilter = new("attention");
    private readonly ClientReactive<string> _mySearch = new("");
    private readonly ClientReactive<string?> _myOpen = new((string?)null);
    private readonly ClientReactive<bool> _myEditing = new(false);
    private readonly ClientReactive<string?> _myConfirm = new((string?)null);
    private readonly ClientReactive<bool> _myBusy = new(false);

    private readonly ClientReactive<string> _eCounterparty = new("");
    private readonly ClientReactive<string> _eAmount = new("");
    private readonly ClientReactive<string> _eDue = new("");
    private readonly ClientReactive<string> _eDate = new("");
    private readonly ClientReactive<string> _eCategory = new(Categories.Other);
    private readonly ClientReactive<string> _eKind = new(nameof(FinKind.Invoice));
    private readonly ClientReactive<string> _eReference = new("");
    private readonly ClientReactive<string> _ePurpose = new("");
    private readonly ClientReactive<string> _eNet = new("");
    private readonly ClientReactive<string> _eNote = new("");
    private readonly ClientReactive<IReadOnlyList<string>> _eErrors = new(Array.Empty<string>());

    private readonly ClientReactive<string> _sBalance = new("");
    private readonly ClientReactive<string> _sPayday = new("");
    private readonly ClientReactive<string> _sNet = new("");
    private readonly ClientReactive<string> _sEveryday = new("");
    private readonly ClientReactive<string> _sScanDays = new("90");
    private readonly ClientReactive<bool> _sAi = new(false);
    private readonly ClientReactive<string?> _sMessage = new((string?)null);
    private readonly ClientReactive<DateTimeOffset?> _removalDate = new((DateTimeOffset?)null);
    private readonly ClientReactive<string?> _forwardDomain = new((string?)null);
    private readonly ClientReactive<bool> _forwardHelp = new(false);

    /// <summary>The signed-in person's id, or null for a guest. Everything personal hangs off this.</summary>
    private string? MyUserId => app.CurrentClientContext is { IsAnonymous: false } ? ReactiveScope.UserIdOrNull : null;

    private static string Money(decimal v) => InvoiceExplainer.Money(v);

    /// <summary>How new mail reaches Omabilo for this person right now, in plain words.</summary>
    private static string SyncCadence(CompanionSnapshot snap) => snap.PushActive(DateTime.UtcNow)
        ? "new mail is processed as it arrives (Gmail push), with an hourly check as backup"
        : "new mail is checked automatically every 5 minutes";

    private static string Day(DateTime d) => InvoiceExplainer.Day(d);

    private static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - utc;
        return s.TotalMinutes < 1 ? "just now" : s.TotalHours < 1 ? $"{(int)s.TotalMinutes} min ago" : s.TotalDays < 1 ? $"{(int)s.TotalHours} h ago" : $"{(int)s.TotalDays} days ago";
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    private async Task OnCompanionClientJoinedAsync(Context ctx)
    {
        if (ctx.IsSnapshot)
        {
            return;
        }

        await LoadMeAsync(ctx);

        if (ctx.IsAnonymous)
        {
            return;
        }

        _section.Value = AppSections.ForPath(app.Navigation.CurrentPath).Key;
        await LoadMyAsync();
        await LoadPremiumAsync();

        // Back from Google's consent page while the first scan runs: keep the status fresh
        if (_my.Value?.Connections.Any(FirstScanRunning) == true)
        {
            _awaitingGmail.Value = true;
            WatchConnection();
        }

        try
        {
            if (app.GlobalState.ServerRunType != ServerRunType.Local)
            {
                var removal = await ClientFunctions.GetAccountRemovalAsync();

                // "Delete now" already erased everything Omabilo kept. Signing in again starts a fresh,
                // empty account, so the earlier request is withdrawn rather than held over it: nothing
                // waits, and nothing new gets erased later by surprise
                if (removal?.ScheduledFor != null && _my.Value is { Items.Count: 0, Connection: null } && _plans.Value is null or { Debts.Count: 0, Goals.Count: 0 }
                    && await ClientFunctions.CancelAccountRemovalAsync() is { Succeeded: true })
                {
                    Log.Instance.Info($"earlier account removal withdrawn on a fresh sign-in for user {MyUserId}");
                    removal = null;
                }

                _removalDate.Value = removal?.ScheduledFor;
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"account removal lookup skipped: {ex.GetType().Name}");
        }
    }

    private async Task LoadMyAsync()
    {
        if (MyUserId is not { } uid)
        {
            _my.Value = null;
            _plans.Value = null;
            return;
        }

        _myLoading.Value = true;
        _myError.Value = null;

        try
        {
            var snapshot = await Companion.LoadAsync(uid, CancellationToken.None);
            _my.Value = snapshot;
            await LoadPlansAsync(uid);
            _forwardDomain.Value = await InboundDomainAsync();
            FillSettingsForm(snapshot.Settings);
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"companion load failed: {ex.GetType().Name}");
            _myError.Value = "Omabilo couldn't load your data just now. Check your connection and try again.";
        }
        finally
        {
            _myLoading.Value = false;
        }
    }

    private void FillSettingsForm(CompanionSettings s)
    {
        _sBalance.Value = s.Balance?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        _sPayday.Value = s.PaydayDay?.ToString(CultureInfo.InvariantCulture) ?? "";
        _sNet.Value = s.NetSalary?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        _sEveryday.Value = s.EverydaySpendPerDay?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        _sScanDays.Value = s.ScanDays.ToString(CultureInfo.InvariantCulture);
        _sAi.Value = s.AiEnabled;
    }

    /// <summary>Polls the database for this client while Gmail is being connected or first scanned.</summary>
    private void WatchConnection()
    {
        var clientId = ReactiveScope.ClientId;
        var uid = MyUserId;

        if (uid == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 90; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));

                try
                {
                    var snapshot = await Companion.LoadAsync(uid, CancellationToken.None);
                    _my.SetFor(clientId, snapshot);

                    if (snapshot.Connections.Count > 0 && !snapshot.Connections.Any(FirstScanRunning))
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    // The client left, or the database is briefly unreachable; stop quietly
                    break;
                }
            }

            try
            {
                _awaitingGmail.SetFor(clientId, false);
            }
            catch (Exception)
            {
            }
        });
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private async Task SignInAsync() => await SignInAsync("Sign in to use Omabilo with your own email");

    private async Task ConnectGmailAsync()
    {
        if (MyUserId is not { } uid)
        {
            await SignInAsync();
            return;
        }

        if (!GmailAvailable())
        {
            Go("my-settings");
            _toasts.Info($"Your free Gmail trial has ended. Gmail is part of Premium ({Membership.PremiumPrice}).");
            return;
        }

        try
        {
            var url = await Engine.BeginConnectAsync(uid, CancellationToken.None);
            _connectUrl.Value = url;
            _awaitingGmail.Value = true;

            // Same-tab navigation is never blocked as a pop-up; Google sends the person back here
            if (!await ClientFunctions.NavigateToAsync(url))
            {
                _myNotice.Value = "Your browser didn't open Google's page. Use the link below to continue.";
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"gmail connect start failed: {ex.GetType().Name}");
            _awaitingGmail.Value = false;
            _myError.Value = "Omabilo couldn't start the Gmail connection. Please try again.";
        }
    }

    private static bool FirstScanRunning(GmailConnection c) => c is { InitialScanDone: false, Status: not ConnectionStatus.NeedsReconnect };

    /// <summary>The account the Gmail bar reports on: one that needs the person first, else the one
    /// still on its first scan, else the first account.</summary>
    private static GmailConnection FocusAccount(CompanionSnapshot snap) =>
        snap.Connections.FirstOrDefault(c => c.Status == ConnectionStatus.NeedsReconnect)
        ?? snap.Connections.FirstOrDefault(c => c.Status == ConnectionStatus.Error)
        ?? snap.Connections.FirstOrDefault(FirstScanRunning)
        ?? snap.Connections[0];

    /// <param name="accountId">One account (its own Sync now), or null for all of the person's accounts.</param>
    private void SyncNow(bool rescan = false, string? accountId = null)
    {
        if (MyUserId is not { } uid || _mySyncing.Value)
        {
            return;
        }

        var clientId = ReactiveScope.ClientId;
        _mySyncing.Value = true;
        _myNotice.Value = null;

        _ = Task.Run(async () =>
        {
            await using var hold = await app.BackgroundWork.StartAsync(TimeSpan.FromMinutes(15), "manual Gmail sync");
            string message;

            try
            {
                var result = accountId == null
                    ? await Engine.SyncAsync(uid, CancellationToken.None, rescan)
                    : await Engine.SyncMailboxAsync(new MailboxRef(uid, accountId), CancellationToken.None, rescan);
                message = result.Message;
            }
            catch (Exception ex)
            {
                Log.Instance.Warning($"manual sync failed: {ex.GetType().Name}");
                message = "Sync didn't finish. Please try again in a moment.";
            }

            try
            {
                _my.SetFor(clientId, await Companion.LoadAsync(uid, CancellationToken.None));
                _myNotice.SetFor(clientId, message);
                _mySyncing.SetFor(clientId, false);
            }
            catch (Exception)
            {
                // The person left; the results are saved and show next time
            }
        });
    }

    private async Task RunMyAsync(Func<string, Task<string?>> action)
    {
        if (MyUserId is not { } uid || _myBusy.Value)
        {
            return;
        }

        _myBusy.Value = true;

        try
        {
            var message = await action(uid);
            await LoadMyAsync();

            if (message != null)
            {
                _toasts.Success(message);
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"companion action failed: {ex.GetType().Name}");
            _toasts.Error("That didn't work. Please try again.");
        }
        finally
        {
            _myBusy.Value = false;
        }
    }

    private void OpenItem(FinancialItem item)
    {
        _myOpen.Value = item.Id;
        _myEditing.Value = false;
        _eErrors.Value = Array.Empty<string>();
        _eCounterparty.Value = item.Counterparty ?? "";
        _eAmount.Value = item.Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        _eDue.Value = item.DueDate?.ToString("d.M.yyyy", CultureInfo.InvariantCulture) ?? "";
        _eDate.Value = item.EventDate?.ToString("d.M.yyyy", CultureInfo.InvariantCulture) ?? "";
        _eCategory.Value = item.Category;
        _eKind.Value = item.Kind.ToString();
        _eReference.Value = item.Reference ?? "";
        _ePurpose.Value = item.Purpose ?? "";
        _eNet.Value = item.Net?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        _eNote.Value = item.UserNote ?? "";
    }

    private async Task SaveEditAsync(FinancialItem item)
    {
        var errors = new List<string>();
        decimal? Num(string text, string label)
        {
            if (text.Trim().Length == 0) return null;
            var v = TextInvoiceExtractor.ParseAmount(text);
            if (v == null && text.Trim() != "0") errors.Add($"{label} isn't a number.");
            return v ?? (text.Trim() == "0" ? 0 : null);
        }

        DateTime? When(string text, string label)
        {
            if (text.Trim().Length == 0) return null;
            var d = RecordExtractor.ParseDate(text.Trim());
            if (d == null) errors.Add($"{label} isn't a date like 25.10.2026.");
            return d;
        }

        var edit = new ItemEdit(
            Counterparty: _eCounterparty.Value.Trim() != (item.Counterparty ?? "") ? _eCounterparty.Value : null,
            Amount: Num(_eAmount.Value, "Amount") is { } a && a != item.Amount ? a : null,
            DueDate: When(_eDue.Value, "Due date") is { } d && d != item.DueDate ? d : null,
            EventDate: When(_eDate.Value, "Date") is { } e && e != item.EventDate ? e : null,
            Category: _eCategory.Value != item.Category ? _eCategory.Value : null,
            Reference: _eReference.Value.Trim() != (item.Reference ?? "") ? _eReference.Value : null,
            Purpose: _ePurpose.Value.Trim() != (item.Purpose ?? "") ? _ePurpose.Value : null,
            Net: item.IsIncome && Num(_eNet.Value, "Net salary") is { } n && n != item.Net ? n : null,
            Note: _eNote.Value.Trim() != (item.UserNote ?? "") ? _eNote.Value : null,
            Kind: Enum.TryParse<FinKind>(_eKind.Value, out var kind) && kind != item.Kind ? kind : null);

        if (errors.Count > 0)
        {
            _eErrors.Value = errors;
            return;
        }

        if (MyUserId is not { } uid)
        {
            return;
        }

        var (updated, problems) = await Companion.EditAsync(uid, item.Id, edit, CancellationToken.None);

        if (problems.Count > 0)
        {
            _eErrors.Value = problems;
            return;
        }

        _eErrors.Value = Array.Empty<string>();
        _myEditing.Value = false;
        await LoadMyAsync();
        var cleared = updated!.IsIncome != item.IsIncome && updated.Amount == null
            ? $" Its amount was cleared because it meant something else before. Add the {(updated.IsIncome ? "net salary" : "amount")} with Correct details."
            : " Check its other details with Correct details.";
        var where = updated.Kind != item.Kind
            ? $" It's now a {KindName(updated.Kind).ToLowerInvariant()}, in {(updated.IsIncome ? "your salary records" : updated.Category)}.{cleared}"
            : "";
        _toasts.Success(updated.UserState == UserState.None
            ? $"Correction saved.{where} Press Looks right once everything is correct."
            : $"Correction saved.{where} Omabilo keeps it even when the message is read again.");
    }

    private async Task SaveSettingsAsync()
    {
        if (MyUserId is not { } uid || _my.Value is not { } snap)
        {
            return;
        }

        var problems = new List<string>();
        decimal? Num(string text, string label, decimal max)
        {
            if (text.Trim().Length == 0) return null;
            var v = TextInvoiceExtractor.ParseAmount(text) ?? (text.Trim() == "0" ? 0 : null);
            if (v is null or < 0 || v > max) problems.Add($"{label} must be a number between 0 and {max:0}.");
            return v;
        }

        var balanceText = _sBalance.Value.Trim();
        var negative = balanceText.StartsWith('-');
        var balance = Num(balanceText.TrimStart('-'), "Balance", 1_000_000m) is { } b ? (negative ? -b : b) : (decimal?)null;
        int? payday = null;

        if (_sPayday.Value.Trim().Length > 0)
        {
            if (int.TryParse(_sPayday.Value.Trim(), out var p) && p is >= 1 and <= 31) payday = p;
            else problems.Add("Payday must be a day of the month from 1 to 31.");
        }

        if (!int.TryParse(_sScanDays.Value.Trim(), out var days) || days is < 7 or > 365)
        {
            problems.Add("The first scan can look back 7 to 365 days.");
        }

        var settings = snap.Settings with
        {
            Balance = balance,
            BalanceAsOfUtc = balance != snap.Settings.Balance ? DateTime.UtcNow : snap.Settings.BalanceAsOfUtc,
            PaydayDay = payday,
            NetSalary = Num(_sNet.Value, "Net salary", 1_000_000m),
            EverydaySpendPerDay = Num(_sEveryday.Value, "Everyday spending", 10_000m),
            ScanDays = Math.Clamp(days, 7, 365),
            // The AI switch is shown on Premium only; elsewhere the saved choice is left as it was
            AiEnabled = IsPremium() ? _sAi.Value : snap.Settings.AiEnabled,
            AiConsentUtc = IsPremium() ? (_sAi.Value ? snap.Settings.AiConsentUtc ?? DateTime.UtcNow : null) : snap.Settings.AiConsentUtc,
        };

        if (problems.Count > 0)
        {
            _sMessage.Value = string.Join(" ", problems);
            return;
        }

        _sMessage.Value = null;
        await RunMyAsync(async id =>
        {
            await Companion.SaveSettingsAsync(id, settings, CancellationToken.None);
            return "Saved.";
        });
    }

    /// <summary>Deletes the account now: a paid Premium ends at once (no further charge), every record
    /// Omabilo keeps is deleted straight away, the sign-in is handed to the platform for removal, and the
    /// person is signed out. Nothing waits on a grace period the person has to remember.</summary>
    private async Task DeleteAccountNowAsync()
    {
        _myConfirm.Value = null;

        if (MyUserId is not { } uid || _myBusy.Value)
        {
            return;
        }

        if (app.GlobalState.ServerRunType == ServerRunType.Local)
        {
            _toasts.Error("Account deletion is off in a local test run.");
            return;
        }

        _myBusy.Value = true;

        try
        {
            if (!await EndPremiumForDeletionAsync())
            {
                _toasts.Error("Your Premium subscription couldn't be stopped just now, so nothing was deleted. Please try again.");
                return;
            }

            // The sign-in itself is removed by the platform; asking again when already asked changes nothing
            if (await ClientFunctions.RequestAccountRemovalAsync() is not { Succeeded: true })
            {
                _toasts.Error("Your account couldn't be deleted just now. Nothing was deleted. Please try again.");
                return;
            }

            await Companion.DeleteAllDataAsync(uid, CancellationToken.None);
            await DeleteFeedbackForAsync(uid);
            _export.Value = null;
            _removalDate.Value = null;
            Log.Instance.Info($"account deleted now for user {uid}");

            // Per-user saved state is erased in the background, the way the platform guide advises
            _ = Task.Run(async () =>
            {
                try { await app.EraseUserStateAsync(uid); }
                catch (Exception ex) { Log.Instance.Warning($"user state erase failed: {ex.GetType().Name}"); }
            });

            _toasts.Success("Your account and all your Omabilo data are deleted.");
            await ClientFunctions.LogoutAsync();
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"account delete failed: {ex.GetType().Name}");
            _toasts.Error("Deleting didn't finish. Please try again; anything already deleted stays deleted.");
        }
        finally
        {
            _myBusy.Value = false;
        }
    }

    // ── Shared pieces ───────────────────────────────────────────────────────

    private static (string Label, SemanticTone Tone) StatusLook(DisplayStatus s) => s switch
    {
        DisplayStatus.NeedsReview => ("Needs review", SemanticTone.Warning),
        DisplayStatus.Upcoming => ("Upcoming", SemanticTone.Info),
        DisplayStatus.DueSoon => ("Due soon", SemanticTone.Warning),
        DisplayStatus.Overdue => ("Overdue", SemanticTone.Error),
        DisplayStatus.ReportedPaid => ("You reported paid", SemanticTone.Success),
        DisplayStatus.ConfirmedPaid => ("Paid, confirmed by message", SemanticTone.Success),
        DisplayStatus.Disputed => ("Disputed", SemanticTone.Neutral),
        DisplayStatus.NotApplicable => ("Not applicable", SemanticTone.Neutral),
        DisplayStatus.Recorded => ("Spent", SemanticTone.Neutral),
        DisplayStatus.ExpectedIncome => ("Expected income", SemanticTone.Info),
        DisplayStatus.IncomeReported => ("Sender says paid", SemanticTone.Success),
        DisplayStatus.IncomeReceived => ("You marked received", SemanticTone.Success),
        _ => ("Information", SemanticTone.Neutral),
    };

    private static string KindIcon(FinKind k) => k switch
    {
        FinKind.Payslip or FinKind.Salary => "banknote-arrow-down",
        FinKind.Receipt => "shopping-bag",
        FinKind.Refund => "undo-2",
        FinKind.PaymentReminder => "alarm-clock",
        FinKind.DebtCollection => "gavel",
        FinKind.TaxNotice => "landmark",
        FinKind.Subscription => "repeat",
        FinKind.PaymentConfirmation => "circle-check",
        FinKind.Insurance => "umbrella",
        FinKind.LoanInstalment => "trending-down",
        _ => "receipt",
    };

    private static string KindName(FinKind k) => k switch
    {
        FinKind.PaymentReminder => "Payment reminder",
        FinKind.DebtCollection => "Collection notice",
        FinKind.TaxNotice => "Tax notice",
        FinKind.LoanInstalment => "Loan instalment",
        FinKind.PaymentConfirmation => "Payment confirmation",
        FinKind.OtherFinancial => "Financial message",
        _ => k.ToString(),
    };

    private static readonly FinKind[] CorrectableKinds = Enum.GetValues<FinKind>().Where(k => k != FinKind.NotFinancial).ToArray();

    /// <summary>A document type with the part of Omabilo it goes to, for the correction form.</summary>
    private static string KindChoice(FinKind k) => KindName(k) + (k switch
    {
        FinKind.Salary or FinKind.Payslip => " (salary)",
        FinKind.Receipt or FinKind.Refund => " (spending)",
        FinKind.TaxNotice => " (bills, Taxes)",
        FinKind.PaymentConfirmation or FinKind.OtherFinancial => " (information only)",
        _ => " (bills)",
    });

    private static void BasisBadge(UIView view, AmountBasis basis)
    {
        var (label, tone) = basis switch
        {
            AmountBasis.UserEntered => ("You entered", SemanticTone.Info),
            AmountBasis.FromEmail => ("From email", SemanticTone.Success),
            AmountBasis.Uploaded => ("Uploaded", SemanticTone.Success),
            AmountBasis.Estimate => ("Estimate", SemanticTone.Warning),
            _ => ("Calculated", SemanticTone.Neutral),
        };
        view.Badge(label, tone: tone, size: BadgeSize.Sm);
    }

    private void SignedOutCard(UIView view)
    {
        view.Column(["gap-6"], content: page =>
        {
            page.Box([Card.Elevated, "p-6 sm:p-8 flex flex-col gap-4"], content: v =>
            {
                IconTile(v, "sprout", "brand", "w-12 h-12");
                v.Heading("Understand every bill before it's due", style: [Text.H3, "m-0 text-foreground"], level: 2);
                v.Text([Text.Body, "text-foreground/90"],
                    "Add a bill and Omabilo reads it, explains it in plain English (Finnish bills too) and reminds you before the due date. Start free; upgrade when you want Omabilo to find your bills for you.");
                v.Button([Button.PrimaryLg, "self-start"], text: _signingIn.Value ? "Opening…" : "Sign in or create a free account", icon: "log-in", disabled: _signingIn.Value,
                    onClick: async () => await SignInAsync("Sign in to start using Omabilo"));
                v.Text([Text.Caption, "text-muted-foreground"],
                    "Any account works: Google, Apple, Microsoft, Finnish bank ID (Signicat), an email code or a passkey. Signing in never opens your email; connecting Gmail is a separate choice.");
            });

            page.Grid(["grid grid-cols-1 md:grid-cols-2 gap-4"], content: g =>
            {
                g.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-3"], content: v =>
                {
                    v.Row(["items-baseline justify-between gap-2"], content: r =>
                    {
                        r.Heading("Free", style: [Text.H5, "m-0 text-foreground"], level: 3);
                        r.Text([Text.BodyStrong, "text-foreground"], "€0");
                    });
                    FeatureList(v, Membership.FreeIncludes);
                });

                g.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-3 border-brand"], content: v =>
                {
                    v.Row(["items-baseline justify-between gap-2"], content: r =>
                    {
                        r.Heading("Premium", style: [Text.H5, "m-0 text-foreground"], level: 3);
                        r.Text([Text.BodyStrong, "text-foreground"], Membership.PremiumPrice);
                    });
                    v.Text([Text.BodySm, "text-muted-foreground"], "Everything in Free, plus:");
                    FeatureList(v, Membership.PremiumAdds.Select(Membership.Describe), "plus");
                });
            });
        });
    }

    private void LoadStates(UIView view)
    {
        if (_myError.Value is { } error)
        {
            view.Alert("Couldn't load your data", tone: SemanticTone.Error, description: error,
                action: a => a.Button([Button.OutlineSm], text: "Try again", onClick: LoadMyAsync));
        }
        else if (_myLoading.Value && _my.Value == null)
        {
            view.Box([Card.Default, "p-6 flex flex-row items-center gap-3"], content: b =>
            {
                b.Icon([Icon.Sm, "animate-spin text-muted-foreground"], name: "loader-circle");
                b.Text([Text.Body, "text-muted-foreground"], "Loading your data…");
            });
        }
    }

    private void ConnectionBar(UIView view, CompanionSnapshot snap)
    {
        var c = FocusAccount(snap);

        // While the free trial has plenty of time left it is a small badge. With 7 days or fewer left,
        // or once it has ended, the full notice (with the Premium upgrade) shows under the bar.
        var trialEnd = IsPremium() ? null : Membership.GmailTrialEnds(snap.Settings, snap.Connection);
        var s = GmailBar.Describe(c, trialEnd, _mySyncing.Value, _awaitingGmail.Value, DateTime.UtcNow);
        var dot = s.Dot switch
        {
            "error" => "bg-error-500",
            "warning" => "bg-warning-500",
            "info" => "bg-info-500",
            "neutral" => "bg-muted-foreground",
            _ => "bg-success-500",
        };
        var syncing = s.Kind == GmailBarKind.MySync || (_mySyncing.Value && s.Action == GmailBarAction.SyncNow);

        view.Box([Card.Default, "p-4 flex flex-col gap-3"], content: bar =>
        {
            bar.Row(["items-center gap-3 flex-wrap sm:flex-nowrap"], content: r =>
            {
                r.Box([$"w-2.5 h-2.5 rounded-full shrink-0 {dot}"]);
                r.Column(["gap-0.5 flex-1 min-w-0"], content: col =>
                {
                    col.Row(["items-center gap-2 flex-wrap"], content: head =>
                    {
                        head.Text([Text.BodyStrong, "text-foreground"], s.Title);
                        if (s.TrialBadge != null)
                        {
                            head.Badge(s.TrialBadge, tone: SemanticTone.Info, size: BadgeSize.Sm);
                        }
                    });
                    col.Text([Text.Caption, "text-muted-foreground truncate"], s.Summary);
                });

                // Once the trial has ended there is nothing to sync: the notice below offers Premium instead
                if (s.Action == GmailBarAction.Reconnect)
                {
                    r.Button([Button.PrimarySm, "shrink-0"], text: "Reconnect Gmail", onClick: ConnectGmailAsync);
                }
                else if (s.Action == GmailBarAction.SyncNow)
                {
                    r.Button([Button.OutlineSm, "shrink-0"], text: syncing ? "Syncing…" : "Sync now", icon: syncing ? "loader-circle" : "refresh-cw",
                        disabled: s.SyncDisabledReason != null, onClick: () => SyncNow());
                }
            });

            if (s.Kind == GmailBarKind.BackgroundSync && s.SyncDisabledReason != null)
            {
                bar.Text([Text.Caption, "text-muted-foreground"], s.SyncDisabledReason);
            }

            // Several accounts: each one's own state, one line each, all filing into this one inbox
            if (snap.Connections.Count > 1)
            {
                bar.Column(["gap-1 border-t border-secondary pt-2"], content: list =>
                {
                    foreach (var account in snap.Connections)
                    {
                        var (tone, state) = AccountState(snap, account);
                        list.Row(["items-center gap-2 min-w-0"], key: $"bar-{account.AccountId}", content: row =>
                        {
                            row.Box([$"w-2 h-2 rounded-full shrink-0 {tone}"]);
                            row.Text([Text.Caption, "text-foreground truncate"], account.GoogleEmail ?? "Gmail");
                            row.Text([Text.Caption, "text-muted-foreground truncate"], state);
                        });
                    }

                    list.Button([Button.LinkSm, "self-start"], text: "Manage Gmail accounts", onClick: () => Go("my-settings"));
                });
            }

            // Detail stays visible whenever the person needs to know or act.
            if (s.AlertTitle != null)
            {
                bar.Alert(s.AlertTitle, tone: s.AlertTone == "error" ? SemanticTone.Error : SemanticTone.Warning, description: s.AlertText);
            }
        });

        if (_myNotice.Value is { } notice)
        {
            view.Alert(notice, tone: SemanticTone.Info, key: "my-notice", onDismiss: async () => _myNotice.Value = null);
        }

        if (s.ShowTrialNotice)
        {
            GmailTrialNote(view, snap);
        }
    }

    /// <summary>The person's private address for forwarding bills: works with any email provider and
    /// gives Omabilo no access to the mailbox. compact shows just the address and the copy button.</summary>
    private void ForwardBillsCard(UIView view, CompanionSnapshot snap, bool compact)
    {
        var domain = _forwardDomain.Value;
        var address = snap.Forward is { } f && domain != null ? $"{f.Token}@{domain}" : null;

        view.Box([compact ? Card.Default : Card.Elevated, compact ? "p-4 flex flex-col gap-3" : "p-6 sm:p-8 flex flex-col gap-4"], content: v =>
        {
            if (!compact)
            {
                IconTile(v, "forward", "brand", "w-12 h-12");
                v.Heading("Forward your bills to Omabilo", style: [Text.H4, "m-0 text-foreground"], level: 2);
                v.Text([Text.Body, "text-foreground/90"],
                    "This is your own private Omabilo address. Forward a bill, payment reminder, payslip or receipt to it from any email (Gmail, Outlook, iCloud, work mail) and it appears here, read and explained, within a minute or two. PDF attachments are read too.");
            }
            else
            {
                v.Row(["items-center gap-2"], content: r =>
                {
                    r.Icon([Icon.Sm, "text-brand-secondary"], name: "forward");
                    r.Text([Text.BodyStrong, "text-foreground"], "Forward bills to your Omabilo address");
                });
            }

            if (address == null)
            {
                v.Alert("Receiving email isn't turned on yet", tone: SemanticTone.Warning,
                    description: "The app owner still needs to turn on receiving email for Omabilo. Until then, use Upload a bill in My inbox to save PDF bills to your account.");
                return;
            }

            v.Column(["gap-2 sm:flex-row sm:items-center"], content: r =>
            {
                r.Text(["font-mono text-sm sm:text-base text-foreground bg-muted rounded-xl px-3 py-2.5 break-all flex-1 min-w-0"], address);
                r.ActionButton([compact ? Button.OutlineMd : Button.PrimaryMd, "shrink-0"], action: ActionKind.CopyToClipboard,
                    options: new CopyToClipboardActionOptions { Text = address },
                    ariaLabel: "Copy address",
                    content: b => b.Row(["items-center gap-2"], content: inner =>
                    {
                        inner.Icon([Icon.Sm], name: "copy");
                        inner.Text([], "Copy address");
                    }));
            });

            if (snap.Forward is { GmailConfirmUtc: { } asked } fa && (fa.LastReceivedUtc == null || fa.LastReceivedUtc < asked || DateTime.UtcNow - asked < TimeSpan.FromDays(2)))
            {
                v.Alert("Gmail is asking you to confirm forwarding", tone: SemanticTone.Info,
                    description: $"{(fa.GmailConfirmFor != null ? $"{fa.GmailConfirmFor} wants" : "Gmail wants")} to forward mail to Omabilo automatically." +
                        (fa.GmailConfirmCode != null ? $" Confirmation code: {fa.GmailConfirmCode}. Enter it in Gmail's forwarding settings," : " Approve it") +
                        " or open Google's confirmation page below. Only do this if you set up the forwarding yourself.",
                    action: fa.GmailConfirmLink is { } link ? a => a.Link([Button.OutlineSm], text: "Open Google's confirmation page", href: link, target: "_blank", rel: "noopener noreferrer") : null);
            }

            if (snap.Forward is { LastReceivedUtc: { } last, ReceivedCount: var count })
            {
                v.Text([Text.Caption, "text-muted-foreground"], $"{count} email{(count == 1 ? "" : "s")} received · last one {Ago(last)}");
            }

            if (compact)
            {
                return;
            }

            v.Column(["gap-2"], content: list =>
            {
                foreach (var line in new[]
                {
                    "Only email sent to this address is read. Omabilo never gets access to your mailbox and never asks for a password.",
                    "Keep the address private, like a phone number: anything sent to it lands in your Omabilo. You can swap it for a new one in Plan & privacy.",
                    "Omabilo keeps the details it found and a short excerpt, then deletes the email itself.",
                })
                {
                    list.Row(["items-start gap-2"], content: row =>
                    {
                        row.Icon([Icon.Sm, "text-brand-secondary mt-0.5 shrink-0"], name: "check");
                        row.Text([Text.BodySm, "text-foreground/90"], line);
                    });
                }
            });

            v.Button([Button.GhostSm, "self-start"], text: _forwardHelp.Value ? "Hide how to forward automatically" : "How to forward bills automatically",
                icon: _forwardHelp.Value ? "chevron-up" : "chevron-down", onClick: () => _forwardHelp.Value = !_forwardHelp.Value);

            if (_forwardHelp.Value)
            {
                v.Column(["gap-3 rounded-xl bg-muted p-4"], content: help =>
                {
                    help.Text([Text.Label, "text-foreground"], "Gmail");
                    help.Text([Text.BodySm, "text-foreground/90"],
                        "1. Settings → See all settings → Forwarding and POP/IMAP → Add a forwarding address, and paste your Omabilo address. " +
                        "2. Gmail sends a confirmation here: it appears on this card within a few minutes with the code. " +
                        "3. Then create a filter (search for words like lasku, invoice, bill, kuitti) → Create filter → Forward it to your Omabilo address. Gmail then forwards only matching mail.");
                    help.Text([Text.Label, "text-foreground"], "Outlook / Hotmail");
                    help.Text([Text.BodySm, "text-foreground/90"],
                        "Settings → Mail → Rules → Add new rule: choose a condition such as \"Subject includes\" lasku or invoice, then the action \"Forward to\" your Omabilo address.");
                    help.Text([Text.Label, "text-foreground"], "Any other email");
                    help.Text([Text.BodySm, "text-foreground/90"], "Press Forward on a bill and send it to your Omabilo address. That's all.");
                });
            }
        });
    }

    private void ConnectGmailCard(UIView view, bool primary = true)
    {
        var missing = Engine.MissingConfiguration();

        view.Box([primary ? Card.Elevated : Card.Default, "p-6 sm:p-8 flex flex-col gap-4"], content: v =>
        {
            IconTile(v, "mail", "brand", "w-12 h-12");
            v.Heading(primary ? "Connect your Gmail" : "Or connect Gmail directly", style: [Text.H4, "m-0 text-foreground"], level: 2);
            v.Text([Text.Body, "text-foreground/90"],
                "Omabilo reads your email to find bills, payment reminders, payslips and receipts, and keeps them organised here. It only reads: it can't send, delete or change anything in your mailbox.");

            v.Column(["gap-2"], content: list =>
            {
                foreach (var line in new[]
                {
                    "Google shows you exactly what you allow. Omabilo never asks for your Google password.",
                    "Clearly unrelated mail isn't downloaded in full. For financial messages Omabilo keeps a short excerpt, not the whole email.",
                    "The first scan looks back 90 days (you can change this). After that, new mail is picked up automatically, without you opening Omabilo.",
                    "Disconnect at any time, and delete everything Omabilo kept.",
                })
                {
                    list.Row(["items-start gap-2"], content: row =>
                    {
                        row.Icon([Icon.Sm, "text-brand-secondary mt-0.5 shrink-0"], name: "check");
                        row.Text([Text.BodySm, "text-foreground/90"], line);
                    });
                }
            });

            if (!IsPremium())
            {
                v.Text([Text.BodySm, "text-muted-foreground"], $"Free for your first {Membership.GmailTrialDays} days from when you connect. After that Gmail syncing is part of Premium ({Membership.PremiumPrice}); everything Omabilo found stays yours.");
            }

            if (missing.Count == 0 && SecretOrNull("GOOGLE_OAUTH_PUBLISHED") != "true")
            {
                v.Alert("Gmail access is in Google's testing stage", tone: SemanticTone.Info,
                    description: "Until Google finishes reviewing Omabilo, only Google accounts the app owner has added as testers can connect Gmail. If Google says \"Access blocked\", ask the owner to add your address. Everything else in Omabilo works without Gmail.");
            }

            if (missing.Count > 0)
            {
                v.Alert("Gmail connection isn't set up on this server yet", tone: SemanticTone.Warning,
                    description: $"The app owner still needs to add: {string.Join("; ", missing)}. Nothing is connected until then.");
            }

            v.Row(["gap-2 flex-wrap items-center"], content: r =>
            {
                r.Button([primary ? Button.PrimaryLg : Button.OutlineLg], text: _awaitingGmail.Value ? "Waiting for Google…" : "Connect Gmail", icon: "mail",
                    disabled: missing.Count > 0 || _awaitingGmail.Value, onClick: ConnectGmailAsync);

                if (_awaitingGmail.Value && _connectUrl.Value is { } url)
                {
                    r.Link(["text-brand-secondary underline text-sm"], text: "Open Google's page", href: url);
                }
            });
        });
    }

    // ── My money ────────────────────────────────────────────────────────────

    private void RenderMyOverview(UIView view)
    {
        if (MyUserId == null)
        {
            SignedOutCard(view);
            return;
        }

        var premium = IsPremium();
        var hasRecords = _my.Value is { } loaded && (loaded.Items.Count > 0 || _plans.Value?.Debts.Count > 0);

        // How things look comes only from saved records; with none there is nothing to judge yet
        RenderHomeHero(view, hasRecords ? HomeSummary.Health(MyReminders(), premium ? _my.Value!.Plan : null) : null);
        LoadStates(view);

        if (_my.Value is not { } snap)
        {
            return;
        }

        if (snap.Connection != null)
        {
            ConnectionBar(view, snap);

            if (!snap.Connection.InitialScanDone && snap.Items.Count == 0)
            {
                view.EmptyState("Your first scan is running", description: "Omabilo is looking through your recent email. This usually takes a few minutes; you can close the page and come back.", icon: "mail-search");
                return;
            }
        }

        // With nothing saved yet, the ways to add a first bill come first
        if (snap.Items.Count == 0)
        {
            RenderHomeAddBills(view, snap);
        }

        RenderHomeTiles(view, snap);

        view.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-6 items-start"], content: g =>
        {
            g.Column(["lg:col-span-3 min-w-0"], content: v => RenderToday(v, snap));
            g.Column(["lg:col-span-2 min-w-0"], content: v => RenderHomeMoneyFlow(v, snap));
        });

        RenderHomeLists(view, snap);

        if (snap.Items.Count > 0)
        {
            RenderHomeAddBills(view, snap);
        }

        if (!premium)
        {
            PremiumTeaser(view);
        }
    }

    /// <summary>Forwarding, upload and Gmail: prominent before the first bill, compact after it.</summary>
    private void RenderHomeAddBills(UIView view, CompanionSnapshot snap)
    {
        if (snap.Connection != null)
        {
            if (snap.Items.Count == 0)
            {
                view.EmptyState("No financial messages found yet", description: "Omabilo didn't find bills, payslips or receipts in the period it scanned. New ones appear here automatically.", icon: "inbox");
            }
            return;
        }

        if (IsPremium())
        {
            ForwardBillsCard(view, snap, compact: snap.Items.Count > 0);
        }

        UploadBillCard(view, compact: snap.Items.Count > 0, allowPaste: false);

        if (GmailAvailable() && snap.Items.Count == 0)
        {
            ConnectGmailCard(view, primary: false);
        }
    }

    // ── Home reminders: deadlines, checks and payday, only from saved records ──

    private IReadOnlyList<Reminder> MyReminders() => _my.Value is { } s
        ? Attention.Build(s.Items, s.Income, _plans.Value?.Debts ?? [], DateTime.UtcNow.Date)
        : [];

    private int MyReminderTasks() => MyReminders().Count(r => r.IsTask);

    private void RenderCashFlow(UIView view, CompanionSnapshot snap)
    {
        var plan = snap.Plan;

        view.Column(["gap-3"], content: col =>
        {
            SectionHeader(col, "Cash flow", plan.PeriodLabel);

            col.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-4"], content: grid =>
            {
                grid.Box([Card.Elevated, "p-5 lg:col-span-3 flex flex-col gap-3"], content: card =>
                {
                    void Line(string label, decimal amount, AmountBasis? basis, string? note, bool minus)
                    {
                        card.Row(["items-start gap-3 py-1.5 border-b border-secondary last:border-b-0"], content: r =>
                        {
                            r.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                            {
                                c.Row(["items-center gap-2 flex-wrap"], content: rr =>
                                {
                                    rr.Text([Text.BodySm, "text-foreground"], label);
                                    if (basis is { } b) BasisBadge(rr, b);
                                });
                                if (note != null) c.Text([Text.Caption, "text-muted-foreground"], note);
                            });
                            r.Text([Text.BodyStrong, "tabular-nums text-foreground shrink-0"], (minus ? "− " : "") + Money(amount));
                        });
                    }

                    if (plan.StartingBalance is { } balance)
                    {
                        Line("Starting balance", balance, AmountBasis.UserEntered, plan.BalanceNote, false);
                    }
                    else
                    {
                        card.Alert("Your balance isn't entered", tone: SemanticTone.Info, description: plan.BalanceNote);
                    }

                    foreach (var l in plan.Income) Line($"+ {l.Label}" + (l.Date is { } d ? $" ({Day(d)})" : ""), l.Amount, l.Basis, l.Note, false);
                    foreach (var l in plan.Obligations) Line(l.Label + (l.Date is { } d ? $" (due {Day(d)})" : ""), l.Amount, l.Basis, l.Note, true);
                    foreach (var l in plan.Recurring) Line(l.Label + (l.Date is { } d ? $" (around {Day(d)})" : ""), l.Amount, l.Basis, l.Note, true);
                    foreach (var l in plan.Everyday) Line(l.Label, l.Amount, l.Basis, l.Note, true);

                    card.Row(["items-center justify-between gap-3 pt-2"], content: r =>
                    {
                        r.Text([Text.Label, "text-foreground"], "Estimated left");
                        r.Text([Text.H4, plan.Shortfall ? "text-error-600 tabular-nums" : "text-foreground tabular-nums"], plan.ProjectedRemainder is { } left ? Money(left) : "—");
                    });
                    card.Text([Text.Caption, "text-muted-foreground"], plan.RemainderExplanation + " This is an estimate, not a promise.");

                    if (plan.NotCounted.Count > 0)
                    {
                        card.Column(["gap-1 rounded-xl bg-muted p-3"], content: c =>
                        {
                            c.Text([Text.Label, "text-foreground"], "Not counted, and why");

                            foreach (var n in plan.NotCounted)
                            {
                                c.Text([Text.Caption, "text-muted-foreground"], $"• {n.Label}: {n.Reason}");
                            }
                        });
                    }
                });

                grid.Box([Card.Default, "p-5 lg:col-span-2 flex flex-col gap-3"], content: form =>
                {
                    form.Heading("Your numbers", style: [Text.H6, "m-0 text-foreground"], level: 3);
                    form.Text([Text.Caption, "text-muted-foreground"], "Omabilo can't see your bank. These help it plan; they're stored only for you.");
                    form.TextField(label: "Current balance (€)", bind: _sBalance, placeholder: "e.g. 812,40");
                    form.TextField(label: "Payday (day of month)", bind: _sPayday, placeholder: "e.g. 25", type: "number", min: "1", step: "1");
                    form.TextField(label: "Net salary (€)", bind: _sNet, placeholder: "Leave empty to use your payslips");
                    form.TextField(label: "Everyday spending per day (€)", bind: _sEveryday, placeholder: "e.g. 20");

                    if (_sMessage.Value is { } m)
                    {
                        form.Alert(tone: SemanticTone.Error, description: m);
                    }

                    form.Button([Button.PrimaryMd, "self-start"], text: _myBusy.Value ? "Saving…" : "Update plan", disabled: _myBusy.Value, onClick: SaveSettingsAsync);
                });
            });
        });
    }

    // ── My inbox ────────────────────────────────────────────────────────────

    private void RenderMyInbox(UIView view)
    {
        if (MyUserId == null)
        {
            SignedOutCard(view);
            return;
        }

        LoadStates(view);

        if (_my.Value is not { } snap)
        {
            return;
        }

        var premium = IsPremium();

        if (snap.Connection == null && snap.Items.Count == 0)
        {
            UploadBillCard(view, compact: false);

            if (premium)
            {
                ForwardBillsCard(view, snap, compact: false);
            }

            if (GmailAvailable())
            {
                ConnectGmailCard(view, primary: false);
            }

            return;
        }

        if (snap.Connection != null)
        {
            ConnectionBar(view, snap);
        }
        else if (premium)
        {
            ForwardBillsCard(view, snap, compact: true);
        }

        UploadBillCard(view, compact: true);

        var today = DateTime.UtcNow.Date;
        var statuses = snap.Items.ToDictionary(i => i.Id, i => ItemRules.StatusOf(i, today));

        bool InFilter(FinancialItem i, string f) => ItemRules.InView(i, statuses[i.Id], f);
        var filters = ItemRules.InboxViews;

        view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-4"], content: card =>
        {
            card.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
            {
                r.ToggleGroupSingle([ToggleGroup.Root, "flex-wrap"], value: _myFilter.Value, ariaLabel: "Show",
                    onValueChange: async v => { if (!string.IsNullOrEmpty(v)) _myFilter.Value = v; },
                    content: group =>
                    {
                        foreach (var (key, label) in filters)
                        {
                            var count = snap.Items.Count(i => InFilter(i, key));
                            group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5"], value: key, content: v =>
                            {
                                v.Text(text: label);
                                v.Text(["text-xs tabular-nums opacity-70"], text: count.ToString(CultureInfo.InvariantCulture));
                            });
                        }
                    });
                r.TextField([Input.DefaultSm, "w-full sm:w-56"], placeholder: "Search company or title", bind: _mySearch, ariaLabel: "Search my inbox");
            });

            var q = _mySearch.Value.Trim();
            var rows = snap.Items.Where(i => InFilter(i, _myFilter.Value))
                .Where(i => q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || (i.Counterparty ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => statuses[i.Id].Status is DisplayStatus.Overdue ? 0 : statuses[i.Id].NeedsReview ? 1 : statuses[i.Id].Status is DisplayStatus.DueSoon ? 2 : 3)
                .ThenBy(i => i.DueDate ?? i.EventDate ?? DateTime.MaxValue)
                .ToList();

            if (rows.Count == 0)
            {
                card.EmptyState(q.Length > 0 ? "Nothing matches" : "Nothing here", description: q.Length > 0 ? "Try another search, or show All." : "Items in this view appear here as Omabilo finds them.",
                    icon: q.Length > 0 ? "search-x" : "inbox",
                    action: q.Length > 0 || _myFilter.Value != "all" ? a => a.Button([Button.OutlineSm], text: "Show all", onClick: () => { _mySearch.Value = ""; _myFilter.Value = "all"; }) : null);
                return;
            }

            card.Column(["gap-2"], content: list =>
            {
                foreach (var item in rows)
                {
                    ItemRow(list, item, statuses[item.Id], compact: false);
                }
            });
        });

        RenderItemSheet(view, snap);
    }

    private void ItemRow(UIView view, FinancialItem item, ItemStatus status, bool compact)
    {
        var (label, tone) = StatusLook(status.Status);

        view.Box([compact ? "rounded-xl p-2 -mx-2 hover:bg-muted" : "rounded-2xl border border-secondary bg-card p-3 sm:p-4 hover:shadow-sm", "flex flex-row items-center gap-3 cursor-pointer min-h-11"],
            key: $"row-{item.Id}", ariaLabel: $"Open {item.Title}", onClick: () => OpenItem(item),
            content: row =>
            {
                if (!compact)
                {
                    IconTile(row, KindIcon(item.Kind), tone switch { SemanticTone.Error => "error", SemanticTone.Warning => "warning", SemanticTone.Success => "success", _ => "brand" });
                }

                row.Column(["gap-0.5 flex-1 min-w-0"], content: c =>
                {
                    c.Text([compact ? Text.BodySm : Text.BodyStrong, "text-foreground truncate"], item.Title);
                    var when = item.DueDate is { } due ? $"Due {Day(due)} · {KindName(item.Kind)}" : item.EventDate is { } ev ? $"{Day(ev)} · {KindName(item.Kind)}" : KindName(item.Kind);
                    c.Text([Text.Caption, "text-muted-foreground truncate"], DocumentNote(item) is { } doc ? $"{when} · {doc}" : when);
                });

                row.Column(["items-end gap-1 shrink-0"], content: c =>
                {
                    c.Text([Text.BodyStrong, "tabular-nums text-foreground"], item.Amount is { } a ? (item.Currency == "EUR" ? Money(a) : $"{a:0.00} {item.Currency}") : "—");
                    c.Row(["gap-1"], content: badges =>
                    {
                        badges.Badge(label, tone: tone, size: BadgeSize.Sm);
                        if (status.NeedsReview && status.Status != DisplayStatus.NeedsReview) badges.Badge("Check", tone: SemanticTone.Warning, size: BadgeSize.Sm);
                    });
                });
            });
    }

    // Where this record is sorted: the inbox keeps the original, and My Money (and Debts & Loans) show
    // the same record by its id, so a correction made in either place shows in both
    private void RenderFiling(UIView view, FinancialItem item)
    {
        var tab = ItemRules.MoneyTab(item);
        var where = tab is { } t
            ? $"In My Money · {MoneyTabLabel(t)}{(ItemRules.IsDebt(item) ? ", and listed on Debts & Loans" : "")}. The original stays in your Financial Inbox."
            : "Kept in your Financial Inbox only. It isn't counted in My Money.";

        view.Row(["items-center justify-between gap-3 flex-wrap rounded-xl bg-muted p-3"], content: r =>
        {
            r.Text([Text.BodySm, "text-foreground flex-1 min-w-[12rem]"], where);

            if (tab != null && !(_section.Value == "my-money" && _moneyTab.Value == tab))
            {
                r.Button([Button.OutlineSm], text: $"Show in {MoneyTabLabel(tab)}", onClick: () => GoMoney(tab));
            }
            else if (_section.Value != "my-inbox")
            {
                r.Button([Button.OutlineSm], text: "Show in Financial Inbox", onClick: () =>
                {
                    _myFilter.Value = "all";
                    Go("my-inbox");
                });
            }
        });
    }

    /// <summary>"PDF read" or "PDF not read" for a list row, or null when the email had no documents.</summary>
    private static string? DocumentNote(FinancialItem item)
    {
        var docs = item.Attachments.Where(a => a.Status != AttachmentStatus.NotSupported).ToList();
        if (docs.Count == 0) return null;
        var read = docs.Count(a => a.WasRead);
        var word = docs.All(a => a.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) ? "PDF" : "document";
        return read == docs.Count ? $"{word}{(docs.Count > 1 ? "s" : "")} read" : read == 0 ? $"{word} not read" : $"{read} of {docs.Count} documents read";
    }

    private void RenderRecurring(UIView view, CompanionSnapshot snap)
    {
        if (snap.Recurring.Count == 0)
        {
            view.EmptyState("No recurring costs found yet", description: "A cost shows here once Omabilo has seen it at least twice about a month apart.", icon: "repeat");
            return;
        }

        foreach (var r in snap.Recurring)
        {
            view.Box(["rounded-2xl border border-secondary p-4 flex flex-row items-center gap-3"], key: $"rec-{r.Counterparty}", content: row =>
            {
                IconTile(row, "repeat", "brand");
                row.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground"], r.Counterparty);
                    c.Text([Text.Caption, "text-muted-foreground"], $"{r.Category} · next around {Day(r.NextExpected)} (estimate) · {r.Basis}");
                });
                row.Text([Text.BodyStrong, "tabular-nums text-foreground"], $"{Money(r.LastAmount)}/mo");
            });
        }
    }

    // ── One item ────────────────────────────────────────────────────────────

    private void RenderItemSheet(UIView view, CompanionSnapshot snap)
    {
        var item = _myOpen.Value is { } id ? snap.Items.FirstOrDefault(i => i.Id == id) : null;
        var status = item != null ? ItemRules.StatusOf(item, DateTime.UtcNow.Date) : null;

        view.Sheet(open: item != null, onOpenChange: async open => { if (!open) { _myOpen.Value = null; _myEditing.Value = false; } },
            title: item?.Title ?? "Item", description: item != null ? $"{KindName(item.Kind)} · {item.Category}" : null,
            style: ["default", "w-full sm:max-w-xl"],
            content: v =>
            {
                if (item == null || status == null)
                {
                    return;
                }

                v.ScrollArea(rootStyle: ["h-full"], content: body => body.Column(["gap-4 pb-8 pr-1"], content: col =>
                {
                    var (label, tone) = StatusLook(status.Status);
                    col.Alert(label, tone: tone, description: status.Why + (status.ReviewReason != null ? $" Check: {status.ReviewReason}." : ""));
                    RenderFiling(col, item);
                    RenderAdvice(col, item);

                    if (_myEditing.Value)
                    {
                        RenderEditForm(col, item);
                    }
                    else
                    {
                        RenderItemFields(col, item);
                    }

                    if (item.Flags.Count > 0)
                    {
                        col.Alert("Check before relying on this", tone: SemanticTone.Warning, content: c => c.Column(["gap-1"], content: l =>
                        {
                            foreach (var f in item.Flags) l.Text([Text.BodySm], $"• {f}");
                            l.Text([Text.Caption, "text-muted-foreground"], "These are reasons to be careful, not proof that something is wrong.");
                        }));
                    }

                    if (item.Missing.Count > 0)
                    {
                        col.Column(["gap-1"], content: c =>
                        {
                            c.Text([Text.Label, "text-foreground"], "Missing or uncertain");
                            foreach (var m in item.Missing) c.Text([Text.BodySm, "text-muted-foreground"], $"• {m}");
                        });
                    }

                    if (item.Evidence.Count > 0)
                    {
                        col.Column(["gap-1"], content: c =>
                        {
                            c.Text([Text.Label, "text-foreground"], "Evidence");
                            foreach (var e in item.Evidence) c.Text([Text.BodySm, "text-muted-foreground"], $"• {e}");
                        });
                    }

                    RenderAiExplanation(col, item);
                    RenderDocuments(col, item, snap.AccountEmail(item) ?? snap.Connection?.GoogleEmail);
                    RenderSources(col, item, snap);

                    col.Text([Text.Caption, "text-muted-foreground"],
                        $"Why Omabilo thinks it's financial: {string.Join("; ", item.ClassificationReasons)}. Read by: {item.ClassifiedBy}. Confidence {item.Confidence:P0}.");

                    RenderItemActions(col, item, snap);
                }));
            });

        if (_myConfirm.Value is { } confirm && confirm.StartsWith("delete-item:") && item != null)
        {
            view.AlertDialog(open: true, onOpenChange: async open => { if (!open) _myConfirm.Value = null; },
                title: "Delete this item?",
                description: "It's removed from Omabilo. The email stays in your Gmail. If the message is read again in a full rescan, it can come back.",
                cancelLabel: "Keep it", actionLabel: "Delete", actionStyle: [Button.ErrorMd],
                onAction: async () =>
                {
                    var target = item.Id;
                    _myConfirm.Value = null;
                    _myOpen.Value = null;
                    await RunMyAsync(async uid => await Companion.DeleteItemAsync(uid, target, CancellationToken.None) ? "Item deleted." : null);
                });
        }
    }

    private static void FieldLine(UIView view, string label, string? value, bool edited = false, FieldProvenance? source = null)
    {
        view.Row(["items-start gap-3 py-2 border-b border-secondary"], content: r =>
        {
            r.Text([Text.BodySm, "text-muted-foreground w-36 shrink-0"], label);
            r.Column(["gap-0.5 flex-1 min-w-0"], content: c =>
            {
                c.Text([Text.BodyStrong, value == null ? "text-muted-foreground italic break-words" : "text-foreground break-words"], value ?? "Not stated");

                if (value != null && !edited && source != null)
                {
                    c.Text([Text.Caption, source.Confidence < 0.7 ? "text-warning-700" : "text-muted-foreground"],
                        $"From {LowerFirst(source.Source)} · {SureWord(source.Confidence)}{(source.Note != null ? $". {source.Note}" : "")}");
                }
            });
            if (edited) r.Badge("Corrected by you", tone: SemanticTone.Info, size: BadgeSize.Sm);
        });
    }

    private static string LowerFirst(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string SureWord(double c) => c >= 0.85 ? "high confidence" : c >= 0.7 ? "fairly sure" : c >= 0.5 ? "a best guess" : "uncertain";

    private static FieldProvenance? Src(FinancialItem i, string field) => i.FieldSources.FirstOrDefault(f => f.Field == field);

    private void RenderItemFields(UIView view, FinancialItem i)
    {
        var e = i.EditedFields.ToHashSet();

        view.Column(["gap-0"], content: c =>
        {
            FieldLine(c, i.IsIncome ? "Employer" : i.IsExpense ? "Merchant" : "Company", i.Counterparty, e.Contains(nameof(FinancialItem.Counterparty)), Src(i, "Company"));

            if (i.IsIncome)
            {
                FieldLine(c, "Net salary", i.Net is { } n ? Money(n) : null, e.Contains(nameof(FinancialItem.Net)), Src(i, "Net salary"));
                FieldLine(c, "Gross salary", i.Gross is { } g ? Money(g) : null, source: Src(i, "Gross salary"));
                FieldLine(c, "Tax withheld", i.Tax is { } t ? Money(t) : null);
                FieldLine(c, "Other deductions", i.OtherDeductions is { } o ? Money(o) : null);
                FieldLine(c, "Pay date", i.EventDate is { } d ? Day(d) : null, e.Contains(nameof(FinancialItem.EventDate)));
                FieldLine(c, "Period", i.Period);
                FieldLine(c, "What this shows", i.IncomeEvidence switch
                {
                    IncomeEvidence.Payslip => "A payslip: a calculation, not proof of payment",
                    IncomeEvidence.ReportedReceived => "The sender says it was paid",
                    _ => "Expected salary",
                });
                return;
            }

            FieldLine(c, "Amount", i.Amount is { } a ? (i.Currency == "EUR" ? Money(a) : $"{a:0.00} {i.Currency}") : null, e.Contains(nameof(FinancialItem.Amount)), Src(i, "Amount"));

            if (i.IsExpense)
            {
                FieldLine(c, "Date", i.EventDate is { } d ? Day(d) : null, e.Contains(nameof(FinancialItem.EventDate)), Src(i, "Date"));
                FieldLine(c, "VAT", i.Vat is { } vat ? Money(vat) : null, source: Src(i, "VAT"));
                FieldLine(c, "Order or receipt no.", i.OrderRef);
            }
            else
            {
                FieldLine(c, "What it's for", i.Purpose, e.Contains(nameof(FinancialItem.Purpose)), Src(i, "What it's for"));
                FieldLine(c, "Due date", i.DueDate is { } due ? Day(due) : null, e.Contains(nameof(FinancialItem.DueDate)), Src(i, "Due date"));
                if (i.OriginalDueDate is { } od) FieldLine(c, "Original due date", Day(od));
                FieldLine(c, "Invoice number", i.InvoiceNumber, source: Src(i, "Invoice number"));
                FieldLine(c, "Reference", i.Reference, e.Contains(nameof(FinancialItem.Reference)), Src(i, "Reference"));
                FieldLine(c, "Account (IBAN)", i.Iban, source: Src(i, "IBAN"));
                FieldLine(c, "VAT", i.Vat is { } vat ? Money(vat) : null, source: Src(i, "VAT"));
                FieldLine(c, "Invoice date", i.InvoiceDate is { } idt ? Day(idt) : null, source: Src(i, "Invoice date"));
                FieldLine(c, "Billing period", i.Period);
                FieldLine(c, "How to pay", i.PaymentInstructions);
            }

            FieldLine(c, "Category", i.Category, e.Contains(nameof(FinancialItem.Category)));
            if (i.UserNote != null) FieldLine(c, "Your note", i.UserNote);
        });
    }

    private void RenderEditForm(UIView view, FinancialItem i)
    {
        view.Column(["gap-3"], content: f =>
        {
            f.Text([Text.BodySm, "text-muted-foreground"], "Correct anything Omabilo read wrong. Your corrections are kept when the message is read again.");
            f.Select(["w-full"], label: "Document type", bind: _eKind, options: CorrectableKinds.Select(k => new SelectOption(k.ToString(), KindChoice(k))).ToArray());
            f.TextField(label: i.IsIncome ? "Employer" : i.IsExpense ? "Merchant" : "Company", bind: _eCounterparty);

            if (i.IsIncome)
            {
                f.TextField(label: "Net salary (€)", bind: _eNet);
                f.TextField(label: "Pay date (d.m.yyyy)", bind: _eDate);
            }
            else
            {
                f.TextField(label: "Amount (€)", bind: _eAmount);

                if (i.IsExpense)
                {
                    f.TextField(label: "Date (d.m.yyyy)", bind: _eDate);
                }
                else
                {
                    f.TextField(label: "Due date (d.m.yyyy)", bind: _eDue);
                    f.TextField(label: "Reference", bind: _eReference);
                    f.TextField(label: "What it's for", bind: _ePurpose);
                }
            }

            f.Select(["w-full"], label: "Category", bind: _eCategory, options: Categories.All.Select(c => new SelectOption(c, c)).ToArray());
            f.TextArea(["default"], label: "Note (only for you)", bind: _eNote, rows: 2, maxLength: 1000);

            if (_eErrors.Value.Count > 0)
            {
                f.Alert("Please fix", tone: SemanticTone.Error, description: string.Join(" ", _eErrors.Value));
            }

            f.Row(["gap-2"], content: r =>
            {
                r.Button([Button.PrimaryMd], text: "Save correction", onClick: async () => await SaveEditAsync(i));
                r.Button([Button.OutlineMd], text: "Cancel", onClick: () => OpenItem(i));
            });
        });
    }

    private void RenderAiExplanation(UIView view, FinancialItem item)
    {
        if (item.Ai is not { } ai)
        {
            return;
        }

        view.Box(["rounded-xl border border-secondary p-4 flex flex-col gap-2"], content: c =>
        {
            c.Text([Text.Label, "text-foreground"], "AI explanation");
            c.AiDisclosure(AiDisclosureKind.GeneratedContent, text: $"Written by an AI model ({ai.Model}). Amounts and statuses above come from Omabilo's own rules, not from AI.");
            c.Text([Text.Body, "text-foreground"], ai.Summary);

            void List(string title, List<string> items)
            {
                if (items.Count == 0) return;
                c.Text([Text.Caption, "text-muted-foreground pt-1"], title);
                foreach (var x in items) c.Text([Text.BodySm, "text-foreground/90"], $"• {x}");
            }

            List("What the message states", ai.Facts);
            List("What the AI infers (may be wrong)", ai.Inferences);
            List("Missing or conflicting", ai.MissingOrConflicting);
            List("Suggested next steps", ai.NextSteps);
            if (ai.WhyFinancial.Length > 0) c.Text([Text.Caption, "text-muted-foreground"], $"Why it's financial: {ai.WhyFinancial}");
            if (ai.Basis.Length > 0) c.Text([Text.Caption, "text-muted-foreground"], $"Based on: {ai.Basis}");
        });
    }

    /// <summary>Omabilo's own advice for a bill, rebuilt with today's date so "overdue" stays true.</summary>
    private static void RenderAdvice(UIView view, FinancialItem item)
    {
        if (InvoiceAnalysis.Advise(item, DateTime.UtcNow) is not { } advice)
        {
            return;
        }

        view.Box([Card.Elevated, "p-4 flex flex-col gap-2"], content: c =>
        {
            c.Row(["items-center gap-2"], content: r =>
            {
                r.Icon([Icon.Sm, "text-brand-secondary"], name: "lightbulb");
                r.Text([Text.Label, "text-foreground"], "What this is and what to do");
            });
            c.Text([Text.Body, "text-foreground"], advice.WhoAndWhat);
            c.Text([Text.BodyStrong, "text-foreground"], advice.AmountAndWhen);
            c.Text([Text.BodySm, "text-muted-foreground"], advice.Situation);

            if (advice.NextSteps.Count > 0)
            {
                c.Text([Text.Caption, "text-muted-foreground pt-1"], "Next steps");
                foreach (var step in advice.NextSteps) c.Text([Text.BodySm, "text-foreground"], $"• {step}");
            }

            if (advice.Checks.Count > 0)
            {
                c.Text([Text.Caption, "text-muted-foreground pt-1"], "Check before relying on it");
                foreach (var check in advice.Checks.Take(6)) c.Text([Text.BodySm, "text-foreground/90"], $"• {check}");
            }

            c.Text([Text.Caption, "text-muted-foreground pt-1"], advice.Basis);
        });
    }

    /// <summary>Each attachment of the source emails: whether Omabilo could read it, and why not.</summary>
    private static void RenderDocuments(UIView view, FinancialItem item, string? gmailAccount)
    {
        if (item.Attachments.Count == 0)
        {
            view.Text([Text.Caption, "text-muted-foreground"],
                item.AnalysisVersion >= InvoiceAnalysis.CurrentVersion
                    ? "Documents: the email had no attachments, so this was read from the email text."
                    : "Documents: not checked yet. Omabilo re-reads older items automatically on the next sync.");
            return;
        }

        view.Column(["gap-2"], content: c =>
        {
            c.Text([Text.Label, "text-foreground"], item.Attachments.Count == 1 ? "Document" : $"Documents ({item.Attachments.Count})");

            foreach (var a in item.Attachments)
            {
                var statusText = InvoiceAnalysis.StatusText(a.Status);

                c.Box(["rounded-xl border border-secondary p-3 flex flex-col gap-1"], key: $"doc-{a.GmailId}-{a.FileName}", content: b =>
                {
                    b.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
                    {
                        r.Row(["items-center gap-2 min-w-0"], content: n =>
                        {
                            n.Icon([Icon.Sm, "text-muted-foreground"], name: a.WasRead ? "file-check" : "file-x");
                            n.Text([Text.BodySm, "text-foreground font-semibold truncate"], a.FileName);
                        });
                        r.Badge(char.ToUpperInvariant(statusText[0]) + statusText[1..],
                            tone: a.WasRead ? SemanticTone.Success : a.Status == AttachmentStatus.NotSupported ? SemanticTone.Neutral : SemanticTone.Warning, size: BadgeSize.Sm);
                    });

                    var facts = new List<string> { a.Size > 0 ? $"{Math.Max(1, a.Size / 1024)} KB" : "size unknown" };
                    if (a.Pages > 0) facts.Add($"{a.Pages} page{(a.Pages == 1 ? "" : "s")}");
                    if (a.WasRead) facts.Add($"{a.TextLength:N0} characters of text read");
                    b.Text([Text.Caption, "text-muted-foreground"], string.Join(" · ", facts));

                    if (a.Problem != null) b.Text([Text.BodySm, a.WasRead ? "text-muted-foreground" : "text-foreground"], a.Problem);
                    if (item.AttachmentRetryAfterUtc is { } retry && a.CanRetry) b.Text([Text.Caption, "text-muted-foreground"], $"Next automatic try after {retry:HH:mm} UTC.");

                    if (BillUpload.IsUploadId(a.GmailId))
                    {
                        b.Text([Text.Caption, "text-muted-foreground"], "A file you uploaded. The file itself isn't kept.");
                        return;
                    }

                    if (BillForwarding.IsForwardedId(a.GmailId))
                    {
                        b.Text([Text.Caption, "text-muted-foreground"], "From an email you forwarded to Omabilo. The email itself isn't kept.");
                        return;
                    }

                    var link = gmailAccount != null
                        ? $"https://mail.google.com/mail/?authuser={Uri.EscapeDataString(gmailAccount)}#all/{Uri.EscapeDataString(a.GmailId)}"
                        : $"https://mail.google.com/mail/#all/{Uri.EscapeDataString(a.GmailId)}";
                    b.Link(["text-brand-secondary underline text-sm self-start"], text: "Open the email with this file in Gmail", href: link, target: "_blank", rel: "noopener noreferrer");
                });
            }
        });
    }

    private static void RenderSources(UIView view, FinancialItem item, CompanionSnapshot snap)
    {
        view.Column(["gap-2"], content: c =>
        {
            c.Text([Text.Label, "text-foreground"], item.Sources.Count == 1 ? "Source email" : $"Source emails ({item.Sources.Count})");

            foreach (var s in item.Sources)
            {
                var gmailAccount = snap.Connections.FirstOrDefault(a => a.AccountId == s.AccountId)?.GoogleEmail;

                c.Box(["rounded-xl bg-muted p-3 flex flex-col gap-1"], key: $"src-{s.AccountId}-{s.GmailId}", content: b =>
                {
                    b.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
                    {
                        r.Text([Text.BodySm, "text-foreground font-semibold truncate"], s.Subject.Length > 0 ? s.Subject : "(no subject)");
                        r.Badge(s.Role, tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                    });
                    b.Text([Text.Caption, "text-muted-foreground"], BillUpload.IsUploadId(s.GmailId) ? $"Uploaded by you · {Day(s.ReceivedUtc)}" : $"{s.From} · {Day(s.ReceivedUtc)}");
                    b.Text(["text-xs font-mono whitespace-pre-wrap break-words text-foreground/80"], s.Excerpt);

                    if (BillUpload.IsUploadId(s.GmailId))
                    {
                        return;
                    }

                    if (BillForwarding.IsForwardedId(s.GmailId))
                    {
                        b.Text([Text.Caption, "text-muted-foreground"], "Forwarded to your Omabilo address");
                        return;
                    }

                    // Which of the person's mailboxes it is in, when there are several (a disconnected one has no address left)
                    if (snap.Connections.Count > 1 || gmailAccount == null && !Mailboxes.IsMain(s.Account))
                    {
                        b.Text([Text.Caption, "text-muted-foreground"], gmailAccount != null ? $"In Gmail account {gmailAccount}" : "In a Gmail account that is no longer connected");
                    }

                    var link = gmailAccount != null
                        ? $"https://mail.google.com/mail/?authuser={Uri.EscapeDataString(gmailAccount)}#all/{Uri.EscapeDataString(s.GmailId)}"
                        : $"https://mail.google.com/mail/#all/{Uri.EscapeDataString(s.GmailId)}";
                    b.Link(["text-brand-secondary underline text-sm self-start"], text: "Open in Gmail", href: link, target: "_blank", rel: "noopener noreferrer");
                });
            }
        });
    }

    private void RenderItemActions(UIView view, FinancialItem item, CompanionSnapshot snap)
    {
        async Task Set(UserState state, string message) => await RunMyAsync(async uid =>
            await Companion.SetStateAsync(uid, item.Id, state, CancellationToken.None) != null ? message : null);

        view.Column(["gap-2 pt-2"], content: c =>
        {
            c.Row(["gap-2 flex-wrap"], content: r =>
            {
                if (!_myEditing.Value) r.Button([Button.OutlineSm], text: "Correct details", icon: "pencil", onClick: () => _myEditing.Value = true);
                if (item.UserState == UserState.None) r.Button([Button.OutlineSm], text: "Looks right", icon: "check", disabled: _myBusy.Value, onClick: async () => await Set(UserState.Reviewed, "Marked as reviewed."));

                if (item.IsObligation || item.Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection)
                {
                    if (item.UserState is not (UserState.ReportedPaid or UserState.ConfirmedPaid))
                    {
                        r.Button([Button.PrimarySm], text: "I've paid this", disabled: _myBusy.Value, onClick: async () => await Set(UserState.ReportedPaid, "Recorded as paid by you. It isn't bank-confirmed."));
                    }

                    if (item.UserState != UserState.Disputed) r.Button([Button.OutlineSm], text: "Dispute", disabled: _myBusy.Value, onClick: async () => await Set(UserState.Disputed, "Marked as disputed."));

                    if (ItemRules.CountsAsOpenObligation(item) && _draftItem.Value != item.Id)
                    {
                        r.Button([Button.OutlineSm], text: "Ask for more time", icon: "mail-plus", onClick: () => OpenExtensionDraft(item));
                    }
                }

                // Expected pay becomes money you have only when you say it arrived
                if (item.IsIncome && item.UserState is not (UserState.ReportedPaid or UserState.ConfirmedPaid))
                {
                    r.Button([Button.PrimarySm], text: "I've received this", icon: "banknote-arrow-down", disabled: _myBusy.Value,
                        onClick: async () => await Set(UserState.ReportedPaid, "Recorded as received by you. It now counts as money on your account."));
                }

                if (item.UserState != UserState.NotApplicable) r.Button([Button.OutlineSm], text: "Not applicable", disabled: _myBusy.Value, onClick: async () => await Set(UserState.NotApplicable, "Marked as not applicable."));
                if (item.UserState != UserState.None) r.Button([Button.GhostSm], text: "Undo my decision", disabled: _myBusy.Value, onClick: async () => await Set(UserState.None, "Decision cleared."));
            });

            c.Row(["gap-2 flex-wrap"], content: r =>
            {
                var forwarded = !SyncEngine.CanReread(item);
                var connected = snap.Connection is { Status: not ConnectionStatus.NeedsReconnect } && !forwarded;
                r.Button([Button.GhostSm], text: "Read the email again", icon: "rotate-ccw", disabled: !connected || _myBusy.Value,
                    tooltip: forwarded ? "Omabilo doesn't keep forwarded emails or uploaded files, so it can't read this one again"
                        : connected ? "Re-reads the source email with the current rules, keeping your corrections" : "Connect Gmail to re-read the email",
                    onClick: async () => await RunMyAsync(async uid => await Engine.ReprocessAsync(uid, item.Id, CancellationToken.None) != null ? "Read again." : "Couldn't read it again."));

                if (snap.Settings.AiEnabled && item.Ai == null && IsPremium())
                {
                    // From the email when it can be read again, else from what Omabilo kept of the record
                    r.Button([Button.GhostSm], text: "Explain with AI", icon: "sparkles", disabled: _myBusy.Value,
                        onClick: async () => await RunMyAsync(async uid =>
                        {
                            var updated = connected
                                ? await Engine.ReprocessAsync(uid, item.Id, CancellationToken.None)
                                : await Engine.ExplainSavedAsync(uid, item.Id, CancellationToken.None);
                            return updated?.Ai != null ? "AI explanation added." : "The AI explanation isn't available right now. The rule-based result is unchanged.";
                        }));
                }

                r.Button([Button.OutlineErrorSm], text: "Delete", icon: "trash-2", onClick: () => _myConfirm.Value = $"delete-item:{item.Id}");
            });

            RenderExtensionDraft(c, item);
        });
    }

    // ── Plan & privacy ───────────────────────────────────────────────

    private void RenderMySettings(UIView view)
    {
        if (MyUserId == null)
        {
            SignedOutCard(view);
            return;
        }

        LoadStates(view);

        if (_my.Value is not { } snap)
        {
            return;
        }

        var premium = IsPremium();

        // Four topics, one card each: email first, because everything else depends on it
        view.Column(["gap-4"], content: col =>
        {
            RenderEmailConnections(col, snap, premium);

            // Lookback and AI are about reading email, which the free plan doesn't use without Gmail
            if (premium || snap.Connection != null)
            {
                RenderScanningCard(col, premium);
            }

            RenderPlanCard(col);
            RenderPrivacyCard(col);
        });

        var confirm = _myConfirm.Value;

        view.AlertDialog(open: confirm == "new-forward", onOpenChange: async open => { if (!open) _myConfirm.Value = null; },
            title: "Get a new forwarding address?",
            description: "Email sent to your current address will no longer reach Omabilo. Update any forwarding rules in your email to the new address. Bills already found stay.",
            cancelLabel: "Cancel", actionLabel: "Get a new address", actionStyle: [Button.PrimaryMd],
            onAction: async () =>
            {
                _myConfirm.Value = null;
                await RunMyAsync(async uid => { await Engine.NewForwardAddressAsync(uid, CancellationToken.None); return "You have a new forwarding address. The old one no longer works."; });
            });

        var leaving = snap.Connections.FirstOrDefault(a => a.AccountId == _disconnectAccount.Value);
        view.AlertDialog(open: confirm == "disconnect" && leaving != null, onOpenChange: async open => { if (!open) _myConfirm.Value = null; },
            title: $"Disconnect {leaving?.GoogleEmail ?? "Gmail"}?",
            description: "Omabilo stops reading this mailbox and asks Google to remove its access to it. Your other connected accounts keep syncing. " +
                "Records already found stay until you delete them. You can check or remove Omabilo's access yourself at any time at myaccount.google.com/permissions.",
            cancelLabel: "Cancel", actionLabel: "Disconnect", actionStyle: [Button.ErrorMd],
            onAction: async () =>
            {
                _myConfirm.Value = null;

                if (leaving is { } account)
                {
                    await RunMyAsync(async uid =>
                    {
                        await Engine.DisconnectAsync(uid, account.AccountId, CancellationToken.None);
                        return $"{account.GoogleEmail ?? "Gmail"} is disconnected.";
                    });
                }
            });

        view.AlertDialog(open: confirm == "delete-all", onOpenChange: async open => { if (!open) _myConfirm.Value = null; },
            title: "Delete all your Omabilo data?",
            description: "This disconnects Gmail and permanently deletes every record, setting, correction and activity entry Omabilo keeps for you, plus any test feedback you sent. Your emails stay in Gmail. This can't be undone.",
            cancelLabel: "Cancel", actionLabel: "Delete everything", actionStyle: [Button.ErrorMd],
            onAction: async () =>
            {
                _myConfirm.Value = null;
                _export.Value = null;
                await RunMyAsync(async uid =>
                {
                    await Companion.DeleteAllDataAsync(uid, CancellationToken.None);
                    await DeleteFeedbackForAsync(uid);
                    return "All your Omabilo data is deleted.";
                });
            });

        view.AlertDialog(open: confirm == "delete-account", onOpenChange: async open => { if (!open) _myConfirm.Value = null; },
            title: "Delete your account?",
            description: "Deleting happens now: every record, setting and correction Omabilo keeps for you is deleted, Gmail is disconnected, " +
                "a Premium subscription ends with no further charge, and you're signed out. Your emails stay in Gmail. This can't be undone.",
            cancelLabel: _removalDate.Value != null ? "Not now" : "Keep my account", actionLabel: "Delete now", actionStyle: [Button.ErrorMd],
            onAction: DeleteAccountNowAsync);
    }

    private readonly ClientReactive<byte[]?> _export = new((byte[]?)null);

    /// <summary>The account the disconnect dialog is asking about.</summary>
    private readonly ClientReactive<string?> _disconnectAccount = new((string?)null);

    /// <summary>The mailbox whose extra actions (scan again, reconnect, disconnect) are showing.</summary>
    private readonly ClientReactive<string?> _mailboxMenu = new((string?)null);

    private readonly ClientReactive<bool> _showForward = new(false);
    private readonly ClientReactive<bool> _showAiDetails = new(false);
    private readonly ClientReactive<bool> _showKept = new(false);

    /// <summary>One Settings topic: an icon, a heading and a line under it, then its content.</summary>
    private static void SettingsCard(UIView view, string key, string icon, string title, string subtitle, Action<UIView> body, Action<UIView>? aside = null)
    {
        view.Box([Card.Default, "p-4 sm:p-6 flex flex-col gap-4"], key: key, content: card =>
        {
            card.Row(["items-start gap-3 sm:gap-4 flex-wrap"], content: head =>
            {
                IconTile(head, icon, "brand", "w-11 h-11");
                head.Column(["gap-0.5 min-w-0 flex-1 basis-56"], content: t =>
                {
                    t.Heading(title, style: [Text.H5, "m-0 text-foreground"], level: 2);
                    t.Text([Text.BodySm, "text-muted-foreground"], subtitle);
                });
                aside?.Invoke(head);
            });
            body(card);
        });
    }

    /// <summary>A plain list row in a Settings card: icon, label, explanation and its control.</summary>
    private static void SettingsRow(UIView view, string icon, string label, string detail, Action<UIView>? control = null, bool danger = false)
    {
        view.Row(["items-center gap-3 py-3 flex-wrap"], content: r =>
        {
            r.Icon([Icon.Md, danger ? "text-error-600 shrink-0" : "text-muted-foreground shrink-0"], name: icon);
            r.Column(["gap-0 min-w-0 flex-1 basis-48"], content: t =>
            {
                t.Text([Text.Label, danger ? "text-error-700" : "text-foreground"], label);
                t.Text([Text.Caption, "text-muted-foreground"], detail);
            });
            control?.Invoke(r);
        });
    }

    // ── Settings: email connections ──────────────────────────────────

    private void RenderEmailConnections(UIView view, CompanionSnapshot snap, bool premium)
    {
        var missing = Engine.MissingConfiguration();
        var count = snap.Connections.Count;
        var full = count >= Mailboxes.MaxPerUser;
        var waiting = _awaitingGmail.Value;
        var canConnect = missing.Count == 0 && !full && GmailAvailable() && !_myBusy.Value && !waiting;

        SettingsCard(view, "settings-email", "mail", "Email connections",
            "Connect one or more Gmail accounts. Financial messages from all of them arrive in your one Financial Inbox.",
            aside: a => a.Row(["items-center gap-2 rounded-xl bg-muted px-3 py-2 basis-full lg:basis-auto lg:max-w-xs"], content: r =>
            {
                r.Icon([Icon.Sm, "text-brand-secondary shrink-0"], name: "shield-check");
                r.Text([Text.Caption, "text-muted-foreground"], "Read-only access. Omabilo only reads financial messages and never sends, deletes or changes your mail.");
            }),
            body: card =>
            {
                GmailTrialNote(card, snap);

                if (count > 0)
                {
                    card.Column(["rounded-xl border border-secondary divide-y divide-secondary overflow-hidden"], content: list =>
                    {
                        foreach (var c in snap.Connections)
                        {
                            GmailAccountRow(list, snap, c);
                        }
                    });
                }

                card.Box(["rounded-xl border border-dashed border-secondary bg-muted/40 p-4 flex flex-col sm:flex-row sm:items-center gap-3"], content: add =>
                {
                    add.Column(["gap-1 min-w-0 flex-1"], content: t =>
                    {
                        t.Text([Text.BodyStrong, "text-foreground"], count == 0 ? "Connect your Gmail" : "Connect another Gmail account");
                        t.Text([Text.BodySm, "text-muted-foreground"],
                            missing.Count > 0 ? "Gmail connection isn't set up on this server yet, so nothing can be connected. The app owner needs to finish it."
                            : full ? $"You have {Mailboxes.MaxPerUser} accounts connected, the most Omabilo reads at once. Disconnect one to add another."
                            : !GmailAvailable() ? $"Your free Gmail trial has ended. Connecting Gmail is part of Premium ({Membership.PremiumPrice})."
                            : waiting ? "Finish on Google's page and choose the account to add. Omabilo reads only the account you pick there."
                            : count == 0 ? "Google asks which account to use and shows exactly what you allow. Omabilo never asks for your Google password."
                            : "Add your work, university or other Gmail. Google asks which account to use; choosing one that's already here just refreshes it, never duplicates it.");

                        if (waiting && _connectUrl.Value is { } url)
                        {
                            t.Link(["text-brand-secondary underline text-sm self-start"], text: "Open Google's page", href: url);
                        }
                    });
                    add.Button([Button.PrimaryMd, "shrink-0 self-start sm:self-center"], text: waiting ? "Waiting for Google…" : count == 0 ? "Connect Gmail" : "Connect email",
                        icon: waiting ? "loader-circle" : "plus", disabled: !canConnect, onClick: ConnectGmailAsync);
                });

                if (missing.Count == 0 && SecretOrNull("GOOGLE_OAUTH_PUBLISHED") != "true")
                {
                    card.Text([Text.Caption, "text-muted-foreground"],
                        "If Google says \"Access blocked\", that Google account isn't on Omabilo's tester list yet while Google reviews the app. Ask the app owner to add it, then try again. Nothing changes in Omabilo when this happens.");
                }

                // Other providers can't be connected; forwarding is the honest way to bring their bills in
                if (premium)
                {
                    card.Row(["items-center gap-3 flex-wrap"], content: r =>
                    {
                        r.Text([Text.BodySm, "text-muted-foreground flex-1 basis-56"], "Use Outlook, iCloud or work email? Forward bills to your private Omabilo address instead.");
                        r.Button([Button.GhostSm], text: _showForward.Value ? "Hide address" : "Show address", icon: _showForward.Value ? "chevron-up" : "chevron-down",
                            onClick: () => _showForward.Value = !_showForward.Value);
                    });

                    if (_showForward.Value)
                    {
                        ForwardBillsCard(card, snap, compact: true);
                        card.Button([Button.OutlineSm, "self-start"], text: "Get a new address", icon: "refresh-cw", disabled: _myBusy.Value || snap.Forward == null,
                            onClick: () => _myConfirm.Value = "new-forward");
                    }
                }
            });
    }

    private static (string Dot, string State) AccountState(CompanionSnapshot snap, GmailConnection c) => c.Status switch
    {
        ConnectionStatus.NeedsReconnect => ("bg-error-500", "Permission expired"),
        ConnectionStatus.Error => ("bg-warning-500", "Last sync failed"),
        _ when c.LastError == SyncEngine.RateLimitNote => ("bg-warning-500", "Paused briefly by Gmail"),
        _ when !c.InitialScanDone => ("bg-info-500", "First scan running"),
        _ => ("bg-success-500", c.LastSuccessUtc is { } t ? $"Synced {Ago(t)}" : "Connected"),
    };

    /// <summary>One connected Gmail account with its own status and controls.</summary>
    private void GmailAccountRow(UIView view, CompanionSnapshot snap, GmailConnection c)
    {
        var (dot, state) = AccountState(snap, c);
        state = dot == "bg-success-500" ? "Connected" : state;
        var email = c.GoogleEmail ?? "Gmail account";
        var signIn = _me.Value.Email is { } me && string.Equals(me, c.GoogleEmail, StringComparison.OrdinalIgnoreCase);
        var reconnect = c.Status == ConnectionStatus.NeedsReconnect;
        var menuOpen = _mailboxMenu.Value == c.AccountId;

        view.Column(["gap-3 p-3 sm:p-4 bg-card"], key: $"gmail-{c.AccountId}", content: row =>
        {
            row.Row(["items-center gap-3 flex-wrap"], content: r =>
            {
                r.Box(["w-10 h-10 rounded-full bg-muted flex items-center justify-center shrink-0"], content: b => b.Icon([Icon.Sm, "text-foreground"], name: "mail"));
                r.Column(["gap-0.5 min-w-0 flex-1 basis-48"], content: who =>
                {
                    who.Row(["items-center gap-2 min-w-0 flex-wrap"], content: n =>
                    {
                        n.Text([Text.BodyStrong, "text-foreground truncate min-w-0"], email);

                        if (signIn)
                        {
                            n.Badge("Sign-in address", tone: SemanticTone.Success, size: BadgeSize.Sm);
                        }
                    });
                    who.Text([Text.Caption, "text-muted-foreground"], "Google Gmail · read-only");
                });
                r.Column(["gap-0.5 min-w-0 flex-1 basis-40"], content: st =>
                {
                    st.Row(["items-center gap-2"], content: s =>
                    {
                        s.Box([$"w-2.5 h-2.5 rounded-full shrink-0 {dot}"]);
                        s.Text([Text.Label, "text-foreground"], state);
                    });
                    st.Text([Text.Caption, "text-muted-foreground"], c.LastSuccessUtc is { } t
                        ? $"Last sync: {Day(t)}, {Ago(t)}" + (c.Status == ConnectionStatus.Connected && c.InitialScanDone ? (snap.PushActive(c, DateTime.UtcNow) ? " · new mail within a minute" : " · checked every few minutes") : "")
                        : "Not synced yet");
                });
                r.Row(["items-center gap-1 shrink-0"], content: act =>
                {
                    if (reconnect)
                    {
                        act.Button([Button.PrimarySm], text: "Reconnect", ariaLabel: $"Reconnect {email}", disabled: _awaitingGmail.Value || !GmailAvailable(), onClick: ConnectGmailAsync);
                    }
                    else
                    {
                        act.Button([Button.OutlineSm], text: _mySyncing.Value ? "Syncing…" : "Sync now", ariaLabel: $"Sync now {email}", disabled: _mySyncing.Value,
                            onClick: () => SyncNow(accountId: c.AccountId));
                    }

                    act.Button([Button.GhostSm, "w-10 h-10 p-0 flex items-center justify-center"], ariaLabel: $"{(menuOpen ? "Hide" : "More")} actions for {email}",
                        onClick: () => _mailboxMenu.Value = menuOpen ? null : c.AccountId,
                        content: v => v.Icon([Icon.Sm], name: menuOpen ? "x" : "ellipsis-vertical"));
                });
            });

            // What went wrong, in words, with the way back
            var problem = c.Status switch
            {
                ConnectionStatus.NeedsReconnect => $"Google no longer lets Omabilo read this mailbox: the permission expired or was removed. Press Reconnect and choose {email} on Google's page. Records already found stay, and your other accounts keep syncing.",
                ConnectionStatus.Error => $"The last sync failed ({c.LastError}). Omabilo retries by itself; Sync now tries straight away.",
                _ when c.LastError == SyncEngine.RateLimitNote => "Gmail asked Omabilo to slow down. Syncing carries on by itself in a few minutes.",
                _ when !c.InitialScanDone => "Looking back over your chosen period. Bills appear in your inbox as they're found.",
                _ => null,
            };

            if (problem != null)
            {
                row.Alert(tone: reconnect ? SemanticTone.Warning : c.Status == ConnectionStatus.Error ? SemanticTone.Error : SemanticTone.Info, description: problem);
            }

            if (menuOpen)
            {
                row.Column(["gap-2 rounded-lg bg-muted/50 p-3"], content: m =>
                {
                    m.Row(["gap-2 flex-wrap"], content: b =>
                    {
                        b.Button([Button.OutlineSm], text: "Scan again from the start", ariaLabel: $"Scan again from the start {email}",
                            disabled: _mySyncing.Value || reconnect,
                            tooltip: "Looks through the whole scan window again; items you deleted may come back", onClick: () => SyncNow(rescan: true, accountId: c.AccountId));

                        if (!reconnect)
                        {
                            b.Button([Button.OutlineSm], text: "Reconnect", ariaLabel: $"Reconnect {email}", disabled: _awaitingGmail.Value || !GmailAvailable(), onClick: ConnectGmailAsync);
                        }

                        b.Button([Button.OutlineErrorSm], text: "Disconnect", ariaLabel: $"Disconnect {email}", onClick: () =>
                        {
                            _disconnectAccount.Value = c.AccountId;
                            _myConfirm.Value = "disconnect";
                        });
                    });
                    m.Text([Text.Caption, "text-muted-foreground"],
                        $"Connected {Day(c.ConnectedUtc)}. Disconnecting stops reading this mailbox only: you stay signed in, other accounts keep syncing and its records stay.");
                });
            }
        });
    }

    // ── Settings: scanning and AI ────────────────────────────────────

    private void RenderScanningCard(UIView view, bool premium)
    {
        SettingsCard(view, "settings-scan", "scan-search", "Financial scanning and AI", "Choose how Omabilo finds and explains your financial messages.", body: card =>
        {
            card.Column(["divide-y divide-secondary"], content: list =>
            {
                // The saved value stays a choice even when it isn't one of the usual periods
                var days = _sScanDays.Value.Trim();
                var options = new[] { "30", "60", "90", "180", "365" }.Append(days).Where(d => int.TryParse(d, out _)).Distinct()
                    .OrderBy(d => int.Parse(d, CultureInfo.InvariantCulture)).Select(d => new SelectOption(d, $"{d} days")).ToList();

                SettingsRow(list, "calendar-search", "First scan looks back", "How far back Omabilo checks a newly connected mailbox.",
                    control: r => r.Select(["w-36"], bind: _sScanDays, options: options, ariaLabel: "First scan looks back"));

                if (!premium)
                {
                    SettingsRow(list, "sparkles", "AI explanations", "Part of Premium. Every bill is still explained by Omabilo's own rules, without AI.");
                }
                else
                {
                    SettingsRow(list, "sparkles", "AI explanations", "Plain-English explanations of your bills, and reading photos or scanned bills. Off by default.",
                        control: r => r.Switch(bind: _sAi, ariaLabel: "Explain my financial emails with AI and read scanned bills"));
                    list.Column(["gap-2 py-3"], content: more =>
                    {
                        more.Button([Button.LinkSm, "self-start"], text: _showAiDetails.Value ? "Hide how AI is used" : "How AI is used and what's sent",
                            onClick: () => _showAiDetails.Value = !_showAiDetails.Value);

                        if (_showAiDetails.Value)
                        {
                            more.AiDisclosure(AiDisclosureKind.Interaction, text:
                                "When this is on, Omabilo sends a shortened copy of each financial email (account numbers, identity codes, card numbers, email addresses and phone numbers removed) to an AI model, Claude Haiku, through the IKON platform with processing pinned to the EU. It writes plain-English explanations only. Scanned PDFs and photos of bills, which have no text Omabilo can read itself, are sent whole to an OCR service (Azure Document Intelligence, also through IKON in the EU) to turn them into text. Amounts, dates and statuses always come from Omabilo's own rules.");
                            more.Text([Text.Caption, "text-muted-foreground"], "Turning it off stops new AI processing; explanations already made stay until you delete the items.");
                        }
                    });
                }
            });

            if (_sMessage.Value is { } m)
            {
                card.Alert(tone: SemanticTone.Error, description: m);
            }

            card.Button([Button.PrimaryMd, "self-start"], text: _myBusy.Value ? "Saving…" : "Save settings", disabled: _myBusy.Value, onClick: SaveSettingsAsync);
        });
    }

    // ── Settings: privacy and account ────────────────────────────────

    private void RenderPrivacyCard(UIView view)
    {
        SettingsCard(view, "settings-privacy", "shield-check", "Privacy and account", "Your data, your profile and your account.", body: card =>
        {
            if (_removalDate.Value != null)
            {
                card.Text([Text.Body, "text-foreground"], "You asked to delete your account. Delete it now, or keep it.");
                RemovalChoice(card);
            }

            card.Column(["divide-y divide-secondary"], content: list =>
            {
                SettingsRow(list, "user-round", "Profile picture and cover", "Your photo beside your name and your own picture on Home.",
                    control: r => r.Button([Button.OutlineSm], text: "Edit profile", onClick: () => Go("my-profile")));

                SettingsRow(list, "download", "Export my data", "A copy of everything Omabilo keeps for you, as a JSON file.", control: r =>
                {
                    if (_export.Value is { Length: > 0 } data)
                    {
                        r.ActionButton([Button.PrimarySm], action: ActionKind.DownloadFile,
                            options: new DownloadFileActionOptions { FileName = $"omabilo-my-data-{DateTime.UtcNow:yyyy-MM-dd}.json", MimeType = "application/json", Data = data },
                            ariaLabel: "Download my data",
                            content: v => v.Row(["items-center gap-2"], content: inner => { inner.Icon([Icon.Sm], name: "download"); inner.Text([], "Download my data"); }));
                    }
                    else
                    {
                        r.Button([Button.OutlineSm], text: "Prepare export", disabled: _myBusy.Value, onClick: async () => await RunMyAsync(async uid =>
                        {
                            _export.Value = await Companion.ExportAsync(uid, CancellationToken.None);
                            return "Your export is ready to download.";
                        }));
                    }
                });

                SettingsRow(list, "log-out", "Sign out", "Sign out on this device. Your connected email keeps syncing.",
                    control: r => r.Button([Button.OutlineSm], text: "Sign out", onClick: async () => await ClientFunctions.LogoutAsync()));

                SettingsRow(list, "trash-2", "Delete my Omabilo data", "Disconnects email and deletes every record. You keep your account.", danger: true,
                    control: r => r.Button([Button.OutlineErrorSm], text: "Delete data…", onClick: () => _myConfirm.Value = "delete-all"));

                if (_removalDate.Value == null)
                {
                    SettingsRow(list, "user-x", "Delete my account", "Deletes the account and all its data straight away, and signs you out.", danger: true,
                        control: r => r.Button([Button.OutlineErrorSm], text: "Delete account…", onClick: () => _myConfirm.Value = "delete-account"));
                }
            });

            card.Button([Button.LinkSm, "self-start"], text: _showKept.Value ? "Hide what Omabilo keeps" : "What Omabilo keeps", onClick: () => _showKept.Value = !_showKept.Value);

            if (_showKept.Value)
            {
                card.Column(["gap-2"], content: kept =>
                {
                    foreach (var line in new[]
                    {
                        "For each financial email: sender, subject, date, a short excerpt (at most 800 characters, card numbers and identity codes hidden), the details read from it and your corrections.",
                        "For unrelated email: only Gmail's message id, so it isn't downloaded again. Deleted after 180 days.",
                        "Google access: stored encrypted on the server, never in your browser. Removed when you disconnect.",
                        "Activity log: what happened (for example \"synced\" or \"corrected\"), never amounts or email text. Deleted after a year.",
                        "Nothing is sold or shared for advertising. Omabilo never pays, sends email or changes your bank for you.",
                    })
                    {
                        kept.Row(["items-start gap-2"], content: row =>
                        {
                            row.Icon([Icon.Sm, "text-muted-foreground mt-0.5 shrink-0"], name: "dot");
                            row.Text([Text.BodySm, "text-foreground/90"], line);
                        });
                    }
                });
            }
        });
    }

    // ── Profile ──────────────────────────────────────────────────────

    private void RenderMyProfile(UIView view)
    {
        if (MyUserId == null)
        {
            SignedOutCard(view);
            return;
        }

        LoadStates(view);

        if (_my.Value is not { } snap)
        {
            return;
        }

        view.Column(["gap-3 max-w-3xl"], content: col =>
        {
            SectionHeader(col, "Profile picture and cover", "Make Omabilo yours: your photo beside your name and your own picture on Home.");
            RenderProfileCard(col, snap);
            col.Button([Button.LinkSm, "self-start"], text: "Back to Settings", icon: "arrow-left", onClick: () => Go("my-settings"));
        });
    }
}
