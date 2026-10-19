// What a signed-in person can do with their own records: correct them, mark them, delete them,
// export everything, and erase it all. The user id always comes from the server-side session,
// never from a request, and the store refuses rows that belong to anyone else.

using System.Text.Json;

/// <summary>A correction. Null fields are left as they are.</summary>
public sealed record ItemEdit(
    string? Counterparty = null,
    decimal? Amount = null,
    DateTime? DueDate = null,
    DateTime? EventDate = null,
    string? Category = null,
    string? Reference = null,
    string? Purpose = null,
    decimal? Net = null,
    string? Note = null,
    FinKind? Kind = null);

public sealed record CompanionSnapshot(
    GmailConnection? Connection,
    CompanionSettings Settings,
    IReadOnlyList<FinancialItem> Items,
    IncomeOutlook Income,
    CashFlowPlan Plan,
    IReadOnlyList<Suggestion> Suggestions,
    IReadOnlyList<RecurringPattern> Recurring,
    GmailWatch? Watch = null,
    ForwardAddress? Forward = null)
{
    /// <summary>True while Gmail push notices are set up for this mailbox, so new mail arrives without polling.</summary>
    public bool PushActive(DateTime now) => Watch is { LastError: null, ExpiresUtc: { } e } && e > now;
}

public static class CompanionEdits
{
    /// <summary>Copies every field the person corrected from the old item onto a re-read one.</summary>
    public static FinancialItem KeepEdits(FinancialItem old, FinancialItem fresh)
    {
        var r = fresh with { EditedFields = old.EditedFields };

        foreach (var f in old.EditedFields)
        {
            r = f switch
            {
                nameof(FinancialItem.Counterparty) => r with { Counterparty = old.Counterparty },
                nameof(FinancialItem.Amount) => r with { Amount = old.Amount },
                nameof(FinancialItem.DueDate) => r with { DueDate = old.DueDate },
                nameof(FinancialItem.EventDate) => r with { EventDate = old.EventDate },
                nameof(FinancialItem.Category) => r with { Category = old.Category },
                nameof(FinancialItem.Reference) => r with { Reference = old.Reference },
                nameof(FinancialItem.Purpose) => r with { Purpose = old.Purpose },
                nameof(FinancialItem.Net) => r with { Net = old.Net, Amount = old.IsIncome ? old.Net : r.Amount },
                // A re-read on the other side of income (a bill read again after it was called a payslip)
                // brings back an amount that doesn't fit the corrected type, so the corrected one is kept
                nameof(FinancialItem.Kind) => r with
                {
                    Kind = old.Kind, IncomeEvidence = old.IncomeEvidence, Category = old.Category,
                    Amount = r.IsIncome != old.IsIncome ? old.Amount : r.Amount,
                    Net = r.IsIncome != old.IsIncome ? old.Net : r.Net,
                    Missing = r.IsIncome != old.IsIncome ? old.Missing : r.Missing,
                },
                _ => r,
            };
        }

        return r;
    }

    /// <summary>A "missing" note about the item's money: its total, amount or salary.</summary>
    private static bool IsAmountGap(string m) =>
        m.Contains("amount", StringComparison.OrdinalIgnoreCase) || m.Contains("total", StringComparison.OrdinalIgnoreCase) || m.Contains("salary", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(ItemEdit e, DateTime today)
    {
        var errors = new List<string>();
        if (e.Amount is < 0 or > 1_000_000m) errors.Add("The amount must be between 0 and 1 000 000.");
        if (e.Net is < 0 or > 1_000_000m) errors.Add("Net salary must be between 0 and 1 000 000.");
        if (e.DueDate is { } d && (d < today.AddYears(-5) || d > today.AddYears(5))) errors.Add("The due date must be within five years of today.");
        if (e.EventDate is { } ev && (ev < today.AddYears(-5) || ev > today.AddYears(5))) errors.Add("The date must be within five years of today.");
        if (e.Category is { } c && !Categories.All.Contains(c)) errors.Add("Choose one of the listed categories.");
        if (e.Counterparty is { Length: > 120 }) errors.Add("The company name is too long.");
        if (e.Purpose is { Length: > 200 }) errors.Add("The description is too long.");
        if (e.Note is { Length: > 1000 }) errors.Add("The note is too long.");
        if (e.Kind is { } k && (k == FinKind.NotFinancial || !Enum.IsDefined(k))) errors.Add("Choose one of the listed document types. To remove something that isn't financial, use Not applicable or Delete.");
        return errors;
    }

    public static FinancialItem Apply(FinancialItem item, ItemEdit e, DateTime now)
    {
        var edited = item.EditedFields.ToHashSet();
        var r = item;

        // A new document type moves the item to that module (a payslip to Salary, a tax notice to
        // Taxes). Nothing is copied into a field the document didn't state: crossing between income
        // and the rest changes what the amount means (a bill's total isn't a net salary), so it is
        // cleared and listed as missing rather than carried over.
        if (e.Kind is { } k && k != item.Kind)
        {
            r = r with { Kind = k };
            var crossed = r.IsIncome != item.IsIncome;
            var gaps = r.Missing.Where(m => !crossed || !IsAmountGap(m)).ToList();

            if (crossed)
            {
                r = r with { Amount = null, Net = null };
            }

            if (r.IsIncome && r.Net == null && !gaps.Any(m => m.Contains("net salary", StringComparison.OrdinalIgnoreCase))) gaps.Add("Net salary");
            if (!r.IsIncome && r.Amount == null && !gaps.Any(IsAmountGap)) gaps.Add("The total amount");

            r = r with
            {
                IncomeEvidence = !r.IsIncome ? IncomeEvidence.None : item.IsIncome ? item.IncomeEvidence : k == FinKind.Payslip ? IncomeEvidence.Payslip : IncomeEvidence.Scheduled,
                Category = edited.Contains(nameof(FinancialItem.Category)) ? r.Category : RecordExtractor.Categorize(k, $"{r.Title} {r.Purpose}", r.Counterparty),
                Missing = gaps,
            };
            edited.Add(nameof(FinancialItem.Kind));
        }

        if (e.Counterparty is { } cp && cp.Trim().Length > 0) { r = r with { Counterparty = cp.Trim() }; edited.Add(nameof(FinancialItem.Counterparty)); }
        if (e.Amount is { } a) { r = r with { Amount = a }; edited.Add(nameof(FinancialItem.Amount)); }
        if (e.DueDate is { } d) { r = r with { DueDate = d.Date }; edited.Add(nameof(FinancialItem.DueDate)); }
        if (e.EventDate is { } ev) { r = r with { EventDate = ev.Date }; edited.Add(nameof(FinancialItem.EventDate)); }
        if (e.Category is { } c) { r = r with { Category = c }; edited.Add(nameof(FinancialItem.Category)); }
        if (e.Purpose is { } p) { r = r with { Purpose = p.Trim() }; edited.Add(nameof(FinancialItem.Purpose)); }
        if (e.Net is { } n) { r = r with { Net = n, Amount = r.IsIncome ? n : r.Amount }; edited.Add(nameof(FinancialItem.Net)); }
        if (e.Note != null) r = r with { UserNote = e.Note.Trim().Length == 0 ? null : e.Note.Trim() };

        if (e.Reference is { } rf)
        {
            var flags = r.Flags.Where(f => !f.Contains("check digit")).ToList();
            if (rf.Trim().Length > 0 && !PaymentReference.IsValid(rf)) flags.Add("The reference you entered doesn't pass the Finnish check-digit test. Check it against the bill.");
            r = r with { Reference = rf.Trim().Length == 0 ? null : rf.Trim(), Flags = flags };
            edited.Add(nameof(FinancialItem.Reference));
        }

        // A field the person filled in is no longer missing
        var missing = r.Missing.Where(m =>
            !(edited.Contains(nameof(FinancialItem.Amount)) && (m.Contains("amount", StringComparison.OrdinalIgnoreCase) || m.Contains("total", StringComparison.OrdinalIgnoreCase)))
            && !(edited.Contains(nameof(FinancialItem.DueDate)) && m.Contains("due date", StringComparison.OrdinalIgnoreCase))
            && !(edited.Contains(nameof(FinancialItem.Counterparty)) && m.Contains("company", StringComparison.OrdinalIgnoreCase))
            && !(edited.Contains(nameof(FinancialItem.Reference)) && m.Contains("reference", StringComparison.OrdinalIgnoreCase))
            && !(edited.Contains(nameof(FinancialItem.Net)) && m.Contains("net salary", StringComparison.OrdinalIgnoreCase))).ToList();

        return r with { EditedFields = edited.ToList(), Missing = missing, UpdatedUtc = now };
    }
}

public sealed class CompanionService(ICompanionStore store, SyncEngine engine, Func<DateTime> clock)
{
    public async Task<CompanionSnapshot> LoadAsync(string userId, CancellationToken ct)
    {
        var today = clock().Date;
        var connection = await store.GetConnectionAsync(userId, ct);
        var settings = await store.GetSettingsAsync(userId, ct);
        var items = await store.GetItemsAsync(userId, ct);
        var income = ItemRules.Income(items, settings, today);
        var plan = CashFlowPlanner.Plan(items, settings, income, today);
        var suggestions = CashFlowPlanner.Suggest(items, plan, income, settings, today);
        var watch = connection == null ? null : await store.GetWatchAsync(userId, ct);
        var forward = await engine.ForwardAddressAsync(userId, ct);
        return new CompanionSnapshot(connection, settings, items, income, plan, suggestions, ItemRules.Recurring(items, today), watch, forward);
    }

    public async Task<(FinancialItem? Item, IReadOnlyList<string> Errors)> EditAsync(string userId, string itemId, ItemEdit edit, CancellationToken ct)
    {
        var errors = CompanionEdits.Validate(edit, clock().Date);

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        if (await store.GetItemAsync(userId, itemId, ct) is not { } item)
        {
            return (null, ["That item doesn't exist."]);
        }

        var updated = CompanionEdits.Apply(item, edit, clock());
        await store.SaveItemAsync(updated, ct);
        await store.AddAuditAsync(userId, "item_corrected", itemId, ct);
        return (updated, []);
    }

    public async Task<FinancialItem?> SetStateAsync(string userId, string itemId, UserState state, CancellationToken ct)
    {
        if (await store.GetItemAsync(userId, itemId, ct) is not { } item)
        {
            return null;
        }

        // "Confirmed paid" only ever comes from evidence, never from a button
        if (state == UserState.ConfirmedPaid)
        {
            state = UserState.ReportedPaid;
        }

        var evidence = item.Evidence.ToList();
        if (state == UserState.ReportedPaid) evidence.Add($"You reported paying this on {InvoiceExplainer.Day(clock())}.");

        // Keeping a flagged duplicate means the person says it is a separate charge
        var updated = item with
        {
            UserState = state,
            Evidence = evidence,
            PossibleDuplicateOf = state == UserState.Reviewed ? null : item.PossibleDuplicateOf,
            Suspicious = state == UserState.Reviewed ? false : item.Suspicious,
            UpdatedUtc = clock(),
        };

        await store.SaveItemAsync(updated, ct);
        await store.AddAuditAsync(userId, $"item_state_{state.ToString().ToLowerInvariant()}", itemId, ct);
        return updated;
    }

    public async Task<bool> DeleteItemAsync(string userId, string itemId, CancellationToken ct)
    {
        var ok = await store.DeleteItemAsync(userId, itemId, ct);
        if (ok) await store.AddAuditAsync(userId, "item_deleted", itemId, ct);
        return ok;
    }

    public async Task SaveSettingsAsync(string userId, CompanionSettings settings, CancellationToken ct)
    {
        var clean = settings with
        {
            ScanDays = Math.Clamp(settings.ScanDays, 7, 365),
            PaydayDay = settings.PaydayDay is >= 1 and <= 31 ? settings.PaydayDay : null,
        };
        await store.SaveSettingsAsync(userId, clean, ct);
        await store.AddAuditAsync(userId, "settings_saved", null, ct);
    }

    /// <summary>Everything Omabilo keeps about the person, without tokens.</summary>
    public async Task<byte[]> ExportAsync(string userId, CancellationToken ct)
    {
        var c = await store.GetConnectionAsync(userId, ct);
        var export = new
        {
            exportedUtc = clock(),
            gmail = c == null ? null : new { c.GoogleEmail, Status = c.Status.ToString(), c.ConnectedUtc, c.LastSuccessUtc, c.GrantedScopes },
            settings = await store.GetSettingsAsync(userId, ct),
            forwarding = await store.GetForwardAsync(userId, ct) is { } f ? new { f.Token, f.CreatedUtc, f.LastReceivedUtc, f.ReceivedCount } : null,
            items =await store.GetItemsAsync(userId, ct),
            activity = await store.GetAuditAsync(userId, 500, ct),
        };
        return JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions(CompanionJson.Options) { WriteIndented = true });
    }

    /// <summary>Disconnects Gmail (revoking Google's grant) and deletes every record kept for the person.</summary>
    public async Task DeleteAllDataAsync(string userId, CancellationToken ct)
    {
        await engine.DisconnectAsync(userId, ct);
        await store.DeleteAllForUserAsync(userId, ct);
    }
}
