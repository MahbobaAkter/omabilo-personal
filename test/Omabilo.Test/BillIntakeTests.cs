using System.Globalization;
using Xunit;

/// <summary>Adding a bill without a mailbox: a photo (read by OCR only with AI reading on) or pasted
/// text, through the same reading, review and duplicate rules as an uploaded PDF. Fictional data only.</summary>
public class BillIntakeTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01];
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private static string D(DateTime d) => d.ToString("d.M.yyyy", CultureInfo.InvariantCulture);

    private static string KuuraText(DateTime today) =>
        $"Kuura Energia Oy\nY-tunnus 1234567-8\nLasku / Invoice\nInvoice number: 2026-77\nTotal to pay 58,40 EUR\nDue date: {D(today.AddDays(20))}\nReference: 1234561\nIBAN FI21 1234 5600 0007 85";

    [Theory]
    [InlineData("lasku.pdf", "application/pdf")]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("scan.png", "image/png")]
    public void A_file_is_accepted_when_its_name_and_content_agree(string name, string mime)
    {
        var data = mime == "application/pdf" ? "%PDF-1.7 x"u8.ToArray() : mime == "image/png" ? Png : Jpeg;
        Assert.Equal(mime, BillUpload.MimeOf(name, data));
        Assert.Null(BillUpload.Reject(name, data));
    }

    [Fact]
    public void A_file_whose_name_and_content_disagree_is_refused()
    {
        Assert.Equal(UploadOutcome.NotAPdf, BillUpload.Reject("photo.png", Jpeg));
        Assert.Equal(UploadOutcome.NotAPdf, BillUpload.Reject("photo.jpg", "%PDF-1.7"u8.ToArray()));
        Assert.Equal(UploadOutcome.NotAPdf, BillUpload.Reject("bill.gif", "GIF89a"u8.ToArray()));
        Assert.Equal("long-name.png".Length, BillUpload.CleanFileName("long-name.png").Length);
        Assert.EndsWith(".png", BillUpload.CleanFileName(new string('a', 300) + ".png"));
    }

    [Fact]
    public async Task A_photo_is_not_read_while_AI_reading_is_off_and_nothing_is_saved()
    {
        var ocr = new FakeOcr(KuuraText(DateTime.UtcNow.Date));
        var rig = new CompanionRig(ocr: ocr);

        var result = await rig.Engine.ImportUploadAsync("alice", "lasku.jpg", Jpeg, default);

        Assert.Equal((UploadOutcome.Unreadable, AttachmentStatus.NotSupported), (result.Outcome, result.Unread));
        Assert.Equal(0, ocr.Calls);
        Assert.Empty(rig.Store.Items);
    }

    [Fact]
    public async Task A_photo_is_read_by_OCR_once_AI_reading_is_on_and_waits_for_review()
    {
        var ocr = new FakeOcr(KuuraText(new CompanionRig().Now.Date));
        var rig = new CompanionRig(ocr: ocr);
        await rig.Store.SaveSettingsAsync("alice", new CompanionSettings { AiEnabled = true, AiConsentUtc = rig.Now }, default);

        var result = await rig.Engine.ImportUploadAsync("alice", "lasku.jpg", Jpeg, default);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        Assert.Equal(1, ocr.Calls);
        var item = rig.Store.Items[result.ItemId!];
        Assert.Equal("alice", item.UserId);
        Assert.Equal(58.40m, item.Amount);
        Assert.Equal("Kuura Energia Oy", item.Counterparty);
        Assert.Equal(AttachmentStatus.ReadByOcr, item.Attachments.Single().Status);
        Assert.Contains(item.FieldSources, f => f.Field == "Amount" && f.Source.Contains("read by OCR"));
        Assert.True(ItemRules.StatusOf(item, rig.Now.Date).NeedsReview);
        Assert.Empty(await rig.Store.GetItemsAsync("bob", default));
    }

    [Fact]
    public async Task Pasted_bill_text_is_read_saved_for_the_person_only_and_filed_once()
    {
        var rig = new CompanionRig();
        var today = rig.Now.Date;

        var result = await rig.Engine.ImportPastedTextAsync("alice", "  " + KuuraText(today).Replace("\n", "\r\n") + "\n", default);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        var item = rig.Store.Items[result.ItemId!];
        Assert.Equal(FinKind.Invoice, item.Kind);
        Assert.Equal("Kuura Energia Oy", item.Counterparty);
        Assert.Equal(58.40m, item.Amount);
        Assert.Equal(today.AddDays(20), item.DueDate);
        Assert.Equal("1234561", item.Reference);
        Assert.Contains(item.FieldSources, f => f.Field == "Amount" && f.Source.Contains(BillUpload.PastedName));
        Assert.True(BillUpload.IsUploadId(item.Sources[0].GmailId));
        Assert.Equal(UserState.None, item.UserState);

        // The same text again (line endings and spaces aside) is the same bill
        var again = await rig.Engine.ImportPastedTextAsync("alice", KuuraText(today), default);
        Assert.Equal((UploadOutcome.AlreadyFiled, item.Id), (again.Outcome, again.ItemId));
        Assert.Single(rig.Store.Items.Values);

        // Nothing went to Google, and bob sees nothing
        Assert.Equal(0, rig.Google.FullFetches);
        Assert.Empty(await rig.Store.GetItemsAsync("bob", default));
        Assert.DoesNotContain(rig.Logs, l => l.Contains("Kuura") || l.Contains("58,40") || l.Contains("1234561"));
    }

    [Fact]
    public async Task Empty_overlong_or_non_financial_pasted_text_saves_nothing()
    {
        var rig = new CompanionRig();

        Assert.Equal(UploadOutcome.Empty, (await rig.Engine.ImportPastedTextAsync("alice", " \n ", default)).Outcome);
        Assert.Equal(UploadOutcome.TooLarge, (await rig.Engine.ImportPastedTextAsync("alice", new string('x', BillUpload.MaxPastedChars + 1), default)).Outcome);
        Assert.Equal(UploadOutcome.NotFinancial, (await rig.Engine.ImportPastedTextAsync("alice", "Blueberry pie\nMix the flour, butter and sugar.\nBake for forty minutes.", default)).Outcome);
        Assert.Empty(rig.Store.Items);
    }
}

/// <summary>A bill added without a mailbox can still get an AI explanation, from what Omabilo kept of it.</summary>
public class ExplainSavedTests
{
    [Fact]
    public async Task A_pasted_bill_is_explained_by_AI_only_when_AI_reading_is_on_and_only_for_its_owner()
    {
        var ai = new FakeAi();
        var rig = new CompanionRig(ai: ai);
        var text = $"Revontuli Energy Oy\nInvoice number: 2026-881\nAmount due 64,20 EUR\nDue date: {rig.Now.Date.AddDays(9):d.M.yyyy}\nReference: 1234561";
        var filed = await rig.Engine.ImportPastedTextAsync("alice", text, default);
        Assert.Equal(0, ai.Calls);

        // Off: nothing is sent to the model
        Assert.Null(await rig.Engine.ExplainSavedAsync("alice", filed.ItemId!, default));
        Assert.Equal(0, ai.Calls);

        await rig.Store.SaveSettingsAsync("alice", new CompanionSettings { AiEnabled = true, AiConsentUtc = rig.Now }, default);

        // Someone else's record is never explained
        Assert.Null(await rig.Engine.ExplainSavedAsync("bob", filed.ItemId!, default));

        var explained = await rig.Engine.ExplainSavedAsync("alice", filed.ItemId!, default);

        Assert.NotNull(explained?.Ai);
        Assert.Equal(1, ai.Calls);
        Assert.Contains("64,20", ai.LastInput);
        Assert.NotNull(rig.Store.Items[filed.ItemId!].Ai);
        Assert.Equal(64.20m, rig.Store.Items[filed.ItemId!].Amount);
    }
}
