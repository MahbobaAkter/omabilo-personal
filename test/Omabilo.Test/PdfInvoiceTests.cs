using System.Globalization;
using System.Net;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

/// <summary>Invoices that arrive as PDF attachments: downloaded through the Gmail attachment API, read,
/// turned into fields with their sources, explained, and kept safe from broken, locked, scanned, huge
/// or slow files. All against the fake Google; the PDFs are fictional and built here.</summary>
public class PdfInvoiceTests
{
    private const string Topic = "projects/omabilo-test/topics/gmail-push";
    private const string PersonalSender = "Mst Test Person <someone.personal@gmail.com>";

    private static string D(DateTime d) => d.ToString("d.M.yyyy", CultureInfo.InvariantCulture);

    /// <summary>A PDF with text placed at (x, y), so tests can lay out columns like a real invoice.</summary>
    internal static byte[] LaidOutPdf(params (double X, double Y, string Text)[] items)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var (x, y, text) in items)
        {
            page.AddText(text.Replace('ä', 'a').Replace('ö', 'o').Replace("€", "EUR"), 10, new PdfPoint(x, y), font);
        }

        return builder.Build();
    }

    /// <summary>A fictional Finnish electricity invoice: company and business id at the top, a table of
    /// labels over values, totals with VAT, and the bank details at the bottom.</summary>
    internal static byte[] KuuraInvoice(DateTime today, string number = "2026-55120", string total = "58,40", int dueIn = 28) => LaidOutPdf(
        (40, 800, "Kuura Energia Oy"), (400, 800, "LASKU / INVOICE"),
        (40, 786, "Sahkotie 5, 90100 Oulu"),
        (40, 772, "Y-tunnus 1234567-8"),
        (40, 740, "Test Customer"),
        (40, 700, "Invoice number"), (170, 700, "Invoice date"), (300, 700, "Due date"), (430, 700, "Reference"),
        (40, 686, number), (170, 686, D(today.AddDays(-2))), (300, 686, D(today.AddDays(dueIn))), (430, 686, "1234561"),
        (40, 650, "Description: Electricity, October 2026"),
        (40, 620, "Subtotal"), (430, 620, "46,53 EUR"),
        (40, 606, "VAT 25,5 %"), (430, 606, "11,87 EUR"),
        (40, 592, "Total to pay"), (430, 592, $"{total} EUR"),
        (40, 560, "IBAN FI21 1234 5600 0007 85"), (300, 560, "BIC NDEAFIHH"),
        (40, 540, "www.kuura-energia.example"));

    private static async Task<CompanionRig> ConnectedAsync(SyncOptions? options = null, IDocumentOcr? ocr = null, IAttachmentReader? reader = null)
    {
        var rig = new CompanionRig(options: options, ocr: ocr, reader: reader) { PushTopic = Topic };
        await rig.ConnectAsync("alice");
        await rig.Engine.SyncAsync("alice", default);
        return rig;
    }

    private static async Task PushAsync(CompanionRig rig)
    {
        var outcome = await rig.Engine.HandlePushAsync(new GmailPushNotice("test.user@example.com", (ulong)rig.Google.HistoryCounter, "p" + rig.Google.HistoryCounter), default);
        Assert.Equal(["alice"], outcome.UsersToSync);
        Assert.True((await rig.Engine.SyncAfterPushAsync("alice", default)).Ok);
    }

    [Fact]
    public async Task An_attachment_only_invoice_forwarded_from_a_personal_address_is_read_from_its_pdf()
    {
        var rig = await ConnectedAsync();
        var today = rig.Now.Date;

        // Empty body, a subject with no financial words: only the PDF says what this is
        rig.Google.Add("m-pdf", PersonalSender, "Fwd: scan", "", rig.Now, files: [new FakeFile("lasku-55120.pdf", "application/pdf", KuuraInvoice(today))]);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(1, rig.Google.AttachmentDownloads);
        Assert.Equal(FinKind.Invoice, item.Kind);
        Assert.Equal("Kuura Energia Oy", item.Counterparty);
        Assert.Equal("Invoice from Kuura Energia Oy", item.Title);
        Assert.Equal(58.40m, item.Amount);
        Assert.Equal(today.AddDays(28), item.DueDate);
        Assert.Equal(today.AddDays(-2), item.InvoiceDate);
        Assert.Equal("2026-55120", item.InvoiceNumber);
        Assert.Equal("1234561", item.Reference);
        Assert.Equal("FI21 1234 5600 0007 85", item.Iban);
        Assert.Equal(11.87m, item.Vat);
        Assert.Equal("EUR", item.Currency);
        Assert.Contains("Electricity", item.Purpose);
        Assert.False(item.Suspicious);

        var doc = Assert.Single(item.Attachments);
        Assert.Equal(AttachmentStatus.Read, doc.Status);
        Assert.Equal("m-pdf", doc.GmailId);
        Assert.True(doc.TextLength > 100);

        // Every value says where it came from
        var company = item.FieldSources.Single(f => f.Field == "Company");
        Assert.Equal("PDF “lasku-55120.pdf”", company.Source);
        Assert.Contains(item.FieldSources, f => f.Field == "Amount" && f.Source == "PDF “lasku-55120.pdf”" && f.Confidence >= 0.85);
        Assert.Contains(item.FieldSources, f => f.Field == "Due date" && f.Source.StartsWith("PDF") && f.Confidence >= 0.85);
        Assert.Contains(item.FieldSources, f => f.Field == "IBAN" && f.Source.StartsWith("PDF"));

        // Advice in plain English, from the rules, saying so
        var advice = item.Advice!;
        Assert.Contains("Kuura Energia Oy sent you an invoice", advice.WhoAndWhat);
        Assert.Contains("58,40 €", advice.AmountAndWhen);
        Assert.Contains("in 28 days", advice.AmountAndWhen);
        Assert.StartsWith("Upcoming", advice.Situation);
        Assert.Contains(advice.NextSteps, s => s.Contains("Pay 58,40 €") && s.Contains("reference 1234561") && s.Contains("FI21 1234 5600 0007 85"));
        Assert.Contains("No AI", advice.Basis);

        // The person's own name from the From line is never taken as the biller
        Assert.DoesNotContain(item.FieldSources, f => f.Value.Contains("Mst Test Person"));
        Assert.True(rig.Store.Messages[("alice", "m-pdf")].IsFinancial);
    }

    [Fact]
    public async Task Repeated_notices_and_a_rescan_never_duplicate_a_pdf_invoice()
    {
        var rig = await ConnectedAsync();
        rig.Google.Add("m-pdf", PersonalSender, "Fwd: lasku", "", rig.Now, files: [new FakeFile("lasku.pdf", "application/pdf", KuuraInvoice(rig.Now.Date))]);
        await PushAsync(rig);

        await rig.Engine.HandlePushAsync(new GmailPushNotice("test.user@example.com", (ulong)rig.Google.HistoryCounter, "again"), default);
        await rig.Engine.SyncAfterPushAsync("alice", default);
        await rig.Engine.SyncAsync("alice", default, fullRescan: true);

        Assert.Single(rig.Store.Items);
        Assert.Equal(1, rig.Google.AttachmentDownloads);
    }

    [Fact]
    public async Task A_forwarded_bill_names_its_original_sender_not_the_person_who_forwarded_it()
    {
        var rig = await ConnectedAsync();
        var body = $"""
            ---------- Forwarded message ---------
            From: Revontuli Energy <billing@revontuli.example>
            Subject: Your invoice

            Amount due: 64,20 €
            Due date: {D(rig.Now.AddDays(10))}
            Reference: 20241182
            """;
        rig.Google.Add("m-fwd", PersonalSender, "Fwd: Your invoice", body, rig.Now);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal("Revontuli Energy", item.Counterparty);
        Assert.Equal("Forwarded email's original sender", item.FieldSources.Single(f => f.Field == "Company").Source);
    }

    [Fact]
    public async Task Without_any_company_named_the_issuer_stays_empty_instead_of_the_senders_name()
    {
        var rig = await ConnectedAsync();
        rig.Google.Add("m-anon", PersonalSender, "Invoice", $"Invoice\nAmount due: 64,20 €\nDue date: {D(rig.Now.AddDays(10))}\nReference: 20241182", rig.Now);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Null(item.Counterparty);
        Assert.Equal("Invoice from an unnamed company", item.Title);
        Assert.Contains(item.Missing, m => m.Contains("company isn't stated") && m.Contains("personal address"));
        Assert.Contains("couldn't tell which company", item.Advice!.WhoAndWhat);
        Assert.Contains(item.Advice.NextSteps, s => s.Contains("check the missing details"));
    }

    [Fact]
    public async Task A_scanned_pdf_says_it_needs_ocr_and_is_read_by_ocr_only_when_the_person_allowed_it()
    {
        var today = DateTime.UtcNow.Date;
        var scan = GmailSyncTests.Pdf("");
        var ocrText = $"Kuura Energia Oy\nY-tunnus 1234567-8\nInvoice number: 2026-77\nTotal to pay 58,40 EUR\nDue date: {D(today.AddDays(20))}\nReference: 1234561";

        // OCR off: the item exists but says plainly that the scan wasn't read
        var off = await ConnectedAsync(ocr: new FakeOcr(ocrText));
        off.Google.Add("m-scan", PersonalSender, "Invoice scan", "Invoice attached.", off.Now, files: [new FakeFile("scan.pdf", "application/pdf", scan)]);
        await PushAsync(off);

        var unread = Assert.Single(off.Store.Items.Values).Attachments.Single();
        Assert.Equal(AttachmentStatus.ScannedNotRead, unread.Status);
        Assert.Contains("Turn on AI reading", unread.Problem);
        Assert.Contains(off.Store.Items.Values.Single().Missing, m => m.Contains("scan.pdf") && m.Contains("couldn't be read"));
        Assert.True(off.Store.Items.Values.Single().Confidence <= 0.65);

        // OCR on (AI reading allowed): the same scan is read, and the source says OCR
        var ocr = new FakeOcr(ocrText);
        var on = await ConnectedAsync(ocr: ocr);
        await on.Store.SaveSettingsAsync("alice", new CompanionSettings { AiEnabled = true, AiConsentUtc = on.Now }, default);
        on.Google.Add("m-scan", PersonalSender, "Invoice scan", "Invoice attached.", on.Now, files: [new FakeFile("scan.pdf", "application/pdf", scan)]);
        await PushAsync(on);

        var item = Assert.Single(on.Store.Items.Values);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal(AttachmentStatus.ReadByOcr, item.Attachments.Single().Status);
        Assert.Equal(58.40m, item.Amount);
        Assert.Equal("Kuura Energia Oy", item.Counterparty);
        Assert.Contains(item.FieldSources, f => f.Field == "Amount" && f.Source.Contains("read by OCR") && f.Confidence < 0.85);
    }

    [Fact]
    public async Task An_email_with_only_an_unreadable_bill_is_kept_as_a_document_to_check_not_dropped()
    {
        var rig = await ConnectedAsync();
        rig.Google.Add("m-scan-only", PersonalSender, "Fwd:", "", rig.Now, files: [new FakeFile("lasku-scan.pdf", "application/pdf", GmailSyncTests.Pdf(""))]);
        rig.Google.Add("m-photo", PersonalSender, "Holiday", "", rig.Now, files: [new FakeFile("beach.pdf", "application/pdf", GmailSyncTests.Pdf(""))]);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal("Document to check: lasku-scan.pdf", item.Title);
        Assert.Equal(AttachmentStatus.ScannedNotRead, item.Attachments.Single().Status);
        Assert.Equal(DisplayStatus.NeedsReview, ItemRules.StatusOf(item, rig.Now).Status);
        Assert.Contains(item.Advice!.Checks, c => c.Contains("lasku-scan.pdf") && c.Contains("couldn't be read"));
        Assert.False(rig.Store.Messages[("alice", "m-photo")].IsFinancial);
    }

    [Fact]
    public async Task Broken_huge_and_unsupported_files_are_recorded_with_reasons_and_never_stop_the_message()
    {
        var rig = await ConnectedAsync(options: new SyncOptions { MaxAttachmentBytes = 1024 * 1024 });
        var body = $"Invoice\nBiller: Revontuli Energy Oy\nAmount due: 64,20 €\nDue date: {D(rig.Now.AddDays(10))}\nReference: 20241182";
        rig.Google.Add("m-mixed", "Revontuli Energy <billing@revontuli.example>", "Your invoice", body, rig.Now, files:
        [
            new FakeFile("broken.pdf", "application/pdf", Encoding.ASCII.GetBytes("not a pdf at all")),
            new FakeFile("huge.pdf", "application/pdf", [1, 2, 3], ReportedSize: 50L * 1024 * 1024),
            new FakeFile("terms.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [1, 2, 3]),
            new FakeFile("logo.png", "image/png", [1, 2, 3], Inline: true),
            new FakeFile("note.txt", "text/plain", Encoding.UTF8.GetBytes("Thank you for your business."), CarriedInMessage: true),
        ]);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(64.20m, item.Amount);
        var byName = item.Attachments.ToDictionary(a => a.FileName);
        Assert.Equal(AttachmentStatus.Damaged, byName["broken.pdf"].Status);
        Assert.Equal(AttachmentStatus.TooLarge, byName["huge.pdf"].Status);
        Assert.Equal(AttachmentStatus.NotSupported, byName["terms.docx"].Status);
        Assert.Equal(AttachmentStatus.Read, byName["note.txt"].Status);
        Assert.False(byName.ContainsKey("logo.png"));

        // Only the broken PDF was downloaded: the huge one was refused by its size, the note came in the message
        Assert.Equal(1, rig.Google.AttachmentDownloads);
    }

    [Fact]
    public async Task A_failed_download_is_retried_later_and_then_completes_the_same_item()
    {
        var rig = await ConnectedAsync();
        rig.Google.AttachmentFailures.Enqueue(HttpStatusCode.ServiceUnavailable);
        rig.Google.AttachmentFailures.Enqueue(HttpStatusCode.ServiceUnavailable);
        rig.Google.AttachmentFailures.Enqueue(HttpStatusCode.ServiceUnavailable);
        rig.Google.AttachmentFailures.Enqueue(HttpStatusCode.ServiceUnavailable);
        rig.Google.Add("m-pdf", "Kuura Energia <laskutus@kuura.example>", "Lasku", "Laskusi on liitteenä.", rig.Now,
            files: [new FakeFile("lasku.pdf", "application/pdf", KuuraInvoice(rig.Now.Date))]);
        await PushAsync(rig);

        var first = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(AttachmentStatus.DownloadFailed, first.Attachments.Single().Status);
        Assert.NotNull(first.AttachmentRetryAfterUtc);
        Assert.Null(first.Amount);

        // Not yet due: the next sync leaves it alone
        await rig.Engine.SyncAsync("alice", default);
        Assert.Equal(AttachmentStatus.DownloadFailed, rig.Store.Items[first.Id].Attachments.Single().Status);

        rig.Now = rig.Now.AddMinutes(20);
        await rig.Engine.SyncAsync("alice", default);

        var done = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(first.Id, done.Id);
        Assert.Equal(AttachmentStatus.Read, done.Attachments.Single().Status);
        Assert.Equal(58.40m, done.Amount);
        Assert.Null(done.AttachmentRetryAfterUtc);
    }

    [Fact]
    public async Task Two_different_invoices_in_one_email_become_two_items_each_with_its_own_document()
    {
        var rig = await ConnectedAsync();
        rig.Google.Add("m-two", PersonalSender, "Fwd: two bills", "Both attached.", rig.Now, files:
        [
            new FakeFile("october.pdf", "application/pdf", KuuraInvoice(rig.Now.Date, "2026-1001", "58,40")),
            new FakeFile("november.pdf", "application/pdf", KuuraInvoice(rig.Now.Date, "2026-1002", "61,10", dueIn: 58)),
        ]);
        await PushAsync(rig);

        var items = rig.Store.Items.Values.OrderBy(i => i.InvoiceNumber).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal([58.40m, 61.10m], items.Select(i => i.Amount!.Value));
        Assert.Equal("october.pdf", items[0].Sources.Single().AttachmentName);
        Assert.Equal("november.pdf", items[1].Sources.Single().AttachmentName);
        Assert.Equal(["october.pdf"], items[0].Attachments.Select(a => a.FileName));
    }

    [Fact]
    public async Task When_the_email_and_its_pdf_disagree_the_pdf_wins_and_the_conflict_is_flagged()
    {
        var rig = await ConnectedAsync();
        rig.Google.Add("m-conflict", "Kuura Energia <laskutus@kuura.example>", "Invoice", "Invoice\nAmount due: 99,00 €", rig.Now,
            files: [new FakeFile("lasku.pdf", "application/pdf", KuuraInvoice(rig.Now.Date))]);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(58.40m, item.Amount);
        Assert.Contains(item.Flags, f => f.Contains("99,00 €") && f.Contains("58,40 €"));
    }

    [Fact]
    public async Task Items_read_by_older_rules_are_read_again_once_and_lose_the_senders_name()
    {
        var rig = await ConnectedAsync();
        rig.Google.Add("m-old", PersonalSender, "Fwd: lasku", "", rig.Now, files: [new FakeFile("lasku.pdf", "application/pdf", KuuraInvoice(rig.Now.Date))]);
        await PushAsync(rig);

        // As the earlier version saved it: the sender's name as the company, no documents recorded
        var current = rig.Store.Items.Values.Single();
        await rig.Store.SaveItemAsync(current with { Counterparty = "Mst Test Person", Title = "Invoice from Mst Test Person", Attachments = [], FieldSources = [], AnalysisVersion = 0, UserNote = "keep me" }, default);

        await rig.Engine.SyncAsync("alice", default);

        var upgraded = rig.Store.Items[current.Id];
        Assert.Equal("Kuura Energia Oy", upgraded.Counterparty);
        Assert.Equal(InvoiceAnalysis.CurrentVersion, upgraded.AnalysisVersion);
        Assert.Equal(AttachmentStatus.Read, upgraded.Attachments.Single().Status);
        Assert.Equal("keep me", upgraded.UserNote);

        var downloads = rig.Google.AttachmentDownloads;
        await rig.Engine.SyncAsync("alice", default);
        Assert.Equal(downloads, rig.Google.AttachmentDownloads);
    }

    [Fact]
    public async Task A_pdf_that_takes_too_long_is_marked_timed_out_and_retried()
    {
        var rig = await ConnectedAsync(options: new SyncOptions { AttachmentTimeout = TimeSpan.FromMilliseconds(200) }, reader: new SlowReader());
        rig.Google.Add("m-slow", "Kuura Energia <laskutus@kuura.example>", "Lasku", "Lasku liitteenä.", rig.Now,
            files: [new FakeFile("lasku.pdf", "application/pdf", KuuraInvoice(rig.Now.Date))]);
        await PushAsync(rig);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal(AttachmentStatus.TimedOut, item.Attachments.Single().Status);
        Assert.NotNull(item.AttachmentRetryAfterUtc);
    }

    private sealed class SlowReader : IAttachmentReader
    {
        public async Task<AttachmentReadResult> ReadAsync(string fileName, string mimeType, byte[] data, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new AttachmentReadResult(AttachmentStatus.Read, "late", 1, null);
        }
    }

    // ── The reader and the rules on their own ──

    [Fact]
    public async Task Columns_are_read_in_order_and_label_rows_are_paired_with_their_values()
    {
        var result = await new PdfAttachmentReader().ReadAsync("x.pdf", "application/pdf", KuuraInvoice(new DateTime(2026, 10, 17)), default);

        Assert.Equal(AttachmentStatus.Read, result.Status);
        Assert.Equal(1, result.Pages);
        Assert.Contains("Due date: 14.11.2026", result.Text);
        Assert.Contains("Invoice number: 2026-55120", result.Text);
        Assert.Contains("Reference: 1234561", result.Text);
        Assert.Contains("Total to pay   58,40 EUR", result.Text);
    }

    /// <summary>A PDF whose trailer says it is encrypted with a password Omabilo doesn't have.</summary>
    internal static byte[] LockedPdf()
    {
        var text = Encoding.Latin1.GetString(GmailSyncTests.Pdf("Secret invoice 58,40 EUR"));
        var at = text.LastIndexOf("trailer", StringComparison.Ordinal);
        var close = text.IndexOf(">>", at, StringComparison.Ordinal);
        var hex = new string('A', 64);
        var encrypt = $" /Encrypt << /Filter /Standard /V 2 /R 3 /Length 128 /O <{hex}> /U <{hex}> /P -3904 >> /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] ";
        return Encoding.Latin1.GetBytes(text[..close] + encrypt + text[close..]);
    }

    [Fact]
    public async Task A_password_protected_pdf_is_reported_as_locked()
    {
        var result = await new PdfAttachmentReader().ReadAsync("locked.pdf", "application/pdf", LockedPdf(), default);
        Assert.Equal(AttachmentStatus.PasswordProtected, result.Status);
        Assert.Null(result.Text);
        Assert.Contains("password-protected", result.Problem);
    }

    [Fact]
    public void Iban_check_digits_are_verified()
    {
        Assert.True(InvoiceAnalysis.IbanIsValid("FI21 1234 5600 0007 85"));
        Assert.False(InvoiceAnalysis.IbanIsValid("FI21 1234 5600 0007 86"));
        Assert.Equal([("FI21 1234 5600 0007 85", true)], InvoiceAnalysis.Ibans("Pay to IBAN FI21 1234 5600 0007 85 please"));
    }

    [Theory]
    [InlineData("Mst Test <someone@gmail.com>", null, true)]
    [InlineData("Owner <owner@company.example>", "owner@company.example", true)]
    [InlineData("Revontuli Energy <billing@revontuli.example>", "owner@gmail.com", false)]
    public void Personal_addresses_are_recognised(string from, string? owner, bool personal) =>
        Assert.Equal(personal, InvoiceAnalysis.IsPersonalAddress(from, owner));

    [Fact]
    public void Advice_follows_the_due_date_and_the_persons_own_decisions()
    {
        var today = new DateTime(2026, 10, 17);
        var bill = new FinancialItem
        {
            Id = "it_1", UserId = "u", Kind = FinKind.Invoice, Counterparty = "Kuura Energia Oy", Amount = 58.40m,
            DueDate = today.AddDays(-3), Reference = "1234561", Confidence = 0.9,
        };

        var overdue = InvoiceAnalysis.Advise(bill, today)!;
        Assert.StartsWith("Overdue", overdue.Situation);
        Assert.Contains(overdue.NextSteps, s => s.Contains("as soon as you can"));
        Assert.Contains(overdue.NextSteps, s => s.Contains("payment plan"));

        var paid = InvoiceAnalysis.Advise(bill with { UserState = UserState.ReportedPaid }, today)!;
        Assert.Equal("You marked this as paid.", paid.Situation);
        Assert.Contains(paid.NextSteps, s => s.StartsWith("Nothing to pay"));

        var suspicious = InvoiceAnalysis.Advise(bill with { Suspicious = true, DueDate = today.AddDays(5) }, today)!;
        Assert.Contains(suspicious.NextSteps, s => s.StartsWith("Don't pay yet"));

        Assert.Null(InvoiceAnalysis.Advise(bill with { Kind = FinKind.Receipt }, today));
    }
}
