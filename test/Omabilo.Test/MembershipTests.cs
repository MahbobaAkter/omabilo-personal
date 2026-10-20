using Xunit;

/// <summary>Free and Premium: what each plan holds, complimentary Premium, and that the free plan
/// never sends a bill to OCR or AI. Fictional data only.</summary>
public class MembershipTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01];

    [Fact]
    public void Premium_is_299_cents_a_month_and_every_premium_feature_is_described()
    {
        Assert.Equal("premium", Membership.PremiumOfferId);
        Assert.Equal(299, Membership.PremiumPriceMinor);
        Assert.Equal(Enum.GetValues<PremiumFeature>().Length, Membership.PremiumAdds.Distinct().Count());
        Assert.All(Membership.PremiumAdds, f => Assert.NotEqual(f.ToString(), Membership.Describe(f)));
        Assert.Contains(Membership.FreeIncludes, line => line.Contains("PDF"));
    }

    [Fact]
    public void A_month_bought_on_19_September_is_paid_through_18_October_and_renews_on_19_October()
    {
        var periodEnd = new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 10, 18), Membership.LastPaidDay(periodEnd));
    }

    [Theory]
    [InlineData("tester@example.com", "owner@example.com, Tester@Example.com", true)]
    [InlineData(" tester@example.com ", "owner@example.com;tester@example.com", true)]
    [InlineData("tester@example.com", "owner@example.com", false)]
    [InlineData("tester@example.com", "", false)]
    [InlineData(null, "tester@example.com", false)]
    [InlineData("example.com", "tester@example.com", false)]
    public void Complimentary_premium_needs_an_exact_email_match(string? email, string? list, bool expected)
    {
        Assert.Equal(expected, Membership.IsComplimentary(email, list));
    }

    [Fact]
    public async Task On_the_free_plan_a_photo_is_not_sent_to_OCR_even_if_AI_reading_was_turned_on_before()
    {
        var ocr = new FakeOcr("Kuura Energia Oy\nInvoice number: 2026-77\nTotal to pay 58,40 EUR");
        var rig = new CompanionRig(ocr: ocr);
        await rig.Store.SaveSettingsAsync("alice", new CompanionSettings { AiEnabled = true, AiConsentUtc = rig.Now }, default);

        var result = await rig.Engine.ImportUploadAsync("alice", "lasku.jpg", Jpeg, default, allowAi: false);

        Assert.Equal((UploadOutcome.Unreadable, AttachmentStatus.NotSupported), (result.Outcome, result.Unread));
        Assert.Equal(0, ocr.Calls);
        Assert.Empty(rig.Store.Items);

        // The person's saved choice is left as it was
        Assert.True((await rig.Store.GetSettingsAsync("alice", default)).AiEnabled);
    }

    [Fact]
    public async Task Gmail_syncs_free_for_30_days_from_the_first_connection_then_only_on_Premium()
    {
        var rig = new CompanionRig { PremiumUsers = [] };
        var start = rig.Now;
        rig.Google.Add("m1", "Kuura Energia <laskut@kuura.example>", "Lasku 2026-77", $"Summa 58,40 EUR\nEräpäivä {start.AddDays(10):d.M.yyyy}", start.AddHours(-1));

        await rig.ConnectAsync("alice");
        Assert.Equal(start, (await rig.Store.GetSettingsAsync("alice", default)).GmailTrialStartUtc);
        Assert.True((await rig.Engine.SyncAsync("alice", default)).Ok);

        // Day 29: still free
        rig.Now = start.AddDays(29);
        Assert.True((await rig.Engine.SyncAsync("alice", default)).Ok);

        // Disconnecting and connecting again doesn't restart the trial
        await rig.Engine.DisconnectAsync("alice", default);
        await rig.ConnectAsync("alice");
        Assert.Equal(start, (await rig.Store.GetSettingsAsync("alice", default)).GmailTrialStartUtc);

        // Day 30: paused, nothing is fetched from Google, and the mailbox waits an hour before the next check
        rig.Now = start.AddDays(30);
        var fetches = (rig.Google.FullFetches, rig.Google.MetadataFetches);
        var paused = await rig.Engine.SyncAsync("alice", default);
        Assert.Equal((false, "trial_ended"), (paused.Ok, paused.Code));
        Assert.Equal(fetches, (rig.Google.FullFetches, rig.Google.MetadataFetches));
        Assert.Equal(rig.Now.AddHours(1), rig.Store.Connections["alice"].NextAttemptUtc);
        Assert.NotEmpty(rig.Store.Items);

        // With Premium it syncs again
        rig.PremiumUsers.Add("alice");
        Assert.True((await rig.Engine.SyncAsync("alice", default)).Ok);
    }

    [Theory]
    [InlineData("NOVA", null, true)]
    [InlineData(" nova ", null, true)]
    [InlineData("NOV", null, false)]
    [InlineData("NOVA2", null, false)]
    [InlineData("", null, false)]
    [InlineData(null, null, false)]
    [InlineData("NOVA", "ORION", false)]
    [InlineData("orion", "ORION", true)]
    [InlineData("NOVA", "  ", true)]
    public void The_access_code_must_match_exactly_ignoring_case(string? entered, string? configured, bool expected) =>
        Assert.Equal(expected, Membership.IsAccessCode(entered, configured));

    [Fact]
    public async Task The_access_code_keeps_Gmail_syncing_after_the_trial_until_the_person_returns_to_free()
    {
        var rig = new CompanionRig { PremiumUsers = [] };
        var start = rig.Now;
        rig.Google.Add("m1", "Kuura Energia <laskut@kuura.example>", "Lasku 2026-77", $"Summa 58,40 EUR\nEräpäivä {start.AddDays(10):d.M.yyyy}", start.AddHours(-1));
        await rig.ConnectAsync("alice");
        rig.Now = start.AddDays(31);
        Assert.Equal("trial_ended", (await rig.Engine.SyncAsync("alice", default)).Code);

        await rig.Service.SetAccessCodeAsync("alice", true, default);
        await rig.Service.SetAccessCodeAsync("alice", true, default);
        rig.Store.Connections["alice"] = rig.Store.Connections["alice"] with { NextAttemptUtc = rig.Now };

        Assert.True((await rig.Engine.SyncAsync("alice", default)).Ok);
        Assert.Equal(rig.Now, (await rig.Store.GetSettingsAsync("alice", default)).AccessCodeUtc);
        Assert.Single(rig.Store.Audit, a => a.Entry.Action == "access_code_redeemed");

        await rig.Service.SetAccessCodeAsync("alice", false, default);
        Assert.Null((await rig.Store.GetSettingsAsync("alice", default)).AccessCodeUtc);
        Assert.Equal("trial_ended", (await rig.Engine.SyncAsync("alice", default)).Code);
        Assert.Contains(rig.Store.Audit, a => a.Entry.Action == "access_code_removed");
    }

    [Fact]
    public void A_connection_made_before_the_trial_existed_counts_from_its_connection_day()
    {
        var connected = new DateTime(2026, 10, 17, 16, 47, 0, DateTimeKind.Utc);
        var c = new GmailConnection { UserId = "alice", GoogleEmail = "a@example.com", ConnectedUtc = connected };

        Assert.Equal(connected.AddDays(30), Membership.GmailTrialEnds(new CompanionSettings(), c));
        Assert.True(Membership.GmailFreeNow(new CompanionSettings(), c, connected.AddDays(29)));
        Assert.False(Membership.GmailFreeNow(new CompanionSettings(), c, connected.AddDays(30)));
        Assert.True(Membership.GmailFreeNow(new CompanionSettings(), null, connected.AddDays(400)));
    }

    [Fact]
    public async Task On_the_free_plan_pasted_text_is_read_by_rules_without_AI()
    {
        var ai = new FakeAi();
        var rig = new CompanionRig(ai: ai);
        await rig.Store.SaveSettingsAsync("alice", new CompanionSettings { AiEnabled = true, AiConsentUtc = rig.Now }, default);
        var text = $"Revontuli Energy Oy\nInvoice number: 2026-881\nAmount due 64,20 EUR\nDue date: {rig.Now.Date.AddDays(9):d.M.yyyy}\nReference: 1234561";

        var result = await rig.Engine.ImportPastedTextAsync("alice", text, default, allowAi: false);

        Assert.Equal(UploadOutcome.Filed, result.Outcome);
        Assert.Equal(0, ai.Calls);
        Assert.Null(rig.Store.Items[result.ItemId!].Ai);
        Assert.Equal(64.20m, rig.Store.Items[result.ItemId!].Amount);
    }
}
