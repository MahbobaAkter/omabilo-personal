// "Forward bills to Omabilo": every signed-in person gets a private address at the app's own mail
// domain. Mail sent there is read with the same rules as a Gmail message, but Omabilo never gets
// access to the person's mailbox, so it works with any mail provider and needs no Google approval.
// Plain C# (no IKON types): the address is the only thing that ties a message to a person.

using System.Security.Cryptography;
using System.Text.RegularExpressions;

/// <summary>A person's private forwarding address; Token is the part before the @.</summary>
public sealed record ForwardAddress
{
    public required string UserId { get; init; }
    public required string Token { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? LastReceivedUtc { get; init; }
    public int ReceivedCount { get; init; }

    /// <summary>Gmail's request to confirm an automatic-forwarding rule, kept so the person can approve it here.</summary>
    public string? GmailConfirmCode { get; init; }
    public string? GmailConfirmLink { get; init; }
    public string? GmailConfirmFor { get; init; }
    public DateTime? GmailConfirmUtc { get; init; }
}

public sealed record ForwardedFile(string FileName, string MimeType, long Size, byte[] Data);

/// <summary>One email delivered to a forwarding address, as the platform decoded it.</summary>
public sealed record ForwardedEmail(string Id, string Recipient, string From, string Subject, DateTime ReceivedUtc, string BodyText, IReadOnlyList<ForwardedFile> Files);

public enum ForwardOutcome { Filed, NotFinancial, UnknownAddress, GmailConfirmation, AlreadyDone, Failed }

public sealed record ForwardResult(ForwardOutcome Outcome, string? UserId, int NewItems = 0);

public sealed record GmailForwardConfirmation(string? Code, string? Link, string? ForAddress);

public static class BillForwarding
{
    public const string Prefix = "bills-";

    /// <summary>Marks a forwarded message's id, so it is never looked up in Gmail.</summary>
    public const string IdPrefix = "fwd:";

    public const int TokenLength = 12;

    // No 0/o, 1/l/i: an address read aloud or retyped stays unambiguous
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static string NewToken() =>
        Prefix + string.Concat(Enumerable.Range(0, TokenLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));

    /// <summary>The token an address names, or null when it isn't a well-formed forwarding address.
    /// Case and a "+tag" are ignored, as mail providers do.</summary>
    public static string? TokenOf(string recipient)
    {
        var address = InvoiceAnalysis.Address(recipient);
        var at = address?.IndexOf('@') ?? -1;

        if (address == null || at <= 0)
        {
            return null;
        }

        var local = address[..at];
        var plus = local.IndexOf('+');

        if (plus > 0)
        {
            local = local[..plus];
        }

        return local.Length == Prefix.Length + TokenLength && local.StartsWith(Prefix, StringComparison.Ordinal) && local[Prefix.Length..].All(Alphabet.Contains)
            ? local
            : null;
    }

    public static bool IsForwardedId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

    private static readonly Regex ForwardSubjectRx = new(@"^\s*(fwd?|vs|wg|tr|vb|doorst)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForwardBodyRx = new(
        @"(?im)(-{2,}\s*(forwarded message|original message|välitetty viesti|alkuperäinen viesti|weitergeleitete nachricht|vidarebefordrat meddelande|ursprungligt meddelande)|^\s*begin forwarded message)",
        RegexOptions.Compiled);

    /// <summary>True when the person forwarded the email by hand: it then comes from their own address and
    /// quotes the original sender. An automatic forward keeps the biller as the sender, so it is false.</summary>
    public static bool IsManualForward(string subject, string body) => ForwardSubjectRx.IsMatch(subject) || ForwardBodyRx.IsMatch(body);

    private static readonly Regex ConfirmCodeSubjectRx = new(@"\(#(\d{6,12})\)", RegexOptions.Compiled);
    private static readonly Regex ConfirmCodeBodyRx = new(@"(?i)confirmation code:\s*(\d{6,12})", RegexOptions.Compiled);

    // Only Google's own settings pages: a look-alike sender can never put another link in front of the person
    private static readonly Regex ConfirmLinkRx = new(@"https://(?:mail-settings|mail)\.google\.com/mail/[^\s<>""')\]]+", RegexOptions.Compiled);
    private static readonly Regex ConfirmForRx = new(@"(?i)receive mail from\s+([\w.+'-]+@[\w-]+(?:\.[\w-]+)+)", RegexOptions.Compiled);

    /// <summary>Gmail's "confirm forwarding" email, which arrives when someone sets an automatic
    /// forwarding rule to their Omabilo address. Null for any other email.</summary>
    public static GmailForwardConfirmation? GmailConfirmation(string from, string subject, string body)
    {
        if (!string.Equals(InvoiceAnalysis.Address(from), "forwarding-noreply@google.com", StringComparison.OrdinalIgnoreCase)
            || !subject.Contains("Forwarding Confirmation", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var code = ConfirmCodeSubjectRx.Match(subject) is { Success: true } s ? s.Groups[1].Value
            : ConfirmCodeBodyRx.Match(body) is { Success: true } b ? b.Groups[1].Value
            : null;
        var link = ConfirmLinkRx.Match(body) is { Success: true } l ? l.Value : null;
        var who = ConfirmForRx.Match(subject + "\n" + body) is { Success: true } w ? w.Groups[1].Value.ToLowerInvariant() : null;

        return code == null && link == null ? null : new GmailForwardConfirmation(code, link, who);
    }

    /// <summary>The start of the body, on one line, as a Gmail snippet would be.</summary>
    public static string Snippet(string body)
    {
        var flat = Regex.Replace(body, @"\s+", " ").Trim();
        return flat.Length > 200 ? flat[..200] : flat;
    }
}
