using System.Globalization;
using System.Security.Claims;
using Omabilo.Api.Auth;
using Omabilo.Api.Finance;
using Omabilo.Api.Storage;

namespace Omabilo.Api.Endpoints;

public record SettingsInput(decimal? DiscretionaryPerDay, int? PayDay, decimal? NetSalary, string? DisplayName);

public record AmountInput(decimal Amount);

public record GoalInput(string? Name, decimal? Target, DateTime? TargetDate);

public record ConfirmInput(bool Confirmed);

/// <summary>Per-user finance routes. Every route reads the owner from the validated sign-in.</summary>
public static class FinanceEndpoints
{
    public static void MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/finance").RequireAuthorization().WithTags("Finance");

        g.MapGet("/overview", async (ClaimsPrincipal user, IFinanceStore finance, IInvoiceStore invoiceStore, TimeProvider clock, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            var today = clock.GetUtcNow().UtcDateTime.Date;
            var d = await finance.LoadAsync(owner, ct);
            var invoices = (await invoiceStore.LoadAsync(owner, ct)).InvoicesOf(owner);
            var plan = FinanceCalculator.PlanUntilPayday(d, invoices, today);
            var thisMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var firstMonth = new DateTime(d.OpeningDate.Year, d.OpeningDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var open = invoices.Where(i => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid).ToList();

            return new
            {
                d.Settings.IsDemoData,
                d.Settings.DisplayName,
                today,
                balance = plan.StartingBalance,
                nextPayday = plan.NextPayday,
                daysToPayday = (plan.NextPayday - today).Days,
                plan = new { plan.ProjectedBalance, plan.SafeToSpendPerDay, plan.PlanUntil },
                invoices = new
                {
                    toReview = invoices.Count(i => i.State == InvoiceProcessingState.NeedsReview),
                    unpaidCount = open.Count,
                    unpaidTotal = open.Sum(i => i.Amount),
                    overdueCount = open.Count(i => i.DueDate.Date < today),
                },
                upcoming = FinanceCalculator.Upcoming(d, invoices, today, 14),
                spentThisMonth = FinanceCalculator.Totals(d, thisMonth, today).Spending,
                incomeLastMonth = FinanceCalculator.Totals(d, thisMonth.AddMonths(-1)).Income,
                debtTotal = d.Loans.Sum(l => l.Balance),
                debtMinimums = d.Loans.Where(l => l.Balance > 0).Sum(l => l.MinPayment),
                goals = new { saved = d.Goals.Sum(x => x.Saved), target = d.Goals.Sum(x => x.Target), count = d.Goals.Count },
                cashflow = Enumerable.Range(0, 12).Select(i => firstMonth.AddMonths(i)).Where(m => m < thisMonth)
                    .Select(m => FinanceCalculator.Totals(d, m)).ToList(),
            };
        });

        g.MapGet("/payday-plan", async (DateTime? until, ClaimsPrincipal user, IFinanceStore finance, IInvoiceStore invoiceStore, TimeProvider clock, CancellationToken ct) =>
        {
            var owner = user.OwnerId();
            var d = await finance.LoadAsync(owner, ct);
            var invoices = (await invoiceStore.LoadAsync(owner, ct)).InvoicesOf(owner);

            try
            {
                return Results.Ok(FinanceCalculator.PlanUntilPayday(d, invoices, clock.GetUtcNow().UtcDateTime, until));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message.Split(" (Parameter")[0] });
            }
        });

        g.MapGet("/settings", async (ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
            (await finance.LoadAsync(user.OwnerId(), ct)).Settings);

        g.MapPut("/settings", async (SettingsInput input, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (input.DiscretionaryPerDay is < 0 or > 500) errors["discretionaryPerDay"] = ["Between 0 and 500 € a day."];
            if (input.PayDay is < 1 or > 28) errors["payDay"] = ["A day from 1 to 28."];
            if (input.NetSalary is < 0 or > 100_000) errors["netSalary"] = ["Between 0 and 100 000 €."];
            if (input.DisplayName?.Length > 40) errors["displayName"] = ["Up to 40 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            return Results.Ok(await finance.UpdateAsync(user.OwnerId(), d =>
            {
                var s = d.Settings with
                {
                    DiscretionaryPerDay = input.DiscretionaryPerDay ?? d.Settings.DiscretionaryPerDay,
                    PayDay = input.PayDay ?? d.Settings.PayDay,
                    NetSalary = input.NetSalary ?? d.Settings.NetSalary,
                    DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? d.Settings.DisplayName : input.DisplayName.Trim(),
                };
                var recurring = d.Recurring.Select(r => r.Id == "rec-salary" ? r with { Amount = s.NetSalary, DayOfMonth = s.PayDay } : r).ToList();
                return (d with { Settings = s, Recurring = recurring }, s);
            }, ct));
        });

        g.MapGet("/transactions", async (string? month, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);

            if (month == null)
            {
                return Results.Ok(d.Transactions.OrderByDescending(t => t.Date).Take(100));
            }

            return TryMonth(month, out var start)
                ? Results.Ok(d.Transactions.Where(t => t.Date >= start && t.Date < start.AddMonths(1)).OrderByDescending(t => t.Date))
                : Results.BadRequest(new { error = "Use a month like 2026-09." });
        });

        g.MapGet("/reports", async (ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            return d.Transactions.Select(t => t.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)).Distinct().OrderDescending().ToList();
        });

        g.MapGet("/reports/{month}", async (string month, ClaimsPrincipal user, IFinanceStore finance, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryMonth(month, out var start))
            {
                return Results.BadRequest(new { error = "Use a month like 2026-09." });
            }

            var today = clock.GetUtcNow().UtcDateTime.Date;

            if (start > today)
            {
                return Results.BadRequest(new { error = "That month hasn't started yet." });
            }

            return Results.Ok(FinanceCalculator.Report(await finance.LoadAsync(user.OwnerId(), ct), start, today));
        });

        // ── Debts ───────────────────────────────────────────────────────────
        g.MapGet("/debts", async (ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
            (await finance.LoadAsync(user.OwnerId(), ct)).Loans);

        g.MapGet("/debts/plan", async (string? strategy, decimal? extra, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            var s = strategy is "snowball" ? "snowball" : "avalanche";
            var x = Math.Clamp(extra ?? 0, 0, 5000);
            var plan = Fin.Simulate(d.Loans.Select(FinanceCalculator.ToDebtAccount), x, s);
            var minimumsOnly = Fin.Simulate(d.Loans.Select(FinanceCalculator.ToDebtAccount), 0, s);
            return new
            {
                strategy = s,
                extraPerMonth = x,
                plan.Months,
                plan.NeverPaysOff,
                totalInterest = Math.Round(plan.TotalInterest, 2),
                interestSavedVsMinimums = minimumsOnly.NeverPaysOff ? (decimal?)null : Math.Round(Math.Max(0, minimumsOnly.TotalInterest - plan.TotalInterest), 2),
                payoffOrder = plan.Order.Select(o => new { debt = o.Name, month = o.Month }),
                remainingByMonth = plan.Remaining.Select(r => Math.Round(r, 2)),
                minimumsOnlyByMonth = minimumsOnly.Remaining.Select(r => Math.Round(r, 2)),
            };
        });

        g.MapPost("/debts/{id}/payments", async (string id, AmountInput input, ClaimsPrincipal user, IFinanceStore finance, TimeProvider clock, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            var loan = d.Loans.FirstOrDefault(l => l.Id == id);

            if (loan == null) return Results.NotFound();
            if (input.Amount <= 0 || input.Amount > loan.Balance) return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [$"Enter an amount from 0.01 to {loan.Balance:0.00} €."] });

            return Results.Ok(await finance.UpdateAsync(user.OwnerId(), data =>
            {
                // Re-read inside the update so two payments at once can't overwrite each other
                var current = data.Loans.First(l => l.Id == id);
                var paid = Math.Min(input.Amount, current.Balance);
                var txn = new FinanceTransaction($"txn-{Guid.NewGuid():N}"[..16], clock.GetUtcNow().UtcDateTime.Date, $"{current.Name} payment", "Debt payments", -paid, LinkedId: current.Id);
                var updated = current with { Balance = current.Balance - paid };
                return (data with
                {
                    Loans = data.Loans.Select(l => l.Id == id ? updated : l).ToList(),
                    Transactions = [.. data.Transactions, txn],
                }, updated);
            }, ct));
        });

        // ── Goals ───────────────────────────────────────────────────────────
        g.MapGet("/goals", async (ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
            (await finance.LoadAsync(user.OwnerId(), ct)).Goals);

        g.MapPost("/goals", async (GoalInput input, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            if (ValidateGoal(input, creating: true) is { } problem) return problem;
            var goal = new SavingsGoalRecord($"goal-{Guid.NewGuid():N}"[..17], input.Name!.Trim(), input.Target!.Value, 0, input.TargetDate?.Date, false);
            return Results.Ok(await finance.UpdateAsync(user.OwnerId(), d => (d with { Goals = [.. d.Goals, goal] }, goal), ct));
        });

        g.MapPatch("/goals/{id}", async (string id, GoalInput input, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            if (ValidateGoal(input, creating: false) is { } problem) return problem;
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            if (d.Goals.All(x => x.Id != id)) return Results.NotFound();

            return Results.Ok(await finance.UpdateAsync(user.OwnerId(), data =>
            {
                var goal = data.Goals.First(x => x.Id == id);
                var next = goal with
                {
                    Name = string.IsNullOrWhiteSpace(input.Name) ? goal.Name : input.Name.Trim(),
                    Target = input.Target ?? goal.Target,
                    TargetDate = input.TargetDate?.Date ?? goal.TargetDate,
                };
                return (data with { Goals = data.Goals.Select(x => x.Id == id ? next : x).ToList() }, next);
            }, ct));
        });

        g.MapDelete("/goals/{id}", async (string id, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            var goal = d.Goals.FirstOrDefault(x => x.Id == id);
            if (goal == null) return Results.NotFound();
            if (goal.Saved > 0) return Results.Conflict(new { error = $"Withdraw the {goal.Saved:0.00} € saved first, so the money isn't lost from your records." });

            await finance.UpdateAsync(user.OwnerId(), data => (data with { Goals = data.Goals.Where(x => x.Id != id).ToList() }, true), ct);
            return Results.NoContent();
        });

        g.MapPost("/goals/{id}/contributions", async (string id, AmountInput input, ClaimsPrincipal user, IFinanceStore finance, TimeProvider clock, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            var goal = d.Goals.FirstOrDefault(x => x.Id == id);
            if (goal == null) return Results.NotFound();
            if (input.Amount == 0 || input.Amount > 100_000 || goal.Saved + input.Amount < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = [$"Add an amount, or withdraw up to {goal.Saved:0.00} €."] });
            }

            return Results.Ok(await finance.UpdateAsync(user.OwnerId(), data =>
            {
                var current = data.Goals.First(x => x.Id == id);
                var amount = Math.Max(input.Amount, -current.Saved);
                var txn = new FinanceTransaction($"txn-{Guid.NewGuid():N}"[..16], clock.GetUtcNow().UtcDateTime.Date,
                    amount > 0 ? $"Transfer to {current.Name}" : $"Withdrawal from {current.Name}", "Savings", -amount, LinkedId: current.Id);
                var next = current with { Saved = current.Saved + amount };
                return (data with
                {
                    Goals = data.Goals.Select(x => x.Id == id ? next : x).ToList(),
                    Transactions = [.. data.Transactions, txn],
                }, next);
            }, ct));
        });

        // ── Expected income: only the person can confirm it ─────────────────
        g.MapGet("/expected-income", async (ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
            (await finance.LoadAsync(user.OwnerId(), ct)).ExpectedIncome);

        g.MapPatch("/expected-income/{id}", async (string id, ConfirmInput input, ClaimsPrincipal user, IFinanceStore finance, CancellationToken ct) =>
        {
            var d = await finance.LoadAsync(user.OwnerId(), ct);
            if (d.ExpectedIncome.All(x => x.Id != id)) return Results.NotFound();

            return Results.Ok(await finance.UpdateAsync(user.OwnerId(), data =>
            {
                var next = data.ExpectedIncome.First(x => x.Id == id) with { Confirmed = input.Confirmed };
                return (data with { ExpectedIncome = data.ExpectedIncome.Select(x => x.Id == id ? next : x).ToList() }, next);
            }, ct));
        });

        g.MapPost("/reset-demo", async (string? confirm, ClaimsPrincipal user, IFinanceStore finance, TimeProvider clock, CancellationToken ct) =>
        {
            if (confirm != "reset-demo-data")
            {
                return Results.BadRequest(new { error = "Add ?confirm=reset-demo-data to replace your finance records with fresh demo data." });
            }

            var owner = user.OwnerId();
            await finance.UpdateAsync(owner, _ => (FinnishDemoSeed.Create(owner, clock.GetUtcNow().UtcDateTime), true), ct);
            return Results.Ok(new { reset = true });
        });
    }

    /// <summary>Keeps the account in step with invoice payments: paying records an outgoing
    /// transaction linked to the invoice, and un-paying removes it.</summary>
    public static async Task SyncInvoicePaymentAsync(IFinanceStore finance, string owner, CanonicalInvoice invoice, DateTime today, CancellationToken ct)
    {
        await finance.UpdateAsync(owner, d =>
        {
            var without = d.Transactions.Where(t => t.LinkedId != invoice.Id).ToList();

            if (invoice.PaymentStatus == InvoicePaymentStatus.Paid)
            {
                without.Add(new FinanceTransaction($"txn-{Guid.NewGuid():N}"[..16], today, $"{invoice.Provider} invoice", "Bills", -invoice.Amount, LinkedId: invoice.Id));
            }

            return (d with { Transactions = without }, true);
        }, ct);
    }

    private static bool TryMonth(string month, out DateTime start)
    {
        var ok = DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start);
        start = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return ok;
    }

    private static IResult? ValidateGoal(GoalInput input, bool creating)
    {
        var errors = new Dictionary<string, string[]>();
        if (creating && string.IsNullOrWhiteSpace(input.Name) || input.Name?.Length > 60) errors["name"] = ["Give the goal a name (up to 60 characters)."];
        if (creating && input.Target is null || input.Target is <= 0 or > 1_000_000) errors["target"] = ["Enter a target above zero."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }
}
