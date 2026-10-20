return await App.Run(args);

public record SessionIdentity(string? UserId);
public record ClientParameters(string Name = "Ikon");

// Visual direction: Nordic winter. A calm money companion on warm ivory, with pine green as the brand,
// teal and glacier blue for the money-flow and info accents, muted amber for what's coming up, and a
// small snowflake now and then. Clean Inter type with tabular numbers, rounded-2xl cards with soft
// warm hairlines, spacious density, motion only where state changes. A deep pine night for dark mode.
// No pink anywhere.
[App]
public partial class OmabiloApp(IApp<SessionIdentity, ClientParameters> app)
{
    private UI UI { get; } = new(app, new IkonTheme
    {
        ["primary"] = "#2f5d50",
        ["background"] = "#f7f3ea",
        ["foreground"] = "#1c2b28",
        ["card"] = "#fffdf8",
        ["muted"] = "#efe9dc",
        ["muted-foreground"] = "#5c6a64",
        ["border"] = "#e4dccb",
        ["input"] = "#d6cdb9",
        ["radius"] = "rounded-2xl",
        ["accent-300"] = "#cfe3ea",

        DarkMode = new IkonTheme
        {
            ["primary"] = "#86bfab",
            ["background"] = "#0e1917",
            ["foreground"] = "#e8eee9",
            ["card"] = "#152320",
            ["muted"] = "#1c2d29",
            ["muted-foreground"] = "#98aaa3",
            ["border"] = "#263a35",
            ["input"] = "#334a44",
            ["accent-800"] = "#2c4a55",
        },
    });

    // Every screen shows the signed-in person's own records; the menu itself is AppSections (plain C#, tested)

    // ── View state, per browser tab ─────────────────────────────────────────
    private readonly ClientReactive<string> _section = new("my-overview");
    private readonly ClientReactive<bool> _moreOpen = new(false);
    private readonly ClientReactive<UndoEntry?> _undo = new((UndoEntry?)null);
    private readonly Toasts _toasts = new();
    private long _undoSeq;

    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task Main()
    {
        app.OnClientJoined(OnCompanionClientJoinedAsync);
        SubscribePaymentEvents();

        // Back and forward in the browser move between screens
        app.Navigation.PathChangedAsync += async e =>
        {
            var target = AppSections.ForPath(e.Path).Key;

            if (target != _section.Value)
            {
                _section.Value = target;
                _moreOpen.Value = false;
                await LoadForSectionAsync(target);
            }
        };

        UI.Root([Page.Default, "h-screen overflow-hidden bg-background"], content: view =>
        {
            // Before sign-in there is only the public landing page: nothing personal, nothing fictional
            if (MyUserId == null)
            {
                RenderLanding(view);
                RenderAccountDialog(view);
                view.ToastHost(_toasts);
                return;
            }

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
                            RenderRemovalBanner(view);

                            // Before sign-in there is only the welcome: nothing personal, nothing fictional
                            if (MyUserId == null)
                            {
                                view.Feature("welcome", content: SignedOutCard);
                                return;
                            }

                            switch (_section.Value)
                            {
                                case "my-inbox": view.Feature("my-inbox", content: RenderMyInbox); break;
                                case "my-money": view.Feature("my-money", content: RenderMoneyScreen); break;
                                case "my-debts": view.Feature("my-debts", content: RenderDebtsScreen); break;
                                case "my-goals": view.Feature("my-goals", content: RenderGoalsScreen); break;
                                case "my-payday": view.Feature("my-payday", content: RenderPaydayScreen); break;
                                case "my-reports": view.Feature("my-reports", content: RenderReportsScreen); break;
                                case "my-assistant": view.Feature("my-assistant", content: RenderAssistantScreen); break;
                                case "my-settings": view.Feature("my-settings", content: RenderMySettings); break;
                                case "my-profile": view.Feature("my-profile", content: RenderMyProfile); break;
                                default: view.Feature("my-overview", content: RenderMyOverview); break;
                            }
                        });
                    });
                });
            });

            RenderMobileNav(view);
            RenderAccountDialog(view);
            RenderUndoToast(view);
            view.ToastHost(_toasts);
        });
    }

    // ── Shell ───────────────────────────────────────────────────────────────

    private void Go(string section)
    {
        var target = AppSections.ByKey(section);
        _section.Value = target.Key;
        _moreOpen.Value = false;

        // Each screen has its own address, so a refresh or a shared link opens the same screen
        _ = SetPathQuietlyAsync(target.Path);

        // A person's own records are read fresh each time they open one of their screens
        _ = LoadForSectionAsync(target.Key);
    }

    private async Task SetPathQuietlyAsync(string path)
    {
        try
        {
            await app.Navigation.SetPathAsync(path);
        }
        catch (Exception ex)
        {
            Log.Instance.Info($"path update skipped: {ex.GetType().Name}");
        }
    }

    private async Task LoadForSectionAsync(string section)
    {
        if (section.StartsWith("my-", StringComparison.Ordinal))
        {
            await LoadMyAsync();
        }
    }

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

            if (MyUserId != null)
            {
                view.Column(["gap-1 min-h-0 overflow-y-auto"], content: view =>
                {
                    var premium = IsPremium();

                    foreach (var s in AppSections.All)
                    {
                        var active = _section.Value == s.Key;
                        var count = s.Key == "my-overview" ? MyReminderTasks() : 0;

                        view.Button([NavItem.Md, active ? NavItem.ActiveBrand : NavItem.Default, "h-11 rounded-xl"], key: s.Key,
                            onClick: () => Go(s.Key),
                            content: v =>
                            {
                                v.Icon([NavItem.Icon], name: s.IconName);
                                v.Text([NavItem.Label, "flex-1 text-left"], text: s.Label);

                                if (s.Premium != null && !premium)
                                {
                                    v.Icon([Icon.Xs, "text-muted-foreground"], name: "lock", ariaLabel: "Premium");
                                }
                                else if (count > 0)
                                {
                                    v.Box(["min-w-6 h-6 px-1.5 rounded-full bg-brand-solid text-primary-on-brand text-xs font-semibold flex items-center justify-center"],
                                        content: c => c.Text([], count.ToString()));
                                }
                            });
                    }
                });
            }

            view.Box(["flex-1"]);

            RenderAccountPanel(view);
        });
    }

    private void RenderTopBar(UIView view)
    {
        var section = AppSections.ByKey(_section.Value);

        view.Row(["shrink-0 items-center justify-between gap-3 px-4 sm:px-6 lg:px-8 py-4 mx-auto w-full max-w-6xl"], content: view =>
        {
            view.Row(["items-center gap-3 min-w-0"], content: view =>
            {
                view.Box(["md:hidden w-9 h-9 rounded-xl bg-brand-solid flex items-center justify-center shrink-0"], content: b =>
                    b.Icon([Icon.Sm, "text-primary-on-brand"], name: "sprout"));
                view.Column(["min-w-0 gap-0"], content: view =>
                {
                    view.Text([Text.Overline, "text-muted-foreground"], Fin.DayLong(Today));
                    view.Heading(MyUserId == null ? "Omabilo" : section.Label, style: [Text.H3, "truncate"], level: 1);
                });
            });

            view.Row(["items-center gap-2 shrink-0"], content: view =>
            {
                PlanBadge(view);
                RenderAccountButton(view);
            });
        });
    }

    private void RenderMobileNav(UIView view)
    {
        view.Row(["md:hidden fixed bottom-0 inset-x-0 z-40 border-t border-secondary bg-card/95 backdrop-blur-sm px-1 pb-[env(safe-area-inset-bottom)]"],
            props: new Dictionary<string, object> { ["role"] = "navigation" }, ariaLabel: "Sections", content: view =>
        {
            if (MyUserId == null)
            {
                return;
            }

            void Tab(string key, string label, string icon, bool active, Action onClick, bool dot = false) =>
                view.Button([$"flex-1 min-w-0 h-14 flex flex-col items-center justify-center gap-0.5 rounded-xl {(active ? "text-brand-secondary font-semibold" : "text-muted-foreground")}"],
                    key: $"m-{key}", ariaLabel: label, onClick: onClick,
                    content: v =>
                    {
                        v.Box(["relative"], content: b =>
                        {
                            b.Icon([Icon.Sm], name: icon);

                            if (dot)
                            {
                                b.Box(["absolute -top-0.5 -right-1 w-2 h-2 rounded-full bg-error-solid"]);
                            }
                        });
                        v.Text(["text-[10px] leading-none truncate"], label);
                    });

            foreach (var key in AppSections.PhoneBar)
            {
                var s = AppSections.ByKey(key);
                Tab(s.Key, s.ShortLabel, s.IconName, _section.Value == s.Key, () => Go(s.Key), s.Key == "my-overview" && MyReminderTasks() > 0);
            }

            Tab("more", "More", "menu", !AppSections.PhoneBar.Contains(_section.Value), () => _moreOpen.Value = true);
        });

        if (MyUserId == null)
        {
            return;
        }

        view.Sheet(open: _moreOpen.Value, onOpenChange: async open => _moreOpen.Value = open, side: Side.Bottom, title: "More", content: sheet =>
            sheet.Column(["gap-1 pb-4"], content: list =>
            {
                var premium = IsPremium();

                foreach (var s in AppSections.All.Where(s => !AppSections.PhoneBar.Contains(s.Key)))
                {
                    list.Button([NavItem.Md, _section.Value == s.Key ? NavItem.ActiveBrand : NavItem.Default, "h-12 rounded-xl"], key: $"more-{s.Key}", onClick: () => Go(s.Key),
                        content: v =>
                        {
                            v.Icon([NavItem.Icon], name: s.IconName);
                            v.Text([NavItem.Label, "flex-1 text-left"], text: s.Label);

                            if (s.Premium != null && !premium)
                            {
                                v.Icon([Icon.Xs, "text-muted-foreground"], name: "lock", ariaLabel: "Premium");
                            }
                        });
                }
            }));
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
        // Nordic accents for money flow and calm highlights
        "teal" => ["bg-teal-500/15", "text-teal-700 dark:text-teal-300"],
        "glacier" => ["bg-sky-300/30", "text-sky-800 dark:text-sky-200"],
        "amber" => ["bg-amber-500/15", "text-amber-700 dark:text-amber-300"],
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
        view.Box([Card.Elevated, "p-4 sm:p-5 flex flex-col gap-1.5 sm:gap-2 text-left min-w-0",
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
                view.Text(["text-xl sm:text-2xl font-semibold leading-tight tabular-nums tracking-tight text-foreground truncate"], value);
                view.Text([Text.Caption, "text-muted-foreground"], caption);
            });
    }
}
