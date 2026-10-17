namespace Omabilo.Api.Finance;

/// <summary>
/// FICTIONAL Finnish demo finances: every company, account and amount is made up. Four months of
/// coherent history ending today, so screens and reports have real-looking data to work with.
/// The same owner always gets the same data (seeded by a stable hash of the owner id).
/// </summary>
public static class FinnishDemoSeed
{
    public const int PayDay = 25;
    public const decimal NetSalary = 2780m;

    /// <summary>The balance the current account shows today in a fresh demo.</summary>
    public const decimal BalanceToday = 812.40m;

    public static FinanceData Create(string ownerId, DateTime today)
    {
        today = today.Date;
        var rng = new Random(StableHash(ownerId));
        var start = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-4);

        var loans = new List<Loan>
        {
            new("loan-card", "Aurora credit card", "Aurora Bank (fictional)", "Credit card", 1150m, 2000m, 18.9m, 45m, 22),
            new("loan-car", "Car loan", "Saari Rahoitus (fictional)", "Car loan", 4200m, 9000m, 6.9m, 190m, 12),
            new("loan-study", "Student loan", "Tunturi Pankki (fictional)", "Student loan", 6800m, 8000m, 3.2m, 110m, 5),
        };

        var goals = new List<SavingsGoalRecord>
        {
            new("goal-buffer", "Emergency fund", 3000m, 1420m, today.AddMonths(10), true),
            new("goal-lapland", "Summer trip to Lapland", 1200m, 380m, new DateTime(today.Year + 1, 6, 15, 0, 0, 0, DateTimeKind.Utc), false),
            new("goal-laptop", "New laptop", 1100m, 610m, today.AddMonths(4), false),
        };

        var recurring = new List<RecurringItem>
        {
            new("rec-salary", "Salary, Revontuli Design Oy (fictional)", "Income", RecurringKind.Income, NetSalary, PayDay),
            new("rec-rent", "Rent, Asunto Oy Koivurinne (fictional)", "Housing", RecurringKind.Essential, 890m, 1),
            new("rec-transport", "Season ticket, Kaupunkiliikenne (fictional)", "Transport", RecurringKind.Essential, 64.70m, 3),
            new("rec-electricity", "Electricity, Pohjoisvirta Energia (fictional)", "Utilities", RecurringKind.Essential, 62m, 20, AmountVaries: true),
            new("rec-internet", "Broadband, Kuituverkko Oy (fictional)", "Utilities", RecurringKind.Essential, 29.90m, 8),
            new("rec-phone", "Phone, Nimbus Mobile (fictional)", "Utilities", RecurringKind.Essential, 19.90m, 9),
            new("rec-insurance", "Home insurance, Lumo Vakuutus (fictional)", "Insurance", RecurringKind.Essential, 18.50m, 14),
            new("rec-gym", "Gym, Tunturi Fitness (fictional)", "Health", RecurringKind.Subscription, 39m, 4),
            new("rec-stream", "Streaming, Kino+ (fictional)", "Entertainment", RecurringKind.Subscription, 12.99m, 6),
            new("rec-card", "Aurora credit card minimum", "Debt payments", RecurringKind.LoanPayment, 45m, 22, LoanId: "loan-card"),
            new("rec-car", "Car loan payment", "Debt payments", RecurringKind.LoanPayment, 190m, 12, LoanId: "loan-car"),
            new("rec-study", "Student loan payment", "Debt payments", RecurringKind.LoanPayment, 110m, 5, LoanId: "loan-study"),
            new("rec-save", "Transfer to emergency fund", "Savings", RecurringKind.Savings, 150m, 26, GoalId: "goal-buffer"),
        };

        var txns = new List<FinanceTransaction>();
        var n = 0;

        void Add(DateTime date, string description, string category, decimal amount, string? recurringId = null)
        {
            if (date <= today && date >= start)
            {
                txns.Add(new FinanceTransaction($"txn-demo-{++n:0000}", date, description, category, amount, recurringId));
            }
        }

        decimal Vary(decimal min, decimal max) => Math.Round(min + (decimal)rng.NextDouble() * (max - min), 2);

        for (var m = start; m <= today; m = m.AddMonths(1))
        {
            foreach (var r in recurring)
            {
                var amount = r.AmountVaries ? Vary(48m, 79m) : r.Amount;
                Add(OnDay(m, r.DayOfMonth), r.Name, r.Category, r.Kind == RecurringKind.Income ? amount : -amount, r.Id);
            }

            string[] shops = ["Kauppahalli Market (fictional)", "Lähikauppa Kotimarket (fictional)"];
            for (var week = 0; week < 5; week++)
            {
                Add(m.AddDays(week * 7 + 1), shops[week % 2], "Groceries", -Vary(42m, 96m));
            }

            string[] cafes = ["Café Sävel (fictional)", "Ravintola Lumikki (fictional)", "Pizzeria Tähti (fictional)"];
            for (var i = 0; i < 3; i++)
            {
                Add(m.AddDays(5 + i * 8 + rng.Next(0, 3)), cafes[(i + m.Month) % cafes.Length], "Dining out", -Vary(12m, 38m));
            }

            Add(m.AddDays(16 + rng.Next(0, 5)), "Kirjakauppa Sivu (fictional)", "Shopping", -Vary(18m, 64m));
            Add(m.AddDays(18), "Apteekki Kuusi (fictional)", "Health", -Vary(8m, 26m));
        }

        txns = txns.OrderBy(t => t.Date).ThenBy(t => t.Id).ToList();
        var opening = BalanceToday - txns.Sum(t => t.Amount);

        var expected = new List<ExpectedIncome>
        {
            new("inc-tax-refund", "Tax refund, Verohallinto demo (fictional)", today.AddDays(6), 184m, Confirmed: true),
            new("inc-resale", "Selling an old bike (not yet agreed)", today.AddDays(4), 120m, Confirmed: false),
        };

        return new FinanceData(
            new FinanceSettings("Demo user", PayDay, NetSalary, "Revontuli Design Oy (fictional)", 18m, IsDemoData: true),
            opening, start, txns, recurring, expected, loans, goals);
    }

    public static DateTime OnDay(DateTime month, int day) =>
        new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)), 0, 0, 0, DateTimeKind.Utc);

    /// <summary>FNV-1a: stable across processes, unlike string.GetHashCode.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 2166136261u;

            foreach (var c in s)
            {
                h = (h ^ c) * 16777619u;
            }

            return (int)(h & 0x7fffffff);
        }
    }
}
