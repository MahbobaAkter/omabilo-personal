// The main menu: one destination per job, each with its own address so a refresh or a shared link
// opens the same screen. The Financial Inbox holds what Omabilo collected (emails, uploads, review);
// My Money shows the same records sorted by what they mean (income, bills, receipts, refunds).
// Debts and Goals are separate destinations with separate records. Plain C#
// with no IKON types, so the tests check the same list the screens draw.

/// <summary>One menu destination. Premium names the plan feature that unlocks it; null is free.</summary>
public sealed record AppSection(string Key, string Path, string Label, string ShortLabel, string IconName, PremiumFeature? Premium = null);

public static class AppSections
{
    public static readonly IReadOnlyList<AppSection> All =
    [
        new("my-overview", "/home", "Home", "Home", "house"),
        new("my-inbox", "/inbox", "Financial Inbox", "Inbox", "mail"),
        new("my-money", "/money", "My Money", "Money", "wallet"),
        new("my-debts", "/debts", "Debts & Loans", "Debts", "trending-down", PremiumFeature.DebtsAndGoals),
        new("my-goals", "/goals", "Goals & Savings", "Goals", "target", PremiumFeature.DebtsAndGoals),
        new("my-payday", "/payday", "Payday Planner", "Payday", "calendar-days", PremiumFeature.PaydayPlan),
        new("my-reports", "/reports", "Monthly Reports", "Reports", "chart-column", PremiumFeature.MonthByMonth),
        new("my-assistant", "/assistant", "Finland Tax & Finance Assistant", "Assistant", "landmark"),
        new("my-settings", "/settings", "Settings", "Settings", "settings"),
    ];

    /// <summary>The destinations on the phone's bottom bar; the rest are under "More".</summary>
    public static readonly IReadOnlyList<string> PhoneBar = ["my-overview", "my-inbox", "my-money", "my-debts"];

    /// <summary>Destinations reached from the account area rather than the menu.</summary>
    public static readonly IReadOnlyList<AppSection> Unlisted =
    [
        new("my-profile", "/profile", "Profile", "Profile", "user-round"),
    ];

    public static AppSection Home => All[0];

    /// <summary>Older keys and addresses: Invoices became My Money's Bills &amp; payments tab.</summary>
    private static string? Current(string? key) => key == "my-invoices" ? "my-money" : key;

    public static AppSection ByKey(string? key) => All.Concat(Unlisted).FirstOrDefault(s => s.Key == Current(key)) ?? Home;

    /// <summary>The destination for a browser path. Unknown paths, "/" and older addresses open Home.</summary>
    public static AppSection ForPath(string? path)
    {
        var p = (path ?? "").Split('?', '#')[0].TrimEnd('/').ToLowerInvariant();
        return All.Concat(Unlisted).FirstOrDefault(s => s.Path == p) ?? p switch
        {
            "/plans" => ByKey("my-debts"),
            "/invoices" => ByKey("my-money"),
            _ => Home,
        };
    }
}
