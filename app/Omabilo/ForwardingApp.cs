// IKON wiring for "Forward bills to Omabilo": the app's inbound mail reaches the EmailReceived trigger
// (and a sweep that catches any wake-up the platform missed), each message is handed to the sync
// engine, and the platform's copy is deleted once it has been read. Logs carry ids and outcomes only.

using Ikon.App.Triggers;
using Ikon.Common.Core.Protocol;

public partial class OmabiloApp
{
    private const long MaxForwardedFileBytes = 10 * 1024 * 1024;
    private const int MaxForwardedFiles = 10;

    private static readonly SemaphoreSlim InboundGate = new(1, 1);

    private string? _inboundDomain;

    /// <summary>The domain forwarding addresses live on, or null until receiving mail is turned on for the app.</summary>
    private async Task<string?> InboundDomainAsync()
    {
        if (_inboundDomain != null)
        {
            return _inboundDomain;
        }

        try
        {
            _inboundDomain = (await app.Email.GetInboundDomainsAsync()).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"inbound domain lookup failed: {ex.GetType().Name}");
        }

        return _inboundDomain;
    }

    [Trigger(TriggerEventType.EmailReceived, MaxParallelism = 2)]
    internal async Task OnBillEmailReceivedAsync(TriggerContext context, CancellationToken ct)
    {
        var envelope = context.GetPayload<EmailReceivedPayload>();

        // Throwing leaves the event pending, so the platform delivers it again later
        await ImportInboundAsync(envelope.Id, ct);
    }

    // The trigger's wake-up is best-effort: this picks up anything still waiting in the inbox
    [Cron("*/10 * * * *")]
    public async Task SweepForwardedBills(CancellationToken ct)
    {
        await using var hold = await app.BackgroundWork.StartAsync(TimeSpan.FromMinutes(9), "forwarded bills sweep");
        var handled = 0;

        try
        {
            await foreach (var summary in app.Email.EnumerateInboxAsync(new InboxQuery { Limit = 50 }, ct))
            {
                if (handled >= 50 || ct.IsCancellationRequested) break;

                // A message that just arrived is the trigger's; it is claimed either way, so this only avoids double work
                if (summary.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(-2)) continue;

                try
                {
                    await ImportInboundAsync(summary.Id, ct);
                    handled++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Instance.Warning($"forward sweep: message {summary.Id} failed with {ex.GetType().Name}");
                }
            }

            await RecordJobRunAsync(ForwardJobKey, $"ok: {handled} message(s) handled", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordJobRunAsync(ForwardJobKey, $"failed: {ex.GetType().Name}", ct);
            throw;
        }
    }

    private string ForwardJobKey => $"forward-sweep@{new Uri(app.PublicUrl).Authority}";

    /// <summary>Reads one inbound email, files it for the address's owner, then deletes the platform's
    /// copy: Omabilo keeps only what it extracted, as it does for Gmail.</summary>
    private async Task ImportInboundAsync(string emailId, CancellationToken ct)
    {
        await InboundGate.WaitAsync(ct);

        try
        {
            InboundEmailDetail detail;

            try
            {
                detail = await app.Email.GetMessageAsync(emailId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already handled and deleted by the other path
                Log.Instance.Info($"forwarded message {emailId} not found: {ex.GetType().Name}");
                return;
            }

            if (BillForwarding.TokenOf(detail.Recipient) == null)
            {
                Log.Instance.Info($"inbound message {emailId}: not a forwarding address, deleted");
                await app.Email.DeleteAsync(emailId, ct);
                return;
            }

            var files = new List<ForwardedFile>();

            foreach (var a in detail.Attachments.Take(MaxForwardedFiles))
            {
                if (a.Size > MaxForwardedFileBytes)
                {
                    // Listed as too large by the reader; never downloaded
                    files.Add(new ForwardedFile(a.Filename, a.MimeType, a.Size, []));
                    continue;
                }

                await using var download = await app.Email.DownloadAttachmentAsync(detail.Id, a.Id, ct);
                using var buffer = new MemoryStream();
                await download.Content.CopyToAsync(buffer, ct);
                files.Add(new ForwardedFile(a.Filename, download.MimeType, Math.Max(a.Size, buffer.Length), buffer.ToArray()));
            }

            var body = !string.IsNullOrWhiteSpace(detail.BodyText) ? detail.BodyText
                : detail.BodyHtml is { } html ? GoogleClient.HtmlToText(html)
                : "";

            var result = await Engine.ImportForwardedAsync(
                new ForwardedEmail(detail.Id, detail.Recipient, detail.From, detail.Subject ?? "", detail.ReceivedAt.UtcDateTime, body, files), ct);

            Log.Instance.Info($"forwarded message {emailId}: {result.Outcome}, {files.Count} file(s), {result.NewItems} new item(s){(result.UserId != null ? $" for user {result.UserId}" : "")}");
            await app.Email.DeleteAsync(emailId, ct);
        }
        finally
        {
            InboundGate.Release();
        }
    }
}
