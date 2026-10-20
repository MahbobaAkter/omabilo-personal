public partial class OmabiloApp
{
    private static readonly (string Icon, string Label)[] GoalIcons =
    [
        ("piggy-bank", "Piggy bank"),
        ("life-buoy", "Safety net"),
        ("plane", "Travel"),
        ("laptop", "Tech"),
        ("car", "Car"),
        ("house", "Home"),
        ("graduation-cap", "Study"),
        ("gift", "Gift"),
        ("heart", "Wellbeing"),
        ("bike", "Bike"),
    ];

    private static decimal MonthlyNeeded(SavingsGoal g)
    {
        var remaining = g.Target - g.Saved;

        if (remaining <= 0)
        {
            return 0;
        }

        var today = DateTime.UtcNow.Date;
        var months = Math.Max(1, (g.TargetDate.Year - today.Year) * 12 + g.TargetDate.Month - today.Month);
        return Math.Ceiling(remaining / months);
    }

    private void RenderGoals(UIView view)
    {
        var target = _goals.Sum(g => g.Target);
        var saved = TotalSaved();
        var monthly = _goals.Sum(MonthlyNeeded);
        var reached = _goals.Count(g => g.Saved >= g.Target);
        var savedThisMonth = SavedIn(MonthStart(Today));

        SectionHeader(view, "Savings & goals", "Give your savings a purpose and watch each goal fill up.", actions: a =>
            a.Button([Button.PrimaryMd], text: "New goal", icon: "plus", onClick: OpenAddGoal));

        view.Grid(["grid grid-cols-2 xl:grid-cols-4 gap-3 sm:gap-4"], content: view =>
        {
            StatTile(view, "Total saved", Fin.Eur(saved), target > 0 ? $"{Fin.Pct((double)(saved / target) * 100)} of {Fin.Eur(target)}" : "Start a goal", "piggy-bank", "success", null);
            StatTile(view, "Added this month", Fin.Eur(savedThisMonth), "Transfers into your goals", "arrow-down-to-line", "brand", null);
            StatTile(view, "Needed per month", Fin.Eur(monthly), "To hit every goal on time", "calendar-range", monthly > (_buckets.FirstOrDefault(b => b.Id == "savings")?.Planned ?? 0) ? "warning" : "info", null);
            StatTile(view, "Goals reached", $"{reached} / {_goals.Count}", reached > 0 ? "Well done 🎉" : "Your first is close", "trophy", "warning", null);
        });

        var savingsPlan = _buckets.FirstOrDefault(b => b.Id == "savings")?.Planned ?? 0;

        if (monthly > savingsPlan && _goals.Count > 0)
        {
            view.Alert("Your payday plan saves less than your goals need", tone: SemanticTone.Info,
                description: $"Goals need {Fin.Eur(monthly)} a month; your Savings bucket is {Fin.Eur(savingsPlan)}. Raise the bucket or move a target date.",
                action: a => a.Button([Button.OutlineSm], text: "Open Payday Planner", onClick: () => Go("payday")));
        }

        if (_goals.Count == 0)
        {
            view.Box([Card.Default, "p-6"], content: v =>
                v.EmptyState("No goals yet", description: "An emergency fund of three months' essentials is a great first goal.", icon: "target",
                    action: a => a.Button([Button.PrimaryMd], text: "New goal", onClick: OpenAddGoal)));
            return;
        }

        view.Grid(["grid grid-cols-1 md:grid-cols-2 gap-4 lg:gap-6"], content: view =>
        {
            foreach (var g in _goals.OrderBy(g => g.Saved >= g.Target).ThenByDescending(g => g.IsEmergencyFund).ThenBy(g => g.TargetDate))
            {
                RenderGoalCard(view, g);
            }
        });
    }

    private void RenderGoalCard(UIView view, SavingsGoal g)
    {
        var pct = g.Target > 0 ? (double)Math.Min(1, g.Saved / g.Target) * 100 : 0;
        var done = g.Saved >= g.Target;
        var needed = MonthlyNeeded(g);
        var daysLeft = (int)(g.TargetDate - Today).TotalDays;

        view.Box([done ? Card.Default : Card.Elevated, "p-5 sm:p-6 flex flex-col gap-4"], key: $"goal-{g.Id}", content: view =>
        {
            view.Row(["items-start gap-3"], content: r =>
            {
                IconTile(r, g.IconName, done ? "success" : "brand", "w-12 h-12");
                r.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                {
                    c.Row(["items-center gap-2 flex-wrap"], content: rr =>
                    {
                        rr.Text([Text.BodyStrong, "text-foreground"], g.Name);

                        if (g.IsEmergencyFund)
                        {
                            rr.Badge("Safety net", tone: SemanticTone.Info, size: BadgeSize.Sm);
                        }

                        if (done)
                        {
                            rr.Badge("Reached", tone: SemanticTone.Success, size: BadgeSize.Sm);
                        }
                    });
                    c.Text([Text.Caption, "text-muted-foreground"],
                        done ? $"Target {Fin.Eur(g.Target)} reached" : $"Target {Fin.Eur(g.Target)} by {g.TargetDate.ToString("MMM yyyy", Fin.Inv)}{(daysLeft < 0 ? " · date passed" : "")}");
                });
                r.Text([Text.H5, "tabular-nums text-brand-secondary shrink-0"], Fin.Pct(pct));
            });

            view.Column(["gap-2"], content: c =>
            {
                c.Row(["items-baseline justify-between gap-2"], content: rr =>
                {
                    rr.Text([Text.H3, "tabular-nums text-foreground"], Fin.Eur(g.Saved));
                    rr.Text([Text.Caption, "tabular-nums text-muted-foreground"], done ? "All there" : $"{Fin.Eur(g.Target - g.Saved)} to go");
                });
                c.Progress(value: pct, max: 100, tone: done ? SemanticTone.Success : SemanticTone.Neutral, ariaLabel: $"{g.Name} progress");
                if (!done)
                {
                    c.Text([Text.Caption, "text-muted-foreground"], $"Save {Fin.Eur(needed)} a month to get there on time.");
                }
            });

            view.Row(["gap-2 flex-wrap pt-3 border-t border-secondary"], content: r =>
            {
                foreach (var quick in new[] { 25m, 50m })
                {
                    r.Button([Button.OutlineSm], text: $"+{Fin.Eur(quick)}", ariaLabel: $"Add {Fin.Eur(quick)} to {g.Name}", disabled: done,
                        onClick: () => MoveGoalMoney(g, quick));
                }

                r.Button([Button.OutlineSm], text: "Add money", ariaLabel: $"Add money to {g.Name}", icon: "plus", disabled: done, onClick: () => OpenGoalMoney(g, true));
                r.Button([Button.GhostSm], text: "Withdraw", ariaLabel: $"Withdraw from {g.Name}", disabled: g.Saved <= 0, onClick: () => OpenGoalMoney(g, false));
                r.Button([Button.GhostSm], text: "Edit", ariaLabel: $"Edit {g.Name}", onClick: () => OpenEditGoal(g));
                r.Button([Button.GhostErrorSm], text: "Remove", ariaLabel: $"Remove {g.Name}", onClick: () => RemoveGoal(g));
            });
        });
    }

    // Positive amounts move money from the current account into the goal, negative ones back out
    private void MoveGoalMoney(SavingsGoal g, decimal amount)
    {
        amount = amount < 0 ? -Math.Min(-amount, g.Saved) : amount;

        if (amount == 0)
        {
            return;
        }

        var txn = new Txn(Fin.NewId(), Today, amount > 0 ? $"Transfer to {g.Name}" : $"Withdrawal from {g.Name}", "Savings", -amount, g.Id);
        _goals.ReplaceWhere(x => x.Id == g.Id, x => x with { Saved = x.Saved + amount });
        _txns.Insert(0, txn);

        var after = g.Saved + amount;
        var message = amount > 0
            ? after >= g.Target && g.Saved < g.Target ? $"🎉 {g.Name} reached!" : $"Added {Fin.Eur(amount, 2)} to {g.Name}"
            : $"Withdrew {Fin.Eur(-amount, 2)} from {g.Name}";

        OfferUndo(message, () =>
        {
            _goals.ReplaceWhere(x => x.Id == g.Id, x => x with { Saved = x.Saved - amount });
            _txns.RemoveAll(t => t.Id == txn.Id);
        });
    }

    private void RemoveGoal(SavingsGoal g)
    {
        var index = _goals.IndexOf(g);
        _goals.RemoveAll(x => x.Id == g.Id);
        OfferUndo($"Removed {g.Name}", () => _goals.Insert(Math.Clamp(index, 0, _goals.Count), g));
    }
}
