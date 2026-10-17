public partial class OmabiloApp
{
    private void RenderDashboard(UIView view)
    {
        var wellbeing = ComputeWellbeing();
        var thisMonth = MonthStart(Today);
        var spent = SpendingIn(thisMonth);
        var lastMonthSoFar = -_txns
            .Where(t => t.Date >= thisMonth.AddMonths(-1) && t.Date <= Today.AddMonths(-1) && t.Amount < 0 && t.Category != "Savings")
            .Sum(t => t.Amount);
        var safe = SafeToSpend();
        var days = DaysToPayday();
        var greeting = DateTime.UtcNow.Hour switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };

        view.Column(["gap-1"], content: view =>
        {
            view.Text([Text.H2, "tracking-tight text-foreground"], $"{greeting} 👋");
            view.Text([Text.Body, "text-muted-foreground"],
                $"Here's where your money stands today. Payday is in {days} day{(days == 1 ? "" : "s")}.");
        });

        // ── Score + payday ──────────────────────────────────────────────────
        view.Grid(["grid grid-cols-1 lg:grid-cols-3 gap-4 lg:gap-6"], content: view =>
        {
            view.Box([Card.Elevated, "lg:col-span-2 p-5 sm:p-6"], content: view => RenderWellbeingCard(view, wellbeing));

            view.Box(["rounded-2xl bg-brand-solid p-5 sm:p-6 flex flex-col gap-4 shadow-md"], content: view =>
            {
                view.Row(["items-center justify-between"], content: r =>
                {
                    r.Text([Text.Label, "text-primary-on-brand/80"], "Safe to spend until payday");
                    r.Icon([Icon.Sm, "text-primary-on-brand/80"], name: "wallet");
                });
                view.Text([Text.Display, "tabular-nums tracking-tight text-primary-on-brand"], Fin.Eur(safe));
                view.Text([Text.BodySm, "text-primary-on-brand/90"],
                    safe > 0
                        ? $"About {Fin.Eur(safe / days)} a day for the next {days} days, after {BillsBeforePayday().Count} bills due before payday."
                        : "Bills due before payday are more than your balance. Open the planner to see what to move.");
                view.Box(["h-px bg-white/20"]);
                view.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
                {
                    r.Column(["gap-0"], content: c =>
                    {
                        c.Text([Text.Caption, "text-primary-on-brand/80"], "Next payday");
                        c.Text([Text.BodyStrong, "text-primary-on-brand"], Fin.DayLong(NextPayday()));
                    });
                    r.Button(["h-10 px-4 rounded-xl bg-white text-teal-800 font-semibold text-sm hover:bg-white/90 transition-colors"],
                        text: "Plan my payday", onClick: () => Go("payday"));
                });
            });
        });

        // ── Summary numbers, each opening its screen ────────────────────────
        view.Grid(["grid grid-cols-2 xl:grid-cols-4 gap-3 sm:gap-4"], content: view =>
        {
            StatTile(view, "Current account", Fin.Eur(CurrentBalance()), "Balance today", "landmark", "brand", () => Go("payday"), "stat-balance");

            var diff = spent - lastMonthSoFar;
            StatTile(view, "Spent this month", Fin.Eur(spent),
                lastMonthSoFar > 0 ? $"{(diff > 0 ? "▲" : "▼")} {Fin.Eur(Math.Abs(diff))} vs same point last month" : "Month to date",
                "receipt", diff > 0 ? "warning" : "success", () => Go("reports"), "stat-spent");

            var startTotal = _debts.Sum(d => d.StartingBalance);
            StatTile(view, "Total debt", Fin.Eur(TotalDebt()),
                startTotal > 0 ? $"{Fin.Pct((double)((startTotal - TotalDebt()) / startTotal) * 100)} paid off so far" : "No debts tracked",
                "trending-down", "info", () => Go("debts"), "stat-debt");

            var target = _goals.Sum(g => g.Target);
            StatTile(view, "Saved for goals", Fin.Eur(TotalSaved()),
                target > 0 ? $"{Fin.Pct((double)(TotalSaved() / target) * 100)} of {Fin.Eur(target)} across {_goals.Count} goals" : "No goals yet",
                "piggy-bank", "success", () => Go("goals"), "stat-saved");
        });

        // ── Cash flow + needs attention ─────────────────────────────────────
        view.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-4 lg:gap-6"], content: view =>
        {
            view.Box([Card.Default, "lg:col-span-3 p-5 sm:p-6 flex flex-col gap-4"], content: view =>
            {
                SectionHeader(view, "Cash flow", "Money in and out over the last six full months", actions: a =>
                    a.Button([Button.GhostSm], text: "View reports", icon: "arrow-right", iconPosition: Align.End, onClick: () => Go("reports")));

                var months = Enumerable.Range(0, 6).Select(i => MonthStart(Today).AddMonths(i - 6)).ToList();
                view.BarChart(["h-64 w-full"],
                    data: months.Select(m => new Dictionary<string, object>
                    {
                        ["month"] = m.ToString("MMM", Fin.Inv),
                        ["Money in"] = (double)IncomeIn(m),
                        ["Money out"] = (double)SpendingIn(m),
                    }),
                    keys: ["Money in", "Money out"],
                    indexBy: "month",
                    groupMode: BarGroupMode.Grouped,
                    colors: ["#0f766e", "#f59e0b"],
                    borderRadius: 6,
                    enableLabel: false,
                    valueUnit: "€");
            });

            view.Box([Card.Default, "lg:col-span-2 p-5 sm:p-6 flex flex-col gap-4"], content: view =>
            {
                var attention = _inbox.Where(i => EffectiveStatus(i) == InboxStatus.New)
                    .OrderBy(i => i.DueDate ?? DateTime.MaxValue).ThenByDescending(i => i.ReceivedAt).Take(4).ToList();

                SectionHeader(view, "Needs attention", $"{AttentionCount()} items in your inbox", actions: a =>
                    a.Button([Button.GhostSm], text: "Open inbox", icon: "arrow-right", iconPosition: Align.End, onClick: () => Go("inbox")));

                if (attention.Count == 0)
                {
                    view.EmptyState("You're all caught up", description: "Nothing in your Financial Inbox needs you right now.", icon: "circle-check");
                }

                foreach (var item in attention)
                {
                    view.Row(["items-center gap-3 rounded-xl p-2 -mx-2 hover:bg-muted/60 transition-colors"], key: $"att-{item.Id}", content: row =>
                    {
                        IconTile(row, InboxIcon(item.Kind), InboxTone(item), "w-9 h-9");
                        row.Column(["flex-1 min-w-0 gap-0"], content: c =>
                        {
                            c.Text([Text.BodyStrong, "truncate text-foreground"], item.Title);
                            c.Text([Text.Caption, "text-muted-foreground truncate"],
                                item.DueDate is { } due && item.Kind == InboxKind.Bill ? $"{Fin.Eur(item.Amount ?? 0, 2)} · {DueText(due)}" : item.From);
                        });

                        if (item.Kind == InboxKind.Bill && item.Amount is > 0)
                        {
                            row.Button([Button.OutlineSm, "shrink-0"], text: "Pay", ariaLabel: $"Pay {item.Title}", onClick: () => MarkBillPaid(item));
                        }
                        else
                        {
                            row.Button([Button.GhostSm, "shrink-0"], text: "Done", ariaLabel: $"Done {item.Title}", onClick: () => SetInboxStatus(item, InboxStatus.Done, "Marked as done"));
                        }
                    });
                }
            });
        });

        // ── Goals and debts at a glance ─────────────────────────────────────
        view.Grid(["grid grid-cols-1 lg:grid-cols-2 gap-4 lg:gap-6"], content: view =>
        {
            view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
            {
                SectionHeader(view, "Goals", null, actions: a =>
                    a.Button([Button.GhostSm], text: "All goals", icon: "arrow-right", iconPosition: Align.End, onClick: () => Go("goals")));

                if (_goals.Count == 0)
                {
                    view.EmptyState("No goals yet", description: "Give your savings a purpose.", icon: "target");
                }

                foreach (var g in _goals.OrderBy(g => g.Saved >= g.Target).ThenBy(g => g.TargetDate).Take(4))
                {
                    var pct = g.Target > 0 ? (double)Math.Min(1, g.Saved / g.Target) * 100 : 0;
                    view.Column(["gap-2"], key: $"dg-{g.Id}", content: c =>
                    {
                        c.Row(["items-center justify-between gap-2"], content: r =>
                        {
                            r.Row(["items-center gap-2 min-w-0"], content: rr =>
                            {
                                rr.Icon([Icon.Sm, "text-brand-secondary"], name: g.IconName);
                                rr.Text([Text.BodyStrong, "truncate text-foreground"], g.Name);
                            });
                            r.Text([Text.Caption, "tabular-nums text-muted-foreground shrink-0"], $"{Fin.Eur(g.Saved)} / {Fin.Eur(g.Target)}");
                        });
                        c.Progress(value: pct, max: 100, tone: pct >= 100 ? SemanticTone.Success : SemanticTone.Neutral, ariaLabel: $"{g.Name} progress");
                    });
                }
            });

            view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
            {
                var plan = Fin.Simulate(_debts, (decimal)_extraPayment.Value, _strategy.Value);
                SectionHeader(view, "Debt-free journey", null, actions: a =>
                    a.Button([Button.GhostSm], text: "See payoff plan", icon: "arrow-right", iconPosition: Align.End, onClick: () => Go("debts")));

                if (_debts.All(d => d.Balance <= 0))
                {
                    view.EmptyState("Debt-free 🎉", description: "No balances left to pay.", icon: "party-popper");
                    return;
                }

                view.Row(["items-center gap-4"], content: r =>
                {
                    IconTile(r, "flag", "success", "w-12 h-12");
                    r.Column(["gap-0"], content: c =>
                    {
                        c.Text([Text.Caption, "text-muted-foreground"], "On your current plan you'll be debt-free in");
                        c.Text([Text.H3, "text-foreground"], plan.NeverPaysOff ? "— raise your payments" : Fin.Month(MonthStart(Today).AddMonths(plan.Months)));
                    });
                });

                foreach (var d in _debts.Where(d => d.Balance > 0).OrderByDescending(d => d.Apr))
                {
                    var paid = d.StartingBalance > 0 ? (double)((d.StartingBalance - d.Balance) / d.StartingBalance) * 100 : 0;
                    view.Column(["gap-1.5"], key: $"dd-{d.Id}", content: c =>
                    {
                        c.Row(["items-center justify-between gap-2"], content: rr =>
                        {
                            rr.Text([Text.BodySm, "text-foreground font-medium truncate"], d.Name);
                            rr.Text([Text.Caption, "tabular-nums text-muted-foreground"], $"{Fin.Eur(d.Balance)} left · {d.Apr.ToString("0.#", Fin.Inv)}% APR");
                        });
                        c.Progress(value: paid, max: 100, ariaLabel: $"{d.Name} paid off");
                    });
                }
            });
        });
    }

    private void RenderWellbeingCard(UIView view, Wellbeing w)
    {
        view.Column(["gap-5"], content: view =>
        {
            view.Row(["items-start justify-between gap-4 flex-wrap"], content: view =>
            {
                view.Column(["gap-1"], content: c =>
                {
                    c.Text([Text.Label, "text-muted-foreground"], "Financial wellbeing score");
                    c.Row(["items-baseline gap-2"], content: r =>
                    {
                        r.Text(["text-6xl font-bold tabular-nums tracking-tight text-foreground leading-none"], w.Score.ToString());
                        r.Text([Text.Body, "text-muted-foreground"], "/ 100");
                    });
                });

                view.Badge(w.Label, tone: w.Tone switch
                {
                    "success" => SemanticTone.Success,
                    "warning" => SemanticTone.Warning,
                    "error" => SemanticTone.Error,
                    _ => SemanticTone.Info,
                }, size: BadgeSize.Lg);
            });

            view.Progress(value: w.Score, max: 100, tone: w.Score >= 60 ? SemanticTone.Success : SemanticTone.Warning, ariaLabel: "Wellbeing score");

            view.Grid(["grid grid-cols-1 sm:grid-cols-2 gap-3"], content: view =>
            {
                foreach (var f in w.Factors)
                {
                    var ratio = f.Score / f.Max;
                    var tone = ratio >= 0.8 ? "success" : ratio >= 0.5 ? "warning" : "error";

                    view.Box(["rounded-xl border border-secondary p-3 flex flex-row items-start gap-3 cursor-pointer hover:bg-muted/50 transition-colors"],
                        key: $"factor-{f.Name}", ariaLabel: $"{f.Name}: {f.Detail}. Open", onClick: () => Go(f.Section),
                        content: b =>
                        {
                            IconTile(b, ratio >= 0.8 ? "circle-check" : "circle-alert", tone, "w-8 h-8");
                            b.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                            {
                                c.Row(["items-center justify-between gap-2"], content: r =>
                                {
                                    r.Text([Text.BodyStrong, "text-foreground"], f.Name);
                                    r.Text([Text.Caption, "tabular-nums text-muted-foreground"], $"{f.Score:0}/{f.Max:0}");
                                });
                                c.Text([Text.Caption, "text-foreground/80"], f.Detail);
                                c.Text([Text.Caption, "text-muted-foreground"], f.Tip);
                            });
                        });
                }
            });
        });
    }
}
