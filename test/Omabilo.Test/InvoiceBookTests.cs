using Xunit;

public class InvoiceBookTests
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";
    private static readonly DateTime Now = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private static InvoiceBook EmptyBook() => new([], [], []);

    private static InvoiceSubmission Email(string messageId, string provider = "Northwind Energy Oy", string? number = "NE-1", string? reference = "12345672",
        decimal amount = 58.20m, int dueInDays = 10, double confidence = 0.9) =>
        new(InvoiceSourceType.ForwardedEmail, $"email:{messageId}#a.pdf", true,
            new ExtractedInvoiceFields(provider, number, reference, Now.AddDays(-3), Now.AddDays(dueInDays), amount, "EUR"), confidence);

    [Fact]
    public void Same_source_twice_is_idempotent()
    {
        var book = EmptyBook();
        var first = book.Ingest(Alice, Email("m1"), Now);
        var again = book.Ingest(Alice, Email("m1"), Now.AddMinutes(5));

        Assert.Equal(IngestOutcome.Created, first.Outcome);
        Assert.Equal(IngestOutcome.AlreadyIngested, again.Outcome);
        Assert.Single(book.InvoicesOf(Alice));
        Assert.Single(book.AllSources);
    }

    [Fact]
    public void Same_reference_from_another_source_links_without_double_counting()
    {
        var book = EmptyBook();
        var created = book.Ingest(Alice, Email("m1"), Now);
        book.Approve(Alice, created.InvoiceId!, Now);

        var fromDocument = new InvoiceSubmission(InvoiceSourceType.Document, "doc:scan-1", true,
            new ExtractedInvoiceFields("Northwind Energy", null, "1234 5672", null, Now.AddDays(10), 58.20m, "EUR"), 0.8);
        var linked = book.Ingest(Alice, fromDocument, Now.AddHours(1));

        Assert.Equal(IngestOutcome.LinkedToExisting, linked.Outcome);
        Assert.Equal(created.InvoiceId, linked.InvoiceId);
        Assert.Single(book.InvoicesOf(Alice));
        Assert.Equal(2, book.InvoicesOf(Alice)[0].SourceRecordIds.Count);
        Assert.Equal(58.20m, book.OpenTotal(Alice));
    }

    [Fact]
    public void Same_provider_and_invoice_number_is_an_exact_match()
    {
        var book = EmptyBook();
        book.Ingest(Alice, Email("m1", reference: null, number: "HW-88231", provider: "Harbor Water (demo)"), Now);
        var second = book.Ingest(Alice, Email("m2", reference: null, number: "hw 88231", provider: "Harbor Water"), Now);

        Assert.Equal(IngestOutcome.LinkedToExisting, second.Outcome);
        Assert.Single(book.InvoicesOf(Alice));
    }

    [Fact]
    public void Same_provider_amount_and_close_due_date_is_flagged_not_merged()
    {
        var book = EmptyBook();
        var first = book.Ingest(Alice, Email("m1", number: null, reference: null), Now);
        var second = book.Ingest(Alice, Email("m2", number: null, reference: null, dueInDays: 12), Now);

        Assert.Equal(IngestOutcome.PossibleDuplicate, second.Outcome);
        var dup = book.InvoicesOf(Alice).Single(i => i.Id == second.InvoiceId);
        Assert.Equal(first.InvoiceId, dup.PossibleDuplicateOfId);
        Assert.Equal(InvoiceProcessingState.NeedsReview, dup.State);
    }

    [Fact]
    public void Merging_a_duplicate_moves_its_sources_and_stops_it_counting()
    {
        var book = EmptyBook();
        var first = book.Ingest(Alice, Email("m1", number: null, reference: null), Now);
        book.Approve(Alice, first.InvoiceId!, Now);
        var second = book.Ingest(Alice, Email("m2", number: null, reference: null, dueInDays: 11), Now);

        book.MergeInto(Alice, second.InvoiceId!, first.InvoiceId!, Now);

        var merged = book.InvoicesOf(Alice).Single(i => i.Id == second.InvoiceId);
        var target = book.InvoicesOf(Alice).Single(i => i.Id == first.InvoiceId);
        Assert.Equal(InvoiceProcessingState.Merged, merged.State);
        Assert.False(merged.CountsInTotals);
        Assert.Equal(2, target.SourceRecordIds.Count);
        Assert.All(book.AllSources, s => Assert.Equal(first.InvoiceId, s.CanonicalInvoiceId));
        Assert.Equal(58.20m, book.OpenTotal(Alice));
    }

    [Fact]
    public void Extracted_invoices_wait_for_review_and_count_only_after_approval()
    {
        var book = EmptyBook();
        var r = book.Ingest(Alice, Email("m1"), Now);

        Assert.Equal(InvoiceProcessingState.NeedsReview, book.InvoicesOf(Alice)[0].State);
        Assert.Equal(0m, book.OpenTotal(Alice));

        book.Approve(Alice, r.InvoiceId!, Now);
        Assert.Equal(58.20m, book.OpenTotal(Alice));

        book.MarkPaid(Alice, r.InvoiceId!, Now);
        Assert.Equal(0m, book.OpenTotal(Alice));
    }

    [Fact]
    public void Manual_entries_are_confirmed_immediately()
    {
        var book = EmptyBook();
        var fields = new ExtractedInvoiceFields("Kallio Dental", "KD-1", null, Now, Now.AddDays(5), 96m, "EUR");
        var r = book.Ingest(Alice, ManualInvoiceSource.Submit(fields, "entry-1"), Now);

        var inv = book.InvoicesOf(Alice).Single();
        Assert.Equal(IngestOutcome.Created, r.Outcome);
        Assert.Equal(InvoiceProcessingState.Ready, inv.State);
        Assert.Equal(InvoiceVerification.Verified, inv.Verification);
    }

    [Fact]
    public void Demo_invoices_count_at_once_and_never_overlap_inbox_demo_bills()
    {
        var book = EmptyBook();

        foreach (var demo in SyntheticInvoices.DemoInvoices())
        {
            Assert.Equal(IngestOutcome.Created, book.Ingest(Alice, demo, Now).Outcome);
        }

        Assert.All(book.InvoicesOf(Alice), i => Assert.True(i.CountsInTotals));
        string[] inboxDemoProviders = ["Northwind", "Aurora", "Nimbus", "Fiberly", "Fjord", "StreamBox"];
        Assert.DoesNotContain(book.InvoicesOf(Alice), i => inboxDemoProviders.Any(p => i.Provider.Contains(p)));
    }

    [Fact]
    public void Missing_required_fields_are_recorded_but_not_turned_into_an_invoice()
    {
        var book = EmptyBook();
        var r = book.Ingest(Alice, new InvoiceSubmission(InvoiceSourceType.Document, "doc:x", true,
            new ExtractedInvoiceFields("Spoke & Chain", null, null, null, Now.AddDays(5), null, null), 0.3), Now);

        Assert.Equal(IngestOutcome.Invalid, r.Outcome);
        Assert.Empty(book.InvoicesOf(Alice));
        Assert.Single(book.AllSources);
        Assert.Contains("amount", r.Message);
    }

    [Fact]
    public void Another_owner_cannot_read_or_change_an_invoice()
    {
        var book = EmptyBook();
        var r = book.Ingest(Alice, Email("m1"), Now);

        Assert.Empty(book.InvoicesOf(Bob));
        Assert.Empty(book.AuditOf(Bob));
        Assert.Throws<InvoiceAccessDeniedException>(() => book.Approve(Bob, r.InvoiceId!, Now));
        Assert.Throws<InvoiceAccessDeniedException>(() => book.MarkPaid(Bob, r.InvoiceId!, Now));
        Assert.Throws<InvoiceAccessDeniedException>(() => book.Approve("", r.InvoiceId!, Now));
        Assert.Equal(InvoiceProcessingState.NeedsReview, book.InvoicesOf(Alice).Single().State);
    }

    [Fact]
    public void Matching_never_crosses_owners()
    {
        var book = EmptyBook();
        book.Ingest(Alice, Email("m1"), Now);
        var bobs = book.Ingest(Bob, Email("m1"), Now);

        Assert.Equal(IngestOutcome.Created, bobs.Outcome);
        Assert.Single(book.InvoicesOf(Alice));
        Assert.Single(book.InvoicesOf(Bob));
    }

    [Fact]
    public void Deleting_removes_only_that_owners_records_and_leaves_a_deletion_event()
    {
        var book = EmptyBook();
        book.Ingest(Alice, Email("m1"), Now);
        book.Ingest(Alice, Email("m2", reference: "99", number: "X-9", amount: 12m), Now);
        book.Ingest(Bob, Email("m3"), Now);

        var removed = book.DeleteAllFor(Alice, Now);

        Assert.Equal(2, removed);
        Assert.Empty(book.InvoicesOf(Alice));
        Assert.DoesNotContain(book.AllSources, s => s.OwnerId == Alice);
        Assert.Single(book.AuditOf(Alice));
        Assert.Equal("data.deleted", book.AuditOf(Alice)[0].Action);
        Assert.Single(book.InvoicesOf(Bob));
    }

    [Fact]
    public void Audit_records_actions_without_amounts_or_document_text()
    {
        var book = EmptyBook();
        var r = book.Ingest(Alice, Email("m1", amount: 1124.90m), Now);
        book.Edit(Alice, r.InvoiceId!, new ExtractedInvoiceFields("Northwind Energy Oy", "NE-1", "12345672", Now.AddDays(-3), Now.AddDays(11), 1200m, "EUR"), Now);
        book.Approve(Alice, r.InvoiceId!, Now);

        var trail = book.AuditOf(Alice, r.InvoiceId);
        Assert.Equal(3, trail.Count);
        Assert.Contains(trail, e => e.Detail.Contains("amount") && e.Detail.Contains("due date"));
        Assert.DoesNotContain(trail, e => e.Detail.Contains("1124") || e.Detail.Contains("1200") || e.Detail.Contains("12345672"));
    }

    [Fact]
    public void Idempotency_key_depends_on_owner_type_and_source_id()
    {
        var a = InvoiceKeys.Idempotency(Alice, InvoiceSourceType.Gmail, "msg-1");
        Assert.Equal(a, InvoiceKeys.Idempotency(Alice, InvoiceSourceType.Gmail, " msg-1 "));
        Assert.NotEqual(a, InvoiceKeys.Idempotency(Bob, InvoiceSourceType.Gmail, "msg-1"));
        Assert.NotEqual(a, InvoiceKeys.Idempotency(Alice, InvoiceSourceType.Outlook, "msg-1"));
    }
}
