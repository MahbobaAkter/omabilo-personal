public partial class OmabiloApp
{
    private void SetExtra(double value) => _extraPayment.Value = Math.Clamp(Math.Round(value / 10) * 10, 0, 1000);

    private void RenderDebts(UIView view)
    {
        var active = _debts.Where(d => d.Balance > 0).ToList();
        var extra = (decimal)_extraPayment.Value;
        var plan = Fin.Simulate(_debts, extra, _strategy.Value);
        var minimumOnly = Fin.Simulate(_debts, 0, _strategy.Value);
        var monthlyInterest = active.Sum(d => d.Balance * d.Apr / 100m / 12m);

        SectionHeader(view, "Your debts", "Track every balance and see exactly when you'll be free of them.", actions: a =>
            a.Button([Button.PrimaryMd], text: "Add debt", icon: "plus", onClick: OpenAddDebt));

        view.Grid(["grid grid-cols-2 xl:grid-cols-4 gap-3 sm:gap-4"], content: view =>
        {
            StatTile(view, "Total owed", Fin.Eur(TotalDebt()), $"{active.Count} active balance{(active.Count == 1 ? "" : "s")}", "landmark", "info", null);
            StatTile(view, "Monthly minimums", Fin.Eur(active.Sum(d => d.MinPayment)),
                $"{Fin.Pct(_payday.Value.NetPay > 0 ? (double)(active.Sum(d => d.MinPayment) / _payday.Value.NetPay) * 100 : 0)} of your pay", "calendar", "brand", null);
            StatTile(view, "Interest this month", Fin.Eur(monthlyInterest, 2), "What your balances cost you", "percent", monthlyInterest > 30 ? "warning" : "neutral", null);
            StatTile(view, "Debt-free by", active.Count == 0 ? "Now 🎉" : plan.NeverPaysOff ? "Not yet" : MonthStart(Today).AddMonths(plan.Months).ToString("MMM yyyy", Fin.Inv),
                active.Count == 0 ? "Nothing left to pay" : plan.NeverPaysOff ? "Payments don't cover interest" : $"{plan.Months} months with your plan", "flag", "success", null);
        });

        if (_debts.Count == 0)
        {
            view.Box([Card.Default, "p-6"], content: v =>
                v.EmptyState("No debts tracked", description: "Add a card, loan or instalment plan to see your payoff plan.", icon: "trending-down",
                    action: a => a.Button([Button.PrimaryMd], text: "Add debt", onClick: OpenAddDebt)));
            return;
        }

        // ── Payoff plan ─────────────────────────────────────────────────────
        view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-5"], content: view =>
        {
            SectionHeader(view, "Payoff plan", "Choose how extra money is used. Minimums are always paid first.");

            view.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-6"], content: view =>
            {
                view.Column(["lg:col-span-2 gap-5"], content: view =>
                {
                    view.Column(["gap-2"], content: view =>
                    {
                        view.Text([Text.Label], "Strategy");
                        view.ToggleGroupSingle([ToggleGroup.Root, "w-full"], value: _strategy.Value, ariaLabel: "Strategy",
                            onValueChange: async v =>
                            {
                                if (!string.IsNullOrEmpty(v))
                                {
                                    _strategy.Value = v;
                                }
                            },
                            content: g =>
                            {
                                g.ToggleGroupItem([ToggleGroup.Item, "flex-1"], value: "avalanche", content: v => v.Text(text: "Avalanche"));
                                g.ToggleGroupItem([ToggleGroup.Item, "flex-1"], value: "snowball", content: v => v.Text(text: "Snowball"));
                            });
                        view.Text([Text.Caption, "text-muted-foreground"], _strategy.Value == "snowball"
                            ? "Smallest balance first. Quick wins keep you motivated."
                            : "Highest interest rate first. Pays the least interest overall.");
                    });

                    view.Column(["gap-3"], content: view =>
                    {
                        view.Row(["items-center justify-between"], content: r =>
                        {
                            r.Text([Text.Label], "Extra each month");
                            r.Text([Text.H5, "tabular-nums text-brand-secondary"], Fin.Eur(extra));
                        });
                        view.Row(["items-center gap-3"], content: r =>
                        {
                            r.Button([Button.OutlineSm, Button.Icon, "h-10 w-10 shrink-0"], ariaLabel: "Less extra (−€10)",
                                onClick: () => SetExtra(_extraPayment.Value - 10), content: v => v.Icon([Icon.Sm], name: "minus"));
                            r.Slider(_extraPayment.Value, style: ["default", "flex-1"], min: 0, max: 600, step: 10, ariaLabel: "Extra each month",
                                onValueChange: async v => SetExtra(v));
                            r.Button([Button.OutlineSm, Button.Icon, "h-10 w-10 shrink-0"], ariaLabel: "More extra (+€10)",
                                onClick: () => SetExtra(_extraPayment.Value + 10), content: v => v.Icon([Icon.Sm], name: "plus"));
                        });
                    });

                    view.Grid(["grid grid-cols-2 gap-3"], content: view =>
                    {
                        MiniStat(view, "Debt-free in", plan.NeverPaysOff ? "—" : $"{plan.Months} mo");
                        MiniStat(view, "Total interest", Fin.Eur(plan.TotalInterest));
                        MiniStat(view, "Interest saved", minimumOnly.NeverPaysOff ? "—" : Fin.Eur(Math.Max(0, minimumOnly.TotalInterest - plan.TotalInterest)));
                        MiniStat(view, "Months saved", minimumOnly.NeverPaysOff || plan.NeverPaysOff ? "—" : Math.Max(0, minimumOnly.Months - plan.Months).ToString());
                    });

                    if (plan.Order.Count > 0)
                    {
                        view.Column(["gap-2"], content: view =>
                        {
                            view.Text([Text.Label], "Payoff order");

                            var step = 1;
                            foreach (var (name, month) in plan.Order)
                            {
                                view.Row(["items-center gap-3"], key: $"order-{name}", content: r =>
                                {
                                    r.Box(["w-7 h-7 rounded-full bg-brand-solid/10 text-brand-secondary text-xs font-bold flex items-center justify-center shrink-0"],
                                        content: b => b.Text([], (step++).ToString()));
                                    r.Text([Text.BodySm, "flex-1 text-foreground"], name);
                                    r.Text([Text.Caption, "tabular-nums text-muted-foreground"], MonthStart(Today).AddMonths(month).ToString("MMM yyyy", Fin.Inv));
                                });
                            }
                        });
                    }
                });

                view.Column(["lg:col-span-3 gap-2 min-w-0"], content: view =>
                {
                    view.Text([Text.Label], "Remaining balance over time");

                    var horizon = Math.Min(Math.Max(plan.Remaining.Count, minimumOnly.Remaining.Count), 120);
                    var stride = horizon > 48 ? 3 : 1;

                    IEnumerable<LineChartPoint> Points(List<decimal> series) => Enumerable.Range(0, horizon)
                        .Where(i => i % stride == 0)
                        .Select(i => new LineChartPoint
                        {
                            X = MonthStart(Today).AddMonths(i).ToString("MMM yy", Fin.Inv),
                            Y = (double)(i < series.Count ? series[i] : 0),
                        });

                    view.LineChart(["h-72 w-full"],
                        data:
                        [
                            new LineChartSeries { Id = "With your plan", Color = "#0f766e", Data = Points(plan.Remaining).ToList() },
                            new LineChartSeries { Id = "Minimums only", Color = "#94a3b8", Data = Points(minimumOnly.Remaining).ToList() },
                        ],
                        enablePoints: false,
                        enableArea: true,
                        areaOpacity: 0.08,
                        curve: LineCurve.MonotoneX,
                        axisBottom: new AxisConfig { TickCount = 6 },
                        valueUnit: "€");
                });
            });
        });

        // ── Each debt ───────────────────────────────────────────────────────
        view.Grid(["grid grid-cols-1 lg:grid-cols-2 gap-4"], content: view =>
        {
            foreach (var d in _debts.OrderBy(d => d.Balance <= 0).ThenByDescending(d => d.Apr))
            {
                RenderDebtCard(view, d);
            }
        });

        // ── Recent payments ─────────────────────────────────────────────────
        var payments = _txns.Where(t => t.Category == "Debt payments").OrderByDescending(t => t.Date).Take(6).ToList();

        view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-3"], content: view =>
        {
            SectionHeader(view, "Recent payments");

            if (payments.Count == 0)
            {
                view.Text([Text.BodySm, "text-muted-foreground"], "Payments you log appear here.");
            }

            foreach (var t in payments)
            {
                view.Row(["items-center gap-3 py-1"], key: $"pay-{t.Id}", content: r =>
                {
                    IconTile(r, "check", "success", "w-8 h-8");
                    r.Text([Text.BodySm, "flex-1 text-foreground truncate"], t.Merchant);
                    r.Text([Text.Caption, "text-muted-foreground"], Fin.Day(t.Date));
                    r.Text([Text.BodyStrong, "tabular-nums text-foreground w-24 text-right"], Fin.Eur(-t.Amount, 2));
                });
            }
        });
    }

    private static void MiniStat(UIView view, string label, string value)
    {
        view.Column(["rounded-xl bg-muted/60 p-3 gap-0.5"], content: c =>
        {
            c.Text([Text.Caption, "text-muted-foreground"], label);
            c.Text([Text.H5, "tabular-nums text-foreground"], value);
        });
    }

    private void RenderDebtCard(UIView view, DebtAccount d)
    {
        var paid = d.StartingBalance > 0 ? (double)Math.Clamp((d.StartingBalance - d.Balance) / d.StartingBalance, 0, 1) * 100 : 0;
        var cleared = d.Balance <= 0;

        view.Box([Card.Default, "p-5 flex flex-col gap-4", cleared ? "opacity-80" : ""], key: $"debt-{d.Id}", content: view =>
        {
            view.Row(["items-start gap-3"], content: view =>
            {
                IconTile(view, Fin.DebtKindIcon(d.Kind), cleared ? "success" : d.Apr >= 10 ? "warning" : "info");
                view.Column(["flex-1 min-w-0 gap-0"], content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground truncate"], d.Name);
                    c.Text([Text.Caption, "text-muted-foreground truncate"], $"{Fin.DebtKindLabel(d.Kind)} · {d.Lender}");
                });
                view.Column(["items-end gap-0 shrink-0"], content: c =>
                {
                    c.Text([Text.H4, "tabular-nums text-foreground"], cleared ? "Paid off" : Fin.Eur(d.Balance, 2));
                    c.Text([Text.Caption, "text-muted-foreground"], $"of {Fin.Eur(d.StartingBalance)}");
                });
            });

            view.Column(["gap-1.5"], content: c =>
            {
                c.Progress(value: paid, max: 100, tone: cleared ? SemanticTone.Success : SemanticTone.Neutral, ariaLabel: $"{d.Name} paid off");
                c.Text([Text.Caption, "text-muted-foreground"], $"{Fin.Pct(paid)} paid off");
            });

            view.Row(["gap-2 flex-wrap"], content: r =>
            {
                r.Badge($"{d.Apr.ToString("0.0#", Fin.Inv)}% APR", tone: d.Apr >= 10 ? SemanticTone.Warning : SemanticTone.Neutral, size: BadgeSize.Sm);
                r.Badge($"Min {Fin.Eur(d.MinPayment)}/mo", size: BadgeSize.Sm);
                r.Badge($"Due on the {Ordinal(d.DueDay)}", size: BadgeSize.Sm);
            });

            view.Row(["gap-2 flex-wrap pt-1 border-t border-secondary"], content: r =>
            {
                r.Button([Button.OutlineSm, "mt-3"], text: "Log payment", icon: "circle-dollar-sign", ariaLabel: $"Log payment {d.Name}", disabled: cleared,
                    onClick: () => OpenDebtPayment(d));
                r.Button([Button.GhostSm, "mt-3"], text: "Edit", ariaLabel: $"Edit {d.Name}", onClick: () => OpenEditDebt(d));
                r.Button([Button.GhostErrorSm, "mt-3"], text: "Remove", ariaLabel: $"Remove {d.Name}", onClick: () => RemoveDebt(d));
            });
        });
    }

    private static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? $"{n}th" : (n % 10) switch
    {
        1 => $"{n}st",
        2 => $"{n}nd",
        3 => $"{n}rd",
        _ => $"{n}th",
    };

    private void RemoveDebt(DebtAccount d)
    {
        var index = _debts.IndexOf(d);
        _debts.RemoveAll(x => x.Id == d.Id);
        OfferUndo($"Removed {d.Name}", () => _debts.Insert(Math.Clamp(index, 0, _debts.Count), d));
    }

    private void LogDebtPayment(DebtAccount d, decimal amount)
    {
        amount = Math.Min(amount, d.Balance);
        var txn = new Txn(Fin.NewId(), Today, $"{d.Name} payment", "Debt payments", -amount, d.Id);
        _debts.ReplaceWhere(x => x.Id == d.Id, x => x with { Balance = Math.Max(0, x.Balance - amount) });
        _txns.Insert(0, txn);

        OfferUndo($"Logged {Fin.Eur(amount, 2)} to {d.Name}", () =>
        {
            _debts.ReplaceWhere(x => x.Id == d.Id, x => x with { Balance = x.Balance + amount });
            _txns.RemoveAll(t => t.Id == txn.Id);
        });
    }
}
