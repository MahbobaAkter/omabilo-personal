using System.Globalization;

// ── Records kept per person (all demo data is fictional) ────────────────────

public enum InboxKind { Bill, Alert, Insight, Income }

public enum InboxStatus { New, Snoozed, Done, Archived }

public record InboxItem(
    string Id,
    InboxKind Kind,
    string Title,
    string Detail,
    string From,
    decimal? Amount,
    DateTime? DueDate,
    DateTime ReceivedAt,
    InboxStatus Status = InboxStatus.New,
    DateTime? SnoozedUntil = null);

public enum DebtKind { CreditCard, StudentLoan, CarLoan, PayLater, Personal }

public record DebtAccount(
    string Id,
    string Name,
    string Lender,
    DebtKind Kind,
    decimal Balance,
    decimal StartingBalance,
    decimal Apr,
    decimal MinPayment,
    int DueDay);

public record SavingsGoal(
    string Id,
    string Name,
    string IconName,
    decimal Target,
    decimal Saved,
    DateTime TargetDate,
    bool IsEmergencyFund = false);

public record PayBucket(string Id, string Name, string IconName, string Hint, decimal Planned, bool Moved);

// The current account balance is OpeningBalance plus every transaction dated on or after BalanceSince
public record PaydaySettings(decimal NetPay, int PayDay, decimal OpeningBalance, DateTime BalanceSince);

// Amount is negative for money going out and positive for money coming in
public record Txn(string Id, DateTime Date, string Merchant, string Category, decimal Amount, string? LinkId = null);

// What the last change was and how to take it back
public sealed record UndoEntry(long Id, string Message, Action Revert);

public record PayoffResult(int Months, decimal TotalInterest, bool NeverPaysOff, List<decimal> Remaining, List<(string Name, int Month)> Order);

// ── Formatting and finance helpers ──────────────────────────────────────────

public static class Fin
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Eur(decimal v, int decimals = 0)
    {
        var abs = Math.Abs(v).ToString(decimals == 0 ? "N0" : "N2", Inv);
        return v < 0 ? $"−€{abs}" : $"€{abs}";
    }

    public static string Signed(decimal v, int decimals = 2) => v > 0 ? $"+{Eur(v, decimals)}" : Eur(v, decimals);

    public static string Pct(double v) => $"{v.ToString("0", Inv)}%";

    public static string Day(DateTime d) => d.ToString("d MMM", Inv);

    public static string DayLong(DateTime d) => d.ToString("ddd d MMM yyyy", Inv);

    public static string Month(DateTime d) => d.ToString("MMMM yyyy", Inv);

    public static string MonthKey(DateTime d) => d.ToString("yyyy-MM", Inv);

    public static bool TryParseAmount(string? text, out decimal amount)
    {
        var cleaned = (text ?? "").Replace("€", "").Replace(" ", "").Replace(",", ".").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, Inv, out amount);
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    public static DateTime NextPayday(DateTime today, int payDay)
    {
        var day = Math.Min(payDay, DateTime.DaysInMonth(today.Year, today.Month));
        var candidate = new DateTime(today.Year, today.Month, day, 0, 0, 0, DateTimeKind.Utc);

        if (candidate < today.Date)
        {
            var next = today.AddMonths(1);
            candidate = new DateTime(next.Year, next.Month, Math.Min(payDay, DateTime.DaysInMonth(next.Year, next.Month)), 0, 0, 0, DateTimeKind.Utc);
        }

        return candidate;
    }

    public static DateTime LastPayday(DateTime today, int payDay)
    {
        var next = NextPayday(today, payDay);

        if (next == today.Date)
        {
            return next;
        }

        var prev = next.AddMonths(-1);
        return new DateTime(prev.Year, prev.Month, Math.Min(payDay, DateTime.DaysInMonth(prev.Year, prev.Month)), 0, 0, 0, DateTimeKind.Utc);
    }

    public static string DebtKindLabel(DebtKind kind) => kind switch
    {
        DebtKind.CreditCard => "Credit card",
        DebtKind.StudentLoan => "Student loan",
        DebtKind.CarLoan => "Car loan",
        DebtKind.PayLater => "Buy now, pay later",
        _ => "Personal loan",
    };

    public static string DebtKindIcon(DebtKind kind) => kind switch
    {
        DebtKind.CreditCard => "credit-card",
        DebtKind.StudentLoan => "graduation-cap",
        DebtKind.CarLoan => "car",
        DebtKind.PayLater => "shopping-bag",
        _ => "landmark",
    };

    // Month-by-month payoff: interest is added, every minimum is paid, and the rest of the
    // budget (the extra plus minimums freed by debts already cleared) goes to the target debt
    public static PayoffResult Simulate(IEnumerable<DebtAccount> debts, decimal extra, string strategy)
    {
        var active = debts.Where(d => d.Balance > 0).Select(d => new SimDebt(d)).ToList();
        var budget = active.Sum(d => d.Debt.MinPayment) + extra;
        var remaining = new List<decimal> { active.Sum(d => d.Balance) };
        var order = new List<(string, int)>();
        decimal interest = 0;
        var month = 0;

        while (active.Any(d => d.Balance > 0.005m) && month < 360)
        {
            month++;

            foreach (var d in active.Where(d => d.Balance > 0))
            {
                var i = Math.Round(d.Balance * d.Debt.Apr / 100m / 12m, 2);
                d.Balance += i;
                interest += i;
            }

            var left = budget;

            foreach (var d in active.Where(d => d.Balance > 0))
            {
                var pay = Math.Min(d.Debt.MinPayment, d.Balance);
                d.Balance -= pay;
                left -= pay;
            }

            var targets = strategy == "snowball"
                ? active.Where(d => d.Balance > 0).OrderBy(d => d.Balance)
                : active.Where(d => d.Balance > 0).OrderByDescending(d => d.Debt.Apr).ThenBy(d => d.Balance);

            foreach (var d in targets.ToList())
            {
                if (left <= 0)
                {
                    break;
                }

                var pay = Math.Min(left, d.Balance);
                d.Balance -= pay;
                left -= pay;
            }

            foreach (var d in active.Where(d => d.Balance <= 0.005m && !d.Done))
            {
                d.Done = true;
                d.Balance = 0;
                order.Add((d.Debt.Name, month));
            }

            remaining.Add(Math.Max(0, active.Sum(d => d.Balance)));
        }

        var never = active.Any(d => d.Balance > 0.005m);
        return new PayoffResult(month, interest, never, remaining, order);
    }

    private sealed class SimDebt(DebtAccount debt)
    {
        public DebtAccount Debt { get; } = debt;
        public decimal Balance { get; set; } = debt.Balance;
        public bool Done { get; set; }
    }

    public static readonly Dictionary<string, string> CategoryColors = new()
    {
        ["Housing"] = "#0f766e",
        ["Groceries"] = "#14b8a6",
        ["Transport"] = "#6366f1",
        ["Utilities"] = "#f59e0b",
        ["Dining out"] = "#c2410c",
        ["Shopping"] = "#a855f7",
        ["Health"] = "#22c55e",
        ["Entertainment"] = "#0ea5e9",
        ["Debt payments"] = "#64748b",
        ["Savings"] = "#84cc16",
        ["Bills"] = "#eab308",
        ["Income"] = "#10b981",
        ["Other"] = "#94a3b8",
    };

    public static string CategoryColor(string category) => CategoryColors.GetValueOrDefault(category, "#94a3b8");

    // The same colours as palette steps, for style classes (charts take the hex values above)
    public static string CategoryPalette(string category) => category switch
    {
        "Housing" => "teal-700",
        "Groceries" => "teal-500",
        "Transport" => "indigo-500",
        "Utilities" => "amber-500",
        "Dining out" => "orange-700",
        "Shopping" => "purple-500",
        "Health" => "green-500",
        "Entertainment" => "sky-500",
        "Debt payments" => "slate-500",
        "Savings" => "lime-500",
        "Bills" => "yellow-500",
        "Income" => "emerald-500",
        _ => "slate-400",
    };

    public static string CategoryIcon(string category) => category switch
    {
        "Housing" => "house",
        "Groceries" => "shopping-cart",
        "Transport" => "bus",
        "Utilities" => "zap",
        "Dining out" => "utensils",
        "Shopping" => "shopping-bag",
        "Health" => "heart-pulse",
        "Entertainment" => "clapperboard",
        "Debt payments" => "credit-card",
        "Savings" => "target",
        "Bills" => "receipt",
        "Income" => "banknote",
        _ => "circle-dot",
    };
}

// ── Fictional demo data, dated relative to today ────────────────────────────

public static class DemoData
{
    public const int PayDay = 25;
    public const decimal NetPay = 2640m;

    private static DateTime Today => DateTime.UtcNow.Date;

    private static DateTime On(DateTime month, int day) =>
        new(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)), 0, 0, 0, DateTimeKind.Utc);

    public static List<Txn> Transactions()
    {
        var rng = new Random(20261017);
        var today = Today;
        var list = new List<Txn>();
        var firstMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-6);

        void Add(DateTime date, string merchant, string category, decimal amount)
        {
            if (date <= today)
            {
                list.Add(new Txn(Fin.NewId(), date, merchant, category, amount));
            }
        }

        decimal Vary(decimal min, decimal max) => Math.Round(min + (decimal)rng.NextDouble() * (max - min), 2);

        for (var m = 0; m <= 6; m++)
        {
            var month = firstMonth.AddMonths(m);

            Add(On(month, PayDay), "Lumi Studio Oy — salary", "Income", NetPay);
            Add(On(month, 1), "Koivu Housing — rent", "Housing", -950m);
            Add(On(month, 3), "Northwind Energy", "Utilities", -Vary(42, 78));
            Add(On(month, 8), "Fiberly broadband", "Utilities", -29.90m);
            Add(On(month, 9), "Nimbus Mobile", "Utilities", -19.90m);
            Add(On(month, 2), "Metro monthly pass", "Transport", -58m);
            Add(On(month, 6), "StreamBox", "Entertainment", -12.99m);
            Add(On(month, 4), "Fjord Fitness", "Health", -39m);
            Add(On(month, 22), "Aurora Card payment", "Debt payments", -55m);
            Add(On(month, 5), "Brightpath Student Loan", "Debt payments", -120m);
            Add(On(month, 12), "Northbank Auto loan", "Debt payments", -210m);
            Add(On(month, 26), "Transfer to Emergency fund", "Savings", -150m);

            foreach (var week in new[] { 2, 9, 16, 23, 29 })
            {
                Add(On(month, week), week % 2 == 0 ? "Lähikauppa Market" : "Harbor Foods", "Groceries", -Vary(48, 96));
            }

            var dining = m == 6 ? 5 : 3;
            string[] cafes = ["Café Sävel", "Noodle Bar Ume", "Pizzeria Luna", "Bistro Kallio", "Taco Tori"];

            for (var i = 0; i < dining; i++)
            {
                Add(On(month, 4 + i * 5 + rng.Next(0, 3)), cafes[(i + m) % cafes.Length], "Dining out", -Vary(14, 46));
            }

            string[] shops = ["Lumo Home", "Kaari Outdoor", "PageTurner Books", "Nordic Threads"];
            Add(On(month, 13 + rng.Next(0, 6)), shops[m % shops.Length], "Shopping", -Vary(24, 120));

            if (m % 2 == 0)
            {
                Add(On(month, 17), "Citybike day pass", "Transport", -Vary(5, 12));
                Add(On(month, 19), "Kino Aurora", "Entertainment", -Vary(13, 28));
            }

            if (m % 3 == 1)
            {
                Add(On(month, 20), "Apteekki Pharmacy", "Health", -Vary(9, 34));
            }
        }

        return list.OrderByDescending(t => t.Date).ToList();
    }

    public static PaydaySettings Payday(List<Txn> txns)
    {
        // Opening balance chosen so the current account shows a realistic amount today
        var last = Fin.LastPayday(Today, PayDay);
        var sinceLast = txns.Where(t => t.Date >= last).Sum(t => t.Amount);
        return new PaydaySettings(NetPay, PayDay, 742m - sinceLast, last);
    }

    public static List<InboxItem> Inbox()
    {
        var t = Today;
        var payday = Fin.NextPayday(t, PayDay);

        return
        [
            new(Fin.NewId(), InboxKind.Bill, "Electricity bill is ready", "Your September usage was 214 kWh, 9% lower than August. Paying on time keeps your account in good standing.", "Northwind Energy", 61.40m, t.AddDays(3), t.AddHours(-5)),
            new(Fin.NewId(), InboxKind.Bill, "Credit card minimum payment due", "Statement balance €1,840. Paying more than the minimum reduces the 19.9% interest you are charged.", "Aurora Card", 55m, t.AddDays(5), t.AddDays(-1)),
            new(Fin.NewId(), InboxKind.Bill, "Phone plan renews", "Unlimited 5G plan, monthly renewal.", "Nimbus Mobile", 19.90m, t.AddDays(4), t.AddDays(-2)),
            new(Fin.NewId(), InboxKind.Bill, "Broadband invoice", "Fiber 300M, November period.", "Fiberly", 29.90m, payday.AddDays(3), t.AddDays(-1)),
            new(Fin.NewId(), InboxKind.Alert, "Dining out is running high", "You have spent 38% more on dining out this month than your 3-month average. €60 more would put you over your Everyday budget.", "Omabilo insights", null, null, t.AddHours(-2)),
            new(Fin.NewId(), InboxKind.Alert, "Subscription price is going up", "StreamBox will cost €14.99 instead of €12.99 from next month. That is €24 more a year.", "StreamBox", 14.99m, t.AddDays(14), t.AddDays(-3)),
            new(Fin.NewId(), InboxKind.Income, "Tax refund on its way", "Your refund of €184 is expected in your current account before the end of the month.", "Tax office (demo)", 184m, t.AddDays(9), t.AddDays(-4)),
            new(Fin.NewId(), InboxKind.Insight, "Emergency fund at 64%", "You have about one and a half months of essentials saved. A few more months of €150 transfers would reach your €4,500 target faster than planned.", "Omabilo insights", null, null, t.AddDays(-5)),
            new(Fin.NewId(), InboxKind.Insight, "Round-ups could add €18 a month", "Rounding card purchases up to the nearest euro and moving the difference into savings would add roughly €18 a month.", "Omabilo insights", null, null, t.AddDays(-6)),
            new(Fin.NewId(), InboxKind.Bill, "Gym membership paid", "Fjord Fitness monthly membership.", "Fjord Fitness", 39m, t.AddDays(-13), t.AddDays(-14), InboxStatus.Done),
        ];
    }

    public static List<DebtAccount> Debts() =>
    [
        new(Fin.NewId(), "Aurora Card", "Aurora Bank (demo)", DebtKind.CreditCard, 1840m, 2600m, 19.9m, 55m, 22),
        new(Fin.NewId(), "Car loan", "Northbank Auto (demo)", DebtKind.CarLoan, 4980m, 9500m, 6.4m, 210m, 12),
        new(Fin.NewId(), "Student loan", "Brightpath Student Finance (demo)", DebtKind.StudentLoan, 7450m, 9000m, 3.1m, 120m, 5),
        new(Fin.NewId(), "Sofa instalments", "PayLater Plus (demo)", DebtKind.PayLater, 240m, 480m, 0m, 80m, 28),
    ];

    public static List<SavingsGoal> Goals()
    {
        var t = Today;
        return
        [
            new(Fin.NewId(), "Emergency fund", "life-buoy", 4500m, 2880m, t.AddMonths(8), true),
            new(Fin.NewId(), "Summer trip to Lisbon", "plane", 1800m, 640m, new DateTime(t.Year + 1, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            new(Fin.NewId(), "New laptop", "laptop", 1200m, 910m, t.AddMonths(3)),
            new(Fin.NewId(), "Winter tyres", "car", 600m, 600m, t.AddDays(20)),
        ];
    }

    public static List<PayBucket> Buckets() =>
    [
        new("bills", "Bills & rent", "receipt", "Rent, energy, phone, broadband", 1150m, false),
        new("debt", "Debt payments", "credit-card", "Minimums plus any extra", 465m, false),
        new("savings", "Savings & goals", "target", "Emergency fund and goals", 300m, false),
        new("everyday", "Everyday spending", "shopping-cart", "Groceries, transport, health", 525m, false),
        new("fun", "Fun & treats", "party-popper", "Eating out, films, hobbies", 150m, false),
    ];
}
