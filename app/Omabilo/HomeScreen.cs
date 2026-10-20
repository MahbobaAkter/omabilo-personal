// Home in the Nordic design, kept short: the person's cover and picture with the greeting, four money
// tiles, one Today list beside one money-flow card, then small lists of bills ahead, recent activity and
// goals. Every number and row comes from HomeSummary and the payday plan (plain C#, tested); this file
// only draws it. Pictures are saved through ProfileScreen's upload, preview and save flow.

public partial class OmabiloApp
{
    private const int TodayRows = 5;

    // ── Profile header: cover, picture, greeting ─────────────────────────────

    private void RenderHomeHero(UIView view, HomeHealth? health)
    {
        var snap = _my.Value;
        var pending = _pfPending.Value;
        var cover = pending is { Kind: ProfileImageKind.Cover } ? pending : snap?.Cover;
        var avatar = pending is { Kind: ProfileImageKind.Avatar } ? pending : snap?.Avatar;
        var focus = _pfFocus.Value ?? cover?.FocusY ?? ProfileImages.DefaultFocusY;
        var busy = _pfBusy.Value != null;
        var canEdit = snap != null && MyUserId != null;

        view.Box([Card.Default, "p-0 overflow-hidden flex flex-col",
            "motion-[0:opacity-0,100:opacity-100] motion-duration-500ms"], key: "home-hero", content: hero =>
        {
            hero.Box(["relative h-32 sm:h-40 lg:h-44 bg-emerald-950"], content: top =>
            {
                if (cover != null)
                {
                    top.Image(["absolute inset-0 w-full h-full object-cover", CoverPosition(focus)], assetUri: PictureUri(cover), alt: "", key: $"home-cover-{cover.Path}");
                }
                else
                {
                    top.Image(["absolute inset-0 w-full h-full object-cover"], src: DefaultCover, alt: "", key: "home-cover-default");
                }

                top.Box(["absolute inset-x-0 bottom-0 h-1/2 bg-gradient-to-t from-black/30 to-transparent"]);

                if (pending != null)
                {
                    top.Box(["absolute top-3 left-3 rounded-full bg-white/90 px-3 py-1 shadow-xs"], content: b =>
                        b.Text([Text.Label, "text-emerald-950"], pending.Kind == ProfileImageKind.Avatar ? "Preview: picture not saved yet" : "Preview: cover not saved yet"));
                }

                if (canEdit)
                {
                    PictureUpload(top, ProfileImageKind.Cover, "Edit cover photo", "camera", busy,
                        style: ["absolute bottom-3 right-3 inline-flex items-center gap-2 rounded-full bg-white/90 hover:bg-white text-emerald-950 px-3 h-10 shadow-sm cursor-pointer",
                            "transition-colors duration-150 focus-visible:ring-2 focus-visible:ring-brand"],
                        labelStyle: "hidden sm:inline", keyPrefix: "home");
                }
            });

            hero.Column(["gap-4 px-4 sm:px-6 pb-5"], content: body =>
            {
                body.Row(["items-end gap-3 sm:gap-5 -mt-10 sm:-mt-14 min-w-0 flex-wrap"], content: r =>
                {
                    r.Box(["relative shrink-0"], content: pic =>
                    {
                        pic.Box(["w-20 h-20 sm:w-28 sm:h-28 rounded-full ring-4 ring-card bg-card overflow-hidden flex items-center justify-center"], content: ring =>
                        {
                            if (avatar != null)
                            {
                                ring.Image(["w-full h-full object-cover"], assetUri: PictureUri(avatar), alt: "Your profile picture", key: $"home-avatar-{avatar.Path}");
                            }
                            else
                            {
                                ring.Box(["w-full h-full rounded-full bg-brand-solid text-primary-on-brand flex items-center justify-center text-2xl sm:text-3xl font-semibold"],
                                    content: b => b.Text([], _me.Value.Initials));
                            }
                        });

                        if (canEdit)
                        {
                            PictureUpload(pic, ProfileImageKind.Avatar, "Edit profile picture", "camera", busy,
                                style: ["absolute bottom-0 right-0 w-10 h-10 rounded-full bg-card border border-secondary text-foreground shadow-sm hover:bg-muted cursor-pointer",
                                    "flex items-center justify-center transition-colors duration-150 focus-visible:ring-2 focus-visible:ring-brand"],
                                labelStyle: "none", keyPrefix: "home");
                        }
                    });

                    r.Column(["gap-1 min-w-0 flex-1 pb-1 basis-56"], content: c =>
                    {
                        c.Heading(Greeting(), style: [Text.H3, "m-0 leading-tight text-foreground"], level: 2);
                        c.Text([Text.BodySm, "text-muted-foreground"], "Here's what needs you today, and how your money looks.");
                    });

                    if (health != null)
                    {
                        var dot = health.Tone switch { "error" => "bg-error-500", "warning" => "bg-warning-500", _ => "bg-success-500" };
                        r.Box(["flex flex-row items-center gap-2 rounded-full bg-muted px-3.5 py-1.5 mb-1",
                            "motion-[0:opacity-0_translate-y-[4px],100:opacity-100_translate-y-0] motion-duration-300ms"], key: $"health-{health.Level}", content: p =>
                        {
                            p.Box([$"w-2.5 h-2.5 rounded-full {dot}"]);
                            p.Text([Text.Label, "text-foreground"], health.Title);
                        });
                    }
                });

                if (pending != null)
                {
                    PendingControls(body, pending, focus, busy);
                }

                if (_pfError.Value is { } error)
                {
                    body.Alert("Nothing was changed", tone: SemanticTone.Warning, description: error, key: "home-pf-error", onDismiss: async () => _pfError.Value = null);
                }
            });
        });
    }

    // ── Four money tiles ─────────────────────────────────────────────────────

    private void RenderHomeTiles(UIView view, CompanionSnapshot snap)
    {
        var today = Today;
        var plans = _plans.Value is { } mp ? HomeSummary.Plans(mp) : null;

        void Goals(UIView grid) => StatTile(grid, "Savings goals", plans is { GoalCount: > 0 } ? Money(plans.Saved) : "None yet",
            plans is { GoalCount: > 0 } ? $"of {Money(plans.Target)} · {plans.GoalCount} goal{(plans.GoalCount == 1 ? "" : "s")}" : "Set a goal to save towards",
            "sprout", "brand", () => Go("my-goals"), "tile-goals");

        view.Grid(["grid grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-4"], content: grid =>
        {
            // Balance and salary belong to the payday plan (Premium); the free plan's tiles come from saved bills
            if (IsPremium())
            {
                var plan = snap.Plan;
                StatTile(grid, "Current balance", snap.Settings.Balance is { } b ? Money(b) : "Not added",
                    snap.Settings.Balance != null ? plan.BalanceNote : "Omabilo can't see your bank", "wallet", "brand", () => Go("my-payday"), "tile-balance");
                StatTile(grid, "Expected income", snap.Income.ExpectedNet is { } net ? Money(net) : "Unknown",
                    (snap.Income.NextPayday is { } p ? $"{Day(p)} · " : "") + "Not counted until it arrives", "banknote-arrow-down", "glacier",
                    () => GoMoney("income"), "tile-income");
                StatTile(grid, "Upcoming bills", Money(plan.ObligationsTotal), $"{plan.Obligations.Count} bill{(plan.Obligations.Count == 1 ? "" : "s")} · {plan.PeriodLabel}",
                    "calendar-clock", plan.Obligations.Any(o => o.Date < today) ? "error" : "amber", () => GoMoney("bills"), "tile-bills");
                Goals(grid);
                return;
            }

            var ahead = HomeSummary.Ahead(snap.Items, today);
            var monthReceipts = snap.Items.Where(i => i.Kind == FinKind.Receipt && i.Amount != null && i.PossibleDuplicateOf == null && i.UserState != UserState.NotApplicable
                && i.EventDate is { } d && d.Year == today.Year && d.Month == today.Month).ToList();

            StatTile(grid, "Upcoming bills", Money(ahead.DueTotal), $"{ahead.DueCount} bill{(ahead.DueCount == 1 ? "" : "s")} in {HomeSummary.AheadDays} days",
                "calendar-clock", "amber", () => GoMoney("bills"), "tile-bills");
            StatTile(grid, "Receipts this month", Money(monthReceipts.Sum(i => i.Amount!.Value)), $"{monthReceipts.Count} receipt{(monthReceipts.Count == 1 ? "" : "s")} by email",
                "receipt", "glacier", null, "tile-receipts");
            StatTile(grid, "Debts", plans is { DebtCount: > 0 } ? Money(plans.DebtTotal) : "None added",
                plans is { DebtCount: > 0 } ? $"{plans.DebtCount} debt{(plans.DebtCount == 1 ? "" : "s")} · {Money(plans.MonthlyPayments)} a month" : "Add a loan or card to track it",
                "trending-down", "neutral", () => Go("my-debts"), "tile-debts");
            Goals(grid);
        });
    }

    // ── Today: one prioritised list ──────────────────────────────────────────

    private void RenderToday(UIView view, CompanionSnapshot snap)
    {
        var items = HomeSummary.TodayList(MyReminders(), snap.Items, IsPremium() ? snap.Plan : null);

        view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-3"], key: "home-today", content: card =>
        {
            card.Row(["items-center justify-between gap-3"], content: r =>
            {
                r.Row(["items-center gap-2 min-w-0"], content: h =>
                {
                    h.Heading("Today", style: [Text.H4, "m-0 leading-tight text-foreground"], level: 2);

                    if (items.Count > 0)
                    {
                        h.Badge(items.Count.ToString(CultureInfo.InvariantCulture), tone: SemanticTone.Warning, size: BadgeSize.Sm);
                    }
                });

                if (items.Count > TodayRows)
                {
                    r.Button([Button.GhostSm, "text-brand-secondary"], text: "View all", icon: "arrow-right", onClick: () => { _myFilter.Value = "attention"; Go("my-inbox"); });
                }
            });

            if (items.Count == 0)
            {
                card.Row(["items-center gap-3 py-2"], content: r =>
                {
                    IconTile(r, "circle-check", "success");
                    r.Column(["gap-0.5 min-w-0"], content: c =>
                    {
                        c.Text([Text.BodyStrong, "text-foreground"], "Nothing needs you today");
                        c.Text([Text.Caption, "text-muted-foreground"], "Late bills, records to check and missing details show here when there are any.");
                    });
                });
                return;
            }

            card.Text([Text.BodySm, "text-muted-foreground -mt-2"], "Things that need your attention");

            card.Column(["flex flex-col"], content: list =>
            {
                foreach (var t in items.Take(TodayRows))
                {
                    var (icon, tone) = t.Kind switch
                    {
                        TodayKind.Overdue => ("circle-alert", "error"),
                        TodayKind.DueToday => ("alarm-clock", "amber"),
                        TodayKind.Review => ("shield-alert", "glacier"),
                        TodayKind.Missing => ("circle-dashed", "glacier"),
                        TodayKind.Unread => ("file-warning", "amber"),
                        _ => ("wallet", "brand"),
                    };

                    list.Row(["items-center gap-3 py-3 border-b border-secondary last:border-b-0 min-w-0"], key: $"today-{t.Kind}-{t.ItemId ?? "plan"}", content: row =>
                    {
                        IconTile(row, icon, tone);
                        row.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                        {
                            c.Text([Text.BodyStrong, "text-foreground truncate"], t.Title);
                            c.Text([Text.Caption, "text-muted-foreground line-clamp-2"], t.Detail);
                        });
                        row.Button([t.Kind == TodayKind.Overdue ? Button.PrimarySm : Button.OutlineSm, "shrink-0 min-h-10"], text: t.Action,
                            ariaLabel: $"{t.Action}: {t.Title}", onClick: () => OpenToday(t, snap));
                    });
                }
            });
        });
    }

    private void OpenToday(TodayItem t, CompanionSnapshot snap)
    {
        if (t.ItemId != null && snap.Items.FirstOrDefault(i => i.Id == t.ItemId) is { } item)
        {
            Go("my-inbox");
            OpenItem(item);
        }
        else if (t.Kind == TodayKind.AddBalance)
        {
            Go("my-payday");
        }
        else
        {
            _myFilter.Value = "attention";
            Go("my-inbox");
        }
    }

    // ── Money flow: one compact card ─────────────────────────────────────────

    private void RenderHomeMoneyFlow(UIView view, CompanionSnapshot snap)
    {
        view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-4"], key: "home-flow", content: card =>
        {
            card.Column(["gap-0.5"], content: c =>
            {
                c.Heading("Money flow", style: [Text.H4, "m-0 leading-tight text-foreground"], level: 2);
                c.Text([Text.Caption, "text-muted-foreground"], IsPremium() ? snap.Plan.PeriodLabel : $"Bills in the next {HomeSummary.AheadDays} days");
            });

            if (!IsPremium())
            {
                var ahead = HomeSummary.Ahead(snap.Items, Today);
                FlowFigure(card, "Going out", Money(ahead.DueTotal), "text-amber-700 dark:text-amber-300",
                    $"{ahead.DueCount} open bill{(ahead.DueCount == 1 ? "" : "s")}" + (ahead.OverdueCount > 0 ? $", plus {Money(ahead.OverdueTotal)} overdue" : ""));
                card.Text([Text.Caption, "text-muted-foreground"], "Money coming in and what's left need your balance and payday, which the Payday Planner in Premium keeps. Omabilo doesn't guess them.");
                card.Button([Button.GhostSm, "self-start px-0 text-brand-secondary"], text: "Open the Payday Planner", icon: "arrow-right", onClick: () => Go("my-payday"));
                return;
            }

            var flow = HomeSummary.Flow(snap.Plan);

            if (flow.InTotal == 0 && flow.OutTotal == 0)
            {
                card.Text([Text.BodySm, "text-muted-foreground"], "Add your balance and payday in the Payday Planner, and the money in and out until payday shows here.");
                card.Button([Button.OutlineSm, "self-start"], text: "Open the Payday Planner", icon: "calendar-days", onClick: () => Go("my-payday"));
                return;
            }

            card.Grid(["grid grid-cols-2 gap-4"], content: g =>
            {
                FlowFigure(g, "Coming in", Money(flow.InTotal), "text-brand-secondary", string.Join(" · ", flow.In.Select(p => p.Label)) is { Length: > 0 } inl ? inl : "Nothing yet");
                FlowFigure(g, "Going out", Money(flow.OutTotal), "text-amber-700 dark:text-amber-300", $"{flow.Out.Count} kind{(flow.Out.Count == 1 ? "" : "s")} of cost");
            });

            // One bar: how much of the money in is already spoken for
            var share = flow.InTotal > 0 ? Math.Min(1d, (double)(flow.OutTotal / flow.InTotal)) : 1d;
            var outPct = (share * 100).ToString("0.#", CultureInfo.InvariantCulture);
            var over = flow.OutTotal > flow.InTotal;
            card.Box(["h-3 w-full rounded-full bg-brand-solid/20 overflow-hidden flex flex-row"],
                ariaLabel: over ? "Going out is more than coming in" : $"Going out is {outPct}% of coming in", content: b =>
                    b.Box([$"h-full w-[{outPct}%] origin-left", over ? "bg-error-500" : "bg-amber-500",
                        "motion-[0:scale-x-0,100:scale-x-100] motion-duration-500ms"]));

            card.Box(["rounded-2xl bg-muted p-4 flex flex-row items-center justify-between gap-3"], content: left =>
            {
                left.Column(["gap-0.5 min-w-0"], content: c =>
                {
                    c.Text([Text.Label, "text-foreground"], "Estimated left");
                    c.Text([Text.Caption, "text-muted-foreground"], flow.Left == null
                        ? "Add your balance to see this. Omabilo doesn't guess."
                        : "From your balance and income already received.");
                });
                left.Text(["text-2xl font-semibold tabular-nums tracking-tight shrink-0", flow.Left is < 0 ? "text-error-600" : "text-foreground"],
                    flow.Left is { } l ? Money(l) : "—");
            });

            if (snap.Income.ExpectedNet is { } expected)
            {
                card.Text([Text.Caption, "text-muted-foreground"],
                    $"Expected salary {Money(expected)}{(snap.Income.NextPayday is { } p ? $" on {Day(p)}" : "")} isn't counted until it arrives.");
            }

            card.Button([Button.GhostSm, "self-start px-0 text-brand-secondary"], text: "See details in the Payday Planner", icon: "arrow-right", onClick: () => Go("my-payday"));
        });
    }

    private static void FlowFigure(UIView view, string label, string value, string colour, string caption)
    {
        view.Column(["gap-0.5 min-w-0"], content: c =>
        {
            c.Text([Text.Label, "text-muted-foreground"], label);
            c.Text(["text-2xl font-semibold tabular-nums tracking-tight truncate", colour], value);
            c.Text([Text.Caption, "text-muted-foreground"], caption);
        });
    }

    // ── Small lists: bills ahead, recent activity, goals and debts ───────────

    private void RenderHomeLists(UIView view, CompanionSnapshot snap)
    {
        view.Grid(["grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 items-start"], content: g =>
        {
            ListCard(g, "Upcoming bills", "home-bills", () => GoMoney("bills"), c =>
            {
                var bills = HomeSummary.UpcomingBills(snap.Items, Today, 4);

                if (bills.Count == 0)
                {
                    c.Text([Text.BodySm, "text-muted-foreground py-2"], "No open bills ahead.");
                }

                foreach (var i in bills)
                {
                    ListRow(c, i.Id, HomeSummary.NameOf(i), Day(i.DueDate!.Value), Money(i.Amount!.Value), "text-foreground", () => { Go("my-inbox"); OpenItem(i); });
                }
            });

            ListCard(g, "Recent activity", "home-recent", () => Go("my-inbox"), c =>
            {
                var recent = HomeSummary.RecentActivity(snap.Items, 4);

                if (recent.Count == 0)
                {
                    c.Text([Text.BodySm, "text-muted-foreground py-2"], "Receipts, refunds and payslips show here as they arrive.");
                }

                foreach (var i in recent)
                {
                    var moneyIn = HomeSummary.IsMoneyIn(i);
                    ListRow(c, i.Id, HomeSummary.NameOf(i), Day(HomeSummary.ActivityDate(i)!.Value), (moneyIn ? "+ " : "− ") + Money(i.Amount!.Value),
                        moneyIn ? "text-brand-secondary" : "text-foreground", () => { Go("my-inbox"); OpenItem(i); });
                }
            });

            ListCard(g, "Goals and debts", "home-goals", () => Go("my-goals"), c =>
            {
                var plans = _plans.Value;

                if (plans == null || (plans.Goals.Count == 0 && plans.Debts.Count == 0))
                {
                    c.Text([Text.BodySm, "text-muted-foreground py-2"], "Add a savings goal or a debt to follow it here.");
                    return;
                }

                foreach (var gp in plans.Goals.Take(2))
                {
                    var pct = Math.Clamp(gp.Percent, 0, 100);
                    c.Box(["flex flex-col gap-1.5 py-2.5 border-b border-secondary rounded-lg cursor-pointer hover:bg-muted/60 px-1 -mx-1"], key: $"goal-{gp.Goal.Id}",
                        ariaLabel: $"{gp.Goal.Name}: {Money(gp.Saved)} of {Money(gp.Goal.Target)}", onClick: () => Go("my-goals"), content: b =>
                    {
                        b.Row(["items-baseline justify-between gap-2"], content: r =>
                        {
                            r.Text([Text.BodySm, "text-foreground truncate"], gp.Goal.Name);
                            r.Text([Text.Caption, "text-muted-foreground tabular-nums shrink-0"], $"{pct.ToString("0", CultureInfo.InvariantCulture)}%");
                        });
                        b.Box(["h-2 w-full rounded-full bg-muted overflow-hidden"], content: bar =>
                            bar.Box([$"h-full rounded-full bg-brand-solid w-[{pct.ToString("0.#", CultureInfo.InvariantCulture)}%]"]));
                        b.Text([Text.Caption, "text-muted-foreground tabular-nums"], $"{Money(gp.Saved)} of {Money(gp.Goal.Target)}");
                    });
                }

                if (plans.Debts.Count > 0)
                {
                    ListRow(c, "debts", "Debts", $"{plans.Debts.Count} debt{(plans.Debts.Count == 1 ? "" : "s")}", Money(plans.TotalDebt), "text-foreground", () => Go("my-debts"));
                }
            });
        });
    }

    private static void ListCard(UIView view, string title, string key, Action seeAll, Action<UIView> rows)
    {
        view.Box([Card.Default, "p-5 flex flex-col gap-1 min-w-0"], key: key, content: card =>
        {
            card.Row(["items-center justify-between gap-2 mb-1"], content: r =>
            {
                r.Heading(title, style: [Text.Label, "m-0 text-base font-semibold text-foreground"], level: 3);
                r.Button([Button.GhostSm, "text-brand-secondary"], text: "See all", ariaLabel: $"See all {title.ToLowerInvariant()}", onClick: seeAll);
            });
            rows(card);
        });
    }

    private static void ListRow(UIView view, string key, string name, string when, string amount, string amountColour, Action open)
    {
        view.Box(["flex flex-row items-center gap-3 py-2.5 px-1 -mx-1 min-h-11 rounded-lg border-b border-secondary last:border-b-0 cursor-pointer hover:bg-muted/60"],
            key: $"row-{key}", ariaLabel: $"{name}, {when}, {amount}. Open", onClick: open, content: r =>
        {
            r.Column(["flex-1 min-w-0 gap-0"], content: c =>
            {
                c.Text([Text.BodySm, "text-foreground truncate"], name);
                c.Text([Text.Caption, "text-muted-foreground"], when);
            });
            r.Text([Text.BodyStrong, "tabular-nums shrink-0", amountColour], amount);
        });
    }
}
