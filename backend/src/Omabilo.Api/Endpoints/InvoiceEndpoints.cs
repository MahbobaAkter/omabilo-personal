using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omabilo.Api.Auth;
using Omabilo.Api.Storage;

namespace Omabilo.Api.Endpoints;

/// <summary>Body for adding or editing an invoice by hand.</summary>
public record InvoiceInput(string? Provider, string? InvoiceNumber, string? Reference, DateTime? InvoiceDate, DateTime? DueDate, decimal? Amount, string? Currency);

public record MergeInput(string TargetInvoiceId);

public record InvoiceDetail(CanonicalInvoice Invoice, IReadOnlyList<InvoiceSourceRecord> Sources, IReadOnlyList<InvoiceAuditEvent> History, bool ReferenceValid);

public static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var invoices = app.MapGroup("/api/invoices").RequireAuthorization().WithTags("Invoices");

        invoices.MapGet("/", async (ClaimsPrincipal user, IInvoiceStore store, string? status, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            var book = await store.LoadAsync(owner, ct);
            return book.InvoicesOf(owner)
                .Where(i => status switch
                {
                    "unpaid" => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid,
                    "review" => i.State == InvoiceProcessingState.NeedsReview,
                    "paid" => i.PaymentStatus == InvoicePaymentStatus.Paid && i.State != InvoiceProcessingState.Merged,
                    "closed" => i.State is InvoiceProcessingState.Rejected or InvoiceProcessingState.Merged,
                    _ => true,
                })
                .OrderBy(i => i.DueDate)
                .ToList();
        });

        invoices.MapGet("/summary", async (ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            var book = await store.LoadAsync(owner, ct);
            var mine = book.InvoicesOf(owner);
            return new
            {
                toReview = mine.Count(i => i.State == InvoiceProcessingState.NeedsReview),
                unpaidTotal = book.OpenTotal(owner),
                unpaidCount = mine.Count(i => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid),
                total = mine.Count,
            };
        });

        invoices.MapGet("/{id}", async (string id, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            var book = await store.LoadAsync(owner, ct);
            var inv = book.InvoicesOf(owner).FirstOrDefault(i => i.Id == id);
            return inv == null
                ? Results.NotFound()
                : Results.Ok(new InvoiceDetail(inv, book.SourcesOf(owner, id), book.AuditOf(owner, id), inv.Reference != null && PaymentReference.IsValid(inv.Reference)));
        });

        invoices.MapPost("/", async (InvoiceInput input, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem)
            {
                return problem;
            }

            var fields = ToFields(input);
            var owner = user.OwnerId();
            var result = await store.UpdateAsync(owner, b => b.Ingest(owner, ManualInvoiceSource.Submit(fields, Guid.NewGuid().ToString("N")), DateTime.UtcNow), ct);
            return Results.Ok(result);
        });

        invoices.MapPost("/demo", async (ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            return await store.UpdateAsync(owner, b => SyntheticInvoices.DemoInvoices().Select(d => b.Ingest(owner, d, DateTime.UtcNow)).ToList(), ct);
        });

        // SIMULATED sources: fictional emails and scans from SyntheticInvoices, read by the real extractor
        invoices.MapPost("/simulate/email/{key}", async (string key, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            var sample = SyntheticInvoices.Emails().FirstOrDefault(e => e.Key == key);

            if (sample == null)
            {
                return Results.NotFound(new { error = $"No sample email '{key}'.", available = SyntheticInvoices.Emails().Select(e => e.Key) });
            }

            var submissions = await EmailInvoiceReader.ReadAsync(sample.Email, new TextInvoiceExtractor(), isSimulated: true, ct);
            var owner = user.OwnerId();
            return Results.Ok(await store.UpdateAsync(owner, b => submissions.Select(s => b.Ingest(owner, s, DateTime.UtcNow)).ToList(), ct));
        });

        invoices.MapPost("/simulate/document/{key}", async (string key, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            var doc = SyntheticInvoices.Documents().FirstOrDefault(d => d.Key == key);

            if (doc.Key == null)
            {
                return Results.NotFound(new { error = $"No sample document '{key}'.", available = SyntheticInvoices.Documents().Select(d => d.Key) });
            }

            var fields = await new TextInvoiceExtractor().ExtractAsync(doc.Document, ct);
            var submission = new InvoiceSubmission(InvoiceSourceType.Document, $"doc:{key}", true, fields, TextInvoiceExtractor.Overall(fields), $"Scanned “{doc.Document.FileName}”");
            var owner = user.OwnerId();
            return Results.Ok(await store.UpdateAsync(owner, b => b.Ingest(owner, submission, DateTime.UtcNow), ct));
        });

        MapAction(invoices, "approve", (b, o, id) => b.Approve(o, id, DateTime.UtcNow));
        MapAction(invoices, "reject", (b, o, id) => b.Reject(o, id, DateTime.UtcNow));
        // Paying (or un-paying) also records (or removes) the payment in the account history,
        // so the balance, payday plan and reports agree with the invoice
        foreach (var (action, paid) in new[] { ("pay", true), ("unpay", false) })
        {
            invoices.MapPost($"/{{id}}/{action}", async (string id, ClaimsPrincipal user, IInvoiceStore store, Omabilo.Api.Finance.IFinanceStore finance, TimeProvider clock, CancellationToken ct) =>
            {
                var owner = user.OwnerId();
                var now = clock.GetUtcNow().UtcDateTime;
                var result = await Guarded(() => store.UpdateAsync(owner, b => paid ? b.MarkPaid(owner, id, now) : b.MarkUnpaid(owner, id, now), ct));

                if (result is Microsoft.AspNetCore.Http.HttpResults.Ok<CanonicalInvoice> ok)
                {
                    await FinanceEndpoints.SyncInvoicePaymentAsync(finance, owner, ok.Value!, now.Date, ct);
                }

                return result;
            });
        }
        MapAction(invoices, "keep-separate", (b, o, id) => b.KeepSeparate(o, id, DateTime.UtcNow));

        invoices.MapPost("/{id}/merge", async (string id, MergeInput input, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
            await Guarded(() => store.UpdateAsync(user.OwnerId(), b => b.MergeInto(user.OwnerId(), id, input.TargetInvoiceId, DateTime.UtcNow), ct)));

        invoices.MapPatch("/{id}", async (string id, InvoiceInput input, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem)
            {
                return problem;
            }

            return await Guarded(() => store.UpdateAsync(user.OwnerId(), b => b.Edit(user.OwnerId(), id, ToFields(input), DateTime.UtcNow), ct));
        });

        invoices.MapGet("/export", async (ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            var book = await store.LoadAsync(owner, ct);
            var export = new
            {
                exportedAt = DateTime.UtcNow,
                invoices = book.InvoicesOf(owner),
                sources = book.AllSources.Where(s => s.OwnerId == owner),
                audit = book.AuditOf(owner),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
            return Results.File(bytes, "application/json", $"omabilo-invoices-{DateTime.UtcNow:yyyy-MM-dd}.json");
        });

        // Permanent: the caller must confirm explicitly so a stray request can't wipe data
        invoices.MapDelete("/", async (string? confirm, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
        {
            if (confirm != "delete-all-invoice-data")
            {
                return Results.BadRequest(new { error = "Add ?confirm=delete-all-invoice-data to permanently delete every invoice." });
            }

            var owner = user.OwnerId();
            var removed = await store.UpdateAsync(owner, b => b.DeleteAllFor(owner, DateTime.UtcNow), ct);
            return Results.Ok(new { deletedInvoices = removed });
        });

        app.MapGet("/api/invoice-sources", () => InvoiceSourceCatalog.All.Select(s => new { s.Type, s.Name, s.Availability, s.Status }))
            .RequireAuthorization().WithTags("Invoices");
    }

    private static void MapAction(RouteGroupBuilder group, string action, Func<InvoiceBook, string, string, CanonicalInvoice> apply) =>
        group.MapPost($"/{{id}}/{action}", async (string id, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct) =>
            await Guarded(() => store.UpdateAsync(user.OwnerId(), b => apply(b, user.OwnerId(), id), ct)));

    /// <summary>Someone else's invoice and a missing one both answer 404, so ids reveal nothing.</summary>
    private static async Task<IResult> Guarded<T>(Func<Task<T>> run)
    {
        try
        {
            return Results.Ok(await run());
        }
        catch (InvoiceAccessDeniedException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult? Validate(InvoiceInput input)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(input.Provider) || input.Provider.Length > 80) errors["provider"] = ["Enter who the invoice is from (up to 80 characters)."];
        if (input.Amount is not > 0 || input.Amount > 1_000_000) errors["amount"] = ["Enter an amount above zero."];
        if (input.DueDate is null) errors["dueDate"] = ["Enter a due date."];
        if (input.InvoiceNumber?.Length > 40) errors["invoiceNumber"] = ["Up to 40 characters."];
        if (input.Reference?.Length > 40) errors["reference"] = ["Up to 40 characters."];

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static ExtractedInvoiceFields ToFields(InvoiceInput i) =>
        new(i.Provider?.Trim(), i.InvoiceNumber, i.Reference, i.InvoiceDate, i.DueDate, i.Amount, string.IsNullOrWhiteSpace(i.Currency) ? "EUR" : i.Currency);
}
