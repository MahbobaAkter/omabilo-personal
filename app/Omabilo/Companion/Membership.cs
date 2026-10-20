// Omabilo's two plans. Free: bills added by hand (a PDF or pasted text), explained by rules, reviewed,
// with reminders on Home and copy-only drafts. Premium (€2.99 a month): bills found automatically
// (Gmail or a forwarding address), photos and scans read, AI explanations, debts and goals, the payday
// plan and the month-by-month summary. Plain C# with no IKON types, so the screens and the tests agree.

public enum PremiumFeature { Gmail, Forwarding, PhotoReading, AiExplanations, DebtsAndGoals, PaydayPlan, MonthByMonth }

public static class Membership
{
    /// <summary>The payment offer's id, as created with <c>ikon offer create premium …</c>.</summary>
    public const string PremiumOfferId = "premium";

    public const long PremiumPriceMinor = 299;

    public const string PremiumPrice = "€2.99 a month";

    /// <summary>Gmail is free for this many days from the first connection, then part of Premium.</summary>
    public const int GmailTrialDays = 30;

    public static readonly IReadOnlyList<string> FreeIncludes =
    [
        "Add bills as a PDF or pasted text",
        "Each bill explained in plain English, Finnish bills included",
        "Check and correct what was read",
        "Reminders on Home before bills are due",
        "\"Ask for more time\" letters to copy",
        $"Gmail free for your first {GmailTrialDays} days",
    ];

    public static readonly IReadOnlyList<PremiumFeature> PremiumAdds =
    [
        PremiumFeature.Gmail,
        PremiumFeature.Forwarding,
        PremiumFeature.PhotoReading,
        PremiumFeature.AiExplanations,
        PremiumFeature.DebtsAndGoals,
        PremiumFeature.PaydayPlan,
        PremiumFeature.MonthByMonth,
    ];

    public static string Describe(PremiumFeature feature) => feature switch
    {
        PremiumFeature.Gmail => $"Gmail after the free {GmailTrialDays} days: bills, payslips and receipts found for you (read-only)",
        PremiumFeature.Forwarding => "Your own forwarding address for any email provider",
        PremiumFeature.PhotoReading => "Photos and scanned bills read with text recognition",
        PremiumFeature.AiExplanations => "AI explanations of each bill (opt-in, processed in the EU)",
        PremiumFeature.DebtsAndGoals => "Debts and savings goals with payoff plans",
        PremiumFeature.PaydayPlan => "Money until payday: what's safe to spend",
        PremiumFeature.MonthByMonth => "Month-by-month summary of bills, pay and receipts",
        _ => feature.ToString(),
    };

    /// <summary>The last day a paid month covers. A month bought on 19 September is paid through
    /// 18 October; unless it was cancelled, the card is charged again on 19 October.</summary>
    public static DateTime LastPaidDay(DateTime periodEndUtc) => periodEndUtc.Date.AddDays(-1);

    /// <summary>When the free Gmail trial ends, or null before Gmail was ever connected. Connections made
    /// before the trial existed count from the day they were connected.</summary>
    public static DateTime? GmailTrialEnds(CompanionSettings settings, GmailConnection? connection) =>
        (settings.GmailTrialStartUtc ?? connection?.ConnectedUtc)?.AddDays(GmailTrialDays);

    /// <summary>True while Gmail needs no Premium: never connected yet, or within the free days.</summary>
    public static bool GmailFreeNow(CompanionSettings settings, GmailConnection? connection, DateTime nowUtc) =>
        GmailTrialEnds(settings, connection) is not { } end || nowUtc < end;

    /// <summary>The user-testing access code: entering it gives Premium without payment, until real
    /// payments go live. The secret OMABILO_ACCESS_CODE replaces it; an empty secret keeps this one.</summary>
    public const string DefaultAccessCode = "NOVA";

    /// <summary>Wrong codes allowed per person before they wait <see cref="AccessCodeLockout"/>.</summary>
    public const int AccessCodeMaxTries = 5;

    public static readonly TimeSpan AccessCodeLockout = TimeSpan.FromMinutes(15);

    /// <summary>True when the entered code is the access code. Case and surrounding spaces don't matter.</summary>
    public static bool IsAccessCode(string? entered, string? configured = null)
    {
        var code = string.IsNullOrWhiteSpace(configured) ? DefaultAccessCode : configured.Trim();
        return !string.IsNullOrWhiteSpace(entered) && string.Equals(entered.Trim(), code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True while the person has Premium from the access code.</summary>
    public static bool HasAccessCode(CompanionSettings settings) => settings.AccessCodeUtc != null;

    /// <summary>Premium given without payment (the owner's test accounts), from a comma- or
    /// semicolon-separated list of emails. Matching ignores case and surrounding spaces.</summary>
    public static bool IsComplimentary(string? email, string? list)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(list))
        {
            return false;
        }

        return list.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(e => e.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
