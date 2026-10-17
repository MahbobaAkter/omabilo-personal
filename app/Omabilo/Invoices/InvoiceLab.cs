// Financial Inbox user test: a tester picks a fictional invoice email (or pastes one), Omabilo
// explains it with the rule-based InvoiceExplainer, and the tester answers seven questions.
//
// Storage: feedback goes to one table in the app's built-in IKON database, so answers from every
// tester land in one place the owner can read (`ikon db query`). The pasted message is never stored;
// a row keeps the example's name, a one-line summary of the result and the answers.

using System.Data.Common;
using System.Security.Cryptography;

public partial class OmabiloApp
{
    private readonly ClientReactive<string> _inboxMode = new("explain");

    private readonly ClientReactive<string> _labScenario = new("");
    private readonly UserReactive<string> _labPasted = new("");
    private readonly ClientReactive<InvoiceExplanation?> _labResult = new((InvoiceExplanation?)null);
    private readonly ClientReactive<string> _labResultFrom = new("");
    private readonly ClientReactive<IReadOnlyList<string>> _labWarnings = new(Array.Empty<string>());
    private readonly ClientReactive<string?> _labError = new((string?)null);

    private readonly ClientReactive<string> _fbRating = new("");
    private readonly ClientReactive<string> _fbClear = new("");
    private readonly ClientReactive<string> _fbNext = new("");
    private readonly ClientReactive<string> _fbUse = new("");
    private readonly UserReactive<string> _fbMissing = new("");
    private readonly UserReactive<string> _fbConfusing = new("");
    private readonly UserReactive<string> _fbFeature = new("");
    private readonly UserReactive<string> _fbComments = new("");
    private readonly ClientReactive<bool> _fbSaving = new(false);
    private readonly ClientReactive<string?> _fbError = new((string?)null);
    private readonly ClientReactive<IReadOnlyList<string>> _fbUnanswered = new(Array.Empty<string>());
    private readonly ClientReactive<TesterFeedback?> _fbSaved = new((TesterFeedback?)null);

    private const string FeedbackTable = "omabilo_test_feedback";

    private void RenderInboxScreen(UIView view)
    {
        view.ToggleGroupSingle([ToggleGroup.Root, "flex-wrap self-start"], value: _inboxMode.Value, ariaLabel: "Inbox view",
            onValueChange: async v =>
            {
                if (!string.IsNullOrEmpty(v))
                {
                    _inboxMode.Value = v;
                }
            },
            content: group =>
            {
                group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5"], value: "explain", content: v =>
                {
                    v.Icon([Icon.Sm], name: "sparkles");
                    v.Text(text: "Explain an invoice");
                });
                group.ToggleGroupItem([ToggleGroup.Item, "gap-1.5"], value: "items", content: v =>
                {
                    v.Icon([Icon.Sm], name: "inbox");
                    v.Text(text: "Bills & alerts");
                });
            });

        if (_inboxMode.Value == "items")
        {
            RenderInbox(view);
        }
        else
        {
            RenderInvoiceLab(view);
        }
    }

    // ── The test flow ───────────────────────────────────────────────────────

    private void RenderInvoiceLab(UIView view)
    {
        var scenarios = InvoiceTestScenarios.All(Today);

        view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-3"], content: view =>
        {
            view.Row(["items-center gap-2 flex-wrap"], content: r =>
            {
                r.Heading("Explain an invoice email", style: [Text.H4, "m-0 text-foreground"], level: 2);
                r.Badge("Prototype", tone: SemanticTone.Info, size: BadgeSize.Sm);
                r.Badge("Demo data only", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
            });
            view.Text([Text.Body, "text-foreground/90"],
                "Pick a fictional example, or paste a fictional invoice email. Omabilo explains what it is and what to do next. Then tell us what you think.");
            view.Row(["items-start gap-2 rounded-xl bg-muted p-3"], content: r =>
            {
                r.Icon([Icon.Sm, "text-muted-foreground mt-0.5 shrink-0"], name: "info");
                r.Text([Text.BodySm, "text-muted-foreground"],
                    "This prototype reads the message with fixed rules, not AI. It isn't connected to any email or bank account. Please don't paste real invoices or personal details such as your identity code or bank details.");
            });
        });

        // Step 1: choose
        view.Column(["gap-3"], content: view =>
        {
            StepTitle(view, 1, "Choose a message");

            view.Grid(["grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3"], content: grid =>
            {
                var icons = new Dictionary<string, string> { ["normal"] = "receipt", ["reminder"] = "alarm-clock", ["unclear"] = "circle-help" };

                foreach (var s in scenarios)
                {
                    ScenarioCard(grid, s.Key, s.Title, s.Summary, icons.GetValueOrDefault(s.Key, "mail"), "Demo data");
                }

                ScenarioCard(grid, "paste", "Paste a message", "Use your own fictional invoice text", "clipboard-paste", "Fictional only");
            });

            if (_labScenario.Value == "paste")
            {
                view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-2"], content: v =>
                {
                    v.TextArea(["default", "min-h-48 font-mono text-sm"], bind: _labPasted, rows: 10, maxLength: InvoiceExplainer.MaxLength,
                        label: "Fictional invoice email",
                        placeholder: "From: Example Company <billing@example.com>\nSubject: Your invoice\n\nAmount due: 25,00 €\nDue date: 30.11.2026\nReference: 12345672");
                    v.Text([Text.Caption, "text-muted-foreground"],
                        $"{_labPasted.Value.Length} / {InvoiceExplainer.MaxLength} characters. What you paste is only read here; it's never saved.");
                });
            }
            else if (scenarios.FirstOrDefault(s => s.Key == _labScenario.Value) is { } chosen)
            {
                view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-2"], content: v =>
                {
                    v.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
                    {
                        r.Text([Text.Label, "text-foreground"], $"Email: {chosen.Title}");
                        r.Badge("Fictional demo email", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                    });
                    v.Box(["rounded-xl border border-secondary bg-background p-3 sm:p-4 max-h-80 overflow-auto"], content: b =>
                        b.Text(["text-sm font-mono whitespace-pre-wrap break-words text-foreground"], chosen.Text));
                });
            }
        });

        // Step 2: analyze
        view.Column(["gap-3"], content: view =>
        {
            StepTitle(view, 2, "Ask Omabilo");

            view.Row(["items-center gap-3 flex-wrap"], content: r =>
            {
                r.Button([Button.PrimaryLg, "min-h-11 w-full sm:w-auto"], text: "Analyze invoice", icon: "scan-text",
                    disabled: _labScenario.Value == "",
                    onClick: AnalyzeInvoice);

                if (_labScenario.Value == "")
                {
                    r.Text([Text.BodySm, "text-muted-foreground"], "Choose a message above first.");
                }
            });

            if (_labError.Value is { } error)
            {
                view.Alert("Can't analyze this yet", tone: SemanticTone.Error, description: error, key: "lab-error");
            }
        });

        if (_labResult.Value is { } result)
        {
            RenderLabResult(view, result);
            RenderFeedbackForm(view, result);
        }
        else
        {
            view.EmptyState("No analysis yet", description: "Choose a message and press Analyze invoice. The explanation and a short feedback form appear here.", icon: "file-search");
        }
    }

    private static void StepTitle(UIView view, int number, string title)
    {
        view.Row(["items-center gap-3"], content: r =>
        {
            r.Box(["w-8 h-8 rounded-full bg-brand-solid flex items-center justify-center shrink-0"], content: b =>
                b.Text(["text-sm font-bold text-primary-on-brand"], number.ToString()));
            r.Heading(title, style: [Text.H5, "m-0 text-foreground"], level: 3);
        });
    }

    private void ScenarioCard(UIView view, string key, string title, string summary, string iconName, string badge)
    {
        var selected = _labScenario.Value == key;

        view.Box([selected ? "rounded-2xl border-2 border-brand bg-brand-solid/5 p-4" : "rounded-2xl border border-secondary bg-card p-4 hover:border-brand",
                "flex flex-col gap-2 cursor-pointer text-left min-h-28 transition-colors duration-150"],
            key: $"scenario-{key}",
            ariaLabel: $"{title}: {summary}{(selected ? " (selected)" : "")}",
            onClick: () => SelectScenario(key),
            content: v =>
            {
                v.Row(["items-center justify-between gap-2"], content: r =>
                {
                    IconTile(r, iconName, selected ? "brand" : "neutral", "w-9 h-9");
                    r.Badge(badge, tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                });
                v.Text([Text.BodyStrong, "text-foreground"], title);
                v.Text([Text.Caption, "text-muted-foreground"], summary);
            });
    }

    private void SelectScenario(string key)
    {
        if (_labScenario.Value == key)
        {
            return;
        }

        _labScenario.Value = key;
        _labError.Value = null;
        _labResult.Value = null;
        ResetFeedback();
    }

    private void AnalyzeInvoice()
    {
        var scenario = InvoiceTestScenarios.All(Today).FirstOrDefault(s => s.Key == _labScenario.Value);
        var text = scenario?.Text ?? _labPasted.Value;
        var problem = _labScenario.Value == "" ? "Choose a message first." : InvoiceExplainer.Validate(text);

        _labResult.Value = null;
        ResetFeedback();

        if (problem != null)
        {
            _labError.Value = problem;
            return;
        }

        try
        {
            _labError.Value = null;
            _labWarnings.Value = scenario == null ? InvoiceExplainer.SensitiveWarnings(text) : Array.Empty<string>();
            _labResultFrom.Value = scenario?.Title ?? "Pasted message";
            _labResult.Value = InvoiceExplainer.Explain(text, Today);
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Invoice explainer failed: {ex.GetType().Name}");
            _labError.Value = "Something went wrong while reading this message. Try again, or try one of the demo examples.";
        }
    }

    // ── Result ──────────────────────────────────────────────────────────────

    private static (SemanticTone Tone, string Icon) UrgencyLook(Urgency u) => u switch
    {
        Urgency.Overdue => (SemanticTone.Error, "circle-alert"),
        Urgency.ActNow => (SemanticTone.Warning, "alarm-clock"),
        Urgency.CheckFirst => (SemanticTone.Warning, "shield-alert"),
        Urgency.Soon => (SemanticTone.Info, "calendar-clock"),
        Urgency.NotUrgent => (SemanticTone.Success, "circle-check"),
        _ => (SemanticTone.Neutral, "circle-help"),
    };

    private static string ToneKey(SemanticTone tone) => tone switch
    {
        SemanticTone.Error => "error",
        SemanticTone.Warning => "warning",
        SemanticTone.Success => "success",
        SemanticTone.Info => "info",
        _ => "neutral",
    };

    private void RenderLabResult(UIView view, InvoiceExplanation r)
    {
        view.Column(["gap-4 motion-[0:opacity-0_translate-y-[6px],100:opacity-100_translate-y-0] motion-duration-300ms"], key: "lab-result", content: view =>
        {
            StepTitle(view, 3, "What Omabilo understood");

            if (_labWarnings.Value.Count > 0)
            {
                view.Alert("Please use fictional content only", tone: SemanticTone.Warning,
                    description: $"{string.Join(" ", _labWarnings.Value)} Nothing you paste is saved, but please test with made-up details.");
            }

            var (tone, icon) = UrgencyLook(r.Urgency);

            view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-5"], content: view =>
            {
                view.Row(["items-center justify-between gap-2 flex-wrap"], content: row =>
                {
                    row.Row(["items-center gap-2 flex-wrap"], content: rr =>
                    {
                        rr.Icon([Icon.Sm, ToneTile(ToneKey(tone))[1]], name: icon);
                        rr.Badge(r.UrgencyLabel, tone: tone);
                        rr.Badge("Prototype result", tone: SemanticTone.Neutral, size: BadgeSize.Sm);
                    });
                    row.Text([Text.Caption, "text-muted-foreground"], $"From: {_labResultFrom.Value}");
                });

                view.Grid(["grid grid-cols-1 sm:grid-cols-2 gap-4"], content: grid =>
                {
                    Answer(grid, "What is this?", r.WhatIsThis, "file-text");
                    Answer(grid, "What needs my attention?", r.Attention, "eye");
                    Answer(grid, "What should I do next?", r.NextStep, "footprints");
                    Answer(grid, "When should I act?", r.WhenToAct, "calendar");
                });
            });

            if (r.CheckBeforePaying.Count > 0)
            {
                view.Alert("Check before you pay", tone: SemanticTone.Warning, icon: "shield-alert", content: c =>
                {
                    c.Column(["gap-1.5"], content: col =>
                    {
                        foreach (var item in r.CheckBeforePaying)
                        {
                            col.Text([Text.BodySm], $"• {item}");
                        }

                        col.Text([Text.Caption, "text-muted-foreground pt-1"],
                            "These are reasons to be careful, not proof that something is wrong. Omabilo can't confirm who really sent a message.");
                    });
                });
            }

            view.Grid(["grid grid-cols-1 lg:grid-cols-5 gap-4"], content: grid =>
            {
                grid.Box([Card.Default, "p-4 sm:p-5 lg:col-span-3 flex flex-col gap-1"], content: card =>
                {
                    card.Heading("Details found in the message", style: [Text.H6, "m-0 text-foreground"], level: 4);
                    card.Text([Text.Caption, "text-muted-foreground pb-2"],
                        "“In the message” means it was stated. “Guessed” means Omabilo inferred it. Nothing is made up.");

                    foreach (var f in r.Fields)
                    {
                        FieldRow(card, f);
                    }
                });

                grid.Column(["lg:col-span-2 gap-4"], content: side =>
                {
                    side.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-2"], content: card =>
                    {
                        card.Heading("Why it may have been sent", style: [Text.H6, "m-0 text-foreground"], level: 4);
                        card.Text([Text.BodySm, "text-foreground/90"], r.WhyItWasSent);
                    });

                    side.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-2"], content: card =>
                    {
                        card.Heading("Missing or uncertain", style: [Text.H6, "m-0 text-foreground"], level: 4);

                        if (r.MissingOrUncertain.Count == 0)
                        {
                            card.Text([Text.BodySm, "text-muted-foreground"], "Nothing important is missing.");
                        }

                        foreach (var m in r.MissingOrUncertain)
                        {
                            card.Row(["items-start gap-2"], content: row =>
                            {
                                row.Icon([Icon.Xs, "text-warning-600 dark:text-warning-400 mt-1 shrink-0"], name: "circle-dashed");
                                row.Text([Text.BodySm, "text-foreground/90"], m);
                            });
                        }
                    });

                    if (r.PayAttentionTo.Count > 0)
                    {
                        side.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-2"], content: card =>
                        {
                            card.Heading("Pay attention to", style: [Text.H6, "m-0 text-foreground"], level: 4);

                            foreach (var p in r.PayAttentionTo)
                            {
                                card.Text([Text.BodySm, "text-foreground/90"], $"• {p}");
                            }
                        });
                    }
                });
            });

            view.Text([Text.Caption, "text-muted-foreground"],
                "Omabilo can't see your bank account, so it never assumes a bill is paid. Suggestions are not financial advice.");
        });
    }

    private static void Answer(UIView view, string question, string answer, string iconName)
    {
        view.Column(["gap-1.5 min-w-0"], content: col =>
        {
            col.Row(["items-center gap-2"], content: r =>
            {
                r.Icon([Icon.Sm, "text-brand-secondary"], name: iconName);
                r.Text([Text.Label, "text-muted-foreground"], question);
            });
            col.Text([Text.Body, "text-foreground"], answer);
        });
    }

    private static void FieldRow(UIView view, ExplainedField f)
    {
        view.Column(["gap-0.5 py-2.5 border-t border-secondary first:border-t-0"], content: col =>
        {
            col.Box(["flex flex-col sm:flex-row sm:items-start sm:justify-between gap-1 sm:gap-3"], content: r =>
            {
                r.Text([Text.BodySm, "text-muted-foreground shrink-0 sm:w-40"], f.Label);
                r.Text([Text.BodyStrong, f.Value == null ? "text-muted-foreground italic flex-1 min-w-0 break-words" : "text-foreground flex-1 min-w-0 break-words"],
                    f.Value ?? "Not stated");

                var (label, tone) = f.Source switch
                {
                    FieldSource.Found => ("In the message", SemanticTone.Success),
                    FieldSource.Guessed => ("Guessed", SemanticTone.Warning),
                    _ => ("Not found", SemanticTone.Neutral),
                };
                r.Box(["self-start shrink-0"], content: b => b.Badge(label, tone: tone, size: BadgeSize.Sm));
            });

            if (f.Note != null)
            {
                col.Text([Text.Caption, "text-muted-foreground sm:pl-40"], f.Note);
            }
        });
    }

    // ── Feedback ────────────────────────────────────────────────────────────

    private void ResetFeedback()
    {
        _fbRating.Value = "";
        _fbClear.Value = "";
        _fbNext.Value = "";
        _fbUse.Value = "";
        _fbMissing.Value = "";
        _fbConfusing.Value = "";
        _fbFeature.Value = "";
        _fbComments.Value = "";
        _fbError.Value = null;
        _fbUnanswered.Value = Array.Empty<string>();
        _fbSaved.Value = null;
    }

    private TesterFeedback BuildFeedback(InvoiceExplanation r) => new(
        $"FB-{Guid.NewGuid():N}"[..11].ToUpperInvariant(),
        DateTime.UtcNow,
        TesterTag(),
        _labResultFrom.Value,
        $"{r.UrgencyLabel}. {r.WhatIsThis}",
        int.TryParse(_fbRating.Value, out var n) ? n : 0,
        _fbClear.Value,
        TesterFeedback.Clean(_fbMissing.Value),
        _fbNext.Value,
        TesterFeedback.Clean(_fbConfusing.Value),
        TesterFeedback.Clean(_fbFeature.Value),
        _fbUse.Value,
        TesterFeedback.Clean(_fbComments.Value));

    // The same tester gets the same short tag on every response, without storing who they are
    private static string TesterTag()
    {
        var user = ReactiveScope.UserIdOrNull ?? "anonymous";
        return "T-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omabilo-test:" + user)))[..8];
    }

    private void RenderFeedbackForm(UIView view, InvoiceExplanation r)
    {
        view.Column(["gap-3"], key: "lab-feedback", content: view =>
        {
            StepTitle(view, 4, "Give feedback");

            if (_fbSaved.Value is { } saved)
            {
                view.Box([Card.Elevated, "p-5 sm:p-6 flex flex-col gap-4"], content: card =>
                {
                    card.Row(["items-start gap-3"], content: row =>
                    {
                        IconTile(row, "circle-check", "success");
                        row.Column(["gap-1 min-w-0"], content: col =>
                        {
                            col.Text([Text.H5, "text-foreground"], "Thank you! Your feedback was saved.");
                            col.Text([Text.BodySm, "text-muted-foreground"],
                                $"Reference {saved.Id}. It's stored in Omabilo's test database, where only the Omabilo team can read it.");
                        });
                    });

                    card.Row(["gap-2 flex-wrap"], content: row =>
                    {
                        FeedbackCopyButtons(row, saved);
                        row.Button([Button.PrimaryMd], text: "Try another message", icon: "rotate-ccw", onClick: () =>
                        {
                            _labResult.Value = null;
                            _labScenario.Value = "";
                            ResetFeedback();
                        });
                    });
                });
                return;
            }

            view.Box([Card.Default, "p-4 sm:p-5 flex flex-col gap-5"], content: form =>
            {
                form.Text([Text.BodySm, "text-muted-foreground"],
                    "Your answers help decide what Omabilo should do next. Questions marked * are required; the rest are optional.");

                ChoiceQuestion(form, "1. How useful was this result? *", _fbRating, [("1", "1"), ("2", "2"), ("3", "3"), ("4", "4"), ("5", "5")],
                    "1 = not useful, 5 = very useful");
                ChoiceQuestion(form, "2. Did Omabilo explain the invoice clearly? *", _fbClear, [("Yes", "Yes"), ("Partly", "Partly"), ("No", "No")]);
                form.TextArea(["default"], bind: _fbMissing, rows: 2, maxLength: TesterFeedback.MaxTextLength,
                    label: "3. Was any important information missing or incorrect?", placeholder: "For example: the due date was wrong");
                ChoiceQuestion(form, "4. Did the recommended next step make sense? *", _fbNext, [("Yes", "Yes"), ("Partly", "Partly"), ("No", "No")]);
                form.TextArea(["default"], bind: _fbConfusing, rows: 2, maxLength: TesterFeedback.MaxTextLength,
                    label: "5. What confused you or made you hesitate?");
                form.TextArea(["default"], bind: _fbFeature, rows: 2, maxLength: TesterFeedback.MaxTextLength,
                    label: "6. What one feature would make Omabilo more useful?");
                ChoiceQuestion(form, "7. Would you use this product in your everyday financial life? *", _fbUse, [("Yes", "Yes"), ("Maybe", "Maybe"), ("No", "No")]);
                form.TextArea(["default"], bind: _fbComments, rows: 3, maxLength: TesterFeedback.MaxTextLength,
                    label: "Anything else? (optional)");

                form.Row(["items-start gap-2 rounded-xl bg-muted p-3"], content: row =>
                {
                    row.Icon([Icon.Sm, "text-muted-foreground mt-0.5 shrink-0"], name: "lock");
                    row.Text([Text.Caption, "text-muted-foreground"],
                        "Where this goes: your answers are saved centrally in Omabilo's test database, which only the Omabilo team can read. The message you analyzed is not saved, only which example you used and a one-line summary of the result. Please don't write personal details.");
                });

                if (_fbUnanswered.Value.Count > 0)
                {
                    form.Alert("A few answers are missing", tone: SemanticTone.Warning,
                        description: $"Please answer: {string.Join(", ", _fbUnanswered.Value)}.");
                }

                if (_fbError.Value is { } error)
                {
                    form.Alert("Your feedback wasn't saved", tone: SemanticTone.Error, description: error, content: c =>
                        c.Row(["gap-2 flex-wrap pt-2"], content: row => FeedbackCopyButtons(row, BuildFeedback(r))));
                }

                form.Button([Button.PrimaryLg, "min-h-11 w-full sm:w-auto self-start"],
                    text: _fbSaving.Value ? "Saving…" : _fbError.Value != null ? "Try again" : "Submit feedback",
                    icon: _fbSaving.Value ? "loader-circle" : "send",
                    disabled: _fbSaving.Value,
                    onClick: async () => await SubmitFeedbackAsync(r));
            });
        });
    }

    private static void ChoiceQuestion(UIView view, string question, ClientReactive<string> answer, (string Value, string Label)[] options, string? hint = null)
    {
        view.Column(["gap-2"], content: col =>
        {
            col.Text([Text.Label, "text-foreground"], question);
            col.ToggleGroupSingle([ToggleGroup.Root, "flex-wrap self-start"], value: answer.Value, ariaLabel: question.TrimEnd('*', ' '),
                onValueChange: async v => answer.Value = v ?? "",
                content: group =>
                {
                    foreach (var (value, label) in options)
                    {
                        group.ToggleGroupItem([ToggleGroup.Item, "min-w-11 h-11 px-4"], value: value, content: v => v.Text(text: label));
                    }
                });

            if (hint != null)
            {
                col.Text([Text.Caption, "text-muted-foreground"], hint);
            }
        });
    }

    private static void FeedbackCopyButtons(UIView view, TesterFeedback f)
    {
        view.ActionButton([Button.OutlineMd], action: ActionKind.CopyToClipboard,
            options: new CopyToClipboardActionOptions { Text = f.ToText() },
            ariaLabel: "Copy my answers",
            content: v => v.Row(["items-center gap-2"], content: inner =>
            {
                inner.Icon([Icon.Sm], name: "copy");
                inner.Text([], "Copy my answers");
            }));
        view.ActionButton([Button.OutlineMd], action: ActionKind.DownloadFile,
            options: new DownloadFileActionOptions
            {
                FileName = $"omabilo-feedback-{f.Id}.txt",
                MimeType = "text/plain",
                Data = Encoding.UTF8.GetBytes(f.ToText()),
            },
            ariaLabel: "Download my answers",
            content: v => v.Row(["items-center gap-2"], content: inner =>
            {
                inner.Icon([Icon.Sm], name: "download");
                inner.Text([], "Download my answers");
            }));
    }

    private async Task SubmitFeedbackAsync(InvoiceExplanation r)
    {
        if (_fbSaving.Value)
        {
            return;
        }

        var unanswered = TesterFeedback.MissingAnswers(int.TryParse(_fbRating.Value, out var n) ? n : 0, _fbClear.Value, _fbNext.Value, _fbUse.Value);
        _fbUnanswered.Value = unanswered;

        if (unanswered.Count > 0)
        {
            return;
        }

        _fbSaving.Value = true;
        _fbError.Value = null;
        var feedback = BuildFeedback(r);

        try
        {
            await SaveFeedbackAsync(feedback);
            _fbSaved.Value = feedback;
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"Saving tester feedback failed: {ex.GetType().Name}");
            _fbError.Value = "We couldn't reach the feedback store. Your answers are still here, so press Try again. You can also copy them and send them to the Omabilo team.";
        }
        finally
        {
            _fbSaving.Value = false;
        }
    }

    private async Task SaveFeedbackAsync(TesterFeedback f)
    {
        await using var conn = await app.DatabaseAsync();
        await conn.OpenAsync();

        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = $"""
                create table if not exists {FeedbackTable} (
                    id text primary key,
                    created_utc timestamptz not null,
                    tester_tag text not null,
                    scenario text not null,
                    result_summary text not null,
                    usefulness int not null,
                    explained_clearly text not null,
                    missing_or_incorrect text,
                    next_step_made_sense text not null,
                    confusing text,
                    wished_feature text,
                    would_use text not null,
                    comments text
                )
                """;
            await ddl.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            insert into {FeedbackTable} (id, created_utc, tester_tag, scenario, result_summary, usefulness, explained_clearly,
                missing_or_incorrect, next_step_made_sense, confusing, wished_feature, would_use, comments)
            values (@id, @created, @tag, @scenario, @summary, @usefulness, @clear, @missing, @next, @confusing, @feature, @use, @comments)
            """;
        AddParam(cmd, "id", f.Id);
        AddParam(cmd, "created", f.CreatedUtc);
        AddParam(cmd, "tag", f.TesterTag);
        AddParam(cmd, "scenario", f.Scenario);
        AddParam(cmd, "summary", f.ResultSummary);
        AddParam(cmd, "usefulness", f.Usefulness);
        AddParam(cmd, "clear", f.ExplainedClearly);
        AddParam(cmd, "missing", f.MissingOrIncorrect);
        AddParam(cmd, "next", f.NextStepMadeSense);
        AddParam(cmd, "confusing", f.Confusing);
        AddParam(cmd, "feature", f.WishedFeature);
        AddParam(cmd, "use", f.WouldUse);
        AddParam(cmd, "comments", f.Comments);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
