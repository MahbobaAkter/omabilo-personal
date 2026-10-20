// What the Gmail bar on Home says for each connection state, worked out in plain C# so the tests
// can check every state: connected, syncing, first scan, error, reconnect, rate-limited and the
// free trial. The screen only draws what this returns.

public enum GmailBarKind { Connected, FirstScanPending, MySync, BackgroundSync, RateLimited, Error, NeedsReconnect, TrialEnded }

public enum GmailBarAction { SyncNow, Reconnect, GetPremium }

/// <param name="Dot">The status dot's colour role: success, info, warning, error or neutral.</param>
/// <param name="SyncDisabledReason">Why Sync now is disabled, shown beside it; null when it can be pressed.</param>
/// <param name="AlertTone">warning or error when <paramref name="AlertTitle"/> is set.</param>
/// <param name="TrialBadge">A small "Free until …" badge while the trial has more than a week left.</param>
/// <param name="ShowTrialNotice">The full trial notice with the Premium upgrade shows under the bar.</param>
public sealed record GmailBarState(
    GmailBarKind Kind,
    string Title,
    string Summary,
    string Dot,
    GmailBarAction Action,
    string? SyncDisabledReason,
    string? AlertTitle,
    string? AlertText,
    string? AlertTone,
    string? TrialBadge,
    bool ShowTrialNotice);

public static class GmailBar
{
    /// <param name="trialEnd">When the free Gmail trial ends; null for Premium (no trial applies).</param>
    /// <param name="mySync">This person pressed Sync now and it is still running.</param>
    /// <param name="awaitingFirstScan">The person has just connected and the first scan hasn't finished.</param>
    public static GmailBarState Describe(GmailConnection c, DateTime? trialEnd, bool mySync, bool awaitingFirstScan, DateTime nowUtc)
    {
        var email = string.IsNullOrWhiteSpace(c.GoogleEmail) ? "Gmail" : c.GoogleEmail!;
        var lastGood = c.LastSuccessUtc is { } t ? $"Last successful sync {Ago(t, nowUtc)}" : "No successful sync yet";
        var trialEnded = trialEnd is { } end && nowUtc >= end;
        var badge = trialEnd is { } te && nowUtc < te && (te - nowUtc).TotalDays > 7 ? $"Free until {InvoiceExplainer.Day(te)}" : null;
        var notice = trialEnd != null && badge == null;

        // Once the trial has ended Omabilo stops reading Gmail, so nothing else about the sync applies
        if (trialEnded)
        {
            return new(GmailBarKind.TrialEnded, "Gmail paused: free trial ended", $"{email} · {lastGood}", "neutral",
                GmailBarAction.GetPremium, null, null, null, null, null, notice);
        }

        if (c.Status == ConnectionStatus.NeedsReconnect)
        {
            return new(GmailBarKind.NeedsReconnect, "Gmail needs reconnecting", $"{email} · {lastGood}", "error",
                GmailBarAction.Reconnect, null, "Google no longer allows access", "Press Reconnect Gmail to continue. The bills Omabilo already found stay here.", "error", badge, notice);
        }

        if (c.Status == ConnectionStatus.Error)
        {
            var why = string.IsNullOrWhiteSpace(c.LastError) ? "Gmail didn't answer" : c.LastError!.TrimEnd('.');
            return new(GmailBarKind.Error, "Gmail sync problem", $"{email} · {lastGood}", "warning",
                GmailBarAction.SyncNow, mySync ? "Syncing…" : null, "Last sync failed", $"{why}. Retrying automatically.", "warning", badge, notice);
        }

        if (mySync || (awaitingFirstScan && !c.InitialScanDone))
        {
            return new(GmailBarKind.MySync, "Gmail connected", $"{email} · Looking for financial messages…", "info",
                GmailBarAction.SyncNow, "Syncing…", null, null, null, badge, notice);
        }

        if (c.SyncLockUntilUtc is { } lockUntil && lockUntil > nowUtc)
        {
            return new(GmailBarKind.BackgroundSync, "Gmail connected", $"{email} · Syncing in the background…", "info",
                GmailBarAction.SyncNow, "Omabilo is already checking your Gmail. Sync now is available again when it finishes.", null, null, null, badge, notice);
        }

        if (c.LastError == SyncEngine.RateLimitNote)
        {
            return new(GmailBarKind.RateLimited, "Gmail connected", $"{email} · {lastGood}", "warning",
                GmailBarAction.SyncNow, null, "Gmail asked Omabilo to slow down", "The scan carries on by itself in a few minutes; nothing found so far is lost.", "warning", badge, notice);
        }

        if (c.LastSuccessUtc is not { } synced)
        {
            return new(GmailBarKind.FirstScanPending, "Gmail connected", $"{email} · First scan pending", "info",
                GmailBarAction.SyncNow, null, null, null, null, badge, notice);
        }

        var scanned = c.LastSyncScanned > 0 ? $" · {c.LastSyncScanned} new message{(c.LastSyncScanned == 1 ? "" : "s")} checked" : "";
        return new(GmailBarKind.Connected, "Gmail connected", $"{email} · Synced {Ago(synced, nowUtc)}{scanned}", "success",
            GmailBarAction.SyncNow, null, null, null, null, badge, notice);
    }

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var s = nowUtc - utc;
        return s.TotalMinutes < 1 ? "just now" : s.TotalHours < 1 ? $"{(int)s.TotalMinutes} min ago" : s.TotalDays < 1 ? $"{(int)s.TotalHours} h ago" : $"{(int)s.TotalDays} days ago";
    }
}
