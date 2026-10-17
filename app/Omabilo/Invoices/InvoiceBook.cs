// The rules for turning source records into canonical invoices. Plain C#: the app loads the
// stored lists into a book, applies one operation and writes the lists back; tests do the same.
//
// Every operation takes the acting owner and refuses records that belong to anyone else, so
// the authorization check sits next to the data change rather than only in the UI.

public enum IngestOutcome
{
    /// <summary>A new canonical invoice was created.</summary>
    Created,

    /// <summary>The source matched an existing invoice exactly and was linked to it; nothing new is counted.</summary>
    LinkedToExisting,

    /// <summary>A new invoice was created but looks like an existing one; the owner decides.</summary>
    PossibleDuplicate,

    /// <summary>This exact source record had already been ingested; nothing changed.</summary>
    AlreadyIngested,

    /// <summary>The submission lacked the fields an invoice needs.</summary>
    Invalid,
}

public record IngestResult(IngestOutcome Outcome, string? InvoiceId, string Message);

public enum MatchKind { None, Exact, Probable }

public record InvoiceMatch(MatchKind Kind, CanonicalInvoice? Invoice, string Reason);

public sealed class InvoiceAccessDeniedException(string message) : Exception(message);

public sealed class InvoiceBook
{
    /// <summary>At or above this confidence a manual entry needs no review; extracted ones always do.</summary>
    public const double AutoAcceptConfidence = 0.95;

    private readonly List<CanonicalInvoice> _invoices;
    private readonly List<InvoiceSourceRecord> _sources;
    private readonly List<InvoiceAuditEvent> _audit;

    public InvoiceBook(IEnumerable<CanonicalInvoice> invoices, IEnumerable<InvoiceSourceRecord> sources, IEnumerable<InvoiceAuditEvent> audit)
    {
        _invoices = invoices.ToList();
        _sources = sources.ToList();
        _audit = audit.ToList();
    }

    public IReadOnlyList<CanonicalInvoice> AllInvoices => _invoices;
    public IReadOnlyList<InvoiceSourceRecord> AllSources => _sources;
    public IReadOnlyList<InvoiceAuditEvent> AllAudit => _audit;

    // ── Reads: always filtered to one owner ─────────────────────────────────

    public IReadOnlyList<CanonicalInvoice> InvoicesOf(string ownerId) => _invoices.Where(i => i.OwnerId == ownerId).ToList();

    public IReadOnlyList<InvoiceSourceRecord> SourcesOf(string ownerId, string invoiceId) =>
        _sources.Where(s => s.OwnerId == ownerId && s.CanonicalInvoiceId == invoiceId).OrderBy(s => s.ReceivedAt).ToList();

    public IReadOnlyList<InvoiceAuditEvent> AuditOf(string ownerId, string? invoiceId = null) =>
        _audit.Where(a => a.OwnerId == ownerId && (invoiceId == null || a.InvoiceId == invoiceId)).OrderByDescending(a => a.At).ToList();

    public decimal OpenTotal(string ownerId) =>
        _invoices.Where(i => i.OwnerId == ownerId && i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid).Sum(i => i.Amount);

    // ── Ingestion ───────────────────────────────────────────────────────────

    public IngestResult Ingest(string ownerId, InvoiceSubmission submission, DateTime now)
    {
        RequireOwner(ownerId);
        var key = InvoiceKeys.Idempotency(ownerId, submission.Type, submission.SourceRef);

        if (_sources.FirstOrDefault(s => s.OwnerId == ownerId && s.IdempotencyKey == key) is { } seen)
        {
            return new IngestResult(IngestOutcome.AlreadyIngested, seen.CanonicalInvoiceId, "This source was already received; nothing was added.");
        }

        var f = submission.Fields;

        if (string.IsNullOrWhiteSpace(f.Provider) || f.Amount is not > 0 || f.DueDate is null)
        {
            var rejected = new InvoiceSourceRecord(InvoiceKeys.NewId("src"), ownerId, submission.Type, submission.SourceRef, key,
                submission.IsSimulated, f, submission.Confidence, now, null, submission.SourceLabel);
            _sources.Add(rejected);
            Audit(ownerId, null, "source.unreadable", "system", now, $"{submission.Type} source could not be read: missing {MissingFields(f)}");
            return new IngestResult(IngestOutcome.Invalid, null, $"Couldn't read {MissingFields(f)} from this source.");
        }

        var match = FindMatch(ownerId, f);
        var sourceId = InvoiceKeys.NewId("src");

        if (match.Kind == MatchKind.Exact && match.Invoice is { } existing)
        {
            _sources.Add(new InvoiceSourceRecord(sourceId, ownerId, submission.Type, submission.SourceRef, key,
                submission.IsSimulated, f, submission.Confidence, now, existing.Id, submission.SourceLabel));
            Replace(existing with
            {
                SourceRecordIds = [.. existing.SourceRecordIds, sourceId],
                Confidence = Math.Max(existing.Confidence, submission.Confidence),
                UpdatedAt = now,
            });
            Audit(ownerId, existing.Id, "source.linked", "system", now, $"Matched a {Label(submission.Type)} source ({match.Reason}); not counted again");
            return new IngestResult(IngestOutcome.LinkedToExisting, existing.Id, $"Already have this invoice ({match.Reason}). Linked the new copy instead of adding it twice.");
        }

        // Typed-in and demo invoices are what the owner entered; anything read from a source waits for a person
        var ownerEntered = submission.Type is InvoiceSourceType.Manual or InvoiceSourceType.Demo;
        var needsReview = !ownerEntered || submission.Confidence < AutoAcceptConfidence || match.Kind == MatchKind.Probable;
        var invoice = new CanonicalInvoice(
            InvoiceKeys.NewId("inv"), ownerId, f.Provider!.Trim(), Clean(f.InvoiceNumber), Clean(f.Reference),
            f.InvoiceDate, f.DueDate!.Value.Date, Math.Round(f.Amount!.Value, 2), string.IsNullOrWhiteSpace(f.Currency) ? "EUR" : f.Currency!.Trim().ToUpperInvariant(),
            InvoicePaymentStatus.Unpaid,
            needsReview ? InvoiceVerification.Unverified : InvoiceVerification.Verified,
            needsReview ? InvoiceProcessingState.NeedsReview : InvoiceProcessingState.Ready,
            submission.Confidence, submission.Type, [sourceId], now, now,
            PossibleDuplicateOfId: match.Kind == MatchKind.Probable ? match.Invoice?.Id : null);

        _sources.Add(new InvoiceSourceRecord(sourceId, ownerId, submission.Type, submission.SourceRef, key,
            submission.IsSimulated, f, submission.Confidence, now, invoice.Id, submission.SourceLabel));
        _invoices.Add(invoice);
        Audit(ownerId, invoice.Id, "invoice.created", "system", now,
            $"Created from a {Label(submission.Type)} source{(submission.IsSimulated ? " (simulated)" : "")}; {(needsReview ? "waiting for review" : "confirmed")}");

        if (match.Kind == MatchKind.Probable)
        {
            Audit(ownerId, invoice.Id, "duplicate.suspected", "system", now, $"Looks like an existing invoice ({match.Reason})");
            return new IngestResult(IngestOutcome.PossibleDuplicate, invoice.Id, $"Added for review: it looks like one you already have ({match.Reason}).");
        }

        return new IngestResult(IngestOutcome.Created, invoice.Id, needsReview ? "Added to your review queue." : "Invoice added.");
    }

    /// <summary>Exact: the same payment reference, or the same provider and invoice number.
    /// Probable: the same provider and amount with due dates within three days.</summary>
    public InvoiceMatch FindMatch(string ownerId, ExtractedInvoiceFields f)
    {
        var candidates = _invoices.Where(i => i.OwnerId == ownerId && i.State is not (InvoiceProcessingState.Rejected or InvoiceProcessingState.Merged)).ToList();
        var provider = InvoiceKeys.NormalizeProvider(f.Provider);
        var reference = InvoiceKeys.NormalizeToken(f.Reference);
        var number = InvoiceKeys.NormalizeToken(f.InvoiceNumber);

        if (reference.Length >= 4 && candidates.FirstOrDefault(i => InvoiceKeys.NormalizeToken(i.Reference) == reference) is { } byRef)
        {
            return new InvoiceMatch(MatchKind.Exact, byRef, "same payment reference");
        }

        if (number.Length >= 3 && provider.Length > 0 &&
            candidates.FirstOrDefault(i => InvoiceKeys.NormalizeToken(i.InvoiceNumber) == number && InvoiceKeys.NormalizeProvider(i.Provider) == provider) is { } byNumber)
        {
            return new InvoiceMatch(MatchKind.Exact, byNumber, "same provider and invoice number");
        }

        if (provider.Length > 0 && f.Amount is { } amount && f.DueDate is { } due &&
            candidates.FirstOrDefault(i => InvoiceKeys.NormalizeProvider(i.Provider) == provider && i.Amount == Math.Round(amount, 2)
                && Math.Abs((i.DueDate - due.Date).TotalDays) <= 3) is { } probable)
        {
            return new InvoiceMatch(MatchKind.Probable, probable, "same provider, amount and due date");
        }

        return new InvoiceMatch(MatchKind.None, null, "");
    }

    // ── Owner actions ───────────────────────────────────────────────────────

    public CanonicalInvoice Approve(string ownerId, string invoiceId, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var next = inv with { Verification = InvoiceVerification.Verified, State = InvoiceProcessingState.Ready, UpdatedAt = now };
        Replace(next);
        Audit(ownerId, inv.Id, "invoice.approved", "user", now, "Fields confirmed by the owner");
        return next;
    }

    public CanonicalInvoice Reject(string ownerId, string invoiceId, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var next = inv with { Verification = InvoiceVerification.Rejected, State = InvoiceProcessingState.Rejected, UpdatedAt = now };
        Replace(next);
        Audit(ownerId, inv.Id, "invoice.rejected", "user", now, "Marked as not a valid invoice");
        return next;
    }

    /// <summary>Folds a suspected duplicate into the invoice it duplicates: its sources move over and it stops counting.</summary>
    public CanonicalInvoice MergeInto(string ownerId, string duplicateId, string targetId, DateTime now)
    {
        var dup = Owned(ownerId, duplicateId);
        var target = Owned(ownerId, targetId);

        if (dup.Id == target.Id || target.State == InvoiceProcessingState.Merged)
        {
            throw new InvalidOperationException("An invoice can only be merged into a different, active invoice.");
        }

        for (var i = 0; i < _sources.Count; i++)
        {
            if (_sources[i].CanonicalInvoiceId == dup.Id)
            {
                _sources[i] = _sources[i] with { CanonicalInvoiceId = target.Id };
            }
        }

        Replace(dup with { State = InvoiceProcessingState.Merged, MergedIntoId = target.Id, SourceRecordIds = [], UpdatedAt = now });
        var merged = target with { SourceRecordIds = [.. target.SourceRecordIds, .. dup.SourceRecordIds], UpdatedAt = now };
        Replace(merged);
        Audit(ownerId, dup.Id, "duplicate.merged", "user", now, "Merged into an existing invoice");
        Audit(ownerId, target.Id, "duplicate.merged", "user", now, $"Took over {dup.SourceRecordIds.Count} source record(s) from a duplicate");
        return merged;
    }

    public CanonicalInvoice KeepSeparate(string ownerId, string invoiceId, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var next = inv with { PossibleDuplicateOfId = null, UpdatedAt = now };
        Replace(next);
        Audit(ownerId, inv.Id, "duplicate.dismissed", "user", now, "Owner confirmed this is a separate invoice");
        return next;
    }

    public CanonicalInvoice Edit(string ownerId, string invoiceId, ExtractedInvoiceFields fields, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var changed = new List<string>();

        string? Track(string name, string? before, string? after)
        {
            if (!string.Equals(before ?? "", after ?? "", StringComparison.Ordinal))
            {
                changed.Add(name);
            }

            return after;
        }

        var next = inv with
        {
            Provider = Track("provider", inv.Provider, Clean(fields.Provider) ?? inv.Provider)!,
            InvoiceNumber = Track("invoice number", inv.InvoiceNumber, Clean(fields.InvoiceNumber)),
            Reference = Track("reference", inv.Reference, Clean(fields.Reference)),
            InvoiceDate = fields.InvoiceDate,
            DueDate = fields.DueDate?.Date ?? inv.DueDate,
            Amount = fields.Amount is > 0 ? Math.Round(fields.Amount.Value, 2) : inv.Amount,
            UpdatedAt = now,
        };

        if (next.InvoiceDate != inv.InvoiceDate) changed.Add("invoice date");
        if (next.DueDate != inv.DueDate) changed.Add("due date");
        if (next.Amount != inv.Amount) changed.Add("amount");

        Replace(next);
        Audit(ownerId, inv.Id, "invoice.edited", "user", now, changed.Count == 0 ? "Saved with no changes" : $"Changed {string.Join(", ", changed)}");
        return next;
    }

    public CanonicalInvoice MarkPaid(string ownerId, string invoiceId, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var next = inv with { PaymentStatus = InvoicePaymentStatus.Paid, PaidAt = now, UpdatedAt = now };
        Replace(next);
        Audit(ownerId, inv.Id, "invoice.paid", "user", now, "Marked as paid");
        return next;
    }

    public CanonicalInvoice MarkUnpaid(string ownerId, string invoiceId, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var next = inv with { PaymentStatus = InvoicePaymentStatus.Unpaid, PaidAt = null, UpdatedAt = now };
        Replace(next);
        Audit(ownerId, inv.Id, "invoice.unpaid", "user", now, "Marked as unpaid again");
        return next;
    }

    public CanonicalInvoice LinkBill(string ownerId, string invoiceId, string? billId, DateTime now)
    {
        var inv = Owned(ownerId, invoiceId);
        var next = inv with { LinkedBillId = billId, UpdatedAt = now };
        Replace(next);
        return next;
    }

    /// <summary>Removes every invoice, source record and audit event the owner has, leaving one
    /// audit event that records the deletion itself. Other owners' records are untouched.</summary>
    public int DeleteAllFor(string ownerId, DateTime now)
    {
        RequireOwner(ownerId);
        var removed = _invoices.RemoveAll(i => i.OwnerId == ownerId);
        _sources.RemoveAll(s => s.OwnerId == ownerId);
        _audit.RemoveAll(a => a.OwnerId == ownerId);
        Audit(ownerId, null, "data.deleted", "user", now, $"Owner deleted all invoice data ({removed} invoices)");
        return removed;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void RequireOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new InvoiceAccessDeniedException("No signed-in owner for this request.");
        }
    }

    private CanonicalInvoice Owned(string ownerId, string invoiceId)
    {
        RequireOwner(ownerId);
        var inv = _invoices.FirstOrDefault(i => i.Id == invoiceId);

        // The same answer for "missing" and "someone else's", so ids reveal nothing
        if (inv == null || inv.OwnerId != ownerId)
        {
            throw new InvoiceAccessDeniedException("Invoice not found.");
        }

        return inv;
    }

    private void Replace(CanonicalInvoice next)
    {
        var index = _invoices.FindIndex(i => i.Id == next.Id);
        _invoices[index] = next;
    }

    private void Audit(string ownerId, string? invoiceId, string action, string actor, DateTime at, string detail) =>
        _audit.Add(new InvoiceAuditEvent(InvoiceKeys.NewId("evt"), ownerId, invoiceId, action, actor, at, detail));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string MissingFields(ExtractedInvoiceFields f)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(f.Provider)) missing.Add("the provider");
        if (f.Amount is not > 0) missing.Add("the amount");
        if (f.DueDate is null) missing.Add("the due date");
        return string.Join(", ", missing);
    }

    public static string Label(InvoiceSourceType type) => type switch
    {
        InvoiceSourceType.Demo => "demo",
        InvoiceSourceType.Manual => "manual",
        InvoiceSourceType.ForwardedEmail => "forwarded email",
        InvoiceSourceType.Document => "document",
        InvoiceSourceType.Gmail => "Gmail",
        InvoiceSourceType.Outlook => "Outlook",
        _ => "Finnish provider",
    };
}
