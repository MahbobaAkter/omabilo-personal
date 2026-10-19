public partial class OmabiloApp
{
    private readonly ClientReactive<string> _invFilter = new("all");
    private readonly ClientReactive<string> _invSearch = new("");
    private readonly ClientReactive<string?> _invoiceDetail = new((string?)null);
    private readonly ClientReactive<(string Text, string Tone, string? InvoiceId)?> _invoiceNotice = new(((string, string, string?)?)null);
    private readonly ClientReactive<string?> _invDialog = new((string?)null);
    private readonly ClientReactive<string> _iProvider = new("");
    private readonly ClientReactive<string> _iNumber = new("");
    private readonly ClientReactive<string> _iRef = new("");
    private readonly ClientReactive<string> _iAmount = new("");
    private readonly ClientReactive<string> _iDue = new("");
    private readonly ClientReactive<string> _iDate = new("");
    private readonly ClientReactive<string?> _iError = new((string?)null);

    private static string SourceIcon(InvoiceSourceType t) => t switch
    {
        InvoiceSourceType.Manual => "pencil-line",
        InvoiceSourceType.ForwardedEmail => "mail",
        InvoiceSourceType.Document => "file-scan",
        InvoiceSourceType.Gmail => "mail-search",
        InvoiceSourceType.Outlook => "mailbox",
        InvoiceSourceType.FinnishProvider => "landmark",
        _ => "sparkles",
    };

    private bool AlreadyIngested(string owner, InvoiceSourceType type, string sourceRef)
    {
        var key = InvoiceKeys.Idempotency(owner, type, sourceRef);
        return _invoiceSources.Any(s => s.OwnerId == owner && s.IdempotencyKey == key);
    }

    private void RenderInvoices(UIView view)
    {
        if (InvoiceOwner() is not { } owner)
        {
            view.Box([Card.Default, "p-6"], content: v => v.EmptyState("Sign in to use Invoices",
                description: "Invoices are stored per person, so they need a signed-in account.", icon: "lock",
                action: a => a.Button([Button.PrimaryMd], text: "Sign in", onClick: async () => await SignInAsync("Sign in to keep your invoices"))));
            return;
        }

        var all = MyInvoices();
        var review = all.Where(i => i.State == InvoiceProcessingState.NeedsReview).OrderBy(i => i.DueDate).ToList();
        var openTotal = all.Where(i => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid).Sum(i => i.Amount);
        var linkedCopies = _invoiceSources.Count(s => s.OwnerId == owner && s.CanonicalInvoiceId != null) - all.Count(i => i.State != InvoiceProcessingState.Merged);
        var mergedCount = all.Count(i => i.State == InvoiceProcessingState.Merged);

        SectionHeader(view, "Your invoices", "Bills from every source in one place. The same invoice arriving twice is matched, never counted twice.", actions: a =>
            a.Button([Button.PrimaryMd], text: "Add invoice", icon: "plus", onClick: () => OpenInvoiceDialog(null)));

        view.Grid(["grid grid-cols-2 xl:grid-cols-4 gap-3 sm:gap-4"], content: view =>
        {
            StatTile(view, "To review", review.Count.ToString(), review.Count == 0 ? "Nothing waiting" : "Check what was read", "list-checks", review.Count > 0 ? "warning" : "success", null, "inv-review");
            StatTile(view, "Unpaid, confirmed", Fin.Eur(openTotal, 2), $"{all.Count(i => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid)} invoices", "receipt", "brand", null, "inv-open");
            StatTile(view, "Duplicates caught", Math.Max(0, linkedCopies).ToString(), mergedCount > 0 ? $"{mergedCount} merged by you" : "Copies linked, not double-counted", "copy-check", "info", null, "inv-dupes");
            StatTile(view, "Sources", $"{InvoiceSourceCatalog.All.Count(s => s.Availability != SourceAvailability.NotConnected)} of {InvoiceSourceCatalog.All.Count}", "Ready now; the rest are planned", "plug", "neutral", null, "inv-sources");
        });

        if (_invoiceNotice.Value is { } notice)
        {
            view.Alert(notice.Text, tone: notice.Tone switch
            {
                "success" => SemanticTone.Success,
                "warning" => SemanticTone.Warning,
                "error" => SemanticTone.Error,
                _ => SemanticTone.Info,
            }, key: $"notice-{notice.Text}",
            onDismiss: async () => _invoiceNotice.Value = null,
            action: notice.InvoiceId is { } nid && all.Any(i => i.Id == nid)
                ? a => a.Button([Button.OutlineSm], text: "Open invoice", onClick: () => _invoiceDetail.Value = nid)
                : null);
        }

        RenderSourcesCard(view, owner);

        if (review.Count > 0)
        {
            view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
            {
                SectionHeader(view, $"Review queue ({review.Count})", "Check what was read before it counts toward your bills. Low-confidence fields are highlighted.");

                foreach (var inv in review)
                {
                    RenderReviewItem(view, owner, inv, all);
                }
            });
        }

        RenderInvoiceList(view, all);
        RenderPrivacyCard(view, all.Count);
        RenderInvoiceDetail(view, owner);
        RenderInvoiceDialogs(view);
    }

    private void RenderSourcesCard(UIView view, string owner)
    {
        var nextEmail = SyntheticInvoices.Emails().FirstOrDefault(e => !e.Email.Attachments.All(a =>
            AlreadyIngested(owner, InvoiceSourceType.ForwardedEmail, $"email:{e.Email.MessageId}#{a.FileName}")));
        var docs = SyntheticInvoices.Documents();
        var demosLeft = SyntheticInvoices.DemoInvoices().Count(d => !AlreadyIngested(owner, d.Type, d.SourceRef));

        view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
        {
            SectionHeader(view, "Collect invoices", "Simulated sources use fictional sample invoices only. No mailbox or bank is connected.");

            view.Grid(["grid grid-cols-1 md:grid-cols-2 gap-3"], content: grid =>
            {
                foreach (var source in InvoiceSourceCatalog.All)
                {
                    grid.Box(["rounded-2xl border border-secondary p-4 flex flex-col gap-3", source.Availability == SourceAvailability.NotConnected ? "bg-muted/30" : "bg-card"],
                        key: $"source-{source.Type}", content: card =>
                    {
                        card.Row(["items-start gap-3"], content: r =>
                        {
                            IconTile(r, SourceIcon(source.Type), source.Availability switch
                            {
                                SourceAvailability.Available => "success",
                                SourceAvailability.Simulated => "info",
                                _ => "neutral",
                            }, "w-10 h-10");
                            r.Column(["flex-1 min-w-0 gap-1"], content: c =>
                            {
                                c.Row(["items-center gap-2 flex-wrap"], content: rr =>
                                {
                                    rr.Text([Text.BodyStrong, "text-foreground"], source.Name);
                                    rr.Badge(source.Availability switch
                                    {
                                        SourceAvailability.Available => "Available",
                                        SourceAvailability.Simulated => "Simulated",
                                        _ => "Not connected",
                                    }, tone: source.Availability switch
                                    {
                                        SourceAvailability.Available => SemanticTone.Success,
                                        SourceAvailability.Simulated => SemanticTone.Info,
                                        _ => SemanticTone.Neutral,
                                    }, size: BadgeSize.Sm);
                                });
                                c.Text([Text.Caption, "text-muted-foreground"], source.Status);
                            });
                        });

                        card.Row(["gap-2 flex-wrap"], content: actions =>
                        {
                            switch (source.Type)
                            {
                                case InvoiceSourceType.Manual:
                                    actions.Button([Button.OutlineSm], text: "Type in an invoice", onClick: () => OpenInvoiceDialog(null));
                                    actions.Button([Button.GhostSm], text: demosLeft > 0 ? $"Load {demosLeft} demo invoices" : "Demo invoices loaded",
                                        disabled: demosLeft == 0,
                                        onClick: async () => await IngestAsync(SyntheticInvoices.DemoInvoices().Where(d => !AlreadyIngested(owner, d.Type, d.SourceRef)).ToList(), "demo invoices"));
                                    break;

                                case InvoiceSourceType.ForwardedEmail:
                                    actions.Button([Button.OutlineSm], text: nextEmail != null ? $"Simulate email: {nextEmail.Title}" : "All sample emails delivered",
                                        icon: "mail-plus", disabled: nextEmail == null,
                                        onClick: async () =>
                                        {
                                            if (nextEmail != null)
                                            {
                                                var subs = await EmailInvoiceReader.ReadAsync(nextEmail.Email, _extractor, isSimulated: true);
                                                await IngestAsync(subs, "a simulated email");
                                            }
                                        });
                                    break;

                                case InvoiceSourceType.Document:
                                    foreach (var (key, title, doc) in docs)
                                    {
                                        var done = AlreadyIngested(owner, InvoiceSourceType.Document, $"doc:{key}");
                                        actions.Button([Button.OutlineSm], text: done ? $"{title} (read)" : $"Simulate scan: {title}", icon: "scan-line",
                                            key: $"doc-{key}", disabled: done,
                                            onClick: async () =>
                                            {
                                                var fields = await _extractor.ExtractAsync(doc);
                                                await IngestAsync([new InvoiceSubmission(InvoiceSourceType.Document, $"doc:{key}", true, fields,
                                                    TextInvoiceExtractor.Overall(fields), $"Scanned “{doc.FileName}”")], "a simulated scan");
                                            });
                                    }

                                    break;

                                default:
                                    actions.Button([Button.OutlineSm], text: "Connect", disabled: true, ariaLabel: $"Connect {source.Name} (not available yet)");
                                    break;
                            }
                        });
                    });
                }
            });
        });
    }

    private void RenderReviewItem(UIView view, string owner, CanonicalInvoice inv, IReadOnlyList<CanonicalInvoice> all)
    {
        var source = _invoiceSources.FirstOrDefault(s => s.Id == inv.SourceRecordIds.FirstOrDefault());
        var duplicateOf = inv.PossibleDuplicateOfId != null ? all.FirstOrDefault(i => i.Id == inv.PossibleDuplicateOfId) : null;

        view.Box(["rounded-2xl border border-secondary p-4 flex flex-col gap-3 motion-[0:opacity-0,100:opacity-100] motion-duration-200ms"], key: $"review-{inv.Id}", content: view =>
        {
            view.Row(["items-start gap-3 flex-wrap"], content: r =>
            {
                IconTile(r, SourceIcon(inv.PrimarySource), "warning");
                r.Column(["flex-1 min-w-0 gap-0.5"], content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground"], inv.Provider);
                    c.Text([Text.Caption, "text-muted-foreground"],
                        $"{source?.SourceLabel ?? InvoiceBook.Label(inv.PrimarySource)} · read with {Fin.Pct(inv.Confidence * 100)} confidence{(source?.IsSimulated == true ? " · simulated" : "")}");
                });
                r.Text([Text.H5, "tabular-nums text-foreground"], Fin.Eur(inv.Amount, 2));
            });

            view.Grid(["grid grid-cols-2 sm:grid-cols-4 gap-2"], content: g =>
            {
                ReviewField(g, "Invoice no.", inv.InvoiceNumber, source?.Fields.ConfidenceOf("invoiceNumber"));
                ReviewField(g, "Reference", inv.Reference, source?.Fields.ConfidenceOf("reference"));
                ReviewField(g, "Due date", Fin.Day(inv.DueDate), source?.Fields.ConfidenceOf("dueDate"));
                ReviewField(g, "Amount", Fin.Eur(inv.Amount, 2), source?.Fields.ConfidenceOf("amount"));
            });

            if (duplicateOf != null)
            {
                view.Alert($"Looks like an invoice you already have: {duplicateOf.Provider} · {Fin.Eur(duplicateOf.Amount, 2)} due {Fin.Day(duplicateOf.DueDate)}",
                    tone: SemanticTone.Warning, description: "Same provider and amount, with due dates within 3 days of each other. If it's the same bill arriving twice, choose Merge so it only counts once. If it's a different bill (for example next month's), choose Keep both. Nothing is merged without you.",
                    action: a =>
                    {
                        a.Button([Button.OutlineSm], text: "Merge", ariaLabel: $"Merge {inv.Provider} into existing", onClick: () =>
                            RunInvoiceAction($"Merged {inv.Provider}", (b, o) => b.MergeInto(o, inv.Id, duplicateOf.Id, DateTime.UtcNow)));
                        a.Button([Button.GhostSm], text: "Keep both", ariaLabel: $"Keep both {inv.Provider}", onClick: () =>
                            RunInvoiceAction($"Kept {inv.Provider} separate", (b, o) => b.KeepSeparate(o, inv.Id, DateTime.UtcNow)));
                    });
            }

            view.Row(["gap-2 flex-wrap items-center"], content: r =>
            {
                var blocked = duplicateOf != null;
                r.Button([Button.PrimarySm], text: "Approve", ariaLabel: $"Approve {inv.Provider}", disabled: blocked,
                    onClick: () => RunInvoiceAction($"Approved {inv.Provider}", (b, o) => b.Approve(o, inv.Id, DateTime.UtcNow)));
                r.Button([Button.OutlineSm], text: "Edit fields", ariaLabel: $"Edit fields {inv.Provider}", onClick: () => OpenInvoiceDialog(inv));
                r.Button([Button.GhostErrorSm], text: "Reject", ariaLabel: $"Reject {inv.Provider}",
                    onClick: () => RunInvoiceAction($"Rejected {inv.Provider}", (b, o) => b.Reject(o, inv.Id, DateTime.UtcNow)));

                if (blocked)
                {
                    r.Text([Text.Caption, "text-muted-foreground"], "Decide on the possible duplicate first.");
                }
            });
        });
    }

    private static void ReviewField(UIView view, string label, string? value, double? confidence)
    {
        var low = confidence is < 0.8;
        view.Column([low ? "rounded-xl bg-warning-500/10 border border-warning-500/40 p-2.5 gap-0.5" : "rounded-xl bg-muted/50 p-2.5 gap-0.5"], key: $"rf-{label}", content: c =>
        {
            c.Row(["items-center justify-between gap-1"], content: r =>
            {
                r.Text([Text.Caption, "text-muted-foreground"], label);

                if (confidence is { } conf)
                {
                    r.Text([Text.Caption, "tabular-nums", low ? "text-warning-700 dark:text-warning-400 font-semibold" : "text-muted-foreground"], value == null ? "missing" : Fin.Pct(conf * 100));
                }
            });
            c.Text([Text.BodySm, "font-medium truncate", value == null ? "text-muted-foreground italic" : "text-foreground"], value ?? "Not found");
        });
    }

    private bool InvoiceInFilter(CanonicalInvoice i, string filter) => filter switch
    {
        "unpaid" => i.CountsInTotals && i.PaymentStatus == InvoicePaymentStatus.Unpaid,
        "paid" => i.PaymentStatus == InvoicePaymentStatus.Paid && i.State != InvoiceProcessingState.Merged,
        "review" => i.State == InvoiceProcessingState.NeedsReview,
        "closed" => i.State is InvoiceProcessingState.Rejected or InvoiceProcessingState.Merged,
        _ => true,
    };

    private void RenderInvoiceList(UIView view, IReadOnlyList<CanonicalInvoice> all)
    {
        view.Box([Card.Default, "p-5 sm:p-6 flex flex-col gap-4"], content: view =>
        {
            SectionHeader(view, "All invoices", $"{all.Count} kept");

            view.Row(["items-center justify-between gap-3 flex-wrap"], content: view =>
            {
                view.ToggleGroupSingle([ToggleGroup.Root, "flex-wrap"], value: _invFilter.Value, ariaLabel: "Invoice status",
                    onValueChange: async v =>
                    {
                        if (!string.IsNullOrEmpty(v))
                        {
                            _invFilter.Value = v;
                        }
                    },
                    content: group =>
                    {
                        foreach (var (key, label) in new[] { ("all", "All"), ("unpaid", "Unpaid"), ("review", "To review"), ("paid", "Paid"), ("closed", "Rejected & merged") })
                        {
                            group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5"], value: key, content: v =>
                            {
                                v.Text(text: label);
                                v.Text(["text-xs tabular-nums opacity-70"], text: all.Count(i => InvoiceInFilter(i, key)).ToString());
                            });
                        }
                    });

                view.TextField([Input.DefaultSm, "w-full sm:w-60"], placeholder: "Search provider, number or reference", bind: _invSearch, ariaLabel: "Search invoices");
            });

            var q = _invSearch.Value.Trim();
            var rows = all.Where(i => InvoiceInFilter(i, _invFilter.Value))
                .Where(i => q.Length == 0 || i.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (i.InvoiceNumber ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (i.Reference ?? "").Replace(" ", "").Contains(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.PaymentStatus == InvoicePaymentStatus.Paid || !i.CountsInTotals).ThenBy(i => i.DueDate)
                .ToList();

            if (q.Length > 0)
            {
                view.Row(["flex-wrap gap-2"], content: r => FilterChip(r, $"Search: {q}", () => _invSearch.Value = ""));
            }

            if (rows.Count == 0)
            {
                view.EmptyState(all.Count == 0 ? "No invoices yet" : "No invoices match",
                    description: all.Count == 0 ? "Load the demo invoices or simulate an email above to see how collection works." : "Try another filter or search.",
                    icon: all.Count == 0 ? "receipt" : "search-x",
                    action: all.Count == 0 ? null : a => a.Button([Button.OutlineSm], text: "Show all", onClick: () => { _invFilter.Value = "all"; _invSearch.Value = ""; }));
                return;
            }

            view.Column(["divide-y divide-secondary"], content: list =>
            {
                foreach (var inv in rows)
                {
                    var sources = inv.SourceRecordIds.Count;
                    list.Box(["flex flex-row items-center gap-3 py-3 px-2 -mx-2 rounded-xl cursor-pointer hover:bg-muted/50 transition-colors"],
                        key: $"invrow-{inv.Id}", ariaLabel: $"Open {inv.Provider} invoice, {Fin.Eur(inv.Amount, 2)}",
                        onClick: () => _invoiceDetail.Value = inv.Id,
                        content: r =>
                        {
                            IconTile(r, SourceIcon(inv.PrimarySource), StatusTone(inv), "w-9 h-9");
                            r.Column(["flex-1 min-w-0 gap-0"], content: c =>
                            {
                                c.Text([Text.BodySm, "font-medium text-foreground truncate"], inv.Provider);
                                c.Text([Text.Caption, "text-muted-foreground truncate"],
                                    $"{inv.InvoiceNumber ?? "No number"} · due {Fin.Day(inv.DueDate)}{(sources > 1 ? $" · {sources} sources" : "")}");
                            });
                            r.Column(["items-end gap-1 shrink-0"], content: c =>
                            {
                                c.Text([Text.BodyStrong, "tabular-nums text-foreground", inv.CountsInTotals ? "" : "line-through opacity-60"], Fin.Eur(inv.Amount, 2));
                                StatusBadge(c, inv);
                            });
                        });
                }
            });
        });
    }

    private static string StatusTone(CanonicalInvoice inv) => inv switch
    {
        { State: InvoiceProcessingState.NeedsReview } => "warning",
        { State: InvoiceProcessingState.Rejected or InvoiceProcessingState.Merged } => "neutral",
        { PaymentStatus: InvoicePaymentStatus.Paid } => "success",
        _ when inv.DueDate < DateTime.UtcNow.Date => "error",
        _ => "brand",
    };

    private static void StatusBadge(UIView view, CanonicalInvoice inv)
    {
        var (text, tone) = inv switch
        {
            { State: InvoiceProcessingState.NeedsReview } => (inv.PossibleDuplicateOfId != null ? "Possible duplicate" : "To review", SemanticTone.Warning),
            { State: InvoiceProcessingState.Rejected } => ("Rejected", SemanticTone.Neutral),
            { State: InvoiceProcessingState.Merged } => ("Merged", SemanticTone.Neutral),
            { PaymentStatus: InvoicePaymentStatus.Paid } => ("Paid", SemanticTone.Success),
            _ when inv.DueDate < DateTime.UtcNow.Date => ("Overdue", SemanticTone.Error),
            _ when inv.DueDate <= DateTime.UtcNow.Date.AddDays(3) => ("Due soon", SemanticTone.Warning),
            _ => ("Unpaid", SemanticTone.Info),
        };
        view.Badge(text, tone: tone, size: BadgeSize.Sm);
    }

    private void RenderPrivacyCard(UIView view, int count)
    {
        view.Box([Card.Subtle, "p-5 sm:p-6 flex flex-col gap-3"], content: view =>
        {
            view.Row(["items-start gap-3"], content: r =>
            {
                IconTile(r, "shield-check", "brand");
                r.Column(["flex-1 min-w-0 gap-1"], content: c =>
                {
                    c.Text([Text.BodyStrong, "text-foreground"], "Your invoice data");
                    c.Text([Text.BodySm, "text-muted-foreground"],
                        "Stored under your account only, in the EU. Every change is recorded in the invoice's history without amounts or document text. Download a copy at any time, or delete it all.");
                });
            });

            view.Row(["gap-2 flex-wrap"], content: r =>
            {
                if (count == 0)
                {
                    r.Button([Button.OutlineSm], text: "Export invoice data", icon: "download", disabled: true, ariaLabel: "Export invoice data (nothing to export yet)");
                }
                else
                {
                    r.ActionButton([Button.OutlineSm], action: ActionKind.DownloadFile,
                        options: new DownloadFileActionOptions
                        {
                            FileName = $"omabilo-invoices-{DateTime.UtcNow:yyyy-MM-dd}.json",
                            MimeType = "application/json",
                            Data = ExportInvoiceData(),
                        },
                        ariaLabel: "Export invoice data as JSON",
                        content: v => v.Row(["items-center gap-2"], content: inner =>
                        {
                            inner.Icon([Icon.Sm], name: "download");
                            inner.Text([], "Export invoice data");
                        }));
                }
                r.Button([Button.OutlineErrorSm], text: "Delete all invoice data", disabled: count == 0, onClick: () => _invDialog.Value = "delete-all");
            });
        });
    }

    private void RenderInvoiceDetail(UIView view, string owner)
    {
        var inv = _invoiceDetail.Value is { } id ? _invoices.FirstOrDefault(i => i.Id == id && i.OwnerId == owner) : null;

        view.Sheet(open: inv != null, onOpenChange: async open => { if (!open) _invoiceDetail.Value = null; },
            title: inv?.Provider ?? "Invoice", description: inv != null ? $"{Fin.Eur(inv.Amount, 2)} · due {Fin.DayLong(inv.DueDate)}" : null,
            style: ["default", "w-full sm:max-w-lg"],
            content: v =>
            {
                if (inv == null)
                {
                    return;
                }

                var book = LoadBook();
                v.ScrollArea(rootStyle: ["flex-1 min-h-0 -mx-1"], content: s => s.Column(["gap-5 px-1 pb-6"], content: c =>
                {
                    c.Row(["gap-2 flex-wrap"], content: r =>
                    {
                        StatusBadge(r, inv);
                        r.Badge(inv.Verification == InvoiceVerification.Verified ? "Verified" : inv.Verification == InvoiceVerification.Rejected ? "Rejected" : "Unverified",
                            tone: inv.Verification == InvoiceVerification.Verified ? SemanticTone.Success : SemanticTone.Neutral, size: BadgeSize.Sm);
                    });

                    c.Grid(["grid grid-cols-2 gap-2"], content: g =>
                    {
                        MiniStat(g, "Invoice no.", inv.InvoiceNumber ?? "—");
                        MiniStat(g, "Reference", inv.Reference ?? "—");
                        MiniStat(g, "Invoice date", inv.InvoiceDate is { } d ? Fin.Day(d) : "—");
                        MiniStat(g, "Due date", Fin.Day(inv.DueDate));
                        MiniStat(g, "Amount", $"{Fin.Eur(inv.Amount, 2)} {(inv.Currency == "EUR" ? "" : inv.Currency)}".Trim());
                        MiniStat(g, "Confidence", Fin.Pct(inv.Confidence * 100));
                    });

                    if (inv.Reference != null)
                    {
                        c.Text([Text.Caption, PaymentReference.IsValid(inv.Reference) ? "text-success-600 dark:text-success-400" : "text-warning-700 dark:text-warning-400"],
                            PaymentReference.IsValid(inv.Reference) ? "✓ Payment reference check digit is valid" : "⚠ Payment reference check digit doesn't match. Check it before paying.");
                    }

                    c.Row(["gap-2 flex-wrap"], content: r =>
                    {
                        if (inv.State == InvoiceProcessingState.NeedsReview)
                        {
                            r.Button([Button.PrimarySm], text: "Approve", disabled: inv.PossibleDuplicateOfId != null,
                                onClick: () => RunInvoiceAction($"Approved {inv.Provider}", (b, o) => b.Approve(o, inv.Id, DateTime.UtcNow)));
                        }
                        else if (inv.CountsInTotals && inv.PaymentStatus == InvoicePaymentStatus.Unpaid)
                        {
                            r.Button([Button.PrimarySm], text: "Mark paid", onClick: () => MarkInvoicePaid(inv));
                        }
                        else if (inv.PaymentStatus == InvoicePaymentStatus.Paid)
                        {
                            r.Button([Button.OutlineSm], text: "Mark unpaid",
                                onClick: () => RunInvoiceAction($"Marked {inv.Provider} unpaid", (b, o) => b.MarkUnpaid(o, inv.Id, DateTime.UtcNow)));
                        }

                        if (inv.State is not (InvoiceProcessingState.Merged or InvoiceProcessingState.Rejected))
                        {
                            r.Button([Button.OutlineSm], text: "Edit", onClick: () => OpenInvoiceDialog(inv));
                            r.Button([Button.GhostErrorSm], text: "Reject",
                                onClick: () => RunInvoiceAction($"Rejected {inv.Provider}", (b, o) => b.Reject(o, inv.Id, DateTime.UtcNow)));
                        }
                    });

                    var sources = book.SourcesOf(owner, inv.Id);
                    c.Column(["gap-2"], content: sc =>
                    {
                        sc.Text([Text.Label], $"Sources ({sources.Count})");

                        foreach (var src in sources)
                        {
                            sc.Row(["items-center gap-3 rounded-xl border border-secondary p-3"], key: $"src-{src.Id}", content: r =>
                            {
                                r.Icon([Icon.Sm, "text-muted-foreground"], name: SourceIcon(src.Type));
                                r.Column(["flex-1 min-w-0 gap-0"], content: cc =>
                                {
                                    cc.Text([Text.BodySm, "text-foreground truncate"], src.SourceLabel ?? InvoiceBook.Label(src.Type));
                                    cc.Text([Text.Caption, "text-muted-foreground"], $"{InvoiceBook.Label(src.Type)} · {Fin.Day(src.ReceivedAt)} · key {src.IdempotencyKey[..8]}");
                                });

                                if (src.IsSimulated)
                                {
                                    r.Badge("Simulated", tone: SemanticTone.Info, size: BadgeSize.Sm);
                                }
                            });
                        }
                    });

                    c.Column(["gap-2"], content: ac =>
                    {
                        ac.Text([Text.Label], "History");

                        foreach (var e in book.AuditOf(owner, inv.Id))
                        {
                            ac.Row(["items-start gap-3"], key: $"evt-{e.Id}", content: r =>
                            {
                                r.Box(["w-2 h-2 rounded-full bg-brand-solid mt-1.5 shrink-0"]);
                                r.Column(["flex-1 min-w-0 gap-0"], content: cc =>
                                {
                                    cc.Text([Text.BodySm, "text-foreground"], e.Detail);
                                    cc.Text([Text.Caption, "text-muted-foreground"], $"{e.At.ToString("d MMM HH:mm", Fin.Inv)} UTC · {(e.Actor == "user" ? "you" : "Omabilo")}");
                                });
                            });
                        }
                    });
                }));
            });
    }

    private void MarkInvoicePaid(CanonicalInvoice inv)
    {
        // Paying through the linked bill also records the payment in your transactions
        if (inv.LinkedBillId != null && _inbox.FirstOrDefault(i => i.Id == inv.LinkedBillId) is { } bill && IsOpenBill(bill))
        {
            MarkBillPaid(bill);
            return;
        }

        RunInvoiceAction($"Marked {inv.Provider} paid", (b, o) => b.MarkPaid(o, inv.Id, DateTime.UtcNow));
    }

    // ── Add / edit / delete-all dialogs ─────────────────────────────────────

    private void OpenInvoiceDialog(CanonicalInvoice? inv)
    {
        _iProvider.Value = inv?.Provider ?? "";
        _iNumber.Value = inv?.InvoiceNumber ?? "";
        _iRef.Value = inv?.Reference ?? "";
        _iAmount.Value = inv?.Amount.ToString("0.00", Fin.Inv) ?? "";
        _iDue.Value = (inv?.DueDate ?? Today.AddDays(14)).ToString("yyyy-MM-dd", Fin.Inv);
        _iDate.Value = (inv?.InvoiceDate ?? Today).ToString("yyyy-MM-dd", Fin.Inv);
        _iError.Value = null;
        _invDialog.Value = inv == null ? "add" : $"edit:{inv.Id}";
    }

    private void RenderInvoiceDialogs(UIView view)
    {
        var d = _invDialog.Value ?? "";
        var editId = d.StartsWith("edit:", StringComparison.Ordinal) ? d[5..] : null;

        view.AlertDialog(open: d == "delete-all", onOpenChange: async open => { if (!open) _invDialog.Value = null; },
            title: "Delete all invoice data?",
            description: "This permanently removes every invoice, its sources and its history from your account. It can't be undone. Download a copy first if you want one.",
            cancelLabel: "Cancel", actionLabel: "Delete permanently", actionStyle: [Button.ErrorMd],
            onAction: async () =>
            {
                DeleteAllInvoiceData();
                _invDialog.Value = null;
            });

        view.Dialog(open: d is "add" || editId != null, onOpenChange: async o => { if (!o) _invDialog.Value = null; },
            showClose: true,
            title: editId != null ? "Edit invoice" : "Add an invoice",
            description: editId != null ? "Correct anything that was read wrong. The change is recorded in the invoice's history." : "Typed-in invoices are confirmed straight away.",
            content: v => v.Column(["gap-4 py-2"], content: f =>
            {
                f.TextField(label: "Provider", bind: _iProvider, placeholder: "e.g. Northwind Energy", autoFocus: true, maxLength: 80);
                f.Grid(["grid grid-cols-1 sm:grid-cols-2 gap-4"], content: g =>
                {
                    g.TextField(label: "Invoice number", bind: _iNumber, placeholder: "Optional", maxLength: 40);
                    g.TextField(label: "Payment reference", bind: _iRef, placeholder: "Optional, e.g. 1234 5612", maxLength: 40);
                    g.TextField(label: "Amount (€)", bind: _iAmount, type: "number", min: "0", step: "0.01");
                    g.DatePicker(label: "Due date", value: _iDue.Value, labelLocale: "en-GB", onValueChange: async v => _iDue.Value = v);
                    g.DatePicker(label: "Invoice date", value: _iDate.Value, labelLocale: "en-GB", onValueChange: async v => _iDate.Value = v);
                });

                if (_iRef.Value.Trim().Length > 0 && !PaymentReference.IsValid(_iRef.Value))
                {
                    f.Text([Text.Caption, "text-warning-700 dark:text-warning-400"], "This reference's check digit doesn't match a Finnish or RF reference. Double-check it.");
                }

                if (_iError.Value is { } error)
                {
                    f.Alert(tone: SemanticTone.Error, description: error);
                }
            }),
            footer: f =>
            {
                f.Button([Button.OutlineMd], text: "Cancel", onClick: () => _invDialog.Value = null);
                f.Button([Button.PrimaryMd], text: editId != null ? "Save changes" : "Add invoice", onClick: async () => await SaveInvoiceDialog(editId));
            });
    }

    private async Task SaveInvoiceDialog(string? editId)
    {
        _iError.Value = null;

        if (_iProvider.Value.Trim().Length == 0)
        {
            _iError.Value = "Enter who the invoice is from.";
            return;
        }

        if (!Fin.TryParseAmount(_iAmount.Value, out var amount) || amount <= 0)
        {
            _iError.Value = "Enter the amount, for example 58.20.";
            return;
        }

        var due = TextInvoiceExtractor.ParseDate(_iDue.Value);

        if (due == null)
        {
            _iError.Value = "Pick a due date.";
            return;
        }

        var fields = new ExtractedInvoiceFields(_iProvider.Value.Trim(), _iNumber.Value, _iRef.Value, TextInvoiceExtractor.ParseDate(_iDate.Value), due, amount, "EUR");

        if (editId == null)
        {
            await IngestAsync([ManualInvoiceSource.Submit(fields, Guid.NewGuid().ToString("N"))], "an invoice");
        }
        else
        {
            RunInvoiceAction($"Edited {fields.Provider}", (b, o) => b.Edit(o, editId, fields, DateTime.UtcNow));
        }

        _invDialog.Value = null;
    }
}
