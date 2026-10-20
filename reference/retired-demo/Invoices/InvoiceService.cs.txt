// App glue for invoices: user-scoped storage, owner checks, and the link to Financial Inbox bills.
//
// Storage (Phase 1): PersistentUserReactive lists. They resolve against the signed-in user's
// scope on the server, are kept in the app's EU Postgres state table, and the platform erases
// them when the user's account is erased. Phase 2 moves invoices to database tables so the
// inbound-email trigger (which runs outside any user's instance) can write them.

using System.Text.Json;
using System.Text.Json.Serialization;

public partial class OmabiloApp
{
    private readonly PersistentUserReactiveList<CanonicalInvoice> _invoices = new(key: "omabilo.invoices.v1");
    private readonly PersistentUserReactiveList<InvoiceSourceRecord> _invoiceSources = new(key: "omabilo.invoice-sources.v1");
    private readonly PersistentUserReactiveList<InvoiceAuditEvent> _invoiceAudit = new(key: "omabilo.invoice-audit.v1");

    private readonly TextInvoiceExtractor _extractor = new();

    /// <summary>The signed-in (or guest) user acting right now, read on the server from the
    /// request's scope; never from anything the browser sends.</summary>
    private static string? InvoiceOwner() => ReactiveScope.UserIdOrNull;

    private InvoiceBook LoadBook() => new(_invoices, _invoiceSources, _invoiceAudit);

    private void SaveBook(InvoiceBook book)
    {
        _invoices.ReplaceAll(book.AllInvoices.ToList());
        _invoiceSources.ReplaceAll(book.AllSources.ToList());
        _invoiceAudit.ReplaceAll(book.AllAudit.ToList());
    }

    private IReadOnlyList<CanonicalInvoice> MyInvoices() =>
        InvoiceOwner() is { } owner ? _invoices.Where(i => i.OwnerId == owner).ToList() : [];

    private int InvoicesToReview() => MyInvoices().Count(i => i.State == InvoiceProcessingState.NeedsReview);

    /// <summary>Runs one invoice operation for the current owner, saves it, keeps linked bills in
    /// step, and offers an Undo that restores the records (the audit trail keeps an "undone" entry).</summary>
    private bool RunInvoiceAction(string undoMessage, Action<InvoiceBook, string> action)
    {
        if (InvoiceOwner() is not { } owner)
        {
            _toasts.Error("Sign in to manage invoices");
            return false;
        }

        var invoicesBefore = _invoices.ToList();
        var sourcesBefore = _invoiceSources.ToList();
        var inboxBefore = _inbox.ToList();
        var txnsBefore = _txns.ToList();

        try
        {
            var book = LoadBook();
            action(book, owner);
            SaveBook(book);
            SyncBills(owner);
        }
        catch (InvoiceAccessDeniedException)
        {
            _toasts.Error("That invoice isn't available", "It may have been removed.");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _toasts.Error("Couldn't do that", ex.Message);
            return false;
        }

        OfferUndo(undoMessage, () =>
        {
            _invoices.ReplaceAll(invoicesBefore);
            _invoiceSources.ReplaceAll(sourcesBefore);
            _inbox.ReplaceAll(inboxBefore);
            _txns.ReplaceAll(txnsBefore);
            _invoiceAudit.Add(new InvoiceAuditEvent(InvoiceKeys.NewId("evt"), owner, null, "change.undone", "user", DateTime.UtcNow, $"Undid: {undoMessage}"));
        });

        return true;
    }

    private async Task IngestAsync(IReadOnlyList<InvoiceSubmission> submissions, string what)
    {
        if (InvoiceOwner() is not { } owner)
        {
            _toasts.Error("Sign in to add invoices");
            return;
        }

        var results = new List<IngestResult>();

        RunInvoiceAction($"Added {what}", (book, o) =>
        {
            foreach (var s in submissions)
            {
                results.Add(book.Ingest(o, s, DateTime.UtcNow));
            }
        });

        // Ids, counts and outcomes only: never invoice contents
        foreach (var r in results)
        {
            Log.Instance.Info($"Invoice ingest outcome={r.Outcome} sourceType={submissions[0].Type} simulated={submissions[0].IsSimulated}");
        }

        var last = results.LastOrDefault();

        if (last != null)
        {
            _invoiceNotice.Value = (last.Message, last.Outcome switch
            {
                IngestOutcome.Created => "success",
                IngestOutcome.LinkedToExisting or IngestOutcome.AlreadyIngested => "info",
                IngestOutcome.PossibleDuplicate => "warning",
                _ => "error",
            }, last.InvoiceId);
        }

        await Task.CompletedTask;
    }

    // ── Keeping Financial Inbox bills in step ───────────────────────────────

    /// <summary>A confirmed unpaid invoice shows as one bill in the Financial Inbox (so the dashboard
    /// and Payday Planner count it once); a rejected or merged one has its open bill archived.</summary>
    private void SyncBills(string owner)
    {
        var book = LoadBook();
        var changed = false;

        foreach (var inv in book.InvoicesOf(owner))
        {
            var bill = inv.LinkedBillId != null ? _inbox.FirstOrDefault(i => i.Id == inv.LinkedBillId) : null;

            if (inv.CountsInTotals && inv.PaymentStatus == InvoicePaymentStatus.Unpaid && bill == null)
            {
                var created = new InboxItem(Fin.NewId(), InboxKind.Bill, $"{inv.Provider} invoice",
                    $"Invoice {inv.InvoiceNumber ?? "without a number"}{(inv.Reference != null ? $" · reference {inv.Reference}" : "")}. Added from your Invoices.",
                    inv.Provider, inv.Amount, inv.DueDate, DateTime.UtcNow);
                _inbox.Insert(0, created);
                book.LinkBill(owner, inv.Id, created.Id, DateTime.UtcNow);
                changed = true;
            }
            else if (bill != null && !inv.CountsInTotals && bill.Status is InboxStatus.New or InboxStatus.Snoozed)
            {
                _inbox.ReplaceWhere(i => i.Id == bill.Id, i => i with { Status = InboxStatus.Archived });
            }
            else if (bill != null && inv.PaymentStatus == InvoicePaymentStatus.Paid && bill.Status != InboxStatus.Done)
            {
                _inbox.ReplaceWhere(i => i.Id == bill.Id, i => i with { Status = InboxStatus.Done });
            }
            else if (bill != null && inv.PaymentStatus == InvoicePaymentStatus.Unpaid && inv.CountsInTotals && bill.Status is InboxStatus.Done or InboxStatus.Archived)
            {
                _inbox.ReplaceWhere(i => i.Id == bill.Id, i => i with { Status = InboxStatus.New });
            }
            else if (bill != null && inv.CountsInTotals && (bill.Amount != inv.Amount || bill.DueDate != inv.DueDate || bill.From != inv.Provider))
            {
                _inbox.ReplaceWhere(i => i.Id == bill.Id, i => i with { Amount = inv.Amount, DueDate = inv.DueDate, From = inv.Provider, Title = $"{inv.Provider} invoice" });
            }
        }

        if (changed)
        {
            SaveBook(book);
        }
    }

    /// <summary>Called when a bill is paid or un-paid from the Financial Inbox, so its invoice agrees.</summary>
    private void SyncInvoiceFromBill(string billId, bool paid)
    {
        if (InvoiceOwner() is not { } owner || _invoices.FirstOrDefault(i => i.OwnerId == owner && i.LinkedBillId == billId) is not { } inv)
        {
            return;
        }

        var book = LoadBook();

        if (paid && inv.PaymentStatus != InvoicePaymentStatus.Paid)
        {
            book.MarkPaid(owner, inv.Id, DateTime.UtcNow);
        }
        else if (!paid && inv.PaymentStatus == InvoicePaymentStatus.Paid)
        {
            book.MarkUnpaid(owner, inv.Id, DateTime.UtcNow);
        }

        SaveBook(book);
    }

    // ── Export and deletion ─────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private byte[] ExportInvoiceData()
    {
        if (InvoiceOwner() is not { } owner)
        {
            return [];
        }

        var book = LoadBook();
        var export = new
        {
            exportedAt = DateTime.UtcNow,
            note = "Omabilo Personal invoice data export. Demo and simulated records are marked isSimulated.",
            invoices = book.InvoicesOf(owner),
            sources = book.AllSources.Where(s => s.OwnerId == owner),
            audit = book.AuditOf(owner),
        };
        return JsonSerializer.SerializeToUtf8Bytes(export, ExportJson);
    }

    private void DeleteAllInvoiceData()
    {
        if (InvoiceOwner() is not { } owner)
        {
            return;
        }

        var book = LoadBook();
        var billIds = book.InvoicesOf(owner).Select(i => i.LinkedBillId).Where(id => id != null).ToHashSet();
        var removed = book.DeleteAllFor(owner, DateTime.UtcNow);
        SaveBook(book);
        _inbox.RemoveAll(i => billIds.Contains(i.Id) && i.Status != InboxStatus.Done);
        _invoiceDetail.Value = null;
        _invoiceNotice.Value = null;
        Log.Instance.Info($"Invoice data deleted count={removed}");
        _toasts.Success("Invoice data deleted", $"{removed} invoice{(removed == 1 ? "" : "s")} and their sources were removed.");
    }
}
