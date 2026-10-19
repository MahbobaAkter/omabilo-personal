using Xunit;

/// <summary>"Forward bills to Omabilo": private addresses, filing for the right person only, Gmail's
/// forwarding confirmation, duplicates, and no Google access at all. All data is fictional.</summary>
public class BillForwardingTests
{
    private const string Domain = "omabilo-test.ikonai.app";

    private static ForwardedEmail Email(string token, string id, string from, string subject, string body, DateTime at, params ForwardedFile[] files) =>
        new(id, $"{token}@{Domain}", from, subject, at, body, files);

    private static ForwardedFile Pdf(string name, byte[] data) => new(name, "application/pdf", data.Length, data);

    [Fact]
    public void Addresses_are_random_and_only_well_formed_ones_are_recognised()
    {
        var a = BillForwarding.NewToken();
        var b = BillForwarding.NewToken();

        Assert.NotEqual(a, b);
        Assert.StartsWith(BillForwarding.Prefix, a);
        Assert.Equal(BillForwarding.Prefix.Length + BillForwarding.TokenLength, a.Length);

        Assert.Equal(a, BillForwarding.TokenOf($"{a}@{Domain}"));
        Assert.Equal(a, BillForwarding.TokenOf($"Omabilo <{a.ToUpperInvariant()}@{Domain}>"));
        Assert.Equal(a, BillForwarding.TokenOf($"{a}+gmail@{Domain}"));
        Assert.Null(BillForwarding.TokenOf($"test@{Domain}"));
        Assert.Null(BillForwarding.TokenOf($"bills-short@{Domain}"));
        Assert.Null(BillForwarding.TokenOf($"bills-{new string('0', BillForwarding.TokenLength)}@{Domain}"));
        Assert.Null(BillForwarding.TokenOf("not an address"));
    }

    [Fact]
    public async Task A_forwarded_pdf_bill_is_filed_for_the_address_owner_without_any_gmail_access()
    {
        var rig = new CompanionRig();
        var alice = await rig.Engine.ForwardAddressAsync("alice", default);
        await rig.Engine.ForwardAddressAsync("bob", default);
        var today = rig.Now.Date;

        var result = await rig.Engine.ImportForwardedAsync(Email(alice.Token, "e1", "Alice Example <alice.example@gmail.com>", "Fwd: scan", "---------- Forwarded message ---------\nFrom: Kuura Energia <laskutus@kuura-energia.example>\n",
            rig.Now, Pdf("lasku-55120.pdf", PdfInvoiceTests.KuuraInvoice(today))), default);

        Assert.Equal(ForwardOutcome.Filed, result.Outcome);
        Assert.Equal("alice", result.UserId);
        Assert.Equal(1, result.NewItems);

        var item = Assert.Single(rig.Store.Items.Values);
        Assert.Equal("alice", item.UserId);
        Assert.Equal(FinKind.Invoice, item.Kind);
        // The forwarded email's original sender, never the person who forwarded it
        Assert.Equal("Kuura Energia", item.Counterparty);
        Assert.Equal(58.40m, item.Amount);
        Assert.Equal(today.AddDays(28), item.DueDate);
        Assert.True(BillForwarding.IsForwardedId(item.Sources[0].GmailId));
        Assert.Null(item.AttachmentRetryAfterUtc);
        Assert.False(SyncEngine.CanReread(item));

        // Nothing went to Google, and bob sees nothing
        Assert.Empty(rig.Google.TokenRequests);
        Assert.Equal(0, rig.Google.FullFetches);
        Assert.Empty(await rig.Store.GetItemsAsync("bob", default));
        Assert.Equal(1, rig.Store.Forwards["alice"].ReceivedCount);
        Assert.Null(await rig.Engine.ReprocessAsync("alice", item.Id, default));
    }

    [Fact]
    public async Task The_same_email_delivered_twice_is_filed_once()
    {
        var rig = new CompanionRig();
        var alice = await rig.Engine.ForwardAddressAsync("alice", default);
        var mail = Email(alice.Token, "e2", "Kuura Energia <laskutus@kuura-energia.example>", "Lasku 2026-55120", "", rig.Now,
            Pdf("lasku.pdf", PdfInvoiceTests.KuuraInvoice(rig.Now.Date)));

        Assert.Equal(ForwardOutcome.Filed, (await rig.Engine.ImportForwardedAsync(mail, default)).Outcome);
        Assert.Equal(ForwardOutcome.AlreadyDone, (await rig.Engine.ImportForwardedAsync(mail, default)).Outcome);
        Assert.Single(rig.Store.Items.Values);
    }

    [Fact]
    public async Task Mail_to_an_unknown_or_replaced_address_is_not_read()
    {
        var rig = new CompanionRig();
        var old = await rig.Engine.ForwardAddressAsync("alice", default);
        var fresh = await rig.Engine.NewForwardAddressAsync("alice", default);

        Assert.NotEqual(old.Token, fresh.Token);
        Assert.Equal(fresh.Token, (await rig.Engine.ForwardAddressAsync("alice", default)).Token);

        foreach (var token in new[] { old.Token, BillForwarding.NewToken() })
        {
            var result = await rig.Engine.ImportForwardedAsync(Email(token, "e-" + token, "Kuura Energia <laskutus@kuura-energia.example>", "Lasku", "", rig.Now,
                Pdf("lasku.pdf", PdfInvoiceTests.KuuraInvoice(rig.Now.Date))), default);
            Assert.Equal(ForwardOutcome.UnknownAddress, result.Outcome);
            Assert.Null(result.UserId);
        }

        Assert.Empty(rig.Store.Items);
        Assert.Empty(rig.Store.Messages);
    }

    [Fact]
    public async Task Gmails_forwarding_confirmation_is_kept_for_the_person_and_never_filed()
    {
        var rig = new CompanionRig();
        var alice = await rig.Engine.ForwardAddressAsync("alice", default);
        const string body = """
            alice.example@gmail.com has requested to automatically forward mail to your email address.
            Confirmation code: 812345678

            To allow alice.example@gmail.com to automatically forward mail to your address, please click the link below to confirm the request:

            https://mail-settings.google.com/mail/vf-%5BANGjdJ_fictional%5D-fictional
            """;

        var result = await rig.Engine.ImportForwardedAsync(Email(alice.Token, "e3", "Gmail Team <forwarding-noreply@google.com>",
            "(#812345678) Gmail Forwarding Confirmation - Receive Mail from alice.example@gmail.com", body, rig.Now), default);

        Assert.Equal(ForwardOutcome.GmailConfirmation, result.Outcome);
        var saved = rig.Store.Forwards["alice"];
        Assert.Equal("812345678", saved.GmailConfirmCode);
        Assert.Equal("https://mail-settings.google.com/mail/vf-%5BANGjdJ_fictional%5D-fictional", saved.GmailConfirmLink);
        Assert.Equal("alice.example@gmail.com", saved.GmailConfirmFor);
        Assert.Empty(rig.Store.Items);
    }

    [Fact]
    public void A_look_alike_confirmation_never_carries_a_foreign_link()
    {
        Assert.Null(BillForwarding.GmailConfirmation("Gmail Team <forwarding-noreply@gmail-security.example>", "(#123456789) Gmail Forwarding Confirmation", "https://evil.example/x"));

        var c = BillForwarding.GmailConfirmation("forwarding-noreply@google.com", "(#123456789) Gmail Forwarding Confirmation", "Click https://evil.example/confirm");
        Assert.NotNull(c);
        Assert.Equal("123456789", c!.Code);
        Assert.Null(c.Link);
    }

    [Fact]
    public async Task An_unrelated_email_is_recorded_but_creates_nothing()
    {
        var rig = new CompanionRig();
        var alice = await rig.Engine.ForwardAddressAsync("alice", default);

        var result = await rig.Engine.ImportForwardedAsync(Email(alice.Token, "e4", "Alice Example <alice.example@gmail.com>", "Fwd: photos from the weekend",
            "Here are the pictures from the cabin trip. See you on Friday!", rig.Now), default);

        Assert.Equal(ForwardOutcome.NotFinancial, result.Outcome);
        Assert.Empty(rig.Store.Items);
    }

    [Fact]
    public async Task Deleting_all_data_removes_the_forwarding_address()
    {
        var rig = new CompanionRig();
        var alice = await rig.Engine.ForwardAddressAsync("alice", default);

        await rig.Service.DeleteAllDataAsync("alice", default);

        Assert.Null(await rig.Store.FindForwardAsync(alice.Token, default));
    }

    [Fact]
    public void Hand_forwards_are_told_apart_from_automatic_ones()
    {
        Assert.True(BillForwarding.IsManualForward("Fwd: Lasku", ""));
        Assert.True(BillForwarding.IsManualForward("VS: Lasku", ""));
        Assert.True(BillForwarding.IsManualForward("Lasku", "---------- Välitetty viesti ----------\nLähettäjä: x"));
        Assert.False(BillForwarding.IsManualForward("Lasku 2026-55120", "Hei, tässä laskusi."));
    }
}
