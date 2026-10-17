// Screens for a signed-in person's own money: My money (overview, attention, cash flow), My inbox
// (records from Gmail with their sources, corrections and statuses) and Connections & privacy.
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

    /// <summary>The signed-in person's id, or null for a guest. Everything personal hangs off this.</summary>
    private string? MyUserId => app.CurrentClientContext is { IsAnonymous: false } ? ReactiveScope.UserIdOrNull : null;

    private static string Money(decimal v) => InvoiceExplainer.Money(v);

    private static string Day(DateTime d) => InvoiceExplainer.Day(d);

    private static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - utc;
        return s.TotalMinutes < 1 ? "just now" : s.TotalHours < 1 ? $"{(int)s.TotalMinutes} min ago" : s.TotalDays < 1 ? $"{(int)s.TotalHours} h ago" : $"{(int)s.TotalDays} days ago";
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    private async Task OnCompanionClientJoinedAsync(Context ctx)
    {
        if (ctx.IsSnapshot || ctx.IsAnonymous)
        {
            return;
        }

        _section.Value = "my-overview";
        await LoadMyAsync();

        // Back from Google's consent page while the first scan runs: keep the status fresh
        if (_my.Value?.Connection is { InitialScanDone: false, Status: not ConnectionStatus.NeedsReconnect })
        {
            _awaitingGmail.Value = true;
            WatchConnection();
        }

        try
        {
            if (app.GlobalState.ServerRunType != ServerRunType.Local)
            {
                _removalDate.Value = (await ClientFunctions.GetAccountRemovalAsync())?.ScheduledFor;
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
            return;
        }

        _myLoading.Value = true;
        _myError.Value = null;

        try
        {
            var snapshot = await Companion.LoadAsync(uid, CancellationToken.None);
            _my.Value = snapshot;
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

                    if (snapshot.Connection is { InitialScanDone: true } or { Status: ConnectionStatus.NeedsReconnect })
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

    private async Task SignInAsync() => await ClientFunctions.LoginShowAsync("Sign in to use Omabilo with your own email");

    private async Task ConnectGmailAsync()
    {
        if (MyUserId is not { } uid)
        {
            await SignInAsync();
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

    private void SyncNow(bool rescan = false)
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
                var result = await Engine.SyncAsync(uid, CancellationToken.None, rescan);
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
            Note: _eNote.Value.Trim() != (item.UserNote ?? "") ? _eNote.Value : null);

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
        _toasts.Success("Correction saved. Omabilo keeps it even when the message is read again.");
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
            AiEnabled = _sAi.Value,
            AiConsentUtc = _sAi.Value ? snap.Settings.AiConsentUtc ?? DateTime.UtcNow : null,
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

    private async Task RequestAccountRemovalAsync()
    {
        if (app.GlobalState.ServerRunType == ServerRunType.Local)
        {
            _toasts.Error("Account deletion is off in a local test run.");
            return;
        }

        var removal = await ClientFunctions.RequestAccountRemovalAsync();

        if (removal is not { Succeeded: true })
        {
            _toasts.Error("Couldn't schedule the deletion. Sign in and try again.");
            return;
        }

        _removalDate.Value = removal.ScheduledFor;
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

    private static void BasisBadge(UIView view, AmountBasis basis)
    {
        var (label, tone) = basis switch
        {
            AmountBasis.UserEntered => ("You entered", SemanticTone.Info),
            AmountBasis.FromEmail => ("From email", SemanticTone.Success),
            AmountBasis.Estimate => ("Estimate", SemanticTone.Warning),
            _ => ("Calculated", SemanticTone.Neutral),
        };
        view.Badge(label, tone: tone, size: BadgeSize.Sm);
    }

    private void SignedOutCard(UIView view)
    {
        view.Box([Card.Elevated, "p-6 sm:p-8 flex flex-col gap-4 max-w-2xl"], content: v =>
        {
            IconTile(v, "lock", "brand", "w-12 h-12");
            v.Heading("Use Omabilo with your own email", style: [Text.H4, "m-0 text-foreground"], level: 2);
            v.Text([Text.Body, "text-foreground/90"],
                "Sign in to connect your Gmail. Omabilo then finds your bills, payslips and receipts, explains them and plans your money until payday. Signing in doesn't give Omabilo access to your email; connecting Gmail is a separate step you choose.");
            v.Row(["gap-2 flex-wrap"], content: r =>
            {
                r.Button([Button.PrimaryMd], text: "Sign in", icon: "log-in", onClick: SignInAsync);
                r.Button([Button.OutlineMd], text: "Try the demo", onClick: () => Go("dashboard"));
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
        var c = snap.Connection!;

        view.Box([Card.Default, "p-4 flex flex-col sm:flex-row sm:items-center gap-3"], content: bar =>
        {
            bar.Row(["items-center gap-3 flex-1 min-w-0"], content: r =>
            {
                var (icon, tone) = c.Status switch
                {
                    ConnectionStatus.NeedsReconnect => ("mail-x", "error"),
                    ConnectionStatus.Error => ("mail-warning", "warning"),
                    _ => ("mail-check", "success"),
                };
                IconTile(r, icon, tone);
                r.Column(["gap-0.5 min-w-0"], content: col =>
                {
                    col.Text([Text.BodyStrong, "text-foreground truncate"], $"Gmail · {c.GoogleEmail}");
                    col.Text([Text.Caption, "text-muted-foreground"], _mySyncing.Value || (_awaitingGmail.Value && !c.InitialScanDone)
                        ? "Looking for financial messages…"
                        : c.Status switch
                        {
                            ConnectionStatus.NeedsReconnect => "Google no longer allows access. Connect again to continue.",
                            ConnectionStatus.Error => $"Last sync failed: {c.LastError}. Retrying automatically.",
                            _ when c.LastSuccessUtc is { } t => $"Last synced {Ago(t)} · {c.LastSyncScanned} new message{(c.LastSyncScanned == 1 ? "" : "s")} checked · syncs every 15 minutes",
                            _ => "First scan pending",
                        });
                });
            });

            bar.Row(["gap-2 flex-wrap"], content: r =>
            {
                if (c.Status == ConnectionStatus.NeedsReconnect)
                {
                    r.Button([Button.PrimarySm], text: "Reconnect Gmail", onClick: ConnectGmailAsync);
                }
                else
                {
                    r.Button([Button.OutlineSm], text: _mySyncing.Value ? "Syncing…" : "Sync now", icon: _mySyncing.Value ? "loader-circle" : "refresh-cw",
                        disabled: _mySyncing.Value, onClick: () => SyncNow());
                }
            });
        });

        if (_myNotice.Value is { } notice)
        {
            view.Alert(notice, tone: SemanticTone.Info, key: "my-notice", onDismiss: async () => _myNotice.Value = null);
        }
    }

    private void ConnectGmailCard(UIView view)
    {
        var missing = Engine.MissingConfiguration();

        view.Box([Card.Elevated, "p-6 sm:p-8 flex flex-col gap-4"], content: v =>
        {
            IconTile(v, "mail", "brand", "w-12 h-12");
            v.Heading("Connect your Gmail", style: [Text.H4, "m-0 text-foreground"], level: 2);
            v.Text([Text.Body, "text-foreground/90"],
                "Omabilo reads your email to find bills, payment reminders, payslips and receipts, and keeps them organised here. It only reads: it can't send, delete or change anything in your mailbox.");

            v.Column(["gap-2"], content: list =>
            {
                foreach (var line in new[]
                {
                    "Google shows you exactly what you allow. Omabilo never asks for your Google password.",
                    "Clearly unrelated mail isn't downloaded in full. For financial messages Omabilo keeps a short excerpt, not the whole email.",
                    "The first scan looks back 90 days (you can change this). New mail is checked every 15 minutes.",
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

            if (missing.Count > 0)
            {
                v.Alert("Gmail connection isn't set up on this server yet", tone: SemanticTone.Warning,
                    description: $"The app owner still needs to add: {string.Join("; ", missing)}. Nothing is connected until then.");
            }

            v.Row(["gap-2 flex-wrap items-center"], content: r =>
            {
                r.Button([Button.PrimaryLg], text: _awaitingGmail.Value ? "Waiting for Google…" : "Connect Gmail", icon: "mail",
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

        LoadStates(view);

        if (_my.Value is not { } snap)
        {
            return;
        }

        if (snap.Connection == null)
        {
            ConnectGmailCard(view);

            // Records kept from an earlier connection still count until the person deletes them
            if (snap.Items.Count > 0)
            {
                RenderOverviewTiles(view, snap);
                RenderAttention(view, snap);
                RenderSuggestions(view, snap);
            }

            RenderCashFlow(view, snap);

            if (snap.Items.Count > 0)
            {
                RenderMonthly(view, snap);
            }

            return;
        }

        ConnectionBar(view, snap);

        if (!snap.Connection.InitialScanDone && snap.Items.Count == 0)
        {
            view.EmptyState("Your first scan is running", description: "Omabilo is looking through your recent email. This usually takes a few minutes; you can close the page and come back.", icon: "mail-search");
            return;
        }

        if (snap.Items.Count == 0)
        {
            view.EmptyState("No financial messages found yet", description: "Omabilo didn't find bills, payslips or receipts in the period it scanned. New ones appear here automatically.", icon: "inbox");
            RenderCashFlow(view, snap);
            return;
        }

        RenderOverviewTiles(view, snap);
        RenderAttention(view, snap);
        RenderSuggestions(view, snap);
        RenderCashFlow(view, snap);
        RenderMonthly(view, snap);
    }

    private void RenderMonthly(UIView view, CompanionSnapshot snap)
    {
        var months = MonthlySummary.Build(snap.Items, DateTime.UtcNow.Date);
        var salaries = MonthlySummary.SalaryHistory(snap.Items);
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-GB");

        view.Column(["gap-3"], content: col =>
        {
            SectionHeader(col, "Month by month", "Totals from your records over the last six months.");

            col.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-4"], content: grid =>
            {
                grid.Box([Card.Elevated, "p-5 lg:col-span-3 flex flex-col gap-2"], content: card =>
                {
                    foreach (var m in months)
                    {
                        var label = new DateTime(m.Year, m.Month, 1).ToString("MMMM yyyy", en);

                        card.Column(["gap-1 py-2 border-b border-secondary last:border-b-0"], content: c =>
                        {
                            c.Text([Text.Label, "text-foreground"], label);
                            c.Text([Text.Caption, "text-muted-foreground tabular-nums"],
                                $"Bills due {Money(m.BillsDue)} ({m.Bills}), of which you marked paid {Money(m.BillsReportedPaid)} · " +
                                $"Receipts {Money(m.Receipts)} ({m.ReceiptCount}) · Refunds {Money(m.Refunds)}");
                            c.Text([Text.Caption, "text-muted-foreground tabular-nums"],
                                $"Net pay on payslips {Money(m.PayslipNet)} ({m.Payslips}) · Reported received {Money(m.IncomeReportedReceived)}" +
                                (m.ItemsWithoutAmount > 0 ? $" · {m.ItemsWithoutAmount} record{(m.ItemsWithoutAmount == 1 ? "" : "s")} without an amount" : ""));
                        });
                    }

                    card.Text([Text.Caption, "text-muted-foreground"], MonthlySummary.Explanation);
                });

                grid.Box([Card.Default, "p-5 lg:col-span-2 flex flex-col gap-2"], content: card =>
                {
                    card.Text([Text.Label, "text-foreground"], "Salary history");

                    if (salaries.Count == 0)
                    {
                        card.Text([Text.BodySm, "text-muted-foreground"], "No payslips or salary messages found yet.");
                    }

                    foreach (var s in salaries.Take(12))
                    {
                        card.Box(["flex flex-row items-start justify-between gap-3 py-1.5 border-b border-secondary last:border-b-0"],
                            onClick: () => { if (snap.Items.FirstOrDefault(i => i.Id == s.ItemId) is { } it) { _myFilter.Value = "income"; Go("my-inbox"); OpenItem(it); } }, ariaLabel: $"Open payslip {s.Period ?? (s.PayDate is { } pd ? Day(pd) : "")}", content: r =>
                        {
                            r.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                            {
                                c.Text([Text.BodySm, "text-foreground"], s.Period ?? (s.PayDate is { } d ? Day(d) : "Date unknown"));
                                c.Text([Text.Caption, "text-muted-foreground"], (s.Employer ?? "Employer unknown") + " · " + (s.Evidence == IncomeEvidence.ReportedReceived ? "you reported it received" : "stated on the payslip"));
                            });
                            r.Text([Text.BodyStrong, "tabular-nums text-foreground shrink-0"], s.Net is { } n ? Money(n) : "—");
                        });
                    }
                });
            });
        });
    }

    private void RenderOverviewTiles(UIView view, CompanionSnapshot snap)
    {
        var plan = snap.Plan;
        var today = DateTime.UtcNow.Date;
        var monthSpend = snap.Items.Where(i => i.Kind == FinKind.Receipt && i.Amount != null && i.PossibleDuplicateOf == null && i.UserState != UserState.NotApplicable
            && i.EventDate is { } d && d.Year == today.Year && d.Month == today.Month).Sum(i => i.Amount!.Value);

        view.Grid(["grid grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-4"], content: grid =>
        {
            StatTile(grid, "Your balance", snap.Settings.Balance is { } b ? Money(b) : "Not entered",
                snap.Settings.Balance != null ? plan.BalanceNote : "Omabilo can't see your bank. Add it in the plan below.", "wallet", snap.Settings.Balance != null ? "brand" : "neutral", null);
            StatTile(grid, "Next salary", snap.Income.ExpectedNet is { } net ? Money(net) : "Unknown",
                snap.Income.NextPayday is { } p ? $"{Day(p)} · {snap.Income.PaydayBasis}" : snap.Income.PaydayBasis, "banknote-arrow-down", "success", () => { _myFilter.Value = "income"; Go("my-inbox"); });
            StatTile(grid, "Bills in this period", Money(plan.ObligationsTotal), $"{plan.Obligations.Count} bill{(plan.Obligations.Count == 1 ? "" : "s")} · {plan.PeriodLabel}", "receipt",
                plan.Obligations.Any(o => o.Date < today) ? "error" : "warning", () => { _myFilter.Value = "bills"; Go("my-inbox"); });
            StatTile(grid, "Left until payday", plan.ProjectedRemainder is { } r ? Money(r) : "Add your balance",
                plan.ProjectedRemainder != null ? "Estimate: balance + income − bills − usual costs" : "Without your balance Omabilo doesn't guess", "piggy-bank",
                plan.Shortfall ? "error" : "success", null);
        });

        view.Text([Text.Caption, "text-muted-foreground"], $"Spending recorded from receipts this month: {Money(monthSpend)}. Receipts only show purchases that sent an email.");
    }

    private void RenderAttention(UIView view, CompanionSnapshot snap)
    {
        var today = DateTime.UtcNow.Date;
        var statuses = snap.Items.ToDictionary(i => i.Id, i => ItemRules.StatusOf(i, today));
        var since = snap.Connection?.PreviousSuccessUtc;

        var groups = new (string Title, string Icon, List<FinancialItem> Items)[]
        {
            ("Overdue", "circle-alert", snap.Items.Where(i => statuses[i.Id].Status == DisplayStatus.Overdue).ToList()),
            ("Due today", "alarm-clock", snap.Items.Where(i => i.IsObligation && i.DueDate!.Value.Date == today && ItemRules.CountsAsOpenObligation(i)).ToList()),
            ("Due in the next 7 days", "calendar-clock", snap.Items.Where(i => i.IsObligation && i.DueDate!.Value.Date > today && i.DueDate.Value.Date <= today.AddDays(7)
                && statuses[i.Id].Status is DisplayStatus.DueSoon or DisplayStatus.NeedsReview).ToList()),
            ("Needs verification", "shield-alert", snap.Items.Where(i => statuses[i.Id].NeedsReview).ToList()),
            ("Missing information", "circle-dashed", snap.Items.Where(i => i.Missing.Count > 0 && i.UserState is UserState.None).ToList()),
            ("New since the previous sync", "sparkles", since is { } s ? snap.Items.Where(i => i.CreatedUtc > s).ToList() : []),
        };

        view.Column(["gap-3"], content: col =>
        {
            SectionHeader(col, "What needs your attention", "Bills by due date, items Omabilo isn't sure about, and what's new.");

            if (groups.All(g => g.Items.Count == 0))
            {
                col.Box([Card.Default, "p-5 flex flex-row items-center gap-3"], content: b =>
                {
                    IconTile(b, "circle-check", "success");
                    b.Text([Text.Body, "text-foreground"], "Nothing needs your attention right now.");
                });
                return;
            }

            col.Grid(["grid grid-cols-1 lg:grid-cols-2 gap-3"], content: grid =>
            {
                foreach (var (title, icon, items) in groups.Where(g => g.Items.Count > 0))
                {
                    grid.Box([Card.Default, "p-4 flex flex-col gap-2"], key: $"att-{title}", content: card =>
                    {
                        card.Row(["items-center gap-2"], content: r =>
                        {
                            r.Icon([Icon.Sm, "text-muted-foreground"], name: icon);
                            r.Text([Text.Label, "text-foreground flex-1"], title);
                            r.Badge(items.Count.ToString(CultureInfo.InvariantCulture), tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                        });

                        foreach (var item in items.Take(4))
                        {
                            ItemRow(card, item, statuses[item.Id], compact: true);
                        }

                        if (items.Count > 4)
                        {
                            card.Button([Button.GhostSm, "self-start"], text: $"Show all {items.Count}", onClick: () => { _myFilter.Value = "attention"; Go("my-inbox"); });
                        }
                    });
                }
            });
        });
    }

    private void RenderSuggestions(UIView view, CompanionSnapshot snap)
    {
        if (snap.Suggestions.Count == 0)
        {
            return;
        }

        view.Column(["gap-3"], content: col =>
        {
            SectionHeader(col, "Suggested next steps", "Each suggestion says why it was made. Omabilo never pays, sends or changes anything for you.");

            foreach (var s in snap.Suggestions.Take(6))
            {
                col.Box([Card.Default, "p-4 flex flex-row gap-3 items-start"], key: $"sg-{s.Title}", content: row =>
                {
                    IconTile(row, s.Tone == "error" ? "circle-alert" : s.Tone == "warning" ? "triangle-alert" : "lightbulb", s.Tone == "error" ? "error" : s.Tone == "warning" ? "warning" : "info");
                    row.Column(["gap-1 flex-1 min-w-0"], content: c =>
                    {
                        c.Text([Text.BodyStrong, "text-foreground"], s.Title);
                        c.Text([Text.BodySm, "text-muted-foreground"], $"Why: {s.Why}");

                        if (s.ItemIds.Count == 1 && snap.Items.FirstOrDefault(i => i.Id == s.ItemIds[0]) is { } item)
                        {
                            c.Button([Button.GhostSm, "self-start px-0 text-brand-secondary"], text: "Open the item", onClick: () => { Go("my-inbox"); OpenItem(item); });
                        }
                    });
                });
            }
        });
    }

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

        if (snap.Connection == null && snap.Items.Count == 0)
        {
            ConnectGmailCard(view);
            return;
        }

        if (snap.Connection != null)
        {
            ConnectionBar(view, snap);
        }

        var today = DateTime.UtcNow.Date;
        var statuses = snap.Items.ToDictionary(i => i.Id, i => ItemRules.StatusOf(i, today));

        bool InFilter(FinancialItem i, string f) => f switch
        {
            "attention" => statuses[i.Id].NeedsReview || statuses[i.Id].Status is DisplayStatus.Overdue or DisplayStatus.DueSoon or DisplayStatus.NeedsReview,
            "bills" => i.IsObligation || i.Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection or FinKind.TaxNotice or FinKind.Subscription or FinKind.Insurance or FinKind.LoanInstalment,
            "income" => i.IsIncome,
            "spending" => i.IsExpense,
            "uncertain" => i.Flags.Count > 0 || i.Missing.Count > 0 || i.Suspicious || i.PossibleDuplicateOf != null || i.Confidence < ItemRules.ReviewConfidence,
            _ => true,
        };

        var filters = new[] { ("attention", "Needs attention"), ("bills", "Bills"), ("income", "Income"), ("spending", "Spending"), ("recurring", "Recurring"), ("uncertain", "Uncertain"), ("all", "All") };

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
                            var count = key == "recurring" ? snap.Recurring.Count : snap.Items.Count(i => InFilter(i, key));
                            group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5"], value: key, content: v =>
                            {
                                v.Text(text: label);
                                v.Text(["text-xs tabular-nums opacity-70"], text: count.ToString(CultureInfo.InvariantCulture));
                            });
                        }
                    });
                r.TextField([Input.DefaultSm, "w-full sm:w-56"], placeholder: "Search company or title", bind: _mySearch, ariaLabel: "Search my inbox");
            });

            if (_myFilter.Value == "recurring")
            {
                RenderRecurring(card, snap);
                return;
            }

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
                    c.Text([Text.Caption, "text-muted-foreground truncate"],
                        item.DueDate is { } due ? $"Due {Day(due)} · {KindName(item.Kind)}" : item.EventDate is { } ev ? $"{Day(ev)} · {KindName(item.Kind)}" : KindName(item.Kind));
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
                    RenderSources(col, item, snap.Connection?.GoogleEmail);

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

    private static void FieldLine(UIView view, string label, string? value, bool edited = false)
    {
        view.Row(["items-start gap-3 py-2 border-b border-secondary"], content: r =>
        {
            r.Text([Text.BodySm, "text-muted-foreground w-36 shrink-0"], label);
            r.Text([Text.BodyStrong, value == null ? "text-muted-foreground italic flex-1 min-w-0 break-words" : "text-foreground flex-1 min-w-0 break-words"], value ?? "Not stated");
            if (edited) r.Badge("Corrected by you", tone: SemanticTone.Info, size: BadgeSize.Sm);
        });
    }

    private void RenderItemFields(UIView view, FinancialItem i)
    {
        var e = i.EditedFields.ToHashSet();

        view.Column(["gap-0"], content: c =>
        {
            FieldLine(c, i.IsIncome ? "Employer" : i.IsExpense ? "Merchant" : "Company", i.Counterparty, e.Contains(nameof(FinancialItem.Counterparty)));

            if (i.IsIncome)
            {
                FieldLine(c, "Net salary", i.Net is { } n ? Money(n) : null, e.Contains(nameof(FinancialItem.Net)));
                FieldLine(c, "Gross salary", i.Gross is { } g ? Money(g) : null);
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

            FieldLine(c, "Amount", i.Amount is { } a ? (i.Currency == "EUR" ? Money(a) : $"{a:0.00} {i.Currency}") : null, e.Contains(nameof(FinancialItem.Amount)));

            if (i.IsExpense)
            {
                FieldLine(c, "Date", i.EventDate is { } d ? Day(d) : null, e.Contains(nameof(FinancialItem.EventDate)));
                FieldLine(c, "VAT", i.Vat is { } vat ? Money(vat) : null);
                FieldLine(c, "Order or receipt no.", i.OrderRef);
            }
            else
            {
                FieldLine(c, "What it's for", i.Purpose, e.Contains(nameof(FinancialItem.Purpose)));
                FieldLine(c, "Due date", i.DueDate is { } due ? Day(due) : null, e.Contains(nameof(FinancialItem.DueDate)));
                if (i.OriginalDueDate is { } od) FieldLine(c, "Original due date", Day(od));
                FieldLine(c, "Invoice number", i.InvoiceNumber);
                FieldLine(c, "Reference", i.Reference, e.Contains(nameof(FinancialItem.Reference)));
                FieldLine(c, "Invoice date", i.InvoiceDate is { } idt ? Day(idt) : null);
                FieldLine(c, "Billing period", i.Period);
                FieldLine(c, "Payment details", i.PaymentInstructions);
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

    private static void RenderSources(UIView view, FinancialItem item, string? gmailAccount)
    {
        view.Column(["gap-2"], content: c =>
        {
            c.Text([Text.Label, "text-foreground"], item.Sources.Count == 1 ? "Source email" : $"Source emails ({item.Sources.Count})");

            foreach (var s in item.Sources)
            {
                c.Box(["rounded-xl bg-muted p-3 flex flex-col gap-1"], key: $"src-{s.GmailId}", content: b =>
                {
                    b.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
                    {
                        r.Text([Text.BodySm, "text-foreground font-semibold truncate"], s.Subject.Length > 0 ? s.Subject : "(no subject)");
                        r.Badge(s.Role, tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                    });
                    b.Text([Text.Caption, "text-muted-foreground"], $"{s.From} · {Day(s.ReceivedUtc)}");
                    b.Text(["text-xs font-mono whitespace-pre-wrap break-words text-foreground/80"], s.Excerpt);

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
                }

                if (item.UserState != UserState.NotApplicable) r.Button([Button.OutlineSm], text: "Not applicable", disabled: _myBusy.Value, onClick: async () => await Set(UserState.NotApplicable, "Marked as not applicable."));
                if (item.UserState != UserState.None) r.Button([Button.GhostSm], text: "Undo my decision", disabled: _myBusy.Value, onClick: async () => await Set(UserState.None, "Decision cleared."));
            });

            c.Row(["gap-2 flex-wrap"], content: r =>
            {
                var connected = snap.Connection is { Status: not ConnectionStatus.NeedsReconnect };
                r.Button([Button.GhostSm], text: "Read the email again", icon: "rotate-ccw", disabled: !connected || _myBusy.Value,
                    tooltip: connected ? "Re-reads the source email with the current rules, keeping your corrections" : "Connect Gmail to re-read the email",
                    onClick: async () => await RunMyAsync(async uid => await Engine.ReprocessAsync(uid, item.Id, CancellationToken.None) != null ? "Read again." : "Couldn't read it again."));

                if (snap.Settings.AiEnabled && item.Ai == null)
                {
                    r.Button([Button.GhostSm], text: "Explain with AI", icon: "sparkles", disabled: !connected || _myBusy.Value,
                        onClick: async () => await RunMyAsync(async uid =>
                        {
                            var updated = await Engine.ReprocessAsync(uid, item.Id, CancellationToken.None);
                            return updated?.Ai != null ? "AI explanation added." : "The AI explanation isn't available right now. The rule-based result is unchanged.";
                        }));
                }

                r.Button([Button.OutlineErrorSm], text: "Delete", icon: "trash-2", onClick: () => _myConfirm.Value = $"delete-item:{item.Id}");
            });
        });
    }

    // ── Connections & privacy ───────────────────────────────────────────────

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

        view.Column(["gap-3"], content: col =>
        {
            SectionHeader(col, "Gmail", "Omabilo reads email only with your permission, and only to find financial messages.");

            if (snap.Connection is not { } c)
            {
                ConnectGmailCard(col);
            }
            else
            {
                col.Box([Card.Default, "p-5 flex flex-col gap-3"], content: card =>
                {
                    FieldLine(card, "Account", c.GoogleEmail);
                    FieldLine(card, "Status", c.Status switch
                    {
                        ConnectionStatus.NeedsReconnect => "Needs reconnecting: Google no longer allows access",
                        ConnectionStatus.Error => $"Last sync failed ({c.LastError}). Retrying automatically.",
                        _ => c.InitialScanDone ? "Connected and syncing every 15 minutes" : "Connected; first scan running",
                    });
                    FieldLine(card, "Permission", "Read-only Gmail access (gmail.readonly). Omabilo can't send, delete or change mail.");
                    FieldLine(card, "Connected", Day(c.ConnectedUtc));
                    FieldLine(card, "Last successful sync", c.LastSuccessUtc is { } t ? $"{Day(t)} ({Ago(t)})" : null);

                    card.Row(["gap-2 flex-wrap pt-1"], content: r =>
                    {
                        if (c.Status == ConnectionStatus.NeedsReconnect) r.Button([Button.PrimarySm], text: "Reconnect Gmail", onClick: ConnectGmailAsync);
                        else r.Button([Button.OutlineSm], text: _mySyncing.Value ? "Syncing…" : "Sync now", disabled: _mySyncing.Value, onClick: () => SyncNow());
                        r.Button([Button.OutlineSm], text: "Scan again from the start", disabled: _mySyncing.Value || c.Status == ConnectionStatus.NeedsReconnect,
                            tooltip: "Looks through the whole scan window again; items you deleted may come back", onClick: () => SyncNow(rescan: true));
                        r.Button([Button.OutlineErrorSm], text: "Disconnect Gmail", onClick: () => _myConfirm.Value = "disconnect");
                    });
                });
            }

            SectionHeader(col, "Settings");
            col.Box([Card.Default, "p-5 flex flex-col gap-4"], content: card =>
            {
                card.TextField(label: "First scan looks back (days)", bind: _sScanDays, type: "number", min: "7", step: "1");
                card.Column(["gap-2"], content: ai =>
                {
                    ai.Switch(bind: _sAi, label: "Explain my financial emails with AI");
                    ai.AiDisclosure(AiDisclosureKind.Interaction, text:
                        "When this is on, Omabilo sends a shortened copy of each financial email (account numbers, identity codes, card numbers, email addresses and phone numbers removed) to an AI model, Claude Haiku, through the IKON platform with processing pinned to the EU. It writes plain-English explanations only. Amounts, dates and statuses always come from Omabilo's own rules.");
                    ai.Text([Text.Caption, "text-muted-foreground"], "Off by default. Turning it off stops new AI processing; explanations already made stay until you delete the items.");
                });

                if (_sMessage.Value is { } m)
                {
                    card.Alert(tone: SemanticTone.Error, description: m);
                }

                card.Button([Button.PrimaryMd, "self-start"], text: _myBusy.Value ? "Saving…" : "Save settings", disabled: _myBusy.Value, onClick: SaveSettingsAsync);
            });

            SectionHeader(col, "Your data", "What Omabilo keeps, and your controls over it.");
            col.Box([Card.Default, "p-5 flex flex-col gap-3"], content: card =>
            {
                foreach (var line in new[]
                {
                    "Kept for each financial email: sender, subject, date, a short excerpt (at most 800 characters, card numbers and identity codes hidden), the details read from it and your corrections.",
                    "Kept for unrelated email: only Gmail's message id, so it isn't downloaded again. Deleted after 180 days.",
                    "Google access: stored encrypted on the server, never in your browser. Removed when you disconnect.",
                    "Activity log: what happened (for example \"synced\" or \"corrected\"), never amounts or email text. Deleted after a year.",
                    "Nothing is sold or shared for advertising. Omabilo never pays, sends email or changes your bank for you.",
                })
                {
                    card.Row(["items-start gap-2"], content: row =>
                    {
                        row.Icon([Icon.Sm, "text-muted-foreground mt-0.5 shrink-0"], name: "dot");
                        row.Text([Text.BodySm, "text-foreground/90"], line);
                    });
                }

                card.Row(["gap-2 flex-wrap pt-2"], content: r =>
                {
                    r.Button([Button.OutlineSm], text: "Prepare export", icon: "download", disabled: _myBusy.Value, onClick: async () => await RunMyAsync(async uid =>
                    {
                        _export.Value = await Companion.ExportAsync(uid, CancellationToken.None);
                        return "Your export is ready to download.";
                    }));

                    if (_export.Value is { Length: > 0 } data)
                    {
                        r.ActionButton([Button.PrimarySm], action: ActionKind.DownloadFile,
                            options: new DownloadFileActionOptions { FileName = $"omabilo-my-data-{DateTime.UtcNow:yyyy-MM-dd}.json", MimeType = "application/json", Data = data },
                            ariaLabel: "Download my data",
                            content: v => v.Row(["items-center gap-2"], content: inner => { inner.Icon([Icon.Sm], name: "download"); inner.Text([], "Download my data"); }));
                    }

                    r.Button([Button.OutlineErrorSm], text: "Delete all my Omabilo data", onClick: () => _myConfirm.Value = "delete-all");
                });
            });

            SectionHeader(col, "Account");
            col.Box([Card.Default, "p-5 flex flex-col gap-3"], content: card =>
            {
                if (_removalDate.Value is { } date)
                {
                    card.Text([Text.Body, "text-foreground"], $"Your account and everything in it will be deleted on {date:d MMMM yyyy}. You can change your mind until then.");
                    card.Button([Button.OutlineMd, "self-start"], text: "Keep my account", onClick: async () =>
                    {
                        if (await ClientFunctions.CancelAccountRemovalAsync() is { Succeeded: true }) _removalDate.Value = null;
                    });
                }
                else
                {
                    card.Text([Text.BodySm, "text-muted-foreground"], "Signs you out of Omabilo, or deletes your account and all its data after a grace period you can cancel.");
                    card.Row(["gap-2 flex-wrap"], content: r =>
                    {
                        r.Button([Button.OutlineSm], text: "Sign out", icon: "log-out", onClick: async () => await ClientFunctions.LogoutAsync());
                        r.Button([Button.OutlineErrorSm], text: "Delete my account…", onClick: () => _myConfirm.Value = "delete-account");
                    });
                }
            });
        });

        var confirm = _myConfirm.Value;

        view.AlertDialog(open: confirm == "disconnect", onOpenChange: async open => { if (!open) _myConfirm.Value = null; },
            title: "Disconnect Gmail?",
            description: "Omabilo stops reading your email and asks Google to remove its access. Records already found stay until you delete them.",
            cancelLabel: "Cancel", actionLabel: "Disconnect", actionStyle: [Button.ErrorMd],
            onAction: async () =>
            {
                _myConfirm.Value = null;
                await RunMyAsync(async uid => { await Engine.DisconnectAsync(uid, CancellationToken.None); return "Gmail disconnected."; });
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
            description: "Your account and all your Omabilo data will be deleted after a grace period, and you can cancel until then.",
            cancelLabel: "Keep it", actionLabel: "Delete my account", actionStyle: [Button.ErrorMd],
            onAction: async () =>
            {
                _myConfirm.Value = null;
                await RequestAccountRemovalAsync();
            });
    }

    private readonly ClientReactive<byte[]?> _export = new((byte[]?)null);
}
