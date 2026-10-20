// Debts and Goals: two separate screens over the signed-in person's own debts and savings goals, from
// the database (PlanService). Every number is one they typed; Omabilo can't see a bank or lender and
// never pays anything. Data is loaded in handlers, never while rendering.

public partial class OmabiloApp
{
    private readonly ClientReactive<MyPlansSnapshot?> _plans = new((MyPlansSnapshot?)null);

    // Which form is open: "new" or a record id; null when closed
    private readonly ClientReactive<string?> _pDebtOpen = new((string?)null);
    private readonly ClientReactive<string?> _pGoalOpen = new((string?)null);
    private readonly ClientReactive<string?> _pMoneyGoal = new((string?)null);
    private readonly ClientReactive<string?> _pConfirm = new((string?)null);
    private readonly ClientReactive<IReadOnlyList<string>> _pErrors = new(Array.Empty<string>());

    private readonly ClientReactive<string> _pName = new("");
    private readonly ClientReactive<string> _pLender = new("");
    private readonly ClientReactive<string> _pKind = new(nameof(MyDebtKind.CreditCard));
    private readonly ClientReactive<string> _pBalance = new("");
    private readonly ClientReactive<string> _pApr = new("");
    private readonly ClientReactive<string> _pMin = new("");
    private readonly ClientReactive<string> _pDue = new("");
    private readonly ClientReactive<string> _pTarget = new("");
    private readonly ClientReactive<string> _pDate = new("");
    private readonly ClientReactive<bool> _pEmergency = new(false);
    private readonly ClientReactive<string> _pAmount = new("");
    private readonly ClientReactive<string?> _pPayDebt = new((string?)null);

    private PlanService? _planService;

    private PlanService Plans => _planService ??= new PlanService(Store, () => DateTime.UtcNow);

    private async Task LoadPlansAsync(string uid) => _plans.Value = await Plans.LoadAsync(uid, CancellationToken.None);

    /// <summary>The top of Debts and Goals: the sign-in and Premium checks, a heading, and the loaded plans.</summary>
    private MyPlansSnapshot? PlansScreenStart(UIView view, string title, string intro, string lockText)
    {
        if (MyUserId == null)
        {
            SignedOutCard(view);
            return null;
        }

        if (!IsPremium())
        {
            PremiumLock(view, PremiumFeature.DebtsAndGoals, lockText);
            return null;
        }

        view.Column(["gap-1"], content: c =>
        {
            c.Heading(title, style: [Text.H4, "m-0 text-foreground"], level: 2);
            c.Text([Text.BodySm, "text-muted-foreground"], intro);
        });
        LoadStates(view);
        return _plans.Value;
    }

    private void RenderDebtsScreen(UIView view)
    {
        if (PlansScreenStart(view, "Your debts", "Only what you enter here. Omabilo can't see your bank or lenders, never pays anything and never contacts anyone for you.",
                "Track what you owe in one place: balances, monthly payments, due days and when each debt could be paid off. You type every number; Omabilo never connects to a bank or lender.") is not { } p)
        {
            return;
        }

        var today = DateTime.UtcNow.Date;
        var next = p.Debts.Select(d => (Debt: d, Date: PlanRules.NextPayment(d, today))).Where(x => x.Date != null).OrderBy(x => x.Date).FirstOrDefault();
        var debtFree = PlanRules.MonthsUntilDebtFree(p.Debts);
        var open = p.Debts.Count(d => d.Balance > 0);

        view.Grid(["grid grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-4"], content: grid =>
        {
            StatTile(grid, "Still owed", Money(p.TotalDebt), $"{open} open debt{(open == 1 ? "" : "s")}", "trending-down", p.TotalDebt > 0 ? "warning" : "neutral", null);
            StatTile(grid, "Monthly payments", Money(p.MonthlyMinimums), "The payments you entered", "calendar-clock", "brand", null);
            StatTile(grid, "Next payment", next.Debt != null ? Day(next.Date!.Value) : "No due days",
                next.Debt is { } nd ? nd.Name + (nd.MinPayment is { } m ? $" · {Money(m)}" : "") : "Add a due day to a debt", "alarm-clock", "info", null);
            StatTile(grid, "Debt-free", p.TotalDebt <= 0 ? "Nothing owed" : debtFree is { } months ? MonthsText(months) : "Can't tell yet",
                p.TotalDebt <= 0 ? "No open debts" : debtFree != null ? "Estimate at the payments you entered" : "Every debt needs a monthly payment that covers its interest",
                "flag", debtFree != null || p.TotalDebt <= 0 ? "success" : "neutral", null);
        });

        var notRecorded = p.Debts.Where(d => PlanRules.PaymentNotRecorded(d, today)).ToList();

        if (notRecorded.Count > 0)
        {
            view.Alert("Payment day passed", tone: SemanticTone.Warning,
                description: $"{string.Join(", ", notRecorded.Select(d => d.Name))}: the due day you entered has passed and no payment is recorded here this month. If you've paid, record the payment; if not, contact the lender before fees are added.");
        }

        var incomplete = p.Debts.Where(d => PlanRules.MissingForPlan(d).Count > 0).ToList();

        if (incomplete.Count > 0)
        {
            view.Alert("Some details are missing", tone: SemanticTone.Info,
                description: string.Join(" ", incomplete.Select(d => $"{d.Name}: add the {string.Join(", ", PlanRules.MissingForPlan(d))}.")) + " Without them the payoff estimates and reminders are less complete.");
        }

        RenderMyDebts(view, p);
        RenderDebtLetters(view);
        RenderPlanDialogs(view, p);
    }

    // Collection letters and loan instalments found in the Financial Inbox. They link to their source
    // but never change a balance above: only the person enters balances.
    private void RenderDebtLetters(UIView view)
    {
        if (_my.Value is not { } snap)
        {
            return;
        }

        var today = DateTime.UtcNow.Date;
        var letters = snap.Items.Where(ItemRules.IsDebt).OrderByDescending(i => i.DueDate ?? i.EventDate ?? i.CreatedUtc).ToList();

        if (letters.Count > 0)
        {
            view.Box([Card.Default, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
            {
                card.Heading($"Loan and debt letters ({letters.Count})", style: [Text.H5, "m-0 text-foreground"], level: 3);
                card.Text([Text.BodySm, "text-muted-foreground"], "Found in your Financial Inbox. They don't change the balances above; update a debt yourself if a letter shows a new amount.");

                foreach (var item in letters)
                {
                    ItemRow(card, item, ItemRules.StatusOf(item, today), false);
                }
            });
        }

        RenderItemSheet(view, snap);
    }

    private static string MonthsText(int m) => m == 0 ? "Now" : m < 12 ? $"{m} month{(m == 1 ? "" : "s")}" : $"{m / 12} y {m % 12} mo";

    private void RenderGoalsScreen(UIView view)
    {
        if (PlansScreenStart(view, "Your savings goals", "Record money as you put it aside, and Omabilo adds it up. Your real savings account isn't touched.",
                "Save toward an emergency fund or anything you want: targets, dates, progress and the monthly amount that gets you there. You type every number.") is not { } p)
        {
            return;
        }

        var target = p.Goals.Sum(g => g.Goal.Target);
        var toGo = p.Goals.Sum(g => Math.Max(0m, g.Goal.Target - g.Saved));

        view.Grid(["grid grid-cols-1 sm:grid-cols-3 gap-3 sm:gap-4"], content: grid =>
        {
            StatTile(grid, "Saved toward goals", Money(p.TotalSaved), $"{p.Goals.Count} goal{(p.Goals.Count == 1 ? "" : "s")}", "target", "success", null);
            StatTile(grid, "Targets", Money(target), $"{p.Goals.Count(g => g.Percent >= 100)} reached", "target", "brand", null);
            StatTile(grid, "Still to save", Money(toGo), "Across all goals", "hourglass", toGo > 0 ? "info" : "success", null);
        });

        view.Text([Text.Caption, "text-muted-foreground"],
            "Money in your goals is set aside. The Payday Planner counts only the balance you enter there, so enter money you can spend, not savings, to avoid counting it twice.");

        RenderMyGoals(view, p);
        RenderPlanDialogs(view, p);
    }

    private void RenderMyDebts(UIView view, MyPlansSnapshot p)
    {
        var today = DateTime.UtcNow.Date;

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-4"], content: card =>
        {
            card.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
            {
                r.Column(["gap-0.5"], content: c =>
                {
                    c.Heading("Debts", style: [Text.H5, "m-0 text-foreground"], level: 3);

                    if (p.Debts.Count(d => d.Apr != null) > 1)
                    {
                        c.Text([Text.Caption, "text-muted-foreground"], "Highest interest first: paying extra on the top one saves the most interest.");
                    }
                });
                r.Button([Button.PrimaryMd], text: "Add a debt", icon: "plus", disabled: _myBusy.Value, onClick: () => OpenDebtForm(null));
            });

            if (p.Debts.Count == 0)
            {
                card.EmptyState("No debts entered", description: "Add a loan, credit card or payment plan to see what you owe in one place and when each one could be paid off.", icon: "trending-down");
                return;
            }

            card.Column(["gap-2"], content: list =>
            {
                foreach (var d in p.Debts)
                {
                    list.Box(["rounded-2xl border border-secondary bg-card p-3 sm:p-4 flex flex-col gap-2"], key: $"debt-{d.Id}", content: row =>
                    {
                        row.Row(["items-start justify-between gap-3"], content: top =>
                        {
                            top.Column(["gap-0.5 min-w-0"], content: c =>
                            {
                                c.Text([Text.BodyStrong, "text-foreground break-words"], d.Name);
                                c.Text([Text.Caption, "text-muted-foreground"], string.Join(" · ", new[]
                                {
                                    PlanRules.KindName(d.Kind), d.Lender, d.Apr is { } a ? $"{a.ToString("0.##", CultureInfo.InvariantCulture)} % a year" : null,
                                    d.MinPayment is { } m ? $"{Money(m)} a month" : null, d.DueDay is { } day ? $"due on day {day}" : null,
                                }.Where(s => !string.IsNullOrEmpty(s))));
                            });
                            top.Text([Text.BodyStrong, "text-foreground tabular-nums shrink-0"], Money(d.Balance));
                        });

                        if (PlanRules.PaidPercent(d) is { } pct && d.StartingBalance > d.Balance)
                        {
                            row.Progress(value: pct, max: 100, tone: pct >= 100 ? SemanticTone.Success : SemanticTone.Neutral, ariaLabel: $"{d.Name}: {pct:0}% paid");
                            row.Text([Text.Caption, "text-muted-foreground tabular-nums"], $"{pct:0}% paid of {Money(d.StartingBalance!.Value)}");
                        }

                        var facts = new List<string>();

                        if (PlanRules.NextPayment(d, today) is { } nextDue)
                        {
                            facts.Add($"Next payment {Day(nextDue)}" + (PlanRules.PaymentNotRecorded(d, today) ? " (this month's isn't recorded)" : ""));
                        }

                        if (d.LastPaymentUtc is { } lp && d.LastPaymentAmount is { } la)
                        {
                            facts.Add($"You recorded {Money(la)} on {Day(lp)}");
                        }

                        if (facts.Count > 0)
                        {
                            row.Text([Text.Caption, "text-muted-foreground"], string.Join(" · ", facts));
                        }

                        row.Text([Text.BodySm, "text-muted-foreground"], PlanRules.PayoffText(d));

                        if (_pPayDebt.Value == d.Id)
                        {
                            row.Column(["gap-2 rounded-xl bg-muted p-3"], content: f =>
                            {
                                f.TextField(label: "Amount you paid (€)", placeholder: d.MinPayment is { } mp ? Money(mp).Replace(" €", "") : "100,00", bind: _pAmount);
                                f.Text([Text.Caption, "text-muted-foreground"], "Record a payment you already made in your bank. Omabilo doesn't pay anything.");
                                PlanErrors(f);
                                f.Row(["gap-2 flex-wrap"], content: r =>
                                {
                                    r.Button([Button.PrimarySm], text: "Record payment", disabled: _myBusy.Value, onClick: async () => await RecordDebtPaymentAsync(d));
                                    r.Button([Button.GhostSm], text: "Cancel", onClick: () => { _pPayDebt.Value = null; _pErrors.Value = []; });
                                });
                            });
                        }

                        row.Row(["gap-2 flex-wrap"], content: a =>
                        {
                            if (d.Balance > 0 && _pPayDebt.Value != d.Id)
                            {
                                a.Button([Button.OutlineSm], text: "Record a payment", ariaLabel: $"Record a payment for {d.Name}", disabled: _myBusy.Value,
                                    onClick: () => { _pPayDebt.Value = d.Id; _pAmount.Value = ""; _pErrors.Value = []; });
                            }

                            a.Button([Button.GhostSm], text: "Edit", ariaLabel: $"Edit {d.Name}", disabled: _myBusy.Value, onClick: () => OpenDebtForm(d));
                            a.Button([Button.GhostSm], text: "Delete", ariaLabel: $"Delete {d.Name}", disabled: _myBusy.Value, onClick: () => _pConfirm.Value = $"debt:{d.Id}");
                        });
                    });
                }
            });
        });
    }

    private async Task RecordDebtPaymentAsync(DebtRecord d)
    {
        var amount = TextInvoiceExtractor.ParseAmount(_pAmount.Value);

        await RunPlansAsync(async uid =>
        {
            var (before, error) = await Plans.RecordDebtPaymentAsync(uid, d.Id, amount, CancellationToken.None);
            _pErrors.Value = error is null ? [] : [error];

            if (before == null)
            {
                return null;
            }

            _pPayDebt.Value = null;
            OfferUndo($"Recorded {Money(amount!.Value)} paid on {d.Name}",
                () => _ = RunPlansAsync(async u => await Plans.RestoreDebtAsync(u, before, CancellationToken.None) ? "Payment removed." : null));
            return null;
        });
    }

    private void RenderMyGoals(UIView view, MyPlansSnapshot p)
    {
        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-4"], content: card =>
        {
            card.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
            {
                r.Heading("Savings goals", style: [Text.H5, "m-0 text-foreground"], level: 3);
                r.Button([Button.PrimaryMd], text: "Add a goal", icon: "plus", disabled: _myBusy.Value, onClick: () => OpenGoalForm(null));
            });

            if (p.Goals.Count == 0)
            {
                card.EmptyState("No goals yet", description: "Start with an emergency fund, or save for something you want. Record money as you put it aside.", icon: "target");
                return;
            }

            card.Grid(["grid grid-cols-1 lg:grid-cols-2 gap-3"], content: grid =>
            {
                foreach (var g in p.Goals)
                {
                    RenderGoal(grid, g);
                }
            });
        });
    }

    private void RenderGoal(UIView view, GoalProgress g)
    {
        var goal = g.Goal;

        view.Box(["rounded-2xl border border-secondary bg-card p-4 flex flex-col gap-3"], key: $"goal-{goal.Id}", content: box =>
        {
            box.Row(["items-start justify-between gap-3"], content: top =>
            {
                top.Column(["gap-0.5 min-w-0"], content: c =>
                {
                    c.Row(["items-center gap-2 flex-wrap"], content: t =>
                    {
                        t.Text([Text.BodyStrong, "text-foreground break-words"], goal.Name);

                        if (goal.IsEmergencyFund)
                        {
                            t.Badge("Emergency fund", tone: SemanticTone.Info, size: BadgeSize.Sm);
                        }
                    });
                    c.Text([Text.Caption, "text-muted-foreground tabular-nums"], $"{Money(g.Saved)} of {Money(goal.Target)}{(goal.TargetDate is { } d ? $" · by {Day(d)}" : "")}");
                });
                top.Text([Text.BodyStrong, "text-foreground tabular-nums shrink-0"], $"{g.Percent:0}%");
            });
            box.Progress(value: g.Percent, max: 100, tone: g.Percent >= 100 ? SemanticTone.Success : SemanticTone.Neutral, ariaLabel: $"{goal.Name} progress");
            box.Text([Text.BodySm, "text-muted-foreground"], g.Pace);

            if (_pMoneyGoal.Value == goal.Id)
            {
                box.Column(["gap-2 rounded-xl bg-muted p-3"], content: f =>
                {
                    f.TextField(label: "Amount (€)", placeholder: "50,00", bind: _pAmount);

                    if (_pErrors.Value.Count > 0)
                    {
                        f.Alert("Please fix", tone: SemanticTone.Error, description: string.Join(" ", _pErrors.Value));
                    }

                    f.Row(["gap-2 flex-wrap"], content: r =>
                    {
                        r.Button([Button.PrimarySm], text: "Put in", disabled: _myBusy.Value, onClick: async () => await SaveMoneyAsync(goal.Id, 1));
                        r.Button([Button.OutlineSm], text: "Take out", disabled: _myBusy.Value, onClick: async () => await SaveMoneyAsync(goal.Id, -1));
                        r.Button([Button.GhostSm], text: "Cancel", onClick: () => { _pMoneyGoal.Value = null; _pErrors.Value = []; });
                    });
                });
            }

            if (g.Contributions.Count > 0)
            {
                box.Column(["gap-1"], content: list =>
                {
                    foreach (var c in g.Contributions.Take(3))
                    {
                        list.Row(["items-center justify-between gap-2 min-h-10"], key: $"entry-{c.Id}", content: r =>
                        {
                            r.Text([Text.Caption, "text-muted-foreground tabular-nums"], $"{Day(c.DateUtc)} · {(c.Amount > 0 ? "put in" : "took out")} {Money(Math.Abs(c.Amount))}");
                            r.Button([Button.GhostSm], text: "Remove", ariaLabel: $"Remove {Money(Math.Abs(c.Amount))} from {Day(c.DateUtc)}", disabled: _myBusy.Value,
                                onClick: async () => await RunPlansAsync(async uid => await Plans.DeleteContributionAsync(uid, c.Id, CancellationToken.None) ? "Entry removed." : null));
                        });
                    }

                    if (g.Contributions.Count > 3)
                    {
                        list.Text([Text.Caption, "text-muted-foreground"], $"And {g.Contributions.Count - 3} earlier entries. All of them are in your data export.");
                    }
                });
            }

            box.Row(["gap-2 flex-wrap"], content: a =>
            {
                if (_pMoneyGoal.Value != goal.Id)
                {
                    a.Button([Button.OutlineSm], text: "Add or take out money", ariaLabel: $"Add or take out money for {goal.Name}", disabled: _myBusy.Value,
                        onClick: () => { _pMoneyGoal.Value = goal.Id; _pAmount.Value = ""; _pErrors.Value = []; });
                }

                a.Button([Button.GhostSm], text: "Edit", ariaLabel: $"Edit {goal.Name}", disabled: _myBusy.Value, onClick: () => OpenGoalForm(goal));
                a.Button([Button.GhostSm], text: "Delete", ariaLabel: $"Delete {goal.Name}", disabled: _myBusy.Value, onClick: () => _pConfirm.Value = $"goal:{goal.Id}");
            });
        });
    }

    private void RenderPlanDialogs(UIView view, MyPlansSnapshot p)
    {
        var editingDebt = _pDebtOpen.Value is { } did && did != "new" ? p.Debts.FirstOrDefault(d => d.Id == did) : null;

        view.Dialog(open: _pDebtOpen.Value != null, onOpenChange: async open => { if (!open) _pDebtOpen.Value = null; },
            title: editingDebt != null ? "Edit debt" : "Add a debt", description: "Copy the numbers from your latest statement or your lender's app.", showClose: true,
            content: f => f.Column(["gap-3"], content: c =>
            {
                c.TextField(label: "Name", placeholder: "For example: Visa card", bind: _pName);
                c.TextField(label: "Lender (optional)", bind: _pLender);
                c.Select(["w-full"], label: "Type", bind: _pKind, options: Enum.GetValues<MyDebtKind>().Select(k => new SelectOption(k.ToString(), PlanRules.KindName(k))).ToArray());
                c.TextField(label: "Still owed (€)", placeholder: "1 250,00", bind: _pBalance);
                c.TextField(label: "Interest, % a year (optional)", placeholder: "12,5", bind: _pApr);
                c.TextField(label: "Monthly payment (€, optional)", placeholder: "60,00", bind: _pMin);
                c.TextField(label: "Due day of the month (optional)", placeholder: "15", bind: _pDue);
                PlanErrors(c);
            }),
            footer: ft => ft.Row(["gap-2 justify-end w-full"], content: r =>
            {
                r.Button([Button.OutlineMd], text: "Cancel", onClick: () => _pDebtOpen.Value = null);
                r.Button([Button.PrimaryMd], text: _myBusy.Value ? "Saving…" : "Save", disabled: _myBusy.Value, onClick: async () => await SaveDebtAsync());
            }));

        var editingGoal = _pGoalOpen.Value is { } gid && gid != "new" ? p.Goals.FirstOrDefault(g => g.Goal.Id == gid)?.Goal : null;

        view.Dialog(open: _pGoalOpen.Value != null, onOpenChange: async open => { if (!open) _pGoalOpen.Value = null; },
            title: editingGoal != null ? "Edit goal" : "Add a savings goal", description: "Record money as you put it aside; Omabilo adds it up.", showClose: true,
            content: f => f.Column(["gap-3"], content: c =>
            {
                c.TextField(label: "Name", placeholder: "For example: Emergency fund", bind: _pName);
                c.TextField(label: "Target (€)", placeholder: "1 000,00", bind: _pTarget);
                c.TextField(label: "Target date (d.m.yyyy, optional)", placeholder: "31.12.2027", bind: _pDate);
                c.Checkbox(label: "This is my emergency fund", bind: _pEmergency);
                PlanErrors(c);
            }),
            footer: ft => ft.Row(["gap-2 justify-end w-full"], content: r =>
            {
                r.Button([Button.OutlineMd], text: "Cancel", onClick: () => _pGoalOpen.Value = null);
                r.Button([Button.PrimaryMd], text: _myBusy.Value ? "Saving…" : "Save", disabled: _myBusy.Value, onClick: async () => await SaveGoalAsync());
            }));

        if (_pConfirm.Value is { } confirm)
        {
            var isDebt = confirm.StartsWith("debt:", StringComparison.Ordinal);
            var id = confirm[(confirm.IndexOf(':') + 1)..];
            var name = isDebt ? p.Debts.FirstOrDefault(d => d.Id == id)?.Name : p.Goals.FirstOrDefault(g => g.Goal.Id == id)?.Goal.Name;

            view.AlertDialog(open: name != null, onOpenChange: async open => { if (!open) _pConfirm.Value = null; },
                title: $"Delete \"{name}\"?",
                description: isDebt ? "It's removed from your debts. This can't be undone." : "The goal and the money you recorded for it are removed from Omabilo. Your real savings aren't touched. This can't be undone.",
                cancelLabel: "Keep it", actionLabel: "Delete", actionStyle: [Button.ErrorMd],
                onAction: async () =>
                {
                    _pConfirm.Value = null;
                    await RunPlansAsync(async uid => isDebt
                        ? await Plans.DeleteDebtAsync(uid, id, CancellationToken.None) ? "Debt deleted." : null
                        : await Plans.DeleteGoalAsync(uid, id, CancellationToken.None) ? "Goal deleted." : null);
                });
        }
    }

    private void PlanErrors(UIView view)
    {
        if (_pErrors.Value.Count > 0)
        {
            view.Alert("Please fix", tone: SemanticTone.Error, description: string.Join(" ", _pErrors.Value));
        }
    }

    private void OpenDebtForm(DebtRecord? d)
    {
        _pErrors.Value = [];
        _pName.Value = d?.Name ?? "";
        _pLender.Value = d?.Lender ?? "";
        _pKind.Value = (d?.Kind ?? MyDebtKind.CreditCard).ToString();
        _pBalance.Value = d != null ? Money(d.Balance).Replace(" €", "") : "";
        _pApr.Value = d?.Apr?.ToString("0.##", CultureInfo.GetCultureInfo("fi-FI")) ?? "";
        _pMin.Value = d?.MinPayment is { } m ? Money(m).Replace(" €", "") : "";
        _pDue.Value = d?.DueDay?.ToString(CultureInfo.InvariantCulture) ?? "";
        _pDebtOpen.Value = d?.Id ?? "new";
    }

    private void OpenGoalForm(GoalRecord? g)
    {
        _pErrors.Value = [];
        _pName.Value = g?.Name ?? "";
        _pTarget.Value = g != null ? Money(g.Target).Replace(" €", "") : "";
        _pDate.Value = g?.TargetDate is { } d ? d.ToString("d.M.yyyy", CultureInfo.InvariantCulture) : "";
        _pEmergency.Value = g?.IsEmergencyFund ?? false;
        _pGoalOpen.Value = g?.Id ?? "new";
    }

    private async Task SaveDebtAsync()
    {
        var id = _pDebtOpen.Value is { } open && open != "new" ? open : null;
        var form = new DebtForm(_pName.Value, _pLender.Value, Enum.TryParse<MyDebtKind>(_pKind.Value, out var k) ? k : MyDebtKind.Other,
            _pBalance.Value, _pApr.Value, _pMin.Value, _pDue.Value);

        await RunPlansAsync(async uid =>
        {
            var errors = await Plans.SaveDebtAsync(uid, id, form, CancellationToken.None);
            _pErrors.Value = errors;

            if (errors.Count > 0)
            {
                return null;
            }

            _pDebtOpen.Value = null;
            return id == null ? "Debt added." : "Debt saved.";
        });
    }

    private async Task SaveGoalAsync()
    {
        var id = _pGoalOpen.Value is { } open && open != "new" ? open : null;
        var form = new GoalForm(_pName.Value, _pTarget.Value, _pDate.Value, _pEmergency.Value);

        await RunPlansAsync(async uid =>
        {
            var errors = await Plans.SaveGoalAsync(uid, id, form, CancellationToken.None);
            _pErrors.Value = errors;

            if (errors.Count > 0)
            {
                return null;
            }

            _pGoalOpen.Value = null;
            return id == null ? "Goal added." : "Goal saved.";
        });
    }

    private async Task SaveMoneyAsync(string goalId, int sign)
    {
        var amount = TextInvoiceExtractor.ParseAmount(_pAmount.Value) is { } a ? a * sign : (decimal?)null;

        await RunPlansAsync(async uid =>
        {
            var error = await Plans.AddContributionAsync(uid, goalId, amount, CancellationToken.None);
            _pErrors.Value = error is null ? [] : [error];

            if (error != null)
            {
                return null;
            }

            _pMoneyGoal.Value = null;
            return sign > 0 ? "Saved to your goal." : "Taken out of your goal.";
        });
    }

    /// <summary>Runs a change for the signed-in person, reloads their plans and shows the outcome.</summary>
    private async Task RunPlansAsync(Func<string, Task<string?>> action)
    {
        if (MyUserId is not { } uid || _myBusy.Value)
        {
            return;
        }

        _myBusy.Value = true;

        try
        {
            var message = await action(uid);
            await LoadPlansAsync(uid);

            // Debt payments are part of the payday plan, so it is rebuilt too
            _my.Value = await Companion.LoadAsync(uid, CancellationToken.None);

            if (message != null)
            {
                _toasts.Success(message);
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"plans action failed: {ex.GetType().Name}");
            _toasts.Error("That didn't work. Please try again.");
        }
        finally
        {
            _myBusy.Value = false;
        }
    }
}
