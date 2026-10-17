public partial class OmabiloApp
{
    private readonly ClientReactive<string> _inboxFilter = new("attention");
    private readonly ClientReactive<string> _inboxSearch = new("");
    private readonly ClientReactive<string> _inboxKind = new("all");

    private static string InboxIcon(InboxKind kind) => kind switch
    {
        InboxKind.Bill => "receipt",
        InboxKind.Alert => "triangle-alert",
        InboxKind.Income => "banknote-arrow-down",
        _ => "lightbulb",
    };

    private string InboxTone(InboxItem item)
    {
        if (item.Kind == InboxKind.Bill && item.DueDate is { } due && EffectiveStatus(item) != InboxStatus.Done)
        {
            return due < Today ? "error" : (due - Today).TotalDays <= 3 ? "warning" : "brand";
        }

        return item.Kind switch
        {
            InboxKind.Alert => "warning",
            InboxKind.Income => "success",
            InboxKind.Insight => "info",
            _ => "brand",
        };
    }

    private static string DueText(DateTime due)
    {
        var days = (int)(due.Date - DateTime.UtcNow.Date).TotalDays;
        return days switch
        {
            < -1 => $"overdue by {-days} days",
            -1 => "overdue since yesterday",
            0 => "due today",
            1 => "due tomorrow",
            _ => $"due {Fin.Day(due)} · in {days} days",
        };
    }

    private void SetInboxStatus(InboxItem item, InboxStatus status, string message, DateTime? snoozeUntil = null)
    {
        _inbox.ReplaceWhere(i => i.Id == item.Id, i => i with { Status = status, SnoozedUntil = snoozeUntil });

        // Moving a paid bill back (or finishing one) keeps a linked invoice's payment status in step
        var paidChanged = (item.Status == InboxStatus.Done) != (status == InboxStatus.Done);

        if (paidChanged && item.Kind == InboxKind.Bill)
        {
            SyncInvoiceFromBill(item.Id, paid: status == InboxStatus.Done);
        }

        OfferUndo($"{message}: {item.Title}", () =>
        {
            _inbox.ReplaceWhere(i => i.Id == item.Id, _ => item);

            if (paidChanged && item.Kind == InboxKind.Bill)
            {
                SyncInvoiceFromBill(item.Id, paid: item.Status == InboxStatus.Done);
            }
        });
    }

    private bool InFilter(InboxItem i, string filter) => filter switch
    {
        "attention" => EffectiveStatus(i) == InboxStatus.New,
        "snoozed" => EffectiveStatus(i) == InboxStatus.Snoozed,
        "done" => EffectiveStatus(i) is InboxStatus.Done or InboxStatus.Archived,
        _ => true,
    };

    private List<InboxItem> VisibleInbox()
    {
        IEnumerable<InboxItem> rows = _inbox;
        rows = rows.Where(i => InFilter(i, _inboxFilter.Value));

        if (_inboxKind.Value != "all")
        {
            rows = rows.Where(i => i.Kind.ToString() == _inboxKind.Value);
        }

        var q = _inboxSearch.Value.Trim();

        if (q.Length > 0)
        {
            rows = rows.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.From.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Detail.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return rows
            .OrderBy(i => EffectiveStatus(i) is InboxStatus.Done or InboxStatus.Archived)
            .ThenBy(i => i.DueDate ?? DateTime.MaxValue)
            .ThenByDescending(i => i.ReceivedAt)
            .ToList();
    }

    private void RenderInbox(UIView view)
    {
        var openBills = _inbox.Where(IsOpenBill).ToList();
        var dueSoon = openBills.Where(i => i.DueDate is { } d && d <= Today.AddDays(7)).ToList();
        var overdue = openBills.Count(i => i.DueDate is { } d && d < Today);

        view.Grid(["grid grid-cols-2 sm:grid-cols-3 gap-3 sm:gap-4"], content: view =>
        {
            StatTile(view, "Needs attention", AttentionCount().ToString(), "Bills, alerts and tips to review", "inbox", "brand", () => _inboxFilter.Value = "attention", "ib-att");
            StatTile(view, "Due in the next 7 days", Fin.Eur(dueSoon.Sum(i => i.Amount ?? 0), 2), $"{dueSoon.Count} unpaid bill{(dueSoon.Count == 1 ? "" : "s")}", "calendar-clock", dueSoon.Count > 0 ? "warning" : "success", null);
            StatTile(view, "Overdue", overdue.ToString(), overdue == 0 ? "Nothing is late. Nice." : "Pay these first", "circle-alert", overdue > 0 ? "error" : "success", null);
        });

        view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-4"], content: view =>
        {
            view.Row(["items-center justify-between gap-3 flex-wrap"], content: view =>
            {
                int Count(string f) => _inbox.Count(i => InFilter(i, f));

                view.ToggleGroupSingle([ToggleGroup.Root, "flex-wrap"], value: _inboxFilter.Value, ariaLabel: "Show",
                    onValueChange: async v =>
                    {
                        if (!string.IsNullOrEmpty(v))
                        {
                            _inboxFilter.Value = v;
                        }
                    },
                    content: group =>
                    {
                        foreach (var (key, label) in new[] { ("attention", "Needs attention"), ("snoozed", "Snoozed"), ("done", "Done"), ("all", "All") })
                        {
                            group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5"], value: key, content: v =>
                            {
                                v.Text(text: label);
                                v.Text(["text-xs tabular-nums opacity-70"], text: Count(key).ToString());
                            });
                        }
                    });

                view.Row(["items-center gap-2 flex-wrap w-full sm:w-auto"], content: view =>
                {
                    view.TextField([Input.DefaultSm, "w-full sm:w-56"], placeholder: "Search inbox", bind: _inboxSearch, ariaLabel: "Search inbox");

                    var kinds = Enum.GetValues<InboxKind>();
                    view.Select(["w-full sm:w-40"], triggerStyle: [Select.Size.Sm], bind: _inboxKind, ariaLabel: "Type",
                        options: [new SelectOption("all", $"All types ({_inbox.Count})"),
                            .. kinds.Select(k => new SelectOption(k.ToString(), $"{KindLabel(k)} ({_inbox.Count(i => i.Kind == k)})"))]);
                });
            });

            if (_inboxSearch.Value.Length > 0 || _inboxKind.Value != "all")
            {
                view.Row(["flex-wrap gap-2"], content: view =>
                {
                    if (_inboxSearch.Value.Length > 0)
                    {
                        FilterChip(view, $"Search: {_inboxSearch.Value}", () => _inboxSearch.Value = "");
                    }

                    if (_inboxKind.Value != "all" && Enum.TryParse<InboxKind>(_inboxKind.Value, out var k))
                    {
                        FilterChip(view, $"Type: {KindLabel(k)}", () => _inboxKind.Value = "all");
                    }
                });
            }

            var rows = VisibleInbox();

            if (rows.Count == 0)
            {
                var filtered = _inboxSearch.Value.Length > 0 || _inboxKind.Value != "all";
                view.EmptyState(
                    filtered ? "Nothing matches" : _inboxFilter.Value == "attention" ? "Inbox zero 🎉" : "Nothing here",
                    description: filtered ? "Try another search or clear the filters." : _inboxFilter.Value == "attention" ? "Every bill and alert has been handled." : "Items you snooze or finish show up here.",
                    icon: filtered ? "search-x" : "inbox",
                    action: filtered
                        ? a => a.Button([Button.OutlineSm], text: "Clear filters", onClick: () => { _inboxSearch.Value = ""; _inboxKind.Value = "all"; })
                        : null);
                return;
            }

            view.Column(["gap-3"], content: view =>
            {
                foreach (var item in rows)
                {
                    RenderInboxRow(view, item);
                }
            });
        });
    }

    private static string KindLabel(InboxKind k) => k switch
    {
        InboxKind.Bill => "Bills",
        InboxKind.Alert => "Alerts",
        InboxKind.Income => "Income",
        _ => "Insights",
    };

    private static void FilterChip(UIView view, string label, Action clear)
    {
        view.Button([Badge.NeutralSm, "gap-1.5 h-8 px-3 cursor-pointer"], ariaLabel: $"Remove filter {label}", onClick: () => clear(), content: v =>
        {
            v.Text(["text-xs font-semibold"], text: label);
            v.Icon([Icon.Xs], name: "x");
        });
    }

    private void RenderInboxRow(UIView view, InboxItem item)
    {
        var status = EffectiveStatus(item);
        var finished = status is InboxStatus.Done or InboxStatus.Archived;

        view.Box([finished ? "rounded-2xl border border-secondary p-4 opacity-70" : "rounded-2xl border border-secondary bg-card p-4 shadow-xs",
                "flex flex-col sm:flex-row gap-4 motion-[0:opacity-0,100:opacity-100] motion-duration-200ms"],
            key: $"inbox-{item.Id}", content: view =>
        {
            view.Row(["gap-3 flex-1 min-w-0 items-start"], content: view =>
            {
                IconTile(view, InboxIcon(item.Kind), finished ? "neutral" : InboxTone(item));

                view.Column(["gap-1 flex-1 min-w-0"], content: view =>
                {
                    view.Row(["items-center gap-2 flex-wrap"], content: r =>
                    {
                        r.Text([Text.BodyStrong, "text-foreground"], item.Title);

                        if (status == InboxStatus.Snoozed && item.SnoozedUntil is { } until)
                        {
                            r.Badge($"Snoozed to {Fin.Day(until)}", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                        }
                        else if (status == InboxStatus.Done)
                        {
                            r.Badge(item.Kind == InboxKind.Bill ? "Paid" : "Done", tone: SemanticTone.Success, size: BadgeSize.Sm);
                        }
                        else if (status == InboxStatus.Archived)
                        {
                            r.Badge("Archived", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                        }
                        else if (item.Kind == InboxKind.Bill && item.DueDate is { } d && d < Today)
                        {
                            r.Badge("Overdue", tone: SemanticTone.Error, size: BadgeSize.Sm);
                        }
                        else if (IsOpenBill(item) && item.DueDate is { } soon && soon <= Today.AddDays(3))
                        {
                            r.Badge("Due soon", tone: SemanticTone.Warning, size: BadgeSize.Sm);
                        }
                        else if (IsOpenBill(item))
                        {
                            r.Badge("Unpaid", tone: SemanticTone.Info, size: BadgeSize.Sm);
                        }
                    });

                    view.Text([Text.Caption, "text-muted-foreground"],
                        $"{item.From} · {KindLabel(item.Kind).TrimEnd('s')} · received {Fin.Day(item.ReceivedAt)}");
                    view.Text([Text.BodySm, "text-foreground/80"], item.Detail);
                });
            });

            view.Column(["gap-2 sm:items-end shrink-0 sm:w-56"], content: view =>
            {
                if (item.Amount is { } amount)
                {
                    view.Text([Text.H5, "tabular-nums text-foreground"], item.Kind == InboxKind.Income ? $"+{Fin.Eur(amount, 2)}" : Fin.Eur(amount, 2));
                }

                if (item.DueDate is { } due && !finished)
                {
                    view.Text([Text.Caption, due < Today ? "text-error-600 font-semibold" : "text-muted-foreground"],
                        item.Kind == InboxKind.Income ? $"expected {Fin.Day(due)}" : item.Kind == InboxKind.Bill ? DueText(due) : $"from {Fin.Day(due)}");
                }

                view.Row(["gap-2 flex-wrap sm:justify-end pt-1"], content: view =>
                {
                    if (finished)
                    {
                        view.Button([Button.GhostSm], text: "Move back", ariaLabel: $"Move back {item.Title}",
                            onClick: () => SetInboxStatus(item, InboxStatus.New, "Moved back"));
                        return;
                    }

                    if (item.Kind == InboxKind.Bill && item.Amount is > 0)
                    {
                        view.Button([Button.PrimarySm], text: "Mark paid", ariaLabel: $"Mark paid {item.Title}", onClick: () => MarkBillPaid(item));
                    }
                    else
                    {
                        view.Button([Button.OutlineSm], text: "Done", ariaLabel: $"Done {item.Title}", onClick: () => SetInboxStatus(item, InboxStatus.Done, "Marked as done"));
                    }

                    RenderSnoozeMenu(view, item);

                    view.Button([Button.GhostSm, "px-2"], ariaLabel: $"Archive {item.Title}", tooltip: "Archive",
                        onClick: () => SetInboxStatus(item, InboxStatus.Archived, "Archived"),
                        content: v => v.Icon([Icon.Sm], name: "archive"));
                });
            });
        });
    }

    private readonly ClientReactive<string?> _snoozeOpen = new((string?)null);

    private void RenderSnoozeMenu(UIView view, InboxItem item)
    {
        view.DropdownMenu(
            open: _snoozeOpen.Value == item.Id,
            onOpenChange: async open => _snoozeOpen.Value = open ? item.Id : null,
            contentStyle: [Menu.Content],
            trigger: v => v.Button([Button.OutlineSm], text: "Snooze", ariaLabel: $"Snooze {item.Title}", icon: "alarm-clock"),
            content: v =>
            {
                v.Text([Menu.Label], text: "Remind me");

                var payday = NextPayday();
                var options = new List<(string Label, DateTime Until)>
                {
                    ("Tomorrow", Today.AddDays(1).AddHours(8)),
                    ("In 3 days", Today.AddDays(3).AddHours(8)),
                    ($"On payday ({Fin.Day(payday)})", payday.AddHours(8)),
                };

                foreach (var (label, until) in options)
                {
                    v.Button([Menu.Item], text: label, onClick: () =>
                    {
                        _snoozeOpen.Value = null;
                        SetInboxStatus(item, InboxStatus.Snoozed, $"Snoozed to {Fin.Day(until)}", until);
                    });
                }
            });
    }
}
