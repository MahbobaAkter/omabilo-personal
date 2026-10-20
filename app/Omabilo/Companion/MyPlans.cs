// A signed-in person's own debts and savings goals, kept in the database per user (om_debts,
// om_goals, om_contributions). Plain C# (no IKON types): the person types every number, so nothing
// here is read from email or guessed. The fictional demo's Debt Tracker and Savings & Goals stay separate.

public enum MyDebtKind { CreditCard, Loan, StudentLoan, CarLoan, PayLater, Collection, Other }

public sealed record DebtRecord
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public string Name { get; init; } = "";
    public string? Lender { get; init; }
    public MyDebtKind Kind { get; init; }
    public decimal Balance { get; init; }

    /// <summary>Interest as a yearly percentage (12.5 means 12.5 %), when the person knows it.</summary>
    public decimal? Apr { get; init; }
    public decimal? MinPayment { get; init; }
    public int? DueDay { get; init; }

    /// <summary>The highest balance the person has entered for this debt: progress is measured from it.</summary>
    public decimal? StartingBalance { get; init; }

    /// <summary>The newest payment the person recorded in Omabilo, and when.</summary>
    public decimal? LastPaymentAmount { get; init; }
    public DateTime? LastPaymentUtc { get; init; }

    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

public sealed record GoalRecord
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public string Name { get; init; } = "";
    public decimal Target { get; init; }
    public DateTime? TargetDate { get; init; }
    public bool IsEmergencyFund { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

/// <summary>Money put into a goal (positive) or taken out of it (negative), as the person reports it.</summary>
public sealed record Contribution
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string GoalId { get; init; }
    public decimal Amount { get; init; }
    public DateTime DateUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
}

/// <summary>The debt form as typed. Numbers are text so the rules, not the screen, decide what's valid.</summary>
public sealed record DebtForm(string Name, string? Lender, MyDebtKind Kind, string Balance, string? Apr, string? MinPayment, string? DueDay);

public sealed record GoalForm(string Name, string Target, string? TargetDate, bool IsEmergencyFund);

public sealed record GoalProgress(GoalRecord Goal, decimal Saved, double Percent, IReadOnlyList<Contribution> Contributions, string Pace);

public sealed record MyPlansSnapshot(IReadOnlyList<DebtRecord> Debts, IReadOnlyList<GoalProgress> Goals)
{
    public decimal TotalDebt => Debts.Sum(d => d.Balance);
    public decimal MonthlyMinimums => Debts.Sum(d => d.MinPayment ?? 0m);
    public decimal TotalSaved => Goals.Sum(g => g.Saved);
}

public static class PlanRules
{
    public const decimal MaxAmount = 10_000_000m;
    public const int MaxGoals = 50;
    public const int MaxDebts = 50;

    public static string KindName(MyDebtKind k) => k switch
    {
        MyDebtKind.CreditCard => "Credit card",
        MyDebtKind.Loan => "Loan",
        MyDebtKind.StudentLoan => "Student loan",
        MyDebtKind.CarLoan => "Car loan",
        MyDebtKind.PayLater => "Buy now, pay later",
        MyDebtKind.Collection => "Debt collection",
        _ => "Other debt",
    };

    public static (DebtRecord? Debt, IReadOnlyList<string> Errors) ParseDebt(DebtForm f, string userId, string id, DateTime now, DateTime? createdUtc = null)
    {
        var errors = new List<string>();
        var name = f.Name.Trim();

        if (name.Length is 0 or > 80)
        {
            errors.Add("Give the debt a name (up to 80 characters).");
        }

        var balance = TextInvoiceExtractor.ParseAmount(f.Balance) ?? (f.Balance.Trim() == "0" ? 0m : null);

        if (balance is not { } b || b < 0 || b > MaxAmount)
        {
            errors.Add("Enter what you still owe in euros, for example 1 250,00.");
        }

        var apr = Optional(f.Apr);

        if (apr is { Ok: false } || apr.Value is < 0 or > 100)
        {
            errors.Add("Interest is a yearly percentage between 0 and 100, or leave it empty.");
        }

        var min = Optional(f.MinPayment);

        if (min is { Ok: false } || min.Value is < 0 || min.Value > MaxAmount)
        {
            errors.Add("Enter the monthly payment in euros, or leave it empty.");
        }

        int? dueDay = null;

        if (!string.IsNullOrWhiteSpace(f.DueDay))
        {
            if (int.TryParse(f.DueDay.Trim(), out var d) && d is >= 1 and <= 31)
            {
                dueDay = d;
            }
            else
            {
                errors.Add("The due day is a day of the month from 1 to 31, or leave it empty.");
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var lender = f.Lender?.Trim();
        return (new DebtRecord
        {
            Id = id, UserId = userId, Name = name, Lender = string.IsNullOrEmpty(lender) ? null : lender[..Math.Min(lender.Length, 80)],
            Kind = f.Kind, Balance = balance!.Value, Apr = apr.Value, MinPayment = min.Value, DueDay = dueDay,
            CreatedUtc = createdUtc ?? now, UpdatedUtc = now,
        }, errors);
    }

    public static (GoalRecord? Goal, IReadOnlyList<string> Errors) ParseGoal(GoalForm f, string userId, string id, DateTime now, DateTime? createdUtc = null)
    {
        var errors = new List<string>();
        var name = f.Name.Trim();

        if (name.Length is 0 or > 80)
        {
            errors.Add("Give the goal a name (up to 80 characters).");
        }

        if (TextInvoiceExtractor.ParseAmount(f.Target) is not { } target || target <= 0 || target > MaxAmount)
        {
            errors.Add("Enter the amount you want to save in euros, for example 1 000,00.");
            target = 0;
        }

        DateTime? date = null;

        if (!string.IsNullOrWhiteSpace(f.TargetDate))
        {
            date = RecordExtractor.ParseDate(f.TargetDate.Trim());

            if (date == null)
            {
                errors.Add("Write the target date as d.m.yyyy, for example 31.12.2027, or leave it empty.");
            }
            else if (date.Value.Date <= now.Date)
            {
                errors.Add("The target date has to be in the future.");
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (new GoalRecord
        {
            Id = id, UserId = userId, Name = name, Target = target, TargetDate = date is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null,
            IsEmergencyFund = f.IsEmergencyFund, CreatedUtc = createdUtc ?? now, UpdatedUtc = now,
        }, errors);
    }

    /// <summary>Checks a deposit (positive) or a withdrawal (negative): a goal can't go below zero.</summary>
    public static string? CheckContribution(decimal? amount, decimal savedNow)
    {
        if (amount is not { } a || a == 0 || Math.Abs(a) > MaxAmount)
        {
            return "Enter an amount in euros, for example 50,00.";
        }

        return savedNow + a < 0 ? $"You can take out at most {InvoiceExplainer.Money(savedNow)}." : null;
    }

    public static string? CheckDebtPayment(decimal? amount, decimal balance)
    {
        if (amount is not { } a || a <= 0 || a > MaxAmount)
        {
            return "Enter the amount you paid in euros, for example 120,00.";
        }

        return a > balance ? $"That's more than the {InvoiceExplainer.Money(balance)} still owed. Edit the debt if the balance has changed." : null;
    }

    /// <summary>How much of the debt is paid, from the highest balance entered; null when unknown.</summary>
    public static double? PaidPercent(DebtRecord d) =>
        d.StartingBalance is { } start && start > 0 ? Math.Clamp((double)((start - d.Balance) / start) * 100, 0, 100) : null;

    /// <summary>The next payment date from the due day the person entered, or null without one.</summary>
    public static DateTime? NextPayment(DebtRecord d, DateTime today) =>
        d.Balance > 0 && d.DueDay is { } day ? Attention.NextDueDate(day, today) : null;

    /// <summary>True when this month's due day has passed and no payment was recorded since it. Omabilo
    /// can't see the lender, so this means "not recorded here", never "unpaid".</summary>
    public static bool PaymentNotRecorded(DebtRecord d, DateTime today)
    {
        today = today.Date;

        if (d.Balance <= 0 || d.DueDay is not { } day || d.MinPayment is not > 0)
        {
            return false;
        }

        var due = new DateTime(today.Year, today.Month, Math.Min(day, DateTime.DaysInMonth(today.Year, today.Month)));
        return due < today && due >= d.CreatedUtc.Date && (d.LastPaymentUtc is not { } paid || paid.Date < due.AddDays(-10));
    }

    /// <summary>What the debt record still lacks for a payoff estimate, in plain words.</summary>
    public static IReadOnlyList<string> MissingForPlan(DebtRecord d)
    {
        var missing = new List<string>();
        if (d.Balance <= 0) return missing;
        if (d.MinPayment is null) missing.Add("monthly payment");
        if (d.Apr is null) missing.Add("interest rate");
        if (d.DueDay is null) missing.Add("due day");
        return missing;
    }

    /// <summary>Months until every debt is paid at the payments entered, or null when any debt lacks a
    /// payment or its payment doesn't cover the interest. Debts are paid side by side, not snowballed.</summary>
    public static int? MonthsUntilDebtFree(IEnumerable<DebtRecord> debts)
    {
        var months = 0;

        foreach (var d in debts.Where(d => d.Balance > 0))
        {
            if (MonthsToPayOff(d.Balance, d.Apr, d.MinPayment) is not { } m)
            {
                return null;
            }

            months = Math.Max(months, m);
        }

        return months;
    }

    public static GoalProgress Progress(GoalRecord goal, IEnumerable<Contribution> all, DateTime today)
    {
        var mine = all.Where(c => c.GoalId == goal.Id && c.UserId == goal.UserId).OrderByDescending(c => c.DateUtc).ThenByDescending(c => c.CreatedUtc).ToList();
        var saved = mine.Sum(c => c.Amount);
        var percent = goal.Target > 0 ? Math.Clamp((double)(saved / goal.Target) * 100, 0, 100) : 0;
        return new GoalProgress(goal, saved, percent, mine, Pace(goal, saved, today));
    }

    /// <summary>What it takes to reach the goal on time, in plain words. Never a promise.</summary>
    public static string Pace(GoalRecord goal, decimal saved, DateTime today)
    {
        var left = goal.Target - saved;

        if (left <= 0)
        {
            return "Reached. Well done.";
        }

        if (goal.TargetDate is not { } date)
        {
            return $"{InvoiceExplainer.Money(left)} to go. Add a target date to see a monthly amount.";
        }

        var months = (date.Year - today.Year) * 12 + date.Month - today.Month;

        if (date.Date <= today.Date)
        {
            return $"The target date has passed with {InvoiceExplainer.Money(left)} to go. Pick a new date if you like.";
        }

        var perMonth = Math.Ceiling(left / Math.Max(1, months) * 100) / 100;
        return $"{InvoiceExplainer.Money(left)} to go: about {InvoiceExplainer.Money(perMonth)} a month until {InvoiceExplainer.Day(date)}.";
    }

    /// <summary>Months to pay off at the monthly payment with monthly interest, or null when the payment
    /// doesn't cover the interest (or isn't known). Capped at 50 years.</summary>
    public static int? MonthsToPayOff(decimal balance, decimal? apr, decimal? payment)
    {
        if (balance <= 0)
        {
            return 0;
        }

        if (payment is not { } p || p <= 0)
        {
            return null;
        }

        var rate = (apr ?? 0m) / 100m / 12m;

        if (p <= Math.Round(balance * rate, 2))
        {
            return null;
        }

        var left = balance;

        for (var month = 1; month <= 600; month++)
        {
            left += Math.Round(left * rate, 2) - p;

            if (left <= 0)
            {
                return month;
            }
        }

        return null;
    }

    public static string PayoffText(DebtRecord d)
    {
        if (d.Balance <= 0)
        {
            return "Paid off.";
        }

        if (d.MinPayment is null)
        {
            return "Add the monthly payment to see when it's paid off.";
        }

        return MonthsToPayOff(d.Balance, d.Apr, d.MinPayment) is { } m
            ? $"Paid off in about {(m < 12 ? $"{m} month{(m == 1 ? "" : "s")}" : $"{m / 12} y {m % 12} mo")} at {InvoiceExplainer.Money(d.MinPayment.Value)} a month{(d.Apr == null ? " (interest not entered)" : "")}. An estimate, not a promise."
            : "At this payment the interest grows faster than you pay it off. Check the numbers with your lender.";
    }

    private static (bool Ok, decimal? Value) Optional(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (true, null);
        }

        var v = TextInvoiceExtractor.ParseAmount(text) ?? (text.Trim() == "0" ? 0m : null);
        return v == null ? (false, null) : (true, v);
    }
}

/// <summary>Adds, changes and removes a person's debts and goals. Every call takes the owner's id from
/// the server-side session, and the store filters every query by it.</summary>
public sealed class PlanService(ICompanionStore store, Func<DateTime> clock)
{
    public async Task<MyPlansSnapshot> LoadAsync(string userId, CancellationToken ct)
    {
        var debts = await store.GetDebtsAsync(userId, ct);
        var goals = await store.GetGoalsAsync(userId, ct);
        var contributions = await store.GetContributionsAsync(userId, ct);
        var today = clock().Date;
        return new MyPlansSnapshot(
            debts.OrderByDescending(d => d.Balance > 0).ThenByDescending(d => d.Apr ?? 0).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            goals.OrderByDescending(g => g.IsEmergencyFund).ThenBy(g => g.TargetDate ?? DateTime.MaxValue).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => PlanRules.Progress(g, contributions, today)).ToList());
    }

    public async Task<IReadOnlyList<string>> SaveDebtAsync(string userId, string? debtId, DebtForm form, CancellationToken ct)
    {
        var existing = debtId == null ? null : (await store.GetDebtsAsync(userId, ct)).FirstOrDefault(d => d.Id == debtId);

        if (debtId != null && existing == null)
        {
            return ["That debt doesn't exist any more."];
        }

        if (existing == null && (await store.GetDebtsAsync(userId, ct)).Count >= PlanRules.MaxDebts)
        {
            return [$"You can keep up to {PlanRules.MaxDebts} debts."];
        }

        var (debt, errors) = PlanRules.ParseDebt(form, userId, existing?.Id ?? NewId("debt"), clock(), existing?.CreatedUtc);

        if (debt == null)
        {
            return errors;
        }

        // Progress counts from the highest balance entered; payments already recorded are kept
        debt = debt with
        {
            StartingBalance = Math.Max(existing?.StartingBalance ?? existing?.Balance ?? debt.Balance, debt.Balance),
            LastPaymentAmount = existing?.LastPaymentAmount,
            LastPaymentUtc = existing?.LastPaymentUtc,
        };

        await store.SaveDebtAsync(debt, ct);
        await store.AddAuditAsync(userId, existing == null ? "debt_added" : "debt_changed", debt.Id, ct);
        return [];
    }

    public async Task<bool> DeleteDebtAsync(string userId, string debtId, CancellationToken ct)
    {
        var deleted = await store.DeleteDebtAsync(userId, debtId, ct);

        if (deleted)
        {
            await store.AddAuditAsync(userId, "debt_deleted", debtId, ct);
        }

        return deleted;
    }

    /// <summary>Records a payment the person made outside Omabilo: the balance goes down by it. Omabilo
    /// never pays anything. Returns the debt as it was before, for undo, or an error.</summary>
    public async Task<(DebtRecord? Before, string? Error)> RecordDebtPaymentAsync(string userId, string debtId, decimal? amount, CancellationToken ct)
    {
        if ((await store.GetDebtsAsync(userId, ct)).FirstOrDefault(d => d.Id == debtId) is not { } debt)
        {
            return (null, "That debt doesn't exist any more.");
        }

        if (PlanRules.CheckDebtPayment(amount, debt.Balance) is { } error)
        {
            return (null, error);
        }

        var now = clock();
        await store.SaveDebtAsync(debt with
        {
            Balance = debt.Balance - amount!.Value,
            StartingBalance = debt.StartingBalance ?? debt.Balance,
            LastPaymentAmount = amount,
            LastPaymentUtc = now,
            UpdatedUtc = now,
        }, ct);
        await store.AddAuditAsync(userId, "debt_payment_recorded", debtId, ct);
        return (debt, null);
    }

    /// <summary>Puts a debt back as it was (the undo of a recorded payment). Only the owner's own debt.</summary>
    public async Task<bool> RestoreDebtAsync(string userId, DebtRecord before, CancellationToken ct)
    {
        if (before.UserId != userId || (await store.GetDebtsAsync(userId, ct)).All(d => d.Id != before.Id))
        {
            return false;
        }

        await store.SaveDebtAsync(before with { UpdatedUtc = clock() }, ct);
        await store.AddAuditAsync(userId, "debt_payment_undone", before.Id, ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> SaveGoalAsync(string userId, string? goalId, GoalForm form, CancellationToken ct)
    {
        var existing = goalId == null ? null : (await store.GetGoalsAsync(userId, ct)).FirstOrDefault(g => g.Id == goalId);

        if (goalId != null && existing == null)
        {
            return ["That goal doesn't exist any more."];
        }

        if (existing == null && (await store.GetGoalsAsync(userId, ct)).Count >= PlanRules.MaxGoals)
        {
            return [$"You can keep up to {PlanRules.MaxGoals} goals."];
        }

        var (goal, errors) = PlanRules.ParseGoal(form, userId, existing?.Id ?? NewId("goal"), clock(), existing?.CreatedUtc);

        if (goal == null)
        {
            return errors;
        }

        await store.SaveGoalAsync(goal, ct);
        await store.AddAuditAsync(userId, existing == null ? "goal_added" : "goal_changed", goal.Id, ct);
        return [];
    }

    /// <summary>Deletes the goal and the deposits and withdrawals recorded for it.</summary>
    public async Task<bool> DeleteGoalAsync(string userId, string goalId, CancellationToken ct)
    {
        var deleted = await store.DeleteGoalAsync(userId, goalId, ct);

        if (deleted)
        {
            await store.AddAuditAsync(userId, "goal_deleted", goalId, ct);
        }

        return deleted;
    }

    /// <summary>Records money put in (positive) or taken out (negative). Returns an error, or null.</summary>
    public async Task<string?> AddContributionAsync(string userId, string goalId, decimal? amount, CancellationToken ct)
    {
        if ((await store.GetGoalsAsync(userId, ct)).FirstOrDefault(g => g.Id == goalId) is not { } goal)
        {
            return "That goal doesn't exist any more.";
        }

        var saved = PlanRules.Progress(goal, await store.GetContributionsAsync(userId, ct), clock().Date).Saved;

        if (PlanRules.CheckContribution(amount, saved) is { } error)
        {
            return error;
        }

        var now = clock();
        var c = new Contribution { Id = NewId("contrib"), UserId = userId, GoalId = goalId, Amount = amount!.Value, DateUtc = now, CreatedUtc = now };
        await store.SaveContributionAsync(c, ct);
        await store.AddAuditAsync(userId, amount > 0 ? "goal_deposit" : "goal_withdrawal", goalId, ct);
        return null;
    }

    /// <summary>Removes one recorded deposit or withdrawal (an undo for a mistyped amount).</summary>
    public async Task<bool> DeleteContributionAsync(string userId, string contributionId, CancellationToken ct)
    {
        var deleted = await store.DeleteContributionAsync(userId, contributionId, ct);

        if (deleted)
        {
            await store.AddAuditAsync(userId, "goal_entry_removed", null, ct);
        }

        return deleted;
    }

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
