return await App.Run(args);

public record SessionIdentity(string? UserId);
public record ClientParameters(string Name = "Ikon");

// Visual direction: a calm, trustworthy money companion. Restrained teal on a warm off-white
// background with deep navy text, white rounded-2xl cards with gentle depth, a clear numeric type scale and
// comfortable density; a deep slate scheme for dark mode.
[App]
public partial class OmabiloApp(IApp<SessionIdentity, ClientParameters> app)
{
    private UI UI { get; } = new(app, new IkonTheme
    {
        ["primary"] = "teal-700",
        ["background"] = "#faf7f2",
        ["foreground"] = "#14213d",
        ["card"] = "white",
        ["muted-foreground"] = "#5d677a",
        ["border"] = "#e7e0d5",
        ["radius"] = "rounded-2xl",

        DarkMode = new IkonTheme
        {
            ["primary"] = "teal-400",
            ["background"] = "#0b1416",
            ["foreground"] = "slate-100",
            ["card"] = "#121e21",
            ["muted-foreground"] = "slate-400",
            ["border"] = "#22323a",
        },
    });

    private record Section(string Key, string Label, string IconName);

    private static readonly Section[] Sections =
    [
        new("dashboard", "Dashboard", "layout-dashboard"),
        new("inbox", "Financial Inbox", "inbox"),
        new("invoices", "Invoices", "file-text"),
        new("debts", "Debt Tracker", "trending-down"),
        new("payday", "Payday Planner", "calendar-clock"),
        new("goals", "Savings & Goals", "piggy-bank"),
        new("reports", "Monthly Reports", "chart-pie"),
    ];

    // ── Each person's money data, kept across restarts ──────────────────────
    private static readonly List<Txn> SeedTxns = DemoData.Transactions();

    private readonly PersistentSessionReactiveList<Txn> _txns = new(SeedTxns, key: "omabilo.txns.v1");
    private readonly PersistentSessionReactiveList<InboxItem> _inbox = new(DemoData.Inbox(), key: "omabilo.inbox.v1");
    private readonly PersistentSessionReactiveList<DebtAccount> _debts = new(DemoData.Debts(), key: "omabilo.debts.v1");
    private readonly PersistentSessionReactiveList<SavingsGoal> _goals = new(DemoData.Goals(), key: "omabilo.goals.v1");
    private readonly PersistentSessionReactiveList<PayBucket> _buckets = new(DemoData.Buckets(), key: "omabilo.buckets.v1");
    private readonly PersistentSessionReactive<PaydaySettings> _payday = new(DemoData.Payday(SeedTxns), key: "omabilo.payday.v1");
    private readonly PersistentSessionReactive<string> _strategy = new("avalanche", key: "omabilo.strategy.v1");
    private readonly PersistentSessionReactive<double> _extraPayment = new(100, key: "omabilo.extra.v1");

    // ── View state, per browser tab ─────────────────────────────────────────
    private readonly ClientReactive<string> _section = new("dashboard");
    private readonly ClientReactive<UndoEntry?> _undo = new((UndoEntry?)null);
    private readonly Toasts _toasts = new();
    private long _undoSeq;

    // One dialog open at a time, named by what it edits ("pay-debt:<id>", "add-goal", …)
    private readonly ClientReactive<string?> _dialog = new((string?)null);
    private readonly ClientReactive<string> _fName = new("");
    private readonly ClientReactive<string> _fSecondary = new("");
    private readonly ClientReactive<string> _fAmount = new("");
    private readonly ClientReactive<string> _fAmount2 = new("");
    private readonly ClientReactive<string> _fRate = new("");
    private readonly ClientReactive<string> _fDay = new("1");
    private readonly ClientReactive<string> _fKind = new("CreditCard");
    private readonly ClientReactive<string> _fIcon = new("piggy-bank");
    private readonly ClientReactive<string> _fDate = new("");
    private readonly ClientReactive<string?> _fError = new((string?)null);

    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task Main()
    {
        UI.Root([Page.Default, "h-screen overflow-hidden bg-background"], content: view =>
        {
            view.Row(["h-full w-full min-h-0"], content: view =>
            {
                RenderSidebar(view);

                view.Column(["flex-1 min-w-0 min-h-0 h-full"], content: view =>
                {
                    RenderTopBar(view);

                    view.ScrollArea(rootStyle: ["flex-1 min-h-0"], key: _section.Value, content: view =>
                    {
                        view.Column(["mx-auto w-full max-w-6xl px-4 sm:px-6 lg:px-8 pt-2 pb-28 md:pb-12 gap-6",
                            "motion-[0:opacity-0_translate-y-[6px],100:opacity-100_translate-y-0] motion-duration-300ms"], content: view =>
                        {
                            switch (_section.Value)
                            {
                                case "inbox": view.Feature("inbox", content: RenderInboxScreen); break;
                                case "invoices": view.Feature("invoices", content: RenderInvoices); break;
                                case "debts": view.Feature("debts", content: RenderDebts); break;
                                case "payday": view.Feature("payday", content: RenderPayday); break;
                                case "goals": view.Feature("goals", content: RenderGoals); break;
                                case "reports": view.Feature("reports", content: RenderReports); break;
                                default: view.Feature("dashboard", content: RenderDashboard); break;
                            }
                        });
                    });
                });
            });

            RenderMobileNav(view);
            RenderDialogs(view);
            RenderUndoToast(view);
            view.ToastHost(_toasts);
        });
    }

    // ── Shell ───────────────────────────────────────────────────────────────

    private int AttentionCount() => _inbox.Count(i => EffectiveStatus(i) == InboxStatus.New);

    private void Go(string section) => _section.Value = section;

    private void RenderSidebar(UIView view)
    {
        view.Column(["hidden md:flex w-64 shrink-0 h-full flex-col border-r border-secondary bg-card px-4 py-6 gap-6"], props: new Dictionary<string, object> { ["role"] = "navigation" }, ariaLabel: "Main", content: view =>
        {
            view.Row(["items-center gap-3 px-2"], content: view =>
            {
                view.Box(["w-10 h-10 rounded-xl bg-brand-solid flex items-center justify-center shadow-sm"], content: b =>
                    b.Icon([Icon.Md, "text-primary-on-brand"], name: "sprout"));
                view.Column(["gap-0"], content: view =>
                {
                    view.Text([Text.H5, "leading-tight"], "Omabilo");
                    view.Text([Text.Caption, "text-muted-foreground"], "Personal");
                });
            });

            view.Column(["gap-1"], content: view =>
            {
                foreach (var s in Sections)
                {
                    var active = _section.Value == s.Key;
                    var count = s.Key switch { "inbox" => AttentionCount(), "invoices" => InvoicesToReview(), _ => 0 };

                    view.Button([NavItem.Md, active ? NavItem.ActiveBrand : NavItem.Default, "h-11 rounded-xl"], key: s.Key,
                        onClick: () => Go(s.Key),
                        content: v =>
                        {
                            v.Icon([NavItem.Icon], name: s.IconName);
                            v.Text([NavItem.Label, "flex-1 text-left"], text: s.Label);

                            if (count > 0)
                            {
                                v.Box(["min-w-6 h-6 px-1.5 rounded-full bg-brand-solid text-primary-on-brand text-xs font-semibold flex items-center justify-center"],
                                    content: c => c.Text([], count.ToString()));
                            }
                        });
                }
            });

            view.Box(["flex-1"]);

            view.Box(["rounded-2xl bg-brand-solid/10 p-4"], content: view =>
            {
                view.Column(["gap-2"], content: view =>
                {
                    view.Row(["items-center gap-2"], content: r =>
                    {
                        r.Icon([Icon.Sm, "text-brand-secondary"], name: "shield-check");
                        r.Text([Text.Label], "Demo mode");
                    });
                    view.Text([Text.Caption, "text-muted-foreground"], "All accounts, people and amounts here are fictional. Nothing connects to a bank.");
                    view.Button([Button.GhostSm, "justify-start px-0 text-brand-secondary"], text: "Reset demo data", onClick: () => _dialog.Value = "reset");
                });
            });
        });
    }

    private void RenderTopBar(UIView view)
    {
        var section = Sections.First(s => s.Key == _section.Value);

        view.Row(["shrink-0 items-center justify-between gap-3 px-4 sm:px-6 lg:px-8 py-4 mx-auto w-full max-w-6xl"], content: view =>
        {
            view.Row(["items-center gap-3 min-w-0"], content: view =>
            {
                view.Box(["md:hidden w-9 h-9 rounded-xl bg-brand-solid flex items-center justify-center shrink-0"], content: b =>
                    b.Icon([Icon.Sm, "text-primary-on-brand"], name: "sprout"));
                view.Column(["min-w-0 gap-0"], content: view =>
                {
                    view.Text([Text.Overline, "text-muted-foreground"], Fin.DayLong(Today));
                    view.Heading(section.Label, style: [Text.H3, "truncate"], level: 1);
                });
            });

            view.Row(["items-center gap-2 shrink-0"], content: view =>
            {
                var attention = AttentionCount();

                view.Button([Button.OutlineMd, "relative h-10"], text: "Inbox", ariaLabel: $"Inbox, {attention} need attention",
                    onClick: () => Go("inbox"),
                    content: v =>
                    {
                        v.Icon([Icon.Sm], name: "bell");
                        v.Text(["hidden sm:inline"], attention > 0 ? $"{attention} to review" : "All clear");

                        if (attention > 0)
                        {
                            v.Box(["sm:hidden absolute -top-1 -right-1 w-4 h-4 rounded-full bg-error-solid text-white text-[10px] font-bold flex items-center justify-center"],
                                content: c => c.Text([], attention.ToString()));
                        }
                    });

                view.Button([Button.GhostMd, Button.Icon, "md:hidden h-10 w-10"], ariaLabel: "Reset demo data",
                    onClick: () => _dialog.Value = "reset",
                    content: v => v.Icon([Icon.Sm], name: "rotate-ccw"));
            });
        });
    }

    private void RenderMobileNav(UIView view)
    {
        view.Row(["md:hidden fixed bottom-0 inset-x-0 z-40 border-t border-secondary bg-card/95 backdrop-blur-sm px-1 pb-[env(safe-area-inset-bottom)]"],
            props: new Dictionary<string, object> { ["role"] = "navigation" }, ariaLabel: "Sections", content: view =>
        {
            foreach (var s in Sections)
            {
                var active = _section.Value == s.Key;
                var shortLabel = s.Label.Split(' ')[^1] switch
                {
                    "Inbox" => "Inbox",
                    "Invoices" => "Invoices",
                    "Tracker" => "Debts",
                    "Planner" => "Payday",
                    "Goals" => "Goals",
                    "Reports" => "Reports",
                    _ => "Home",
                };

                view.Button([$"flex-1 min-w-0 h-14 flex flex-col items-center justify-center gap-0.5 rounded-xl {(active ? "text-brand-secondary font-semibold" : "text-muted-foreground")}"],
                    key: $"m-{s.Key}", ariaLabel: s.Label, onClick: () => Go(s.Key),
                    content: v =>
                    {
                        v.Box(["relative"], content: b =>
                        {
                            b.Icon([Icon.Sm], name: s.IconName);

                            if ((s.Key == "inbox" && AttentionCount() > 0) || (s.Key == "invoices" && InvoicesToReview() > 0))
                            {
                                b.Box(["absolute -top-0.5 -right-1 w-2 h-2 rounded-full bg-error-solid"]);
                            }
                        });
                        v.Text(["text-[10px] leading-none truncate"], shortLabel);
                    });
            }
        });
    }

    // ── Undo ────────────────────────────────────────────────────────────────

    private void OfferUndo(string message, Action revert) =>
        _undo.Value = new UndoEntry(Interlocked.Increment(ref _undoSeq), message, revert);

    private void RenderUndoToast(UIView view)
    {
        if (_undo.Value is not { } entry)
        {
            return;
        }

        view.Toast(open: true, durationMs: 8000, key: $"undo-{entry.Id}",
            onOpenChange: async open =>
            {
                if (!open && _undo.Value?.Id == entry.Id)
                {
                    _undo.Value = null;
                }
            },
            content: toast => toast.Row(["items-center gap-3"], content: row =>
            {
                row.Icon([Icon.Sm, "text-success-600"], name: "circle-check");
                row.Text([Text.BodySm, "text-foreground"], entry.Message);
                row.Button([Button.OutlineSm], text: "Undo", onClick: () =>
                {
                    entry.Revert();
                    _undo.Value = null;
                    _toasts.Info("Change undone");
                });
            }));
    }

    // ── Shared building blocks ──────────────────────────────────────────────

    private static string[] ToneTile(string tone) => tone switch
    {
        "success" => ["bg-success-500/15", "text-success-600 dark:text-success-400"],
        "warning" => ["bg-warning-500/15", "text-warning-600 dark:text-warning-400"],
        "error" => ["bg-error-500/15", "text-error-600 dark:text-error-400"],
        "info" => ["bg-info-500/15", "text-info-600 dark:text-info-400"],
        "neutral" => ["bg-muted", "text-muted-foreground"],
        _ => ["bg-brand-solid/10", "text-brand-secondary"],
    };

    private static void IconTile(UIView view, string iconName, string tone = "brand", string size = "w-10 h-10")
    {
        var t = ToneTile(tone);
        view.Box([$"{size} rounded-xl {t[0]} flex items-center justify-center shrink-0"], content: b =>
            b.Icon([Icon.Sm, t[1]], name: iconName));
    }

    private static void SectionHeader(UIView view, string title, string? subtitle = null, Action<UIView>? actions = null)
    {
        view.Row(["items-end justify-between gap-3 flex-wrap"], content: view =>
        {
            view.Column(["gap-1 min-w-0"], content: view =>
            {
                view.Heading(title, style: [Text.H4, "m-0 leading-tight text-foreground"], level: 2);

                if (subtitle != null)
                {
                    view.Text([Text.BodySm, "text-muted-foreground"], subtitle);
                }
            });

            if (actions != null)
            {
                view.Row(["items-center gap-2 flex-wrap"], content: actions);
            }
        });
    }

    // A summary number that opens the screen holding its rows
    private static void StatTile(UIView view, string label, string value, string caption, string iconName, string tone, Action? onClick, string? key = null)
    {
        view.Box([Card.Elevated, "p-4 sm:p-5 flex flex-col gap-2 sm:gap-3 text-left min-w-0",
                onClick != null ? "cursor-pointer hover:shadow-md hover:-translate-y-0.5 transition-all duration-150" : ""],
            key: key, ariaLabel: onClick != null ? $"{label}: {value}. Open" : null,
            onClick: onClick,
            content: view =>
            {
                view.Row(["items-center justify-between gap-2"], content: r =>
                {
                    r.Text([Text.Label, "text-muted-foreground leading-snug"], label);
                    IconTile(r, iconName, tone, "w-8 h-8 sm:w-9 sm:h-9");
                });
                view.Text(["text-2xl sm:text-3xl font-bold leading-tight tabular-nums tracking-tight text-foreground truncate"], value);
                view.Text([Text.Caption, "text-muted-foreground"], caption);
            });
    }
}
