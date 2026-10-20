// Every add/edit form is one dialog, named by _dialog. The handler that opens it fills the fields.
public partial class OmabiloApp
{
    private void OpenDialog(string name)
    {
        _fError.Value = null;
        _dialog.Value = name;
    }

    private void OpenAddDebt()
    {
        _fName.Value = "";
        _fSecondary.Value = "";
        _fKind.Value = nameof(DebtKind.CreditCard);
        _fAmount.Value = "";
        _fRate.Value = "";
        _fAmount2.Value = "";
        _fDay.Value = "15";
        OpenDialog("add-debt");
    }

    private void OpenEditDebt(DebtAccount d)
    {
        _fName.Value = d.Name;
        _fSecondary.Value = d.Lender;
        _fKind.Value = d.Kind.ToString();
        _fAmount.Value = d.Balance.ToString("0.##", Fin.Inv);
        _fRate.Value = d.Apr.ToString("0.##", Fin.Inv);
        _fAmount2.Value = d.MinPayment.ToString("0.##", Fin.Inv);
        _fDay.Value = d.DueDay.ToString(Fin.Inv);
        OpenDialog($"edit-debt:{d.Id}");
    }

    private void OpenDebtPayment(DebtAccount d)
    {
        _fAmount.Value = d.MinPayment.ToString("0.##", Fin.Inv);
        OpenDialog($"pay-debt:{d.Id}");
    }

    private void OpenAddGoal()
    {
        _fName.Value = "";
        _fAmount.Value = "";
        _fAmount2.Value = "";
        _fIcon.Value = "piggy-bank";
        _fDate.Value = Today.AddMonths(6).ToString("yyyy-MM-dd", Fin.Inv);
        OpenDialog("add-goal");
    }

    private void OpenEditGoal(SavingsGoal g)
    {
        _fName.Value = g.Name;
        _fAmount.Value = g.Target.ToString("0.##", Fin.Inv);
        _fIcon.Value = g.IconName;
        _fDate.Value = g.TargetDate.ToString("yyyy-MM-dd", Fin.Inv);
        OpenDialog($"edit-goal:{g.Id}");
    }

    private void OpenGoalMoney(SavingsGoal g, bool deposit)
    {
        _fAmount.Value = deposit ? Math.Min(100, g.Target - g.Saved).ToString("0.##", Fin.Inv) : "";
        OpenDialog($"{(deposit ? "goal-in" : "goal-out")}:{g.Id}");
    }

    private void OpenPaydaySettings()
    {
        _fAmount.Value = _payday.Value.NetPay.ToString("0.##", Fin.Inv);
        _fDay.Value = _payday.Value.PayDay.ToString(Fin.Inv);
        OpenDialog("payday");
    }

    private void OpenBucketAmount(PayBucket b)
    {
        _fAmount.Value = b.Planned.ToString("0.##", Fin.Inv);
        OpenDialog($"bucket:{b.Id}");
    }

    private (string Kind, string? Id) DialogParts()
    {
        var value = _dialog.Value ?? "";
        var colon = value.IndexOf(':');
        return colon < 0 ? (value, null) : (value[..colon], value[(colon + 1)..]);
    }

    private void RenderDialogs(UIView view)
    {
        var (kind, id) = DialogParts();
        var debt = id != null ? _debts.FirstOrDefault(d => d.Id == id) : null;
        var goal = id != null ? _goals.FirstOrDefault(g => g.Id == id) : null;
        var bucket = id != null ? _buckets.FirstOrDefault(b => b.Id == id) : null;

        view.AlertDialog(open: kind == "reset", onOpenChange: async open => { if (!open) _dialog.Value = null; },
            title: "Reset demo data?",
            description: "Every bill, debt, goal, plan and transaction goes back to the original fictional examples. You can undo this for a few seconds afterwards.",
            cancelLabel: "Cancel", actionLabel: "Reset", actionStyle: [Button.ErrorMd],
            onAction: async () =>
            {
                ResetDemoData();
                _dialog.Value = null;
            });

        var (title, description, saveLabel) = kind switch
        {
            "add-debt" => ("Add a debt", "A card, loan or instalment plan you're paying off.", "Add debt"),
            "edit-debt" => ($"Edit {debt?.Name}", "Update the balance or terms.", "Save changes"),
            "pay-debt" => ($"Log a payment to {debt?.Name}", debt != null ? $"Balance {Fin.Eur(debt.Balance, 2)} · minimum {Fin.Eur(debt.MinPayment)}" : "", "Log payment"),
            "add-goal" => ("New savings goal", "What are you saving for?", "Create goal"),
            "edit-goal" => ($"Edit {goal?.Name}", "Change the target, date or icon.", "Save changes"),
            "goal-in" => ($"Add money to {goal?.Name}", goal != null ? $"{Fin.Eur(goal.Saved)} saved of {Fin.Eur(goal.Target)}" : "", "Add money"),
            "goal-out" => ($"Withdraw from {goal?.Name}", goal != null ? $"{Fin.Eur(goal.Saved, 2)} available" : "", "Withdraw"),
            "payday" => ("Pay details", "Your take-home pay and the day it usually arrives.", "Save"),
            "bucket" => ($"Plan for {bucket?.Name}", $"How much of your {Fin.Eur(_payday.Value.NetPay)} pay goes here?", "Set amount"),
            _ => ("", "", ""),
        };

        var open = kind is not ("" or "reset");

        view.Dialog(open: open, onOpenChange: async o => { if (!o) _dialog.Value = null; },
            showClose: true, title: title, description: description,
            content: v =>
            {
                v.Column(["gap-4 py-2"], content: form =>
                {
                    switch (kind)
                    {
                        case "add-debt":
                        case "edit-debt":
                            DebtFields(form);
                            break;
                        case "pay-debt" when debt != null:
                            AmountField(form, "Payment amount (€)", [("Minimum", debt.MinPayment), ("+€50", debt.MinPayment + 50), ("Pay it all", debt.Balance)]);
                            break;
                        case "add-goal":
                        case "edit-goal":
                            GoalFields(form, kind == "add-goal");
                            break;
                        case "goal-in" when goal != null:
                            AmountField(form, "Amount to add (€)", [("€25", 25), ("€100", 100), ("Finish it", goal.Target - goal.Saved)]);
                            break;
                        case "goal-out" when goal != null:
                            AmountField(form, "Amount to withdraw (€)", [("€50", Math.Min(50, goal.Saved)), ("All", goal.Saved)]);
                            break;
                        case "payday":
                            form.TextField(label: "Net pay per month (€)", bind: _fAmount, type: "number", min: "0", step: "10");
                            form.Select(label: "Pay arrives on day", bind: _fDay,
                                options: Enumerable.Range(1, 28).Select(d => new SelectOption(d.ToString(Fin.Inv), Ordinal(d))).ToList());
                            break;
                        case "bucket":
                            AmountField(form, "Amount (€)", []);
                            break;
                    }

                    if (_fError.Value is { } error)
                    {
                        form.Alert(tone: SemanticTone.Error, description: error);
                    }
                });
            },
            footer: f =>
            {
                f.Button([Button.OutlineMd], text: "Cancel", onClick: () => _dialog.Value = null);
                f.Button([Button.PrimaryMd], text: saveLabel, onClick: () => SaveDialog(kind, debt, goal, bucket));
            });
    }

    private void AmountField(UIView view, string label, (string Label, decimal Value)[] quick)
    {
        view.TextField(label: label, bind: _fAmount, type: "number", min: "0", step: "0.01", autoFocus: true);

        if (quick.Length > 0)
        {
            view.Row(["gap-2 flex-wrap"], content: r =>
            {
                foreach (var (text, value) in quick.Where(q => q.Value > 0))
                {
                    r.Button([Badge.NeutralMd, "h-9 px-3 cursor-pointer hover:bg-muted"], text: $"{text} · {Fin.Eur(value, value % 1 == 0 ? 0 : 2)}",
                        onClick: () => _fAmount.Value = value.ToString("0.##", Fin.Inv));
                }
            });
        }
    }

    private void DebtFields(UIView view)
    {
        view.TextField(label: "Name", bind: _fName, placeholder: "e.g. Credit card", autoFocus: true, maxLength: 40);
        view.Grid(["grid grid-cols-1 sm:grid-cols-2 gap-4"], content: g =>
        {
            g.TextField(label: "Lender", bind: _fSecondary, placeholder: "e.g. Example Bank", maxLength: 40);
            g.Select(label: "Type", bind: _fKind,
                options: Enum.GetValues<DebtKind>().Select(k => new SelectOption(k.ToString(), Fin.DebtKindLabel(k))).ToList());
            g.TextField(label: "Balance owed (€)", bind: _fAmount, type: "number", min: "0", step: "0.01");
            g.TextField(label: "Interest rate (APR %)", bind: _fRate, type: "number", min: "0", step: "0.1");
            g.TextField(label: "Minimum payment (€/month)", bind: _fAmount2, type: "number", min: "0", step: "1");
            g.Select(label: "Due on day", bind: _fDay,
                options: Enumerable.Range(1, 28).Select(d => new SelectOption(d.ToString(Fin.Inv), Ordinal(d))).ToList());
        });
    }

    private void GoalFields(UIView view, bool adding)
    {
        view.TextField(label: "Goal name", bind: _fName, placeholder: "e.g. Weekend in Tallinn", autoFocus: true, maxLength: 40);
        view.Grid(["grid grid-cols-1 sm:grid-cols-2 gap-4"], content: g =>
        {
            g.TextField(label: "Target (€)", bind: _fAmount, type: "number", min: "0", step: "10");

            if (adding)
            {
                g.TextField(label: "Already saved (€)", bind: _fAmount2, type: "number", min: "0", step: "10", placeholder: "0");
            }

            g.DatePicker(label: "Target date", value: _fDate.Value, labelLocale: "en-GB", minDate: Today.ToString("yyyy-MM-dd", Fin.Inv),
                onValueChange: async d => _fDate.Value = d);
        });

        view.Column(["gap-2"], content: c =>
        {
            c.Text([Text.Label], "Icon");
            c.Row(["gap-2 flex-wrap"], content: r =>
            {
                foreach (var (icon, label) in GoalIcons)
                {
                    var selected = _fIcon.Value == icon;
                    r.Button([$"w-10 h-10 rounded-xl flex items-center justify-center border transition-colors {(selected ? "border-brand bg-brand-solid/10 text-brand-secondary" : "border-secondary hover:bg-muted text-muted-foreground")}"],
                        ariaLabel: $"Icon {label}{(selected ? " (selected)" : "")}", onClick: () => _fIcon.Value = icon,
                        content: v => v.Icon([Icon.Sm], name: icon));
                }
            });
        });
    }

    private void SaveDialog(string kind, DebtAccount? debt, SavingsGoal? goal, PayBucket? bucket)
    {
        _fError.Value = null;
        Fin.TryParseAmount(_fAmount.Value, out var amount);

        switch (kind)
        {
            case "add-debt":
            case "edit-debt":
            {
                var name = _fName.Value.Trim();

                if (name.Length == 0)
                {
                    _fError.Value = "Give the debt a name.";
                    return;
                }

                if (!Fin.TryParseAmount(_fAmount.Value, out var balance) || balance < 0)
                {
                    _fError.Value = "Enter the balance owed, for example 1250.";
                    return;
                }

                if (!Fin.TryParseAmount(_fRate.Value, out var apr) || apr < 0 || apr > 100)
                {
                    _fError.Value = "Enter the interest rate as a percentage between 0 and 100.";
                    return;
                }

                if (!Fin.TryParseAmount(_fAmount2.Value, out var min) || min <= 0)
                {
                    _fError.Value = "Enter the minimum monthly payment.";
                    return;
                }

                var debtKind = Enum.TryParse<DebtKind>(_fKind.Value, out var k) ? k : DebtKind.Personal;
                var day = int.TryParse(_fDay.Value, out var dd) ? dd : 1;
                var lender = _fSecondary.Value.Trim();

                if (kind == "add-debt")
                {
                    var created = new DebtAccount(Fin.NewId(), name, lender.Length > 0 ? lender : "—", debtKind, balance, balance, apr, min, day);
                    _debts.Add(created);
                    OfferUndo($"Added {name}", () => _debts.RemoveAll(x => x.Id == created.Id));
                }
                else if (debt != null)
                {
                    var before = debt;
                    _debts.ReplaceWhere(x => x.Id == debt.Id, x => x with
                    {
                        Name = name, Lender = lender.Length > 0 ? lender : "—", Kind = debtKind, Balance = balance,
                        StartingBalance = Math.Max(x.StartingBalance, balance), Apr = apr, MinPayment = min, DueDay = day,
                    });
                    OfferUndo($"Updated {name}", () => _debts.ReplaceWhere(x => x.Id == before.Id, _ => before));
                }

                break;
            }

            case "pay-debt" when debt != null:
                if (amount <= 0)
                {
                    _fError.Value = "Enter how much you paid.";
                    return;
                }

                if (amount > debt.Balance)
                {
                    _fError.Value = $"That's more than the {Fin.Eur(debt.Balance, 2)} left. Use \"Pay it all\" to clear it.";
                    return;
                }

                LogDebtPayment(debt, amount);
                break;

            case "add-goal":
            case "edit-goal":
            {
                var name = _fName.Value.Trim();

                if (name.Length == 0)
                {
                    _fError.Value = "Give the goal a name.";
                    return;
                }

                if (amount <= 0)
                {
                    _fError.Value = "Enter a target amount above zero.";
                    return;
                }

                if (!DateTime.TryParseExact(_fDate.Value, "yyyy-MM-dd", Fin.Inv, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                {
                    _fError.Value = "Pick a target date.";
                    return;
                }

                date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                if (kind == "add-goal")
                {
                    Fin.TryParseAmount(_fAmount2.Value, out var already);
                    var created = new SavingsGoal(Fin.NewId(), name, _fIcon.Value, amount, Math.Max(0, already), date);
                    _goals.Add(created);
                    OfferUndo($"Created {name}", () => _goals.RemoveAll(x => x.Id == created.Id));
                }
                else if (goal != null)
                {
                    var before = goal;
                    _goals.ReplaceWhere(x => x.Id == goal.Id, x => x with { Name = name, Target = amount, TargetDate = date, IconName = _fIcon.Value });
                    OfferUndo($"Updated {name}", () => _goals.ReplaceWhere(x => x.Id == before.Id, _ => before));
                }

                break;
            }

            case "goal-in" when goal != null:
                if (amount <= 0)
                {
                    _fError.Value = "Enter an amount above zero.";
                    return;
                }

                MoveGoalMoney(goal, amount);
                break;

            case "goal-out" when goal != null:
                if (amount <= 0 || amount > goal.Saved)
                {
                    _fError.Value = $"Enter an amount up to {Fin.Eur(goal.Saved, 2)}.";
                    return;
                }

                MoveGoalMoney(goal, -amount);
                break;

            case "payday":
            {
                if (amount <= 0)
                {
                    _fError.Value = "Enter your take-home pay.";
                    return;
                }

                var before = _payday.Value;
                var day = int.TryParse(_fDay.Value, out var d) ? d : before.PayDay;
                _payday.Value = before with { NetPay = amount, PayDay = day };
                OfferUndo("Updated pay details", () => _payday.Value = before);
                break;
            }

            case "bucket" when bucket != null:
            {
                if (amount < 0)
                {
                    _fError.Value = "Enter zero or more.";
                    return;
                }

                var before = bucket.Planned;
                _buckets.ReplaceWhere(b => b.Id == bucket.Id, b => b with { Planned = Math.Round(amount, 2) });
                OfferUndo($"{bucket.Name} set to {Fin.Eur(amount)}", () => _buckets.ReplaceWhere(b => b.Id == bucket.Id, b => b with { Planned = before }));
                break;
            }

            default:
                _fError.Value = "That item no longer exists.";
                return;
        }

        _dialog.Value = null;
    }

    private void ResetDemoData()
    {
        var txns = _txns.ToList();
        var inbox = _inbox.ToList();
        var debts = _debts.ToList();
        var goals = _goals.ToList();
        var buckets = _buckets.ToList();
        var payday = _payday.Value;
        var strategy = _strategy.Value;
        var extra = _extraPayment.Value;

        var fresh = DemoData.Transactions();
        _txns.ReplaceAll(fresh);
        _inbox.ReplaceAll(DemoData.Inbox());
        _debts.ReplaceAll(DemoData.Debts());
        _goals.ReplaceAll(DemoData.Goals());
        _buckets.ReplaceAll(DemoData.Buckets());
        _payday.Value = DemoData.Payday(fresh);
        _strategy.Value = "avalanche";
        _extraPayment.Value = 100;

        OfferUndo("Demo data reset", () =>
        {
            _txns.ReplaceAll(txns);
            _inbox.ReplaceAll(inbox);
            _debts.ReplaceAll(debts);
            _goals.ReplaceAll(goals);
            _buckets.ReplaceAll(buckets);
            _payday.Value = payday;
            _strategy.Value = strategy;
            _extraPayment.Value = extra;
        });
    }
}
