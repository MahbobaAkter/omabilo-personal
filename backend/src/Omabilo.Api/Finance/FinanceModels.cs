namespace Omabilo.Api.Finance;

// Per-user finance records for the standalone API. Amounts are euros; negative amounts leave
// the account. Every record is fictional demo data until a person edits or adds their own.

public enum RecurringKind { Income, Essential, Subscription, LoanPayment, Savings }

/// <summary>How sure a value is, so screens can label it honestly.</summary>
public enum Certainty
{
    /// <summary>Recorded in the account or confirmed by the person.</summary>
    Confirmed,

    /// <summary>A fixed scheduled amount (a rent or a loan minimum) that hasn't happened yet.</summary>
    Scheduled,

    /// <summary>A recurring cost whose amount varies, using a typical figure.</summary>
    Estimate,

    /// <summary>A figure the person chose for planning (spending allowance, savings).</summary>
    Planned,
}

public record FinanceSettings(
    string DisplayName,
    int PayDay,
    decimal NetSalary,
    string Employer,
    decimal DiscretionaryPerDay,
    bool IsDemoData);

public record FinanceTransaction(
    string Id,
    DateTime Date,
    string Description,
    string Category,
    decimal Amount,
    string? RecurringId = null,
    string? LinkedId = null);

/// <summary>Something that happens every month on a day: salary, rent, a loan payment.</summary>
public record RecurringItem(
    string Id,
    string Name,
    string Category,
    RecurringKind Kind,
    decimal Amount,
    int DayOfMonth,
    bool AmountVaries = false,
    string? LoanId = null,
    string? GoalId = null);

/// <summary>Money expected once. Only Confirmed ones count in a plan.</summary>
public record ExpectedIncome(string Id, string Description, DateTime Date, decimal Amount, bool Confirmed);

public record Loan(
    string Id,
    string Name,
    string Lender,
    string Kind,
    decimal Balance,
    decimal StartingBalance,
    decimal Apr,
    decimal MinPayment,
    int DueDay);

public record SavingsGoalRecord(string Id, string Name, decimal Target, decimal Saved, DateTime? TargetDate, bool IsEmergencyFund);

/// <summary>Everything one person's finance screens need, stored as one document per owner.</summary>
public record FinanceData(
    FinanceSettings Settings,
    decimal OpeningBalance,
    DateTime OpeningDate,
    List<FinanceTransaction> Transactions,
    List<RecurringItem> Recurring,
    List<ExpectedIncome> ExpectedIncome,
    List<Loan> Loans,
    List<SavingsGoalRecord> Goals);
