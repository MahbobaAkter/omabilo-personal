using System.Globalization;

// My Money, Payday Planner, Monthly Reports and the Finland Tax & Finance Assistant: four screens over
// the signed-in person's own saved records (_my). The maths is plain C# (MonthlyReports,
// FinanceAssistant, CashFlowPlanner); these methods only draw it. Nothing here pays, sends or contacts
// anyone, and nothing connects to Vero or a bank.
public partial class OmabiloApp
{
    private readonly ClientReactive<int> _rMonth = new(0);
    private readonly ClientReactive<string> _aQuestion = new(nameof(AssistantQuestion.ReviewThisMonth));
    private readonly ClientReactive<string> _aVatPeriod = new("month");
    private readonly ClientReactive<string> _aAccountantMonth = new("last");
    private readonly ClientReactive<string> _aGlossary = new("");
    private readonly ClientReactive<string> _tRate = new("");
    private readonly ClientReactive<string> _tAdd = new("");
    private readonly ClientReactive<string> _tLimit = new("");
    private readonly ClientReactive<string> _tYear = new("");
    private readonly ClientReactive<string[]> _tErrors = new(Array.Empty<string>());
    private readonly ClientReactive<bool> _tEditing = new(false);

    private CompanionSnapshot? ScreenStart(UIView view, string title, string intro)
    {
        if (MyUserId == null)
        {
            SignedOutCard(view);
            return null;
        }

        view.Column(["gap-1"], content: c =>
        {
            c.Heading(title, style: [Text.H4, "m-0 text-foreground"], level: 2);
            c.Text([Text.BodySm, "text-muted-foreground"], intro);
        });
        LoadStates(view);
        return _my.Value;
    }

    // ── My Money ────────────────────────────────────────────────────────────
    // The same records as the Financial Inbox, sorted by what they mean. Each item sits in at most one
    // tab (ItemRules.MoneyTab) and opens the same sheet, with the email or document it came from.

    private readonly ClientReactive<string> _moneyTab = new("overview");

    private void GoMoney(string tab)
    {
        _moneyTab.Value = tab;
        Go("my-money");
    }

    private static string MoneyTabLabel(string tab) => ItemRules.MoneyTabs.FirstOrDefault(t => t.Key == tab).Label ?? "Overview";

    private static readonly (string Title, Func<ItemStatus, bool> Match)[] BillGroups =
    [
        ("Overdue", s => s.Status == DisplayStatus.Overdue),
        ("Due soon", s => s.Status == DisplayStatus.DueSoon),
        ("Needs your review", s => s.Status == DisplayStatus.NeedsReview || (s.NeedsReview && s.Status is not (DisplayStatus.Overdue or DisplayStatus.DueSoon))),
        ("Upcoming", s => s.Status == DisplayStatus.Upcoming && !s.NeedsReview),
        ("Paid", s => s.Status is DisplayStatus.ReportedPaid or DisplayStatus.ConfirmedPaid),
    ];

    private void RenderMoneyScreen(UIView view)
    {
        if (ScreenStart(view, "My Money", "Your income, bills, receipts and refunds, sorted from your Financial Inbox. Open any record to see the email or document it came from.") is not { } snap)
        {
            return;
        }

        var tab = ItemRules.MoneyTabs.Any(t => t.Key == _moneyTab.Value) ? _moneyTab.Value : "overview";
        int Count(string key) => snap.Items.Count(i => ItemRules.MoneyTab(i) == key);

        view.Box(["w-full overflow-x-auto -mx-1 px-1"], content: strip => strip.ToggleGroupSingle([ToggleGroup.Root, "flex-nowrap w-max"], value: tab, ariaLabel: "My Money sections",
            onValueChange: async v => { if (!string.IsNullOrEmpty(v)) _moneyTab.Value = v; },
            content: group =>
            {
                foreach (var (key, label) in ItemRules.MoneyTabs)
                {
                    group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5 whitespace-nowrap"], value: key, content: v =>
                    {
                        v.Text(text: label);
                        if (key != "overview") v.Text(["text-xs tabular-nums opacity-70"], text: Count(key).ToString(CultureInfo.InvariantCulture));
                    });
                }
            }));

        switch (tab)
        {
            case "bills":
                RenderBillsTab(view, snap);
                break;
            case "income":
                MoneyList(view, snap, "income", "No income records yet",
                    "Payslips and salary emails. Expected salary and payslips count as income only after you mark the money received.", "trending-up");
                break;
            case "receipts":
                MoneyList(view, snap, "receipts", "No receipts yet", "Purchase receipts from your email and uploads.", "shopping-bag");
                break;
            case "refunds":
                MoneyList(view, snap, "refunds", "No refunds yet", "Money returned to you, such as a cancelled order or a returned item.", "undo-2");
                break;
            default:
                RenderMoneyOverview(view, snap);
                break;
        }

        RenderItemSheet(view, snap);
    }

    private void RenderMoneyOverview(UIView view, CompanionSnapshot snap)
    {
        var today = DateTime.UtcNow.Date;
        var bills = snap.Items.Where(i => i.IsObligation).Select(i => (Item: i, Status: ItemRules.StatusOf(i, today))).ToList();
        BillTiles(view, bills);

        var next = bills.Where(b => b.Status.Status is DisplayStatus.Overdue or DisplayStatus.DueSoon or DisplayStatus.Upcoming)
            .OrderBy(b => b.Item.DueDate).Take(5).ToList();

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Row(["items-center justify-between gap-3"], content: r =>
            {
                r.Heading("Coming up", style: [Text.H5, "m-0 text-foreground"], level: 3);
                r.Button([Button.GhostSm, "text-brand-secondary"], text: "All bills", icon: "arrow-right", onClick: () => _moneyTab.Value = "bills");
            });

            if (next.Count == 0)
            {
                card.Text([Text.BodySm, "text-muted-foreground"], "No open bills with a due date. Bills without one are listed in Bills & payments.");
            }

            foreach (var (item, status) in next)
            {
                ItemRow(card, item, status, true);
            }
        });

        view.Grid(["grid grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-4"], content: grid =>
        {
            foreach (var (key, label) in ItemRules.MoneyTabs.Skip(1))
            {
                var n = snap.Items.Count(i => ItemRules.MoneyTab(i) == key);
                grid.Box([Card.Default, "p-4 flex flex-col gap-1 cursor-pointer hover:shadow-sm min-h-11"], key: $"mt-{key}", ariaLabel: $"{label}: {n} records",
                    onClick: () => _moneyTab.Value = key, content: c =>
                    {
                        c.Text([Text.Caption, "text-muted-foreground"], label);
                        c.Text([Text.H5, "m-0 tabular-nums text-foreground"], n.ToString(CultureInfo.InvariantCulture));
                    });
            }
        });

        if (snap.Recurring.Count > 0)
        {
            view.Box([Card.Default, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
            {
                card.Heading("Usual monthly costs", style: [Text.H5, "m-0 text-foreground"], level: 3);
                RenderRecurring(card, snap);
            });
        }
    }

    private void MoneyList(UIView view, CompanionSnapshot snap, string tab, string emptyTitle, string intro, string icon)
    {
        var today = DateTime.UtcNow.Date;
        var rows = snap.Items.Where(i => ItemRules.MoneyTab(i) == tab)
            .OrderByDescending(i => i.EventDate ?? i.InvoiceDate ?? i.CreatedUtc).ToList();

        if (rows.Count == 0)
        {
            view.EmptyState(emptyTitle, description: intro, icon: icon);
            return;
        }

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Text([Text.BodySm, "text-muted-foreground"], intro);

            foreach (var item in rows)
            {
                ItemRow(card, item, ItemRules.StatusOf(item, today), false);
            }
        });
    }

    private void BillTiles(UIView view, List<(FinancialItem Item, ItemStatus Status)> bills)
    {
        decimal Sum(Func<ItemStatus, bool> m) => bills.Where(b => m(b.Status)).Sum(b => b.Item.Amount ?? 0);
        int Num(Func<ItemStatus, bool> m) => bills.Count(b => m(b.Status));

        view.Grid(["grid grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-4"], content: grid =>
        {
            StatTile(grid, "Overdue", Money(Sum(BillGroups[0].Match)), $"{Num(BillGroups[0].Match)} bills", "alarm-clock", "warning", null);
            StatTile(grid, "Due soon", Money(Sum(BillGroups[1].Match)), $"{Num(BillGroups[1].Match)} bills", "calendar-clock", "info", null);
            StatTile(grid, "To review", Num(BillGroups[2].Match).ToString(CultureInfo.InvariantCulture), "Check the details read from the bill", "scan-search", "brand", null);
            StatTile(grid, "Paid", Money(Sum(BillGroups[4].Match)), $"{Num(BillGroups[4].Match)} bills", "circle-check", "success", null);
        });
    }

    // The former Invoices screen: upload, totals and bills grouped by status. A bill counts as paid
    // only when the person marks it paid or a payment email confirms it; an unknown status stays unknown.
    private void RenderBillsTab(UIView view, CompanionSnapshot snap)
    {
        view.Text([Text.BodySm, "text-muted-foreground"], "Bills from your inbox, uploads and forwarded mail. A bill counts as paid only when you mark it paid or a payment email confirms it.");
        UploadBillCard(view, true);

        var today = DateTime.UtcNow.Date;
        var all = snap.Items.Where(i => ItemRules.MoneyTab(i) == "bills").ToList();
        var bills = all.Where(i => i.IsObligation).Select(i => (Item: i, Status: ItemRules.StatusOf(i, today))).ToList();
        BillTiles(view, bills);

        if (all.Count == 0)
        {
            view.EmptyState("No bills yet", description: "Upload a bill, or connect Gmail on the Financial Inbox to find bills in your email.", icon: "receipt");
            return;
        }

        var shown = new HashSet<string>();

        foreach (var (title, match) in BillGroups)
        {
            var rows = bills.Where(b => !shown.Contains(b.Item.Id) && match(b.Status)).OrderBy(b => b.Item.DueDate).ToList();
            InvoiceGroup(view, title, rows, shown);
        }

        InvoiceGroup(view, "Other", bills.Where(b => !shown.Contains(b.Item.Id)).OrderBy(b => b.Item.DueDate).ToList(), shown);

        // Reminders and letters without a due date or amount: listed, never assumed unpaid
        InvoiceGroup(view, "Without a due date or amount", all.Where(i => !shown.Contains(i.Id))
            .Select(i => (Item: i, Status: ItemRules.StatusOf(i, today))).OrderByDescending(b => b.Item.CreatedUtc).ToList(), shown);
    }

    private void InvoiceGroup(UIView view, string title, List<(FinancialItem Item, ItemStatus Status)> rows, HashSet<string> shown)
    {
        if (rows.Count == 0)
        {
            return;
        }

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], key: $"inv-{title}", content: card =>
        {
            card.Heading($"{title} ({rows.Count})", style: [Text.H5, "m-0 text-foreground"], level: 3);

            foreach (var (item, status) in rows)
            {
                shown.Add(item.Id);
                ItemRow(card, item, status, false);
            }
        });
    }

    // ── Payday Planner ──────────────────────────────────────────────────────

    private void RenderPaydayScreen(UIView view)
    {
        if (MyUserId != null && !IsPremium())
        {
            PremiumLock(view, PremiumFeature.PaydayPlan, "See what's left until payday after bills, debt payments and savings. Every estimate is labelled.");
            return;
        }

        if (ScreenStart(view, "Payday Planner", "Bills due, debt payments and savings before your next payday. Estimates are labelled; Omabilo never moves money.") is not { } snap)
        {
            return;
        }

        var plan = snap.Plan;

        if (plan.Shortfall)
        {
            view.Alert("Your obligations are more than the money available", tone: SemanticTone.Error,
                description: $"Before {Day(plan.End)} you'd be about {Money(-plan.ProjectedRemainder!.Value)} short. Consider which payments can move, and contact a creditor early if one can't be paid on time.");
        }

        if (plan.Planned.Count > 0)
        {
            view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-2"], content: card =>
            {
                card.Heading("Debt payments and savings in this plan", style: [Text.H5, "m-0 text-foreground"], level: 3);

                foreach (var line in plan.Planned)
                {
                    card.Row(["items-center justify-between gap-3"], key: $"pl-{line.Label}", content: r =>
                    {
                        r.Column(["min-w-0"], content: c =>
                        {
                            c.Text([Text.BodySm, "text-foreground"], line.Label);
                            c.Text([Text.Caption, "text-muted-foreground"], $"{BasisText(line.Basis)}{(line.Date is { } d ? $" · {Day(d)}" : "")}");
                        });
                        r.Text([Text.BodyStrong, "tabular-nums"], Money(line.Amount));
                    });
                }

                card.Text([Text.Caption, "text-muted-foreground"], "Taken off what's left below. Change these on Debts or in the savings field.");
            });
        }

        RenderCashFlow(view, snap);
        RenderItemSheet(view, snap);
    }

    private static string BasisText(AmountBasis b) => b switch
    {
        AmountBasis.UserEntered => "You entered",
        AmountBasis.FromEmail => "From an email",
        AmountBasis.Estimate => "Estimate",
        AmountBasis.Calculated => "Calculated",
        _ => "From an upload",
    };

    // ── Monthly Reports ─────────────────────────────────────────────────────

    private void RenderReportsScreen(UIView view)
    {
        if (MyUserId != null && !IsPremium())
        {
            PremiumLock(view, PremiumFeature.MonthByMonth, "Income, spending and what's left each month, by category, with every total traced to its records.");
            return;
        }

        if (ScreenStart(view, "Monthly Reports", "What actually happened each month. Expected money and unpaid bills are shown apart and never counted.") is not { } snap)
        {
            return;
        }

        var today = DateTime.UtcNow.Date;
        var reports = MonthlyReports.Build(snap.Items, today, 6, snap.Income);

        if (reports.Count == 0)
        {
            view.EmptyState("No months to show", description: "Reports appear once you have records in Omabilo.", icon: "chart-column");
            return;
        }

        var index = Math.Clamp(_rMonth.Value, 0, reports.Count - 1);
        var r = reports[index];
        var prev = index + 1 < reports.Count ? reports[index + 1] : null;
        string Label(MonthReport m) => new DateTime(m.Year, m.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        string Change(decimal now, decimal? before) => before is not { } b ? "No earlier month" : now == b ? "Same as last month" : $"{(now > b ? "+" : "−")}{Money(Math.Abs(now - b))} vs last month";

        view.Row(["items-center justify-between gap-3"], content: row =>
        {
            row.Button([Button.OutlineSm], text: "Earlier", icon: "chevron-left", disabled: index >= reports.Count - 1, onClick: () => _rMonth.Value = index + 1);
            row.Text([Text.BodyStrong, "text-foreground"], Label(r));
            row.Button([Button.OutlineSm], text: "Later", disabled: index == 0, onClick: () => _rMonth.Value = index - 1);
        });

        view.Grid(["grid grid-cols-1 sm:grid-cols-3 gap-3 sm:gap-4"], content: grid =>
        {
            StatTile(grid, "Income received", Money(r.IncomeTotal), Change(r.IncomeTotal, prev?.IncomeTotal), "trending-up", "success", null);
            StatTile(grid, "Spent", Money(r.ExpenseTotal), Change(r.ExpenseTotal, prev?.ExpenseTotal), "shopping-cart", "warning", null);
            StatTile(grid, "Net", Money(r.Net), Change(r.Net, prev?.Net), "scale", r.Net < 0 ? "warning" : "brand", null);
        });

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-2"], content: card =>
        {
            card.Heading("Last six months", style: [Text.H5, "m-0 text-foreground"], level: 3);
            card.BarChart(["h-64 w-full"],
                data: reports.Reverse().Select(m => new Dictionary<string, object>
                {
                    ["month"] = new DateTime(m.Year, m.Month, 1).ToString("MMM", CultureInfo.InvariantCulture),
                    ["Income"] = (double)m.IncomeTotal,
                    ["Spent"] = (double)m.ExpenseTotal,
                }),
                keys: ["Income", "Spent"], indexBy: "month", groupMode: BarGroupMode.Grouped, valueUnit: "€");
        });

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Heading("Spending by category", style: [Text.H5, "m-0 text-foreground"], level: 3);

            if (r.ExpenseCategories.Count == 0)
            {
                card.Text([Text.BodySm, "text-muted-foreground"], "No paid bills or receipts this month.");
            }

            foreach (var cat in r.ExpenseCategories)
            {
                card.Column(["gap-1"], key: $"cat-{cat.Category}", content: c =>
                {
                    c.Row(["items-center justify-between gap-3"], content: h =>
                    {
                        h.Text([Text.BodyStrong, "text-foreground"], $"{cat.Category} ({cat.Count})");
                        h.Text([Text.BodyStrong, "tabular-nums"], Money(cat.Amount));
                    });
                    ReportLines(c, snap, r.Expenses.Where(l => l.Category == cat.Category));
                });
            }
        });

        ReportSection(view, snap, "Income received", r.Income, "No income recorded as received this month.");
        ReportSection(view, snap, "Expected, not counted", r.ExpectedIncome.Concat(r.PayslipsNotConfirmed), null);
        ReportSection(view, snap, "Bills still open, not counted", r.BillsStillOpen, null);

        view.Box(["rounded-2xl bg-muted p-4 flex flex-col gap-1"], content: c =>
        {
            c.Text([Text.Caption, "text-muted-foreground"], MonthlyReports.HowCounted);

            foreach (var note in r.LeftOut)
            {
                c.Text([Text.Caption, "text-muted-foreground"], note);
            }
        });
    }

    private void ReportSection(UIView view, CompanionSnapshot snap, string title, IEnumerable<ReportLine> lines, string? empty)
    {
        var list = lines.ToList();

        if (list.Count == 0 && empty == null)
        {
            return;
        }

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-2"], key: $"rs-{title}", content: card =>
        {
            card.Row(["items-center justify-between gap-3"], content: h =>
            {
                h.Heading(title, style: [Text.H5, "m-0 text-foreground"], level: 3);
                h.Text([Text.BodyStrong, "tabular-nums"], Money(list.Sum(l => l.Amount)));
            });

            if (list.Count == 0)
            {
                card.Text([Text.BodySm, "text-muted-foreground"], empty!);
            }

            ReportLines(card, snap, list);
        });
    }

    private void ReportLines(UIView view, CompanionSnapshot snap, IEnumerable<ReportLine> lines)
    {
        foreach (var line in lines)
        {
            var item = snap.Items.FirstOrDefault(i => i.Id == line.ItemId);

            view.Box(["flex flex-row items-center justify-between gap-3 rounded-xl px-2 py-2 hover:bg-muted cursor-pointer min-h-10"], key: $"rl-{line.ItemId}-{line.Basis}",
                ariaLabel: $"{line.Title}, {Money(line.Amount)}, open the record",
                onClick: () =>
                {
                    if (item != null)
                    {
                        Go("my-inbox");
                        OpenItem(item);
                    }
                },
                content: row =>
                {
                    row.Column(["min-w-0"], content: c =>
                    {
                        c.Text([Text.BodySm, "text-foreground truncate"], line.Title);
                        c.Text([Text.Caption, "text-muted-foreground"], $"{Day(line.Date)} · {line.Basis}");
                    });
                    row.Text([Text.BodySm, "tabular-nums shrink-0"], Money(line.Amount));
                });
        }
    }

    // ── Finland Tax & Finance Assistant ─────────────────────────────────────

    private void RenderAssistantScreen(UIView view)
    {
        if (ScreenStart(view, "Finland Tax & Finance Assistant", "Answers built only from your own records in Omabilo, each with its sources.") is not { } snap)
        {
            return;
        }

        var today = DateTime.UtcNow.Date;
        view.Alert("Not tax advice", tone: SemanticTone.Info, description: FinanceAssistant.Disclaimer);

        // Questions
        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Heading("Ask about your records", style: [Text.H5, "m-0 text-foreground"], level: 3);
            card.ToggleGroupSingle([ToggleGroup.Root, "flex-wrap"], value: _aQuestion.Value, ariaLabel: "Question",
                onValueChange: async v => { if (!string.IsNullOrEmpty(v)) _aQuestion.Value = v; },
                content: g =>
                {
                    foreach (var q in Enum.GetValues<AssistantQuestion>())
                    {
                        g.ToggleGroupItem([ToggleGroup.Item], value: q.ToString(), content: v => v.Text(text: FinanceAssistant.QuestionText(q)));
                    }
                });

            var question = Enum.TryParse<AssistantQuestion>(_aQuestion.Value, out var parsed) ? parsed : AssistantQuestion.ReviewThisMonth;

            // The accountant pack covers one whole month: last month by default, or this month so far
            if (question == AssistantQuestion.ForAccountant)
            {
                card.ToggleGroupSingle([ToggleGroup.Root, "self-start"], value: _aAccountantMonth.Value, ariaLabel: "Month for your accountant",
                    onValueChange: async v => { if (!string.IsNullOrEmpty(v)) _aAccountantMonth.Value = v; },
                    content: g =>
                    {
                        g.ToggleGroupItem([ToggleGroup.Item], value: "last", content: v => v.Text(text: "Last month"));
                        g.ToggleGroupItem([ToggleGroup.Item], value: "this", content: v => v.Text(text: "This month"));
                    });
            }

            var month = _aAccountantMonth.Value == "this" ? today : today.AddMonths(-1);
            var answer = question == AssistantQuestion.ForAccountant
                ? FinanceAssistant.ForAccountant(snap.Items, month.Year, month.Month)
                : FinanceAssistant.Answer(question, snap.Items, snap.Plan, today);
            AnswerBlock(card, snap, answer);

            if (question == AssistantQuestion.ForAccountant && answer.Sources.Count == 0)
            {
                card.Text([Text.Caption, "text-muted-foreground"], "There's no draft to copy until Omabilo holds a record for that month.");
            }
            else if (question == AssistantQuestion.ForAccountant)
            {
                var draft = FinanceAssistant.AccountantDraft(answer);
                card.Text([Text.BodySm, "whitespace-pre-wrap rounded-xl bg-muted p-3 text-foreground"], draft);
                card.ActionButton([Button.PrimaryMd, "self-start"], action: ActionKind.CopyToClipboard,
                    options: new CopyToClipboardActionOptions { Text = draft }, ariaLabel: "Copy draft",
                    content: b => b.Row(["items-center gap-2"], content: inner =>
                    {
                        inner.Icon([Icon.Sm], name: "copy");
                        inner.Text([], "Copy draft");
                    }));
                card.Text([Text.Caption, "text-muted-foreground"], "Omabilo doesn't send this. Paste it into your own email to your accountant.");
            }
        });

        // VAT
        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
            {
                r.Heading("VAT (ALV) on your records", style: [Text.H5, "m-0 text-foreground"], level: 3);
                r.ToggleGroupSingle([ToggleGroup.Root], value: _aVatPeriod.Value, ariaLabel: "VAT period",
                    onValueChange: async v => { if (!string.IsNullOrEmpty(v)) _aVatPeriod.Value = v; },
                    content: g =>
                    {
                        g.ToggleGroupItem([ToggleGroup.Item], value: "month", content: v => v.Text(text: "This month"));
                        g.ToggleGroupItem([ToggleGroup.Item], value: "year", content: v => v.Text(text: "This year"));
                    });
            });

            var vat = FinanceAssistant.Vat(snap.Items, today.Year, _aVatPeriod.Value == "month" ? today.Month : null);
            card.Text([Text.BodySm, "text-foreground"], $"{vat.PeriodLabel}: {Money(vat.VatTotal)} VAT on {Money(vat.GrossTotal)} across {vat.Lines.Count} record{(vat.Lines.Count == 1 ? "" : "s")} that show VAT.");

            foreach (var l in vat.Lines)
            {
                card.Row(["items-center justify-between gap-3"], key: $"vat-{l.ItemId}", content: row =>
                {
                    row.Text([Text.Caption, "text-muted-foreground min-w-0 truncate"], $"{l.DateLabel} {Day(l.Date)} · {l.Title}{(l.RatePercent is { } p ? $" · {p:0.#} %" : "")}");
                    row.Text([Text.Caption, "tabular-nums"], Money(l.Vat));
                });
            }

            if (vat.WithoutVat.Count > 0)
            {
                card.Text([Text.Caption, "text-muted-foreground"], $"{vat.WithoutVat.Count} record{(vat.WithoutVat.Count == 1 ? "" : "s")} in this period show no VAT amount and aren't included.");
            }
        });

        RenderTaxCard(view, snap, today);

        // Glossary
        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Heading("Finnish money words", style: [Text.H5, "m-0 text-foreground"], level: 3);
            card.TextField(placeholder: "Search, e.g. eräpäivä or due date", ariaLabel: "Search the glossary", bind: _aGlossary);
            var q = _aGlossary.Value.Trim();
            var terms = FinanceAssistant.Glossary.Where(t => q.Length == 0
                || t.Finnish.Contains(q, StringComparison.OrdinalIgnoreCase) || t.English.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            if (terms.Count == 0)
            {
                card.Text([Text.BodySm, "text-muted-foreground"], "No word matches that search.");
            }

            foreach (var t in terms)
            {
                card.Column(["gap-0"], key: $"g-{t.Finnish}", content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground"], $"{t.Finnish} — {t.English}");
                    c.Text([Text.Caption, "text-muted-foreground"], t.Meaning);
                });
            }
        });

        RenderItemSheet(view, snap);
    }

    private void AnswerBlock(UIView view, CompanionSnapshot snap, AssistantAnswer answer)
    {
        view.Text([Text.Body, "text-foreground"], answer.Summary);

        if (answer.Sources.Count > 0)
        {
            view.Text([Text.Overline, "text-muted-foreground"], "Sources");

            foreach (var s in answer.Sources)
            {
                var item = s.ItemId is { } id ? snap.Items.FirstOrDefault(i => i.Id == id) : null;
                view.Box(["flex flex-row items-center justify-between gap-3 rounded-xl px-2 py-2 min-h-10 " + (item != null ? "hover:bg-muted cursor-pointer" : "")],
                    key: $"src-{s.ItemId ?? s.DebtId ?? s.Label}",
                    onClick: () =>
                    {
                        if (item != null)
                        {
                            OpenItem(item);
                        }
                        else if (s.DebtId != null)
                        {
                            Go("my-debts");
                        }
                    },
                    content: row =>
                    {
                        row.Column(["min-w-0"], content: c =>
                        {
                            c.Text([Text.BodySm, "text-foreground truncate"], s.Label);
                            c.Text([Text.Caption, "text-muted-foreground"], s.Detail);
                        });

                        if (s.Amount is { } a)
                        {
                            row.Text([Text.BodySm, "tabular-nums shrink-0"], Money(a));
                        }
                    });
            }
        }

        foreach (var m in answer.Missing)
        {
            view.Text([Text.Caption, "text-warning-700"], m);
        }

        view.Text([Text.Caption, "text-muted-foreground"], answer.Basis);
    }

    private void RenderTaxCard(UIView view, CompanionSnapshot snap, DateTime today)
    {
        var tc = FinanceAssistant.TaxCard(snap.Items, snap.Settings, today);

        view.Box([Card.Elevated, "p-4 sm:p-6 flex flex-col gap-3"], content: card =>
        {
            card.Row(["items-center justify-between gap-3 flex-wrap"], content: r =>
            {
                r.Heading("Your tax card (verokortti)", style: [Text.H5, "m-0 text-foreground"], level: 3);

                if (!_tEditing.Value)
                {
                    r.Button([Button.OutlineSm], text: tc.RatePercent == null ? "Enter tax card" : "Edit", disabled: _myBusy.Value, onClick: () =>
                    {
                        var s = snap.Settings;
                        _tRate.Value = s.TaxRatePercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
                        _tAdd.Value = s.TaxAdditionalRatePercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
                        _tLimit.Value = s.TaxIncomeLimit?.ToString("0", CultureInfo.InvariantCulture) ?? "";
                        _tYear.Value = (s.TaxCardYear ?? today.Year).ToString(CultureInfo.InvariantCulture);
                        _tErrors.Value = [];
                        _tEditing.Value = true;
                    });
                }
            });

            card.Text([Text.BodySm, "text-foreground"], tc.Summary);

            foreach (var s in tc.Sources)
            {
                card.Text([Text.Caption, "text-muted-foreground"], $"{s.Label}: {s.Detail}");
            }

            if (_tEditing.Value)
            {
                card.Grid(["grid grid-cols-1 sm:grid-cols-2 gap-3"], content: g =>
                {
                    g.TextField(label: "Withholding rate (%)", placeholder: "18,5", bind: _tRate);
                    g.TextField(label: "Additional rate (%)", placeholder: "40", bind: _tAdd);
                    g.TextField(label: "Income limit for the year (€)", placeholder: "30000", bind: _tLimit);
                    g.TextField(label: "Tax card year", placeholder: today.Year.ToString(CultureInfo.InvariantCulture), bind: _tYear);
                });
                card.Text([Text.Caption, "text-muted-foreground"], "Copy these from your own tax card in OmaVero. Omabilo doesn't connect to Vero.");

                foreach (var e in _tErrors.Value)
                {
                    card.Text([Text.Caption, "text-error-700"], e);
                }

                card.Row(["gap-2"], content: r =>
                {
                    r.Button([Button.PrimarySm], text: "Save tax card", disabled: _myBusy.Value, onClick: async () => await SaveTaxCardAsync(snap));
                    r.Button([Button.GhostSm], text: "Cancel", onClick: () => _tEditing.Value = false);
                });
            }
        });
    }

    private async Task SaveTaxCardAsync(CompanionSnapshot snap)
    {
        var errors = new List<string>();

        decimal? Num(string text, string label, decimal max)
        {
            if (text.Trim().Length == 0) return null;
            var v = TextInvoiceExtractor.ParseAmount(text) ?? (text.Trim() == "0" ? 0 : null);
            if (v is null or < 0 || v > max) { errors.Add($"{label} must be a number between 0 and {max:0}."); return null; }
            return v;
        }

        var rate = Num(_tRate.Value, "Withholding rate", 100);
        var add = Num(_tAdd.Value, "Additional rate", 100);
        var limit = Num(_tLimit.Value, "Income limit", 10_000_000);
        int? year = null;

        if (_tYear.Value.Trim().Length > 0)
        {
            if (int.TryParse(_tYear.Value.Trim(), out var y) && y is >= 2000 and <= 2100) year = y;
            else errors.Add("Tax card year must be a year such as 2026.");
        }

        _tErrors.Value = errors.ToArray();

        if (errors.Count > 0)
        {
            return;
        }

        var before = snap.Settings;
        var updated = before with { TaxRatePercent = rate, TaxAdditionalRatePercent = add, TaxIncomeLimit = limit, TaxCardYear = year };

        await RunMyAsync(async uid =>
        {
            await Companion.SaveSettingsAsync(uid, updated, CancellationToken.None);
            _tEditing.Value = false;
            OfferUndo("Tax card saved", () => _ = RunMyAsync(async u =>
            {
                await Companion.SaveSettingsAsync(u, before, CancellationToken.None);
                return "Tax card restored.";
            }));
            return null;
        });
    }
}
