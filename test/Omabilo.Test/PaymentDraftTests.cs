using Xunit;

/// <summary>"Ask for more time" drafts use only the bill's saved details and say what is missing,
/// never a guess. Fictional data only.</summary>
public class PaymentDraftTests
{
    private static readonly DateTime Today = new(2026, 10, 19);

    private static FinancialItem Bill(decimal? amount = 64.20m, int? dueIn = 5, string? number = "2026-881", string? reference = "1234561", string? party = "Revontuli Energy Oy") => new()
    {
        Id = "b1", UserId = "u1", Kind = FinKind.Invoice, Title = "Invoice", Counterparty = party, Amount = amount,
        DueDate = dueIn is { } d ? Today.AddDays(d) : null, InvoiceNumber = number, Reference = reference, CreatedUtc = Today, UpdatedUtc = Today,
    };

    [Fact]
    public void An_english_draft_names_the_bill_the_amount_the_dates_and_the_sender()
    {
        var d = PaymentDrafts.Extension(Bill(), Today.AddDays(19), "Test Person");

        Assert.Equal("Request for more time to pay, invoice 2026-881", d.Subject);
        Assert.Contains("Hello Revontuli Energy Oy,", d.Body);
        Assert.Contains("my invoice (invoice number 2026-881, reference 1234561) for 64,20 €, due 24 Oct 2026", d.Body.Replace(' ', ' '));
        Assert.Contains("until 7 Nov 2026", d.Body);
        Assert.EndsWith("Kind regards,\nTest Person", d.Body);
        Assert.Empty(d.Gaps);
    }

    [Fact]
    public void A_finnish_draft_uses_finnish_dates()
    {
        var d = PaymentDrafts.Extension(Bill(), new DateTime(2026, 11, 7), null, "fi");

        Assert.Equal("Maksuaikapyyntö, lasku 2026-881", d.Subject);
        Assert.Contains("eräpäivä 24.10.2026", d.Body);
        Assert.Contains("7.11.2026 asti", d.Body);
        Assert.EndsWith("[Nimesi]", d.Body);
    }

    [Fact]
    public void What_the_bill_did_not_say_is_listed_not_invented()
    {
        var d = PaymentDrafts.Extension(Bill(amount: null, dueIn: null, number: null, reference: null, party: null), Today.AddDays(14), null);

        Assert.Equal(4, d.Gaps.Count);
        Assert.Contains("Hello,", d.Body);
        Assert.DoesNotContain("€", d.Body);
        Assert.DoesNotContain("due ", d.Body);
        Assert.Equal("Request for more time to pay", d.Subject);
    }

    [Fact]
    public void The_suggested_date_is_two_weeks_after_the_due_date_or_today_if_overdue()
    {
        Assert.Equal(Today.AddDays(19), PaymentDrafts.DefaultNewDate(Bill(dueIn: 5), Today));
        Assert.Equal(Today.AddDays(14), PaymentDrafts.DefaultNewDate(Bill(dueIn: -3), Today));
    }
}
