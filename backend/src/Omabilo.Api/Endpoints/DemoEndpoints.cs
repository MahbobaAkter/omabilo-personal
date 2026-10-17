using System.Globalization;

namespace Omabilo.Api.Endpoints;

/// <summary>
/// Read-only FICTIONAL data for debts and monthly reports, from the same DemoData the IKON
/// app starts with. These are not per-user records yet: the IKON app keeps debts, goals and
/// transactions in IKON persistent state, which has no standalone replacement in this phase.
/// </summary>
public static class DemoEndpoints
{
    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/api/demo").RequireAuthorization().WithTags("Demo data (fictional, read-only)");

        demo.MapGet("/debts", () => DemoData.Debts());

        demo.MapGet("/debts/plan", (string? strategy, decimal? extra) =>
        {
            var s = strategy is "snowball" ? "snowball" : "avalanche";
            var plan = Fin.Simulate(DemoData.Debts(), Math.Clamp(extra ?? 100, 0, 5000), s);
            return new
            {
                strategy = s,
                extraPerMonth = Math.Clamp(extra ?? 100, 0, 5000),
                months = plan.Months,
                neverPaysOff = plan.NeverPaysOff,
                totalInterest = Math.Round(plan.TotalInterest, 2),
                payoffOrder = plan.Order.Select(o => new { debt = o.Name, month = o.Month }),
                remainingByMonth = plan.Remaining.Select(r => Math.Round(r, 2)),
            };
        });

        demo.MapGet("/reports/{month}", (string month) =>
        {
            if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            {
                return Results.BadRequest(new { error = "Use a month like 2026-09." });
            }

            start = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var txns = DemoData.Transactions().Where(t => t.Date >= start && t.Date < start.AddMonths(1)).ToList();
            var income = txns.Where(t => t.Amount > 0).Sum(t => t.Amount);
            var spending = -txns.Where(t => t.Amount < 0 && t.Category != "Savings").Sum(t => t.Amount);

            return Results.Ok(new
            {
                month,
                income,
                spending,
                saved = -txns.Where(t => t.Amount < 0 && t.Category == "Savings").Sum(t => t.Amount),
                keptPercent = income > 0 ? Math.Round((income - spending) / income * 100, 1) : 0,
                byCategory = txns.Where(t => t.Amount < 0 && t.Category != "Savings")
                    .GroupBy(t => t.Category)
                    .Select(g => new { category = g.Key, amount = -g.Sum(t => t.Amount) })
                    .OrderByDescending(x => x.amount),
                transactionCount = txns.Count,
            });
        });
    }
}
