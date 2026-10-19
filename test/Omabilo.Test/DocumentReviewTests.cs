using Xunit;

/// <summary>Reviewing what Omabilo read: an upload stays unconfirmed until the person checks it, and
/// correcting the document type moves it to the right module. All data is fictional.</summary>
public class DocumentReviewTests
{
    private static async Task<(CompanionRig Rig, FinancialItem Item)> UploadedInvoice()
    {
        var rig = new CompanionRig();
        var result = await rig.Engine.ImportUploadAsync("alice", "lasku.pdf", PdfInvoiceTests.KuuraInvoice(rig.Now.Date), default);
        return (rig, rig.Store.Items[result.ItemId!]);
    }

    [Fact]
    public async Task An_upload_needs_review_until_the_person_says_it_looks_right()
    {
        var (rig, item) = await UploadedInvoice();
        var today = rig.Now.Date;

        var before = ItemRules.StatusOf(item, today);
        Assert.True(before.NeedsReview);
        Assert.Contains("haven't checked", before.ReviewReason);
        Assert.Equal(DisplayStatus.Upcoming, before.Status);

        // A correction alone doesn't confirm the rest of what was read
        var (corrected, _) = await rig.Service.EditAsync("alice", item.Id, new ItemEdit(Purpose: "Electricity, October"), default);
        Assert.Equal(UserState.None, corrected!.UserState);
        Assert.True(ItemRules.StatusOf(corrected, today).NeedsReview);

        var reviewed = await rig.Service.SetStateAsync("alice", item.Id, UserState.Reviewed, default);
        Assert.False(ItemRules.StatusOf(reviewed!, today).NeedsReview);
        Assert.Equal("Electricity, October", reviewed!.Purpose);
    }

    [Fact]
    public async Task Correcting_the_type_to_payslip_moves_it_from_bills_to_salary()
    {
        var (rig, item) = await UploadedInvoice();
        Assert.True(ItemRules.CountsAsOpenObligation(item));

        var (moved, errors) = await rig.Service.EditAsync("alice", item.Id, new ItemEdit(Kind: FinKind.Payslip), default);

        Assert.Empty(errors);
        Assert.Equal(FinKind.Payslip, moved!.Kind);
        Assert.True(moved.IsIncome);
        Assert.False(moved.IsBill);
        Assert.False(ItemRules.CountsAsOpenObligation(moved));
        Assert.Equal(Categories.Income, moved.Category);
        Assert.Equal(IncomeEvidence.Payslip, moved.IncomeEvidence);
        Assert.Contains(nameof(FinancialItem.Kind), moved.EditedFields);

        // Nothing is invented: the bill's total isn't a net salary, so the amount is cleared and the
        // net salary is listed as missing instead of the bill total being counted as income
        Assert.Null(moved.Net);
        Assert.Null(moved.Amount);
        Assert.Contains("Net salary", moved.Missing);
        Assert.Equal(UserState.None, moved.UserState);

        var items = await rig.Store.GetItemsAsync("alice", default);
        var income = ItemRules.Income(items, new CompanionSettings(), rig.Now.Date);
        Assert.Contains(income.History, i => i.Id == item.Id);
        Assert.Null(Assert.Single(MonthlySummary.SalaryHistory(items)).Net);
        Assert.All(MonthlySummary.Build(items, rig.Now.Date), m => Assert.Equal(0m, m.PayslipNet));

        // The net salary the person adds clears the gap
        var (withNet, _) = await rig.Service.EditAsync("alice", item.Id, new ItemEdit(Net: 2150.00m), default);
        Assert.Equal(2150.00m, withNet!.Net);
        Assert.Equal(2150.00m, withNet.Amount);
        Assert.DoesNotContain("Net salary", withNet.Missing);
    }

    [Fact]
    public async Task Correcting_the_type_to_tax_notice_files_it_under_taxes_unless_a_category_is_chosen()
    {
        var (rig, item) = await UploadedInvoice();

        var (tax, _) = await rig.Service.EditAsync("alice", item.Id, new ItemEdit(Kind: FinKind.TaxNotice), default);
        Assert.Equal(Categories.Taxes, tax!.Category);
        Assert.True(tax.IsBill);
        Assert.True(ItemRules.CountsAsOpenObligation(tax));
        Assert.Equal(IncomeEvidence.None, tax.IncomeEvidence);

        var (chosen, _) = await rig.Service.EditAsync("alice", item.Id, new ItemEdit(Kind: FinKind.Invoice, Category: Categories.Housing), default);
        Assert.Equal(FinKind.Invoice, chosen!.Kind);
        Assert.Equal(Categories.Housing, chosen.Category);
    }

    [Fact]
    public async Task Another_person_cannot_change_the_type_and_invalid_types_are_refused()
    {
        var (rig, item) = await UploadedInvoice();

        var (none, errors) = await rig.Service.EditAsync("bob", item.Id, new ItemEdit(Kind: FinKind.Payslip), default);
        Assert.Null(none);
        Assert.NotEmpty(errors);
        Assert.Equal(FinKind.Invoice, rig.Store.Items[item.Id].Kind);

        Assert.NotEmpty(CompanionEdits.Validate(new ItemEdit(Kind: FinKind.NotFinancial), rig.Now.Date));
        Assert.NotEmpty(CompanionEdits.Validate(new ItemEdit(Kind: (FinKind)999), rig.Now.Date));
        Assert.Empty(CompanionEdits.Validate(new ItemEdit(Kind: FinKind.Receipt), rig.Now.Date));
    }

    [Fact]
    public void A_corrected_type_survives_the_document_being_read_again()
    {
        var now = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
        var read = new FinancialItem { Id = "i1", UserId = "alice", Kind = FinKind.Invoice, Category = Categories.Other, Amount = 1800m };
        var corrected = CompanionEdits.Apply(read, new ItemEdit(Kind: FinKind.Payslip), now);

        var again = CompanionEdits.KeepEdits(corrected, read);

        Assert.Equal(FinKind.Payslip, again.Kind);
        Assert.Equal(IncomeEvidence.Payslip, again.IncomeEvidence);
        Assert.Equal(Categories.Income, again.Category);

        // The bill total read again doesn't come back as a salary
        Assert.Null(again.Amount);
        Assert.Contains("Net salary", again.Missing);
    }

    [Fact]
    public async Task Correcting_a_payslip_to_an_invoice_clears_the_salary_instead_of_making_it_a_bill()
    {
        var rig = new CompanionRig();
        var result = await rig.Engine.ImportUploadAsync("alice", "palkkalaskelma.pdf", GmailSyncTests.Pdf(Mails.Payslip(rig.Now.Date.AddDays(8))), default);
        var payslip = rig.Store.Items[result.ItemId!];
        Assert.Equal(2412.40m, payslip.Net);

        var (bill, _) = await rig.Service.EditAsync("alice", payslip.Id, new ItemEdit(Kind: FinKind.Invoice), default);

        Assert.Null(bill!.Amount);
        Assert.False(ItemRules.CountsAsOpenObligation(bill));
        Assert.Contains("The total amount", bill.Missing);
        Assert.DoesNotContain(bill.Missing, m => m.Contains("salary", StringComparison.OrdinalIgnoreCase));
        Assert.True(ItemRules.StatusOf(bill, rig.Now.Date).NeedsReview);

        // The amount the person enters fills the gap
        var (tax, _) = await rig.Service.EditAsync("alice", payslip.Id, new ItemEdit(Kind: FinKind.TaxNotice, Amount: 312m), default);
        Assert.Equal(312m, tax!.Amount);
        Assert.DoesNotContain("The total amount", tax.Missing);
    }
}
