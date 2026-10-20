public partial class OmabiloApp
{
    private static readonly Dictionary<string, string> BucketColors = new()
    {
        ["bills"] = "bg-teal-700",
        ["debt"] = "bg-indigo-500",
        ["savings"] = "bg-lime-500",
        ["everyday"] = "bg-amber-400",
        ["fun"] = "bg-rose-400",
    };

    private string BucketColor(string id) => BucketColors.GetValueOrDefault(id, "bg-slate-400");

    private void AdjustBucket(string id, decimal delta) =>
        _buckets.ReplaceWhere(b => b.Id == id, b => b with { Planned = Math.Max(0, b.Planned + delta) });

    private void ApplyTemplate(string name, Func<PayBucket, decimal> planned)
    {
        var before = _buckets.ToList();
        _buckets.ReplaceAll(before.Select(b => b with { Planned = Math.Round(planned(b) / 5m) * 5m, Moved = false }).ToList());
        OfferUndo($"Applied {name}", () => _buckets.ReplaceAll(before));
    }

    private void RenderPayday(UIView view)
    {
        var p = _payday.Value;
        var payday = NextPayday();
        var days = DaysToPayday();
        var balance = CurrentBalance();
        var bills = BillsBeforePayday();
        var billTotal = bills.Sum(b => b.Amount ?? 0);
        var plan = ComputePaydayPlan();
        var safe = plan.SafeToSpend;
        var planned = _buckets.Sum(b => b.Planned);
        var left = p.NetPay - planned;

        SectionHeader(view, "Before your next pay lands", "Give every euro of your next pay a job, and see what's safe to spend until then.", actions: a =>
            a.Button([Button.OutlineMd], text: "Pay details", icon: "settings-2", onClick: OpenPaydaySettings));

        view.Grid(["grid grid-cols-1 lg:grid-cols-3 gap-4 lg:gap-6"], content: view =>
        {
            // ── Countdown ────────────────────────────────────────────────
            view.Box(["rounded-2xl bg-brand-solid p-5 sm:p-6 flex flex-col gap-3 shadow-md"], content: view =>
            {
                view.Row(["items-center gap-2"], content: r =>
                {
                    r.Icon([Icon.Sm, "text-primary-on-brand/80"], name: "calendar-heart");
                    r.Text([Text.Label, "text-primary-on-brand/80"], "Next payday");
                });
                view.Row(["items-baseline gap-2"], content: r =>
                {
                    r.Text(["text-6xl font-bold tabular-nums leading-none text-primary-on-brand"], days.ToString());
                    r.Text([Text.BodyLg, "text-primary-on-brand/90"], days == 1 ? "day to go" : "days to go");
                });
                view.Text([Text.BodyStrong, "text-primary-on-brand"], Fin.DayLong(payday));
                view.Text([Text.BodySm, "text-primary-on-brand/85"], $"Expected net pay {Fin.Eur(p.NetPay)} from Lumi Studio Oy (demo employer).");
            });

            // ── Safe to spend ────────────────────────────────────────────
            view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-3"], content: view =>
            {
                view.Text([Text.Label, "text-muted-foreground"], "Safe to spend until payday");
                view.Text([Text.Display, "tabular-nums tracking-tight", safe >= 0 ? "text-foreground" : "text-error-600"], Fin.Eur(safe));
                view.Text([Text.BodySm, "text-muted-foreground"], safe > 0 ? $"≈ {Fin.Eur(safe / days)} per day for {days} days" : "Move money or snooze a bill to stay above zero.");
                view.Separator(["default", "my-1"]);
                view.Text([Text.Overline, "text-muted-foreground"], "How it's worked out");
                CalcLine(view, "Current account today", Fin.Eur(plan.Balance, 2), sureness: Sureness.Confirmed);

                foreach (var l in plan.Bills.Concat(plan.DebtPayments).Concat(plan.Savings))
                {
                    CalcLine(view, $"{l.Label} · {Fin.Day(l.Date!.Value)}", Fin.Eur(-l.Amount, 2), sureness: l.Sureness);
                }

                CalcLine(view, "Safe to spend", Fin.Eur(safe, 2), strong: true);

                if (plan.NotCounted.Count > 0)
                {
                    view.Column(["gap-1 rounded-xl bg-muted/60 p-3"], content: c =>
                    {
                        c.Text([Text.Caption, "font-semibold text-foreground"], "Not counted, so nothing is counted twice");
                        foreach (var (label, reason) in plan.NotCounted)
                        {
                            c.Text([Text.Caption, "text-muted-foreground"], $"{label}: {reason}");
                        }
                    });
                }

                view.Column(["gap-1 rounded-xl border border-dashed border-secondary p-3"], content: c =>
                {
                    c.Row(["items-center gap-2"], content: r =>
                    {
                        r.Text([Text.Caption, "font-semibold text-foreground"], "At your usual pace");
                        SurenessBadge(r, Sureness.Estimate);
                    });
                    c.Text([Text.Caption, "text-muted-foreground"],
                        $"Last month you spent about {Fin.Eur(plan.EverydayPerDay)} a day on groceries, eating out and shopping. {days} days like that is {Fin.Eur(plan.EverydayEstimate)}, leaving about {Fin.Eur(plan.LeftAfterEveryday)} at payday.");
                });
            });

            // ── Bills before payday ──────────────────────────────────────
            view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-3"], content: view =>
            {
                view.Row(["items-center justify-between"], content: r =>
                {
                    r.Text([Text.H5, "text-foreground"], "Due before payday");
                    r.Text([Text.Caption, "tabular-nums text-muted-foreground"], Fin.Eur(billTotal, 2));
                });

                if (bills.Count == 0)
                {
                    view.EmptyState("No bills before payday", description: "Everything due before your pay is handled.", icon: "circle-check");
                }

                foreach (var b in bills)
                {
                    view.Row(["items-center gap-3"], key: $"pb-{b.Id}", content: r =>
                    {
                        IconTile(r, "receipt", InboxTone(b), "w-9 h-9");
                        r.Column(["flex-1 min-w-0 gap-0"], content: c =>
                        {
                            c.Text([Text.BodySm, "font-medium truncate text-foreground"], b.From);
                            c.Text([Text.Caption, "text-muted-foreground"], $"{Fin.Eur(b.Amount ?? 0, 2)} · {DueText(b.DueDate!.Value)}");
                        });
                        r.Button([Button.OutlineSm, "shrink-0"], text: "Pay", ariaLabel: $"Pay {b.From}", onClick: () => MarkBillPaid(b));
                    });
                }
            });
        });

        RenderSpendGuard(view, plan);

        // ── Plan the next pay ───────────────────────────────────────────────
        view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-5"], content: view =>
        {
            SectionHeader(view, "Plan your next pay", $"Split {Fin.Eur(p.NetPay)} across what matters before it arrives.", actions: a =>
            {
                a.Button([Button.OutlineSm], text: "50 / 30 / 20", onClick: () =>
                {
                    // 50% needs, 30% wants, 20% to the future (debt payments first, the rest saved)
                    var net = _payday.Value.NetPay;
                    var debtShare = Math.Min(_debts.Where(d => d.Balance > 0).Sum(d => d.MinPayment) + (decimal)_extraPayment.Value, net * 0.2m);
                    ApplyTemplate("the 50/30/20 split", b => b.Id switch
                    {
                        "bills" => net * 0.5m,
                        "everyday" => net * 0.3m * 0.75m,
                        "fun" => net * 0.3m * 0.25m,
                        "debt" => debtShare,
                        "savings" => net * 0.2m - debtShare,
                        _ => b.Planned,
                    });
                });
                a.Button([Button.OutlineSm], text: "Use last month", onClick: () =>
                {
                    var last = MonthStart(Today).AddMonths(-1);
                    var spentBy = TxnsIn(last).Where(t => t.Amount < 0).GroupBy(t => t.Category).ToDictionary(g => g.Key, g => -g.Sum(t => t.Amount));
                    decimal Cat(params string[] cats) => cats.Sum(c => spentBy.GetValueOrDefault(c));
                    ApplyTemplate("last month's spending", b => b.Id switch
                    {
                        "bills" => Cat("Housing", "Utilities"),
                        "debt" => Cat("Debt payments"),
                        "savings" => Cat("Savings"),
                        "everyday" => Cat("Groceries", "Transport", "Health"),
                        "fun" => Cat("Dining out", "Entertainment", "Shopping"),
                        _ => b.Planned,
                    });
                });
            });

            // Allocation bar
            view.Column(["gap-2"], content: view =>
            {
                view.Row(["h-4 w-full rounded-full overflow-hidden bg-muted"], ariaLabel: "How your pay is split", content: bar =>
                {
                    foreach (var b in _buckets.Where(b => b.Planned > 0))
                    {
                        var pct = p.NetPay > 0 ? (double)Math.Min(1, b.Planned / Math.Max(p.NetPay, planned)) * 100 : 0;
                        bar.Box([$"h-full {BucketColor(b.Id)} transition-all duration-300 w-[{pct.ToString("0.##", Fin.Inv)}%]"], key: $"seg-{b.Id}");
                    }
                });

                view.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
                {
                    r.Text([Text.BodySm, "text-muted-foreground"], $"Planned {Fin.Eur(planned)} of {Fin.Eur(p.NetPay)}");

                    if (left == 0)
                    {
                        r.Badge("Every euro has a job", tone: SemanticTone.Success, size: BadgeSize.Md);
                    }
                    else if (left > 0)
                    {
                        r.Badge($"{Fin.Eur(left)} left to plan", tone: SemanticTone.Warning, size: BadgeSize.Md);
                    }
                    else
                    {
                        r.Badge($"Over by {Fin.Eur(-left)}", tone: SemanticTone.Error, size: BadgeSize.Md);
                    }
                });
            });

            view.Column(["gap-3"], content: view =>
            {
                foreach (var b in _buckets)
                {
                    RenderBucketRow(view, b, p.NetPay);
                }
            });

            var minimums = _debts.Where(d => d.Balance > 0).Sum(d => d.MinPayment);
            var debtBucket = _buckets.FirstOrDefault(b => b.Id == "debt")?.Planned ?? 0;

            if (debtBucket < minimums)
            {
                view.Alert("Debt bucket is below your minimums", tone: SemanticTone.Warning,
                    description: $"Your minimum payments add up to {Fin.Eur(minimums)}. Missing one can add fees and hurt your credit.",
                    action: a => a.Button([Button.OutlineSm], text: $"Set to {Fin.Eur(minimums)}",
                        onClick: () => AdjustBucket("debt", minimums - debtBucket)));
            }
            else if (left > 0)
            {
                view.Alert($"{Fin.Eur(left)} has no job yet", tone: SemanticTone.Info,
                    description: "Unplanned money tends to disappear. Give it to savings, or put it toward your highest-interest debt.",
                    action: a => a.Button([Button.OutlineSm], text: "Add to savings", onClick: () => AdjustBucket("savings", left)));
            }
        });
    }

    private static void CalcLine(UIView view, string label, string value, bool strong = false, Sureness? sureness = null)
    {
        view.Row(["items-center justify-between gap-2"], content: r =>
        {
            r.Text([strong ? Text.BodyStrong : Text.BodySm, "flex-1 min-w-0", strong ? "text-foreground" : "text-muted-foreground"], label);

            if (sureness is { } s)
            {
                SurenessBadge(r, s);
            }

            r.Text([strong ? Text.BodyStrong : Text.BodySm, "tabular-nums whitespace-nowrap shrink-0 text-foreground"], value);
        });
    }

    /// <summary>Confirmed = recorded or received; Scheduled = a fixed amount still to happen; Estimate = a typical figure.</summary>
    private static void SurenessBadge(UIView view, Sureness s) => view.Badge(s switch
    {
        Sureness.Confirmed => "Confirmed",
        Sureness.Scheduled => "Scheduled",
        _ => "Estimate",
    }, tone: s switch
    {
        Sureness.Confirmed => SemanticTone.Success,
        Sureness.Scheduled => SemanticTone.Info,
        _ => SemanticTone.Warning,
    }, size: BadgeSize.Sm);

    private readonly ClientReactive<string> _spendAmount = new("");

    private void RenderSpendGuard(UIView view, PaydayPlan plan)
    {
        view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
        {
            SectionHeader(view, "Smart Spend Guard", "Thinking of buying something? See what it does to the rest of your month before you decide.");

            view.Row(["items-end gap-2 flex-wrap"], content: r =>
            {
                r.TextField(["default", "w-44"], label: "Price (€)", bind: _spendAmount, type: "number", min: "0", step: "1", placeholder: "e.g. 120");

                foreach (var quick in new[] { 25m, 60m, 150m })
                {
                    r.Button([Button.OutlineSm, "h-10"], text: Fin.Eur(quick), ariaLabel: $"Try {Fin.Eur(quick)}",
                        onClick: () => _spendAmount.Value = quick.ToString(Fin.Inv));
                }
            });

            if (!Fin.TryParseAmount(_spendAmount.Value, out var amount) || amount <= 0)
            {
                view.Text([Text.BodySm, "text-muted-foreground"], "Enter a price to see how it fits. Nothing is spent or saved here; it's only a check.");
                return;
            }

            var check = CheckPurchase(plan, amount);
            view.Alert(check.Verdict, tone: check.Tone switch { "error" => SemanticTone.Error, "warning" => SemanticTone.Warning, _ => SemanticTone.Success });

            view.Column(["gap-1.5"], content: c =>
            {
                CalcLine(c, "Safe to spend until payday now", Fin.Eur(plan.SafeToSpend, 2));
                CalcLine(c, "This purchase", Fin.Eur(-amount, 2));
                CalcLine(c, "Safe to spend afterwards", Fin.Eur(check.SafeAfter, 2), strong: true);
                CalcLine(c, "Per day until payday", $"{Fin.Eur(check.PerDayBefore, 2)} → {Fin.Eur(check.PerDayAfter, 2)}");
                CalcLine(c, "Left at payday at your usual pace", Fin.Eur(check.LeftAfterEveryday, 2), sureness: Sureness.Estimate);
            });

            if (check.AffectedGoal is { } g)
            {
                var after = g.Saved - Math.Min(check.FromSavings, g.Saved);
                view.Text([Text.BodySm, "text-foreground"],
                    $"Covering the {Fin.Eur(check.FromSavings)} gap from \"{g.Name}\" would take it from {Fin.Eur(g.Saved)} to {Fin.Eur(after)} " +
                    $"({Fin.Pct((double)(after / g.Target) * 100)} of its {Fin.Eur(g.Target)} target).");
            }

            view.Text([Text.Caption, "text-muted-foreground"],
                "Assumptions: bills and loan payments due before payday are paid on time; \"usual pace\" is last month's everyday spending; savings transfers happen after payday.");
        });
    }

    private void RenderBucketRow(UIView view, PayBucket b, decimal netPay)
    {
        var pct = netPay > 0 ? (double)(b.Planned / netPay) * 100 : 0;

        view.Box(["rounded-2xl border border-secondary p-3 sm:p-4 flex flex-col sm:flex-row sm:items-center gap-3"], key: $"bucket-{b.Id}", content: view =>
        {
            view.Row(["items-center gap-3 flex-1 min-w-0"], content: r =>
            {
                r.Box([$"w-1.5 self-stretch rounded-full {BucketColor(b.Id)} shrink-0"]);
                IconTile(r, b.IconName, "brand", "w-10 h-10");
                r.Column(["flex-1 min-w-0 gap-0"], content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground"], b.Name);
                    c.Text([Text.Caption, "text-muted-foreground truncate"], $"{b.Hint} · {Fin.Pct(pct)} of pay");
                });
            });

            view.Row(["items-center gap-2 justify-between sm:justify-end"], content: r =>
            {
                r.Row(["items-center gap-1"], content: rr =>
                {
                    rr.Button([Button.OutlineSm, Button.Icon, "h-10 w-10"], ariaLabel: $"Less for {b.Name} (−€25)",
                        disabled: b.Planned <= 0, onClick: () => AdjustBucket(b.Id, -25), content: v => v.Icon([Icon.Sm], name: "minus"));
                    rr.Button(["h-10 min-w-24 px-3 rounded-xl hover:bg-muted text-base font-semibold tabular-nums text-foreground transition-colors"],
                        text: Fin.Eur(b.Planned), ariaLabel: $"Set amount for {b.Name}, now {Fin.Eur(b.Planned)}",
                        onClick: () => OpenBucketAmount(b));
                    rr.Button([Button.OutlineSm, Button.Icon, "h-10 w-10"], ariaLabel: $"More for {b.Name} (+€25)",
                        onClick: () => AdjustBucket(b.Id, 25), content: v => v.Icon([Icon.Sm], name: "plus"));
                });

                r.Checkbox(value: b.Moved, label: "Moved", ariaLabel: $"Moved {b.Name}",
                    onValueChange: async moved => _buckets.ReplaceWhere(x => x.Id == b.Id, x => x with { Moved = moved }));
            });
        });
    }
}
