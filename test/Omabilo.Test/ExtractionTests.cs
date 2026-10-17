using Xunit;

public class ExtractionTests
{
    [Theory]
    [InlineData("12345672", true)]
    [InlineData("1234 5672", true)]
    [InlineData("12345671", false)]
    [InlineData("RF18539007547034", true)]
    [InlineData("RF18 5390 0754 7034", true)]
    [InlineData("RF19539007547034", false)]
    [InlineData("12", false)]
    [InlineData("", false)]
    public void Payment_references_are_checked(string reference, bool valid) =>
        Assert.Equal(valid, PaymentReference.IsValid(reference));

    [Fact]
    public void Generated_references_carry_a_valid_check_digit() =>
        Assert.True(PaymentReference.IsValid(PaymentReference.WithFinnishCheckDigit("4407719")));

    [Theory]
    [InlineData("58,20", 58.20)]
    [InlineData("1 124,90", 1124.90)]
    [InlineData("1.124,90", 1124.90)]
    [InlineData("1,124.90", 1124.90)]
    [InlineData("212.40", 212.40)]
    public void Amounts_in_finnish_and_english_formats(string text, double expected) =>
        Assert.Equal((decimal)expected, TextInvoiceExtractor.ParseAmount(text));

    [Theory]
    [InlineData("74,5O")]
    [InlineData("abc")]
    [InlineData("0,00")]
    public void Unreadable_amounts_are_rejected(string text) =>
        Assert.Null(TextInvoiceExtractor.ParseAmount(text));

    [Fact]
    public void Finnish_invoice_text_is_read_with_high_confidence()
    {
        var f = TextInvoiceExtractor.Extract("""
            Laskuttaja: Harbor Water Utility (demo)
            Laskun numero: HW-88231
            Laskun päiväys: 15.10.2026
            Eräpäivä: 2.11.2026
            Viitenumero: 12345672
            Yhteensä: 1 124,90 €
            """);

        Assert.Equal("Harbor Water Utility (demo)", f.Provider);
        Assert.Equal("HW-88231", f.InvoiceNumber);
        Assert.Equal(1124.90m, f.Amount);
        Assert.Equal(new DateTime(2026, 11, 2), f.DueDate);
        Assert.Equal(new DateTime(2026, 10, 15), f.InvoiceDate);
        Assert.Equal("EUR", f.Currency);
        Assert.Equal(0.99, f.ConfidenceOf("reference"));
        Assert.True(TextInvoiceExtractor.Overall(f) >= 0.85);
    }

    [Fact]
    public void A_poor_scan_has_low_confidence_and_cannot_become_an_invoice()
    {
        var scan = SyntheticInvoices.Documents().Single(d => d.Key == "doc-poor-scan").Document;
        var f = TextInvoiceExtractor.Extract(scan.ExtractedText!);

        Assert.Null(f.Amount);
        Assert.Equal(0.4, f.ConfidenceOf("reference"));
        Assert.Equal(0, TextInvoiceExtractor.Overall(f));

        var r = new InvoiceBook([], [], []).Ingest("u", new InvoiceSubmission(InvoiceSourceType.Document, "doc:poor", true, f, TextInvoiceExtractor.Overall(f)), DateTime.UtcNow);
        Assert.Equal(IngestOutcome.Invalid, r.Outcome);
    }

    [Fact]
    public async Task Email_reader_uses_message_and_attachment_as_the_source_id()
    {
        var sample = SyntheticInvoices.Emails().First();
        var subs = await EmailInvoiceReader.ReadAsync(sample.Email, new TextInvoiceExtractor(), isSimulated: true);

        var s = Assert.Single(subs);
        Assert.Equal(InvoiceSourceType.ForwardedEmail, s.Type);
        Assert.True(s.IsSimulated);
        Assert.Equal($"email:{sample.Email.MessageId}#{sample.Email.Attachments[0].FileName}", s.SourceRef);
        Assert.Equal(58.20m, s.Fields.Amount);
    }

    [Fact]
    public async Task The_resent_sample_email_is_matched_to_the_first_one()
    {
        var emails = SyntheticInvoices.Emails();
        var book = new InvoiceBook([], [], []);
        var reader = new TextInvoiceExtractor();
        var now = DateTime.UtcNow;

        foreach (var s in await EmailInvoiceReader.ReadAsync(emails[0].Email, reader, true)) book.Ingest("u", s, now);
        var again = (await EmailInvoiceReader.ReadAsync(emails[2].Email, reader, true)).Select(s => book.Ingest("u", s, now)).Single();

        Assert.Equal(IngestOutcome.LinkedToExisting, again.Outcome);
        Assert.Single(book.InvoicesOf("u"));
    }

    [Fact]
    public void Every_sample_is_fictional_and_simulated()
    {
        Assert.All(SyntheticInvoices.Emails(), e => Assert.EndsWith("example.invalid", e.Email.MessageId));
        Assert.All(SyntheticInvoices.DemoInvoices(), d => Assert.True(d.IsSimulated));
        Assert.All(InvoiceSourceCatalog.All.Where(s => s.Type is InvoiceSourceType.Gmail or InvoiceSourceType.Outlook or InvoiceSourceType.FinnishProvider),
            s => Assert.Equal(SourceAvailability.NotConnected, s.Availability));
    }
}
