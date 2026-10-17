using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Omabilo.Api.Endpoints;
using Xunit;

/// <summary>Starts the API in memory (Development, demo sign-in, in-memory storage) and calls it over HTTP.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:Provider", "InMemory");
        builder.UseSetting("Auth:Mode", "Demo");
    }

    public HttpClient As(string demoUser)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", demoUser);
        return client;
    }
}

public class ApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<T> Read<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static object Invoice(string provider = "Kallio Dental (demo)", decimal amount = 96m) => new
    {
        provider,
        invoiceNumber = "KD-30418",
        reference = "12345672",
        dueDate = DateTime.UtcNow.Date.AddDays(8),
        amount,
    };

    [Fact]
    public async Task Health_needs_no_sign_in()
    {
        using var f = new ApiFactory();
        var r = await f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("alice")]
    [InlineData("demo-ALICE")]
    [InlineData("demo-alice' OR 1=1")]
    public async Task Invoices_refuse_missing_or_malformed_users(string? user)
    {
        using var f = new ApiFactory();
        var client = user == null ? f.CreateClient() : f.As(user);
        var r = await client.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Manual_invoice_is_added_and_listed()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");

        var result = await Read<IngestResult>(await alice.PostAsJsonAsync("/api/invoices", Invoice()));
        Assert.Equal(IngestOutcome.Created, result.Outcome);

        var list = await Read<List<CanonicalInvoice>>(await alice.GetAsync("/api/invoices"));
        var inv = Assert.Single(list);
        Assert.Equal("demo-alice", inv.OwnerId);
        Assert.True(inv.CountsInTotals);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_field_errors()
    {
        using var f = new ApiFactory();
        var r = await f.As("demo-alice").PostAsJsonAsync("/api/invoices", new { provider = "", amount = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("provider", body);
        Assert.Contains("amount", body);
        Assert.Contains("dueDate", body);
    }

    [Fact]
    public async Task One_user_cannot_see_or_change_another_users_invoice()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var bob = f.As("demo-bob");

        var created = await Read<IngestResult>(await alice.PostAsJsonAsync("/api/invoices", Invoice()));
        var id = created.InvoiceId!;

        Assert.Empty(await Read<List<CanonicalInvoice>>(await bob.GetAsync("/api/invoices")));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/invoices/{id}/pay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PatchAsJsonAsync($"/api/invoices/{id}", Invoice("Changed by bob"))).StatusCode);

        var still = await Read<InvoiceDetail>(await alice.GetAsync($"/api/invoices/{id}"));
        Assert.Equal(InvoicePaymentStatus.Unpaid, still.Invoice.PaymentStatus);
        Assert.Equal("Kallio Dental (demo)", still.Invoice.Provider);
    }

    [Fact]
    public async Task Simulated_email_is_idempotent_and_a_resent_copy_is_linked()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");

        var first = await Read<List<IngestResult>>(await alice.PostAsync("/api/invoices/simulate/email/email-energy", null));
        var again = await Read<List<IngestResult>>(await alice.PostAsync("/api/invoices/simulate/email/email-energy", null));
        var copy = await Read<List<IngestResult>>(await alice.PostAsync("/api/invoices/simulate/email/email-energy-again", null));

        Assert.Equal(IngestOutcome.Created, first.Single().Outcome);
        Assert.Equal(IngestOutcome.AlreadyIngested, again.Single().Outcome);
        Assert.Equal(IngestOutcome.LinkedToExisting, copy.Single().Outcome);

        var detail = await Read<InvoiceDetail>(await alice.GetAsync($"/api/invoices/{first.Single().InvoiceId}"));
        Assert.Equal(2, detail.Sources.Count);
        Assert.All(detail.Sources, s => Assert.True(s.IsSimulated));
        Assert.True(detail.ReferenceValid);
    }

    [Fact]
    public async Task Review_then_approve_then_pay_updates_the_summary()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var id = (await Read<List<IngestResult>>(await alice.PostAsync("/api/invoices/simulate/email/email-water", null))).Single().InvoiceId!;

        var before = await Read<JsonElement>(await alice.GetAsync("/api/invoices/summary"));
        Assert.Equal(1, before.GetProperty("toReview").GetInt32());
        Assert.Equal(0m, before.GetProperty("unpaidTotal").GetDecimal());

        await Read<CanonicalInvoice>(await alice.PostAsync($"/api/invoices/{id}/approve", null));
        var approved = await Read<JsonElement>(await alice.GetAsync("/api/invoices/summary"));
        Assert.Equal(1124.90m, approved.GetProperty("unpaidTotal").GetDecimal());

        await Read<CanonicalInvoice>(await alice.PostAsync($"/api/invoices/{id}/pay", null));
        var paid = await Read<JsonElement>(await alice.GetAsync("/api/invoices/summary"));
        Assert.Equal(0m, paid.GetProperty("unpaidTotal").GetDecimal());

        var history = (await Read<InvoiceDetail>(await alice.GetAsync($"/api/invoices/{id}"))).History;
        Assert.Contains(history, e => e.Action == "invoice.approved");
        Assert.Contains(history, e => e.Action == "invoice.paid");
    }

    [Fact]
    public async Task Unreadable_scan_is_reported_not_added()
    {
        using var f = new ApiFactory();
        var r = await Read<IngestResult>(await f.As("demo-alice").PostAsync("/api/invoices/simulate/document/doc-poor-scan", null));
        Assert.Equal(IngestOutcome.Invalid, r.Outcome);
        Assert.Contains("amount", r.Message);
    }

    [Fact]
    public async Task Export_contains_only_the_callers_data()
    {
        using var f = new ApiFactory();
        await f.As("demo-alice").PostAsJsonAsync("/api/invoices", Invoice("Alice Provider (demo)"));
        await f.As("demo-bob").PostAsJsonAsync("/api/invoices", Invoice("Bob Provider (demo)"));

        var r = await f.As("demo-alice").GetAsync("/api/invoices/export");
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("Alice Provider", text);
        Assert.DoesNotContain("Bob Provider", text);
        Assert.DoesNotContain("demo-bob", text);
    }

    [Fact]
    public async Task Delete_all_needs_explicit_confirmation_and_only_affects_the_caller()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");
        var bob = f.As("demo-bob");
        await alice.PostAsJsonAsync("/api/invoices", Invoice());
        await bob.PostAsJsonAsync("/api/invoices", Invoice());

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.DeleteAsync("/api/invoices")).StatusCode);
        Assert.Single(await Read<List<CanonicalInvoice>>(await alice.GetAsync("/api/invoices")));

        var deleted = await Read<JsonElement>(await alice.DeleteAsync("/api/invoices?confirm=delete-all-invoice-data"));
        Assert.Equal(1, deleted.GetProperty("deletedInvoices").GetInt32());
        Assert.Empty(await Read<List<CanonicalInvoice>>(await alice.GetAsync("/api/invoices")));
        Assert.Single(await Read<List<CanonicalInvoice>>(await bob.GetAsync("/api/invoices")));
    }

    [Fact]
    public async Task Source_catalog_marks_external_connectors_not_connected()
    {
        using var f = new ApiFactory();
        var catalog = await Read<JsonElement>(await f.As("demo-alice").GetAsync("/api/invoice-sources"));
        var gmail = catalog.EnumerateArray().Single(s => s.GetProperty("type").GetString() == "Gmail");
        Assert.Equal("NotConnected", gmail.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Demo_debt_plan_and_report_use_fictional_data()
    {
        using var f = new ApiFactory();
        var alice = f.As("demo-alice");

        var plan = await Read<JsonElement>(await alice.GetAsync("/api/demo/debts/plan?strategy=snowball&extra=150"));
        Assert.Equal("snowball", plan.GetProperty("strategy").GetString());
        Assert.True(plan.GetProperty("months").GetInt32() > 0);
        Assert.False(plan.GetProperty("neverPaysOff").GetBoolean());

        var lastMonth = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM");
        var report = await Read<JsonElement>(await alice.GetAsync($"/api/demo/reports/{lastMonth}"));
        Assert.Equal(2640m, report.GetProperty("income").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/demo/reports/not-a-month")).StatusCode);
    }
}

/// <summary>The server must refuse to start rather than run without real sign-in.</summary>
public class StartupSafetyTests
{
    private sealed class ProductionFactory(string? authMode) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:Provider", "InMemory");
            builder.UseSetting("Auth:Mode", authMode ?? "");
        }
    }

    [Fact]
    public void Production_without_an_identity_provider_does_not_start()
    {
        using var f = new ProductionFactory(null);
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Sign-in is not configured", ex.ToString());
    }

    [Fact]
    public void Demo_sign_in_is_refused_outside_development()
    {
        using var f = new ProductionFactory("Demo");
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("only in the Development environment", ex.ToString());
    }
}
