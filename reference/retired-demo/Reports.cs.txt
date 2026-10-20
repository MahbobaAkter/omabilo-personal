public partial class OmabiloApp
{
    // Opens on the last complete month; the current one is a pick away
    private readonly ClientReactive<string> _reportMonth = new(Fin.MonthKey(DateTime.UtcNow.AddMonths(-1)));
    private readonly ClientReactive<string> _txnSearch = new("");
    private readonly ClientReactive<string> _txnCategory = new("all");
    private readonly ClientReactive<string> _txnSort = new("newest");
    private readonly ClientReactive<bool> _txnShowAll = new(false);

    private DateTime SelectedMonth()
    {
        var parts = _reportMonth.Value.Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], out var y) && int.TryParse(parts[1], out var m)
            ? new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc)
            : MonthStart(Today);
    }

    private Dictionary<string, decimal> SpendByCategory(DateTime month) =>
        TxnsIn(month).Where(t => t.Amount < 0 && t.Category != "Savings")
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => -g.Sum(t => t.Amount));

    private List<Txn> VisibleTxns(DateTime month)
    {
        IEnumerable<Txn> rows = TxnsIn(month);

        if (_txnCategory.Value != "all")
        {
            rows = rows.Where(t => t.Category == _txnCategory.Value);
        }

        var q = _txnSearch.Value.Trim();

        if (q.Length > 0)
        {
            rows = rows.Where(t => t.Merchant.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        rows = _txnSort.Value switch
        {
            "oldest" => rows.OrderBy(t => t.Date),
            "largest" => rows.OrderByDescending(t => Math.Abs(t.Amount)),
            _ => rows.OrderByDescending(t => t.Date),
        };

        return rows.ToList();
    }

    private static string BuildCsv(IEnumerable<Txn> rows)
    {
        static string Esc(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

        var sb = new StringBuilder("Date,Merchant,Category,Amount (EUR)\n");

        foreach (var t in rows)
        {
            sb.Append(t.Date.ToString("yyyy-MM-dd", Fin.Inv)).Append(',')
                .Append(Esc(t.Merchant)).Append(',')
                .Append(Esc(t.Category)).Append(',')
                .Append(t.Amount.ToString("0.00", Fin.Inv)).Append('\n');
        }

        return sb.ToString();
    }

    private void RenderReports(UIView view)
    {
        var month = SelectedMonth();
        var prev = month.AddMonths(-1);
        var isCurrent = month == MonthStart(Today);
        var income = IncomeIn(month);
        var spending = SpendingIn(month);
        var saved = SavedIn(month);
        var rate = income > 0 ? (double)((income - spending) / income) * 100 : 0;
        // A month in progress is compared with the same days of the month before
        var prevCutoff = isCurrent ? prev.AddDays(Today.Day) : prev.AddMonths(1);
        var prevTxns = _txns.Where(t => t.Date >= prev && t.Date < prevCutoff).ToList();
        var prevSpending = -prevTxns.Where(t => t.Amount < 0 && t.Category != "Savings").Sum(t => t.Amount);
        var prevIncome = prevTxns.Where(t => t.Amount > 0).Sum(t => t.Amount);
        var compareLabel = isCurrent ? "same days last month" : "last month";
        var byCat = SpendByCategory(month);
        var prevByCat = SpendByCategory(prev);

        var months = _txns.Select(t => MonthStart(t.Date)).Distinct().OrderByDescending(m => m).ToList();

        if (!months.Contains(MonthStart(Today)))
        {
            months.Insert(0, MonthStart(Today));
        }

        SectionHeader(view, isCurrent ? $"{Fin.Month(month)} so far" : Fin.Month(month),
            isCurrent ? $"Month to date, up to {Fin.Day(Today)}." : "A full month of money in and out.", actions: a =>
        {
            var index = months.IndexOf(month);
            a.Button([Button.OutlineSm, Button.Icon, "h-10 w-10"], ariaLabel: "Previous month", disabled: index < 0 || index >= months.Count - 1,
                onClick: () => SetReportMonth(months[Math.Min(months.Count - 1, months.IndexOf(SelectedMonth()) + 1)]),
                content: v => v.Icon([Icon.Sm], name: "chevron-left"));
            a.Select(["w-44"], bind: _reportMonth, ariaLabel: "Month",
                options: months.Select(m => new SelectOption(Fin.MonthKey(m), Fin.Month(m))).ToList());
            a.Button([Button.OutlineSm, Button.Icon, "h-10 w-10"], ariaLabel: "Next month", disabled: index <= 0,
                onClick: () => SetReportMonth(months[Math.Max(0, months.IndexOf(SelectedMonth()) - 1)]),
                content: v => v.Icon([Icon.Sm], name: "chevron-right"));
        });

        view.Grid(["grid grid-cols-2 xl:grid-cols-4 gap-3 sm:gap-4"], content: view =>
        {
            StatTile(view, "Money in", Fin.Eur(income),
                isCurrent && income == 0 ? $"Pay expected {Fin.Day(NextPayday())}" : ChangeText(income, prevIncome, compareLabel),
                "banknote-arrow-down", "success", null);
            StatTile(view, "Money out", Fin.Eur(spending), ChangeText(spending, prevSpending, compareLabel), "banknote-arrow-up",
                spending > prevSpending * 1.05m ? "warning" : "brand", null);
            StatTile(view, "Moved to savings", Fin.Eur(saved), "Transfers into your goals", "piggy-bank", "info", null);
            StatTile(view, "Kept this month", Fin.Pct(rate), income > 0 ? $"{Fin.Eur(income - spending)} of income not spent" : "No income recorded yet", "percent",
                rate >= 20 ? "success" : rate >= 5 ? "warning" : "error", null);
        });

        view.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-4 lg:gap-6"], content: view =>
        {
            // ── Where the money went ─────────────────────────────────────
            view.Box([Card.Elevated, "lg:col-span-2 p-5 sm:p-6 flex flex-col gap-4"], content: view =>
            {
                SectionHeader(view, "Where it went", "Tap a slice to see its transactions");

                if (byCat.Count == 0)
                {
                    view.EmptyState("No spending this month", description: "Pick another month above.", icon: "chart-pie");
                    return;
                }

                view.Box(["relative h-60 w-full"], content: b =>
                {
                    b.PieChart(["h-60 w-full"],
                        data: byCat.OrderByDescending(kv => kv.Value).Select(kv => new PieChartDatum
                        {
                            Id = kv.Key, Label = kv.Key, Value = (double)kv.Value, Color = Fin.CategoryColor(kv.Key),
                        }),
                        innerRadius: 0.62, padAngle: 1.5, cornerRadius: 4,
                        enableArcLabels: false, enableArcLinkLabels: false,
                        margin: new ChartMargin { Top = 8, Bottom = 8, Left = 8, Right = 8 },
                        valueUnit: "€",
                        onClick: async args =>
                        {
                            if (args.Id is { } id)
                            {
                                _txnCategory.Value = id;
                                _txnShowAll.Value = false;
                            }
                        });
                    b.Column(["absolute inset-0 items-center justify-center pointer-events-none gap-0"], content: c =>
                    {
                        c.Text([Text.Caption, "text-muted-foreground"], "Spent");
                        c.Text([Text.H4, "tabular-nums text-foreground"], Fin.Eur(spending));
                    });
                });

                view.Column(["gap-1"], content: view =>
                {
                    foreach (var (cat, amount) in byCat.OrderByDescending(kv => kv.Value))
                    {
                        var share = spending > 0 ? (double)(amount / spending) * 100 : 0;
                        var before = prevByCat.GetValueOrDefault(cat);
                        var active = _txnCategory.Value == cat;

                        view.Box([$"flex flex-row items-center gap-3 rounded-xl px-2 py-1.5 cursor-pointer transition-colors {(active ? "bg-brand-solid/10" : "hover:bg-muted/60")}"],
                            key: $"cat-{cat}", ariaLabel: $"{cat}: {Fin.Eur(amount)}. Show transactions",
                            onClick: () => { _txnCategory.Value = active ? "all" : cat; _txnShowAll.Value = false; },
                            content: r =>
                            {
                                r.Box([$"w-2.5 h-2.5 rounded-full shrink-0 bg-{Fin.CategoryPalette(cat)}"]);
                                r.Text([Text.BodySm, "flex-1 text-foreground truncate"], cat);
                                if (before > 0 && !isCurrent)
                                {
                                    var change = (double)((amount - before) / before) * 100;
                                    r.Text([Text.Caption, Math.Abs(change) < 5 ? "text-muted-foreground" : change > 0 ? "text-warning-600 dark:text-warning-400" : "text-success-600 dark:text-success-400", "tabular-nums"],
                                        Math.Abs(change) < 1 ? "same" : $"{(change > 0 ? "▲" : "▼")}{Math.Abs(change):0}%");
                                }
                                r.Text([Text.Caption, "tabular-nums text-muted-foreground w-10 text-right"], Fin.Pct(share));
                                r.Text([Text.BodySm, "tabular-nums font-semibold text-foreground w-20 text-right"], Fin.Eur(amount));
                            });
                    }
                });
            });

            view.Column(["lg:col-span-3 gap-4 lg:gap-6 min-w-0"], content: view =>
            {
                // ── Compared with last month ─────────────────────────────
                view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
                {
                    SectionHeader(view, "Compared with last month", $"{Fin.Month(prev)} → {Fin.Month(month)}");

                    var cats = byCat.Keys.Union(prevByCat.Keys)
                        .OrderByDescending(c => byCat.GetValueOrDefault(c) + prevByCat.GetValueOrDefault(c))
                        .Take(6).Reverse().ToList();

                    view.BarChart(["h-72 w-full"],
                        data: cats.Select(c => new Dictionary<string, object>
                        {
                            ["category"] = c,
                            [prev.ToString("MMM", Fin.Inv)] = (double)prevByCat.GetValueOrDefault(c),
                            [month.ToString("MMM", Fin.Inv)] = (double)byCat.GetValueOrDefault(c),
                        }),
                        keys: [prev.ToString("MMM", Fin.Inv), month.ToString("MMM", Fin.Inv)],
                        indexBy: "category",
                        groupMode: BarGroupMode.Grouped,
                        layout: BarLayout.Horizontal,
                        colors: ["#94a3b8", "#0f766e"],
                        borderRadius: 4,
                        enableLabel: false,
                        valueUnit: "€");
                });

                // ── Highlights ───────────────────────────────────────────
                view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-3"], content: view =>
                {
                    SectionHeader(view, "Highlights");
                    var spends = TxnsIn(month).Where(t => t.Amount < 0 && t.Category != "Savings").ToList();

                    if (spends.Count == 0)
                    {
                        view.Text([Text.BodySm, "text-muted-foreground"], "No spending recorded this month.");
                        return;
                    }

                    var biggest = spends.OrderBy(t => t.Amount).First();
                    var topMerchant = spends.GroupBy(t => t.Merchant).OrderByDescending(g => g.Count()).ThenBy(g => g.Sum(t => t.Amount)).First();
                    var flexible = spends.Where(t => t.Category is "Dining out" or "Shopping" or "Entertainment").Sum(t => -t.Amount);
                    var rising = byCat.Where(kv => prevByCat.GetValueOrDefault(kv.Key) > 0)
                        .Select(kv => (Cat: kv.Key, Diff: kv.Value - prevByCat[kv.Key]))
                        .OrderByDescending(x => x.Diff).FirstOrDefault();

                    Highlight(view, "receipt", "brand", "Biggest payment", $"{biggest.Merchant} · {Fin.Eur(-biggest.Amount, 2)} on {Fin.Day(biggest.Date)}");
                    Highlight(view, "store", "info", "Most visited", $"{topMerchant.Key} · {Fin.Eur(-topMerchant.Sum(t => t.Amount), 2)} over {topMerchant.Count()} payment{(topMerchant.Count() == 1 ? "" : "s")}");
                    Highlight(view, "sparkles", "warning", "Flexible spending", $"{Fin.Eur(flexible)} on dining out, shopping and entertainment — the easiest place to trim.");

                    if (rising.Cat != null && rising.Diff > 10 && !isCurrent)
                    {
                        Highlight(view, "trending-up", "error", $"{rising.Cat} went up", $"{Fin.Eur(rising.Diff)} more than {Fin.Month(prev)}.");
                    }
                });
            });
        });

        RenderTransactions(view, month);
    }

    private void SetReportMonth(DateTime month)
    {
        _reportMonth.Value = Fin.MonthKey(month);
        _txnShowAll.Value = false;
    }

    private static string ChangeText(decimal now, decimal before, string label)
    {
        if (before <= 0)
        {
            return "No earlier month to compare";
        }

        var change = (double)((now - before) / before) * 100;
        return Math.Abs(change) < 0.5 ? $"Same as {label}" : $"{(change > 0 ? "▲" : "▼")} {Math.Abs(change):0}% vs {label}";
    }

    private static void Highlight(UIView view, string iconName, string tone, string title, string text)
    {
        view.Row(["items-start gap-3"], key: $"hl-{title}", content: r =>
        {
            IconTile(r, iconName, tone, "w-9 h-9");
            r.Column(["flex-1 min-w-0 gap-0"], content: c =>
            {
                c.Text([Text.BodyStrong, "text-foreground"], title);
                c.Text([Text.BodySm, "text-muted-foreground"], text);
            });
        });
    }

    private void RenderTransactions(UIView view, DateTime month)
    {
        var all = TxnsIn(month).ToList();
        var rows = VisibleTxns(month);
        const int pageSize = 25;

        view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
        {
            SectionHeader(view, "Transactions", $"{all.Count} in {Fin.Month(month)}", actions: a =>
            {
                a.ActionButton([Button.OutlineSm], action: ActionKind.DownloadFile,
                    disabled: rows.Count == 0,
                    options: new DownloadFileActionOptions
                    {
                        FileName = $"omabilo-{Fin.MonthKey(month)}.csv",
                        MimeType = "text/csv",
                        Data = Encoding.UTF8.GetBytes(BuildCsv(rows)),
                    },
                    onActionComplete: async e =>
                    {
                        if (e.Success)
                        {
                            _toasts.Success($"Exported {rows.Count} transactions");
                        }
                    },
                    ariaLabel: $"Export {rows.Count} transactions as CSV",
                    content: v => v.Row(["items-center gap-2"], content: inner =>
                    {
                        inner.Icon([Icon.Sm], name: "download");
                        inner.Text([], $"Export CSV ({rows.Count})");
                    }));
            });

            view.Row(["items-center gap-2 flex-wrap"], content: view =>
            {
                view.TextField([Input.DefaultSm, "w-full sm:w-60"], placeholder: "Search merchant or category", bind: _txnSearch, ariaLabel: "Search transactions");

                var categories = all.GroupBy(t => t.Category).OrderBy(g => g.Key).ToList();
                var options = new List<SelectOption> { new("all", $"All categories ({all.Count})") };
                options.AddRange(categories.Select(g => new SelectOption(g.Key, $"{g.Key} ({g.Count()})")));

                // A chosen category that this month lacks still shows, with a zero count, so the filter stays visible
                if (_txnCategory.Value != "all" && categories.All(g => g.Key != _txnCategory.Value))
                {
                    options.Add(new SelectOption(_txnCategory.Value, $"{_txnCategory.Value} (0)"));
                }

                view.Select(["w-full sm:w-52"], triggerStyle: [Select.Size.Sm], bind: _txnCategory, ariaLabel: "Category", options: options);
                view.Select(["w-full sm:w-40"], triggerStyle: [Select.Size.Sm], bind: _txnSort, ariaLabel: "Sort by",
                    options: [new SelectOption("newest", "Newest first"), new SelectOption("oldest", "Oldest first"), new SelectOption("largest", "Largest amount")]);
            });

            if (_txnSearch.Value.Length > 0 || _txnCategory.Value != "all")
            {
                view.Row(["flex-wrap gap-2 items-center"], content: view =>
                {
                    if (_txnSearch.Value.Length > 0)
                    {
                        FilterChip(view, $"Search: {_txnSearch.Value}", () => _txnSearch.Value = "");
                    }

                    if (_txnCategory.Value != "all")
                    {
                        FilterChip(view, $"Category: {_txnCategory.Value}", () => _txnCategory.Value = "all");
                    }

                    view.Text([Text.Caption, "text-muted-foreground"], $"Showing {rows.Count} of {all.Count} · total {Fin.Signed(rows.Sum(t => t.Amount))}");
                });
            }

            if (rows.Count == 0)
            {
                view.EmptyState(all.Count == 0 ? "No transactions this month" : "No transactions match",
                    description: all.Count == 0 ? "Pick another month above." : "Try another search or category.",
                    icon: "search-x",
                    action: all.Count == 0 ? null : a => a.Button([Button.OutlineSm], text: "Clear filters", onClick: () => { _txnSearch.Value = ""; _txnCategory.Value = "all"; }));
                return;
            }

            view.Column(["divide-y divide-secondary"], content: view =>
            {
                foreach (var t in _txnShowAll.Value ? rows : rows.Take(pageSize))
                {
                    view.Row(["items-center gap-3 py-2.5"], key: $"txn-{t.Id}", content: r =>
                    {
                        r.Box([$"w-9 h-9 rounded-xl flex items-center justify-center shrink-0 bg-{Fin.CategoryPalette(t.Category)}/15"], content: b =>
                            b.Icon([Icon.Sm, $"text-{Fin.CategoryPalette(t.Category)}"], name: Fin.CategoryIcon(t.Category)));
                        r.Column(["flex-1 min-w-0 gap-0"], content: c =>
                        {
                            c.Text([Text.BodySm, "font-medium text-foreground truncate"], t.Merchant);
                            c.Text([Text.Caption, "text-muted-foreground"], $"{t.Category} · {t.Date.ToString("ddd d MMM", Fin.Inv)}");
                        });
                        r.Text([Text.BodyStrong, "tabular-nums shrink-0", t.Amount > 0 ? "text-success-600 dark:text-success-400" : "text-foreground"], Fin.Signed(t.Amount));
                    });
                }
            });

            if (rows.Count > pageSize)
            {
                view.Button([Button.GhostSm, "self-center"], text: _txnShowAll.Value ? "Show fewer" : $"Show all {rows.Count}",
                    onClick: () => _txnShowAll.Value = !_txnShowAll.Value);
            }
        });
    }
}
