using Xunit;

// Step 2 of the V1 roadmap: every read document lands in the right inbox view, and an unclear one
// waits in "Needs review" instead of being filed silently. Fictional senders and amounts only.

public class DocumentRoutingTests
{
    private static readonly DateTime Today = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static FinancialItem Read(string subject, string body, string from = "Sender <noreply@sender.example>") =>
        RecordExtractor.Extract("u1", Mails.Make(from, subject, body), MessageClassifier.Classify(from, subject, body), Today);

    private static string[] ViewsOf(FinancialItem i)
    {
        var status = ItemRules.StatusOf(i, Today);
        return ItemRules.Views.Select(v => v.Key).Where(v => v != "all" && ItemRules.InView(i, status, v)).ToArray();
    }

    [Fact]
    public void An_invoice_is_a_bill_and_not_tax_or_debt()
    {
        var i = Read("Lasku 4471", "Revontuli Energia Oy\nLasku 4471\nMaksettava 64,20 €\nEräpäivä 30.10.2026\nViitenumero 12345672", "Revontuli Energia <laskutus@revontuli.example>");

        Assert.Equal(FinKind.Invoice, i.Kind);
        Assert.Contains("bills", ViewsOf(i));
        Assert.DoesNotContain("tax", ViewsOf(i));
        Assert.DoesNotContain("debt", ViewsOf(i));
    }

    [Fact]
    public void A_payslip_is_income_only()
    {
        var i = Read("Palkkalaskelma lokakuu", "Kuusi Works Oy\nPalkkalaskelma 10/2026\nBruttopalkka 3 100,00 €\nEnnakonpidätys 520,00 €\nNettopalkka 2 412,40 €\nMaksupäivä 25.10.2026");

        Assert.True(i.IsIncome);
        Assert.Contains("income", ViewsOf(i));
        Assert.DoesNotContain("bills", ViewsOf(i));
        Assert.DoesNotContain("tax", ViewsOf(i));
    }

    [Fact]
    public void A_tax_decision_goes_to_tax_and_bills()
    {
        var i = Read("Verotuspäätös 2025", "Verohallinto\nVerotuspäätös 2025\nJäännösvero 312,00 €\nEräpäivä 3.12.2026\nViitenumero RF471234567890", "Verohallinto <noreply@vero.example>");

        Assert.Equal(FinKind.TaxNotice, i.Kind);
        Assert.Equal(Categories.Taxes, i.Category);
        Assert.Contains("tax", ViewsOf(i));
        Assert.Contains("bills", ViewsOf(i));
        Assert.DoesNotContain("debt", ViewsOf(i));
    }

    [Fact]
    public void A_collection_letter_goes_to_debt_and_waits_for_review()
    {
        var i = Read("Perintäilmoitus", "Perintätoimisto Kivi Oy\nSaatava: lasku 4471\nMaksettava 120,00 €\nEräpäivä 30.10.2026\nViitenumero 12345672");

        Assert.Equal(FinKind.DebtCollection, i.Kind);
        Assert.Contains("debt", ViewsOf(i));
        Assert.Contains("review", ViewsOf(i));
        Assert.DoesNotContain("tax", ViewsOf(i));
    }

    [Fact]
    public void A_loan_instalment_is_debt()
    {
        var loan = new FinancialItem { Id = "l", UserId = "u1", Kind = FinKind.LoanInstalment, Title = "Loan instalment", Amount = 180m, DueDate = Today.AddDays(10), Confidence = 0.9 };
        var card = new FinancialItem { Id = "c", UserId = "u1", Kind = FinKind.Invoice, Category = Categories.Debt, Title = "Credit card bill", Amount = 75m, DueDate = Today.AddDays(10), Confidence = 0.9 };

        Assert.Contains("debt", ViewsOf(loan));
        Assert.Contains("debt", ViewsOf(card));
    }

    [Fact]
    public void An_unclear_document_waits_in_needs_review_until_checked()
    {
        var unsure = new FinancialItem { Id = "u", UserId = "u1", Kind = FinKind.OtherFinancial, Title = "Letter about your account", Confidence = 0.5 };

        Assert.Contains("review", ViewsOf(unsure));
        Assert.DoesNotContain("review", ViewsOf(unsure with { UserState = UserState.Reviewed }));
    }

    [Fact]
    public void All_and_an_unknown_view_show_everything()
    {
        var i = new FinancialItem { Id = "x", UserId = "u1", Kind = FinKind.Receipt, Title = "Receipt", Confidence = 0.9 };
        var status = ItemRules.StatusOf(i, Today);

        Assert.True(ItemRules.InView(i, status, "all"));
        Assert.True(ItemRules.InView(i, status, "uncertain"));
        Assert.Equal("all", ItemRules.Views[^1].Key);
    }
}
