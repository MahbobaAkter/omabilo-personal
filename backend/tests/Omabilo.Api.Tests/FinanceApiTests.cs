using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omabilo.Api.Finance;
using Xunit;

public class FinanceApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<T> Read<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task<decimal> Balance(HttpClient c) =>
        (await Read<JsonElement>(await c.GetAsync("/api/finance/overview"))).GetProperty("balance").GetDecimal();

    [Fact]
    public async Task Finance_routes_need_sign_in()
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/finance/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/finance/payday-plan")).StatusCode);
    }

    [Fact]
    public async Task A_new_user_starts_with_labelled_demo_data()
    {
        using var f = new ApiFactory();
        var o = await Read<JsonElement>(await f.As("demo-alice").GetAsync("/api/finance/overview"));

        Assert.True(o.GetProperty("isDemoData").GetBoolean());
        Assert.Equal(FinnishDemoSeed.BalanceToday, o.GetProperty("balance").GetDecimal());
        Assert.True(o.GetProperty("cashflow").GetArrayLength() >= 3);
        Assert.True(o.GetProperty("debtTotal").GetDecimal() > 0);
    }

    [Fact]
    public async Task Paying_an_invoice_records_the_payment_and_unpaying_removes_it()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var before = await Balance(alice);

        var created = await Read<IngestResult>(await alice.PostAsJsonAsync("/api/invoices",
            new { provider = "Kallio Dental (fictional)", amount = 96m, dueDate = DateTime.UtcNow.Date }));
        var id = created.InvoiceId!;

        var unpaidPlan = await Read<PaydayPlan>(await alice.GetAsync("/api/finance/payday-plan"));
        Assert.Contains(unpaidPlan.Lines, l => l.Label.StartsWith("Kallio Dental") && l.Certainty == Certainty.Confirmed);

        await Read<CanonicalInvoice>(await alice.PostAsync($"/api/invoices/{id}/pay", null));
        Assert.Equal(before - 96m, await Balance(alice));
        var paidPlan = await Read<PaydayPlan>(await alice.GetAsync("/api/finance/payday-plan"));
        Assert.DoesNotContain(paidPlan.Lines, l => l.Label.StartsWith("Kallio Dental"));

        // Paying twice must not take the money twice
        await Read<CanonicalInvoice>(await alice.PostAsync($"/api/invoices/{id}/pay", null));
        Assert.Equal(before - 96m, await Balance(alice));

        await Read<CanonicalInvoice>(await alice.PostAsync($"/api/invoices/{id}/unpay", null));
        Assert.Equal(before, await Balance(alice));
    }

    [Fact]
    public async Task Payday_plan_rejects_dates_after_payday()
    {
        using var f = new ApiFactory();
        var r = await f.As("demo-alice").GetAsync($"/api/finance/payday-plan?until={DateTime.UtcNow.Date.AddDays(45):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("day before payday", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Confirming_expected_income_adds_it_to_the_plan()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var before = await Read<PaydayPlan>(await alice.GetAsync("/api/finance/payday-plan"));
        var unconfirmed = (await Read<List<ExpectedIncome>>(await alice.GetAsync("/api/finance/expected-income"))).Single(i => !i.Confirmed);

        await Read<ExpectedIncome>(await alice.PatchAsJsonAsync($"/api/finance/expected-income/{unconfirmed.Id}", new { confirmed = true }));
        var after = await Read<PaydayPlan>(await alice.GetAsync("/api/finance/payday-plan"));

        var inPeriod = unconfirmed.Date.Date <= before.PlanUntil;
        Assert.Equal(before.ConfirmedIncome + (inPeriod ? unconfirmed.Amount : 0), after.ConfirmedIncome);
    }

    [Fact]
    public async Task Goal_contributions_move_money_and_a_goal_with_savings_cannot_be_deleted()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var start = await Balance(alice);

        var goal = await Read<SavingsGoalRecord>(await alice.PostAsJsonAsync("/api/finance/goals", new { name = "Sauna weekend", target = 300m }));
        var after = await Read<SavingsGoalRecord>(await alice.PostAsJsonAsync($"/api/finance/goals/{goal.Id}/contributions", new { amount = 50m }));
        Assert.Equal(50m, after.Saved);
        Assert.Equal(start - 50m, await Balance(alice));

        Assert.Equal(HttpStatusCode.Conflict, (await alice.DeleteAsync($"/api/finance/goals/{goal.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/finance/goals/{goal.Id}/contributions", new { amount = -80m })).StatusCode);

        await Read<SavingsGoalRecord>(await alice.PostAsJsonAsync($"/api/finance/goals/{goal.Id}/contributions", new { amount = -50m }));
        Assert.Equal(start, await Balance(alice));
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/finance/goals/{goal.Id}")).StatusCode);
    }

    [Fact]
    public async Task Debt_payments_lower_the_loan_and_the_account_and_cannot_overpay()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var start = await Balance(alice);
        var card = (await Read<List<Loan>>(await alice.GetAsync("/api/finance/debts"))).Single(l => l.Id == "loan-card");

        var paid = await Read<Loan>(await alice.PostAsJsonAsync("/api/finance/debts/loan-card/payments", new { amount = 100m }));
        Assert.Equal(card.Balance - 100m, paid.Balance);
        Assert.Equal(start - 100m, await Balance(alice));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/finance/debts/loan-card/payments", new { amount = 999999m })).StatusCode);

        var plan = await Read<JsonElement>(await alice.GetAsync("/api/finance/debts/plan?strategy=snowball&extra=100"));
        Assert.True(plan.GetProperty("months").GetInt32() > 0);
    }

    [Fact]
    public async Task One_user_cannot_see_or_change_another_users_finances()
    {
        using var f = new ApiFactory();
        var bobGoal = await Read<SavingsGoalRecord>(await f.As("demo-bob").PostAsJsonAsync("/api/finance/goals", new { name = "Bob's goal", target = 100m }));
        var alice = f.As("demo-alice");

        var aliceGoals = await Read<List<SavingsGoalRecord>>(await alice.GetAsync("/api/finance/goals"));
        Assert.DoesNotContain(aliceGoals, g => g.Id == bobGoal.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PatchAsJsonAsync($"/api/finance/goals/{bobGoal.Id}", new { name = "Taken" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/finance/goals/{bobGoal.Id}/contributions", new { amount = 10m })).StatusCode);
    }

    [Fact]
    public async Task Reports_and_settings_validate_input()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/finance/reports/2026-13")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/finance/reports/{DateTime.UtcNow.AddMonths(2):yyyy-MM}")).StatusCode);
        var months = await Read<List<string>>(await alice.GetAsync("/api/finance/reports"));
        Assert.True(months.Count >= 4);
        var report = await Read<MonthReport>(await alice.GetAsync($"/api/finance/reports/{months[1]}"));
        Assert.False(report.IsPartialMonth);

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync("/api/finance/settings", new { payDay = 31 })).StatusCode);
        var s = await Read<FinanceSettings>(await alice.PutAsJsonAsync("/api/finance/settings", new { discretionaryPerDay = 25m }));
        Assert.Equal(25m, s.DiscretionaryPerDay);
    }

    [Fact]
    public async Task Reset_needs_confirmation()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        await alice.PostAsJsonAsync("/api/finance/goals", new { name = "Temp", target = 10m });

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync("/api/finance/reset-demo", null)).StatusCode);
        await Read<JsonElement>(await alice.PostAsync("/api/finance/reset-demo?confirm=reset-demo-data", null));
        Assert.DoesNotContain(await Read<List<SavingsGoalRecord>>(await alice.GetAsync("/api/finance/goals")), g => g.Name == "Temp");
    }
}
