// Numbers derived from the stored records. Plain methods, so every screen that calls them
// re-renders when the records they read change.
public partial class OmabiloApp
{
    private static DateTime MonthStart(DateTime d) => new(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private IEnumerable<Txn> TxnsIn(DateTime monthStart)
    {
        var end = monthStart.AddMonths(1);
        return _txns.Where(t => t.Date >= monthStart && t.Date < end);
    }

    private decimal IncomeIn(DateTime monthStart) => TxnsIn(monthStart).Where(t => t.Amount > 0).Sum(t => t.Amount);

    // Money that left the account, savings transfers excluded (they are still the person's money)
    private decimal SpendingIn(DateTime monthStart) =>
        -TxnsIn(monthStart).Where(t => t.Amount < 0 && t.Category != "Savings").Sum(t => t.Amount);

    private decimal SavedIn(DateTime monthStart) =>
        -TxnsIn(monthStart).Where(t => t.Amount < 0 && t.Category == "Savings").Sum(t => t.Amount);

    private decimal CurrentBalance()
    {
        var p = _payday.Value;
        return p.OpeningBalance + _txns.Where(t => t.Date >= p.BalanceSince).Sum(t => t.Amount);
    }

    private DateTime NextPayday() => Fin.NextPayday(Today, _payday.Value.PayDay);

    private static InboxStatus EffectiveStatus(InboxItem item) =>
        item.Status == InboxStatus.Snoozed && item.SnoozedUntil is { } until && until <= DateTime.UtcNow
            ? InboxStatus.New
            : item.Status;

    private static bool IsOpenBill(InboxItem i) =>
        i.Kind == InboxKind.Bill && i.Amount is > 0 && EffectiveStatus(i) is InboxStatus.New or InboxStatus.Snoozed;

    private List<InboxItem> BillsBeforePayday()
    {
        var payday = NextPayday();
        return _inbox.Where(i => IsOpenBill(i) && i.DueDate is { } due && due <= payday)
            .OrderBy(i => i.DueDate).ToList();
    }

    private decimal SafeToSpend() => CurrentBalance() - BillsBeforePayday().Sum(i => i.Amount ?? 0);

    private int DaysToPayday() => Math.Max(1, (int)(NextPayday() - Today).TotalDays);

    private decimal TotalDebt() => _debts.Sum(d => d.Balance);

    private decimal TotalSaved() => _goals.Sum(g => g.Saved);

    private record Factor(string Name, string Detail, double Score, double Max, string Section, string Tip);

    private record Wellbeing(int Score, string Label, string Tone, List<Factor> Factors);

    private Wellbeing ComputeWellbeing()
    {
        var lastFull = MonthStart(Today).AddMonths(-1);
        var income = IncomeIn(lastFull);
        var spending = SpendingIn(lastFull);
        var rate = income > 0 ? (double)((income - spending) / income) * 100 : 0;
        var savingsScore = Math.Clamp(rate / 20.0, 0, 1) * 30;

        var essentials = -TxnsIn(lastFull)
            .Where(t => t.Amount < 0 && t.Category is "Housing" or "Utilities" or "Groceries" or "Transport" or "Debt payments")
            .Sum(t => t.Amount);
        var emergency = _goals.FirstOrDefault(g => g.IsEmergencyFund)?.Saved ?? 0;
        var months = essentials > 0 ? (double)(emergency / essentials) : 0;
        var bufferScore = Math.Clamp(months / 3.0, 0, 1) * 25;

        var minimums = _debts.Where(d => d.Balance > 0).Sum(d => d.MinPayment);
        var dti = _payday.Value.NetPay > 0 ? (double)(minimums / _payday.Value.NetPay) * 100 : 0;
        var debtScore = Math.Clamp((40 - dti) / 30.0, 0, 1) * 25;

        var overdue = _inbox.Count(i => IsOpenBill(i) && i.DueDate is { } due && due < Today);
        var billsScore = Math.Max(0, 20 - overdue * 10);

        var factors = new List<Factor>
        {
            new("Savings rate", $"{Fin.Pct(rate)} of last month's income kept", savingsScore, 30, "reports",
                rate >= 20 ? "Great: you keep a fifth or more of what you earn." : "Aim to keep 20% of income. Trim one category in Monthly Reports."),
            new("Safety buffer", $"{months.ToString("0.0", Fin.Inv)} months of essentials saved", bufferScore, 25, "goals",
                months >= 3 ? "Your emergency fund covers three months." : "Three months of essentials is a solid buffer."),
            new("Debt load", $"{Fin.Pct(dti)} of pay goes to minimum payments", debtScore, 25, "debts",
                dti <= 10 ? "Debt payments are comfortably low." : "Under 10% of pay is comfortable. An extra payment helps."),
            new("Bills on time", overdue == 0 ? "No overdue bills" : $"{overdue} overdue bill{(overdue == 1 ? "" : "s")}", billsScore, 20, "inbox",
                overdue == 0 ? "Everything due has been handled." : "Clear overdue bills in your Financial Inbox."),
        };

        var score = (int)Math.Round(factors.Sum(f => f.Score));
        var (label, tone) = score switch
        {
            >= 80 => ("Thriving", "success"),
            >= 60 => ("Steady", "brand"),
            >= 40 => ("Building", "warning"),
            _ => ("Under pressure", "error"),
        };

        return new Wellbeing(score, label, tone, factors);
    }

    // ── Shared actions that touch several records ───────────────────────────

    private void MarkBillPaid(InboxItem item)
    {
        var txn = new Txn(Fin.NewId(), Today, item.From, BillCategory(item), -(item.Amount ?? 0), item.Id);
        _inbox.ReplaceWhere(i => i.Id == item.Id, i => i with { Status = InboxStatus.Done, SnoozedUntil = null });
        _txns.Insert(0, txn);

        // A card or loan bill also lowers that debt's balance
        var debt = _debts.FirstOrDefault(d => d.Name == item.From);

        if (debt != null)
        {
            _debts.ReplaceWhere(d => d.Id == debt.Id, d => d with { Balance = Math.Max(0, d.Balance - (item.Amount ?? 0)) });
        }

        OfferUndo($"Paid {item.From} · {Fin.Eur(item.Amount ?? 0, 2)}", () =>
        {
            _inbox.ReplaceWhere(i => i.Id == item.Id, _ => item);
            _txns.RemoveAll(t => t.Id == txn.Id);

            if (debt != null)
            {
                _debts.ReplaceWhere(d => d.Id == debt.Id, d => d with { Balance = d.Balance + (item.Amount ?? 0) });
            }
        });
    }

    private static string BillCategory(InboxItem item) => item.From switch
    {
        var f when f.Contains("Energy") || f.Contains("Mobile") || f.Contains("Fiberly") => "Utilities",
        var f when f.Contains("Card") => "Debt payments",
        var f when f.Contains("Fitness") => "Health",
        var f when f.Contains("Stream") => "Entertainment",
        _ => "Bills",
    };
}
