// Connected companion: the records a signed-in person's Gmail produces. Plain C# (no IKON types)
// so the test project compiles these files in. Every record carries the owner's user id, and every
// store method takes it, so one person's data is never reachable through another's id.

using System.Text.Json.Serialization;

public enum FinKind
{
    Invoice,
    PaymentReminder,
    DebtCollection,
    Receipt,
    Refund,
    Subscription,
    Salary,
    Payslip,
    TaxNotice,
    Insurance,
    LoanInstalment,
    PaymentConfirmation,
    OtherFinancial,
    NotFinancial,
}

/// <summary>What the person has said or shown about an item. The displayed status is worked out
/// from this plus the dates; it is never stored, so it can't go stale.</summary>
public enum UserState { None, Reviewed, ReportedPaid, ConfirmedPaid, Disputed, NotApplicable }

public enum DisplayStatus
{
    NeedsReview,
    Upcoming,
    DueSoon,
    Overdue,
    ReportedPaid,
    ConfirmedPaid,
    Disputed,
    NotApplicable,
    Recorded,
    ExpectedIncome,
    IncomeReported,
    Information,
}

/// <summary>How sure a salary figure is. A payslip shows a calculation, not money arriving.</summary>
public enum IncomeEvidence { None, Payslip, Scheduled, ReportedReceived, Estimated, UserEntered }

public static class Categories
{
    public const string Housing = "Housing";
    public const string Utilities = "Electricity & utilities";
    public const string Groceries = "Groceries";
    public const string Transport = "Transport";
    public const string PhoneInternet = "Phone & internet";
    public const string Insurance = "Insurance";
    public const string Health = "Health";
    public const string Subscriptions = "Subscriptions";
    public const string Debt = "Debt repayments";
    public const string Shopping = "Shopping";
    public const string Taxes = "Taxes";
    public const string Income = "Income";
    public const string Other = "Other";

    public static readonly string[] All = [Housing, Utilities, Groceries, Transport, PhoneInternet, Insurance, Health, Subscriptions, Debt, Shopping, Taxes, Income, Other];
}

/// <summary>One email as the sync engine hands it on: decoded text, never raw MIME.</summary>
public sealed record MailMessage(
    string Id,
    string ThreadId,
    string From,
    string Subject,
    DateTime ReceivedUtc,
    string Snippet,
    string BodyText,
    IReadOnlyList<MailAttachment> Attachments);

public sealed record MailAttachment(string FileName, string MimeType, long Size, string? Text, string? Problem);

public sealed record Classification(bool IsFinancial, FinKind Kind, double Confidence, IReadOnlyList<string> Reasons, string Method);

/// <summary>Where an item came from, kept small: an excerpt, never the whole message.</summary>
public sealed record SourceRef(string GmailId, string From, string Subject, DateTime ReceivedUtc, string Excerpt, string Role);

public sealed record FinancialItem
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public FinKind Kind { get; init; }
    public string Category { get; init; } = Categories.Other;
    public string Title { get; init; } = "";
    public string? Counterparty { get; init; }
    public string? Purpose { get; init; }
    public string? InvoiceNumber { get; init; }
    public string? Reference { get; init; }
    public decimal? Amount { get; init; }
    public string Currency { get; init; } = "EUR";
    public DateTime? InvoiceDate { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime? OriginalDueDate { get; init; }

    /// <summary>Purchase date for a receipt, payment date for salary.</summary>
    public DateTime? EventDate { get; init; }
    public string? Period { get; init; }
    public string? PaymentInstructions { get; init; }

    public decimal? Gross { get; init; }
    public decimal? Net { get; init; }
    public decimal? Tax { get; init; }
    public decimal? OtherDeductions { get; init; }
    public IncomeEvidence IncomeEvidence { get; init; }

    public decimal? Vat { get; init; }
    public string? OrderRef { get; init; }

    public double Confidence { get; init; }
    public string ClassifiedBy { get; init; } = "rules";
    public List<string> ClassificationReasons { get; init; } = [];
    public List<string> Missing { get; init; } = [];
    public List<string> Flags { get; init; } = [];
    public List<string> Evidence { get; init; } = [];
    public List<SourceRef> Sources { get; init; } = [];
    public bool Suspicious { get; init; }
    public string? PossibleDuplicateOf { get; init; }

    public UserState UserState { get; init; }
    public string? UserNote { get; init; }
    public List<string> EditedFields { get; init; } = [];

    /// <summary>The validated AI explanation, when the person turned AI on.</summary>
    public AiExplanation? Ai { get; init; }

    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    [JsonIgnore]
    public bool IsObligation => Kind is FinKind.Invoice or FinKind.PaymentReminder or FinKind.DebtCollection or FinKind.Subscription
        or FinKind.Insurance or FinKind.LoanInstalment or FinKind.TaxNotice && Amount is > 0 && DueDate != null;

    [JsonIgnore]
    public bool IsIncome => Kind is FinKind.Salary or FinKind.Payslip;

    [JsonIgnore]
    public bool IsExpense => Kind is FinKind.Receipt or FinKind.Refund;
}

/// <summary>A validated AI explanation. Facts are things the message states; inferences are guesses.</summary>
public sealed record AiExplanation(
    string Summary,
    string WhyFinancial,
    List<string> Facts,
    List<string> Inferences,
    List<string> MissingOrConflicting,
    List<string> NextSteps,
    string Basis,
    string Model,
    DateTime CreatedUtc);

public enum ConnectionStatus { Connected, Syncing, NeedsReconnect, Error, Disconnected }

public sealed record GmailConnection
{
    public required string UserId { get; init; }
    public string? GoogleEmail { get; init; }
    public string GrantedScopes { get; init; } = "";
    public string? RefreshTokenEnc { get; init; }
    public string? AccessTokenEnc { get; init; }
    public DateTime? AccessExpiresUtc { get; init; }
    public string? HistoryId { get; init; }
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Connected;
    public DateTime? LastSyncUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public DateTime? PreviousSuccessUtc { get; init; }
    public string? LastError { get; init; }
    public int FailureCount { get; init; }
    public DateTime NextAttemptUtc { get; init; }
    public DateTime? SyncLockUntilUtc { get; init; }
    public int LastSyncNewItems { get; init; }
    public int LastSyncScanned { get; init; }
    public bool InitialScanDone { get; init; }
    public DateTime ConnectedUtc { get; init; }
}

public sealed record OAuthState(string State, string UserId, string CodeVerifier, string RedirectUri, DateTime ExpiresUtc);

/// <summary>A processed Gmail message. Non-financial ones keep only the id, so they aren't fetched again.</summary>
public sealed record ProcessedMessage(
    string UserId,
    string GmailId,
    DateTime ReceivedUtc,
    bool IsFinancial,
    FinKind Kind,
    string? ItemId,
    string? Error,
    DateTime ProcessedUtc);

public sealed record CompanionSettings
{
    public decimal? Balance { get; init; }
    public DateTime? BalanceAsOfUtc { get; init; }
    public bool AiEnabled { get; init; }
    public DateTime? AiConsentUtc { get; init; }
    public int ScanDays { get; init; } = 90;

    /// <summary>The person's own payday (day of month) and net salary; they win over estimates.</summary>
    public int? PaydayDay { get; init; }
    public decimal? NetSalary { get; init; }
    public decimal? EverydaySpendPerDay { get; init; }
}

/// <summary>One mailbox's Gmail push subscription (users.watch). Kept apart from the connection row
/// so a sync saving the connection can never overwrite what a notification just recorded.</summary>
public sealed record GmailWatch
{
    public required string UserId { get; init; }
    public string? Topic { get; init; }
    public DateTime? ExpiresUtc { get; init; }
    public DateTime? RenewedUtc { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastPushUtc { get; init; }

    /// <summary>The highest history id any notification has announced: older or repeated ones are duplicates.</summary>
    public ulong LastPushHistoryId { get; init; }
    public long PushCount { get; init; }
    public long DuplicatePushCount { get; init; }

    /// <summary>When the newest push-triggered sync started, to tell whether a push arrived after it.</summary>
    public DateTime? LastPushSyncStartedUtc { get; init; }
}
