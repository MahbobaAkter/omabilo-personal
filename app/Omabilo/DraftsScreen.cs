// "Ask for more time": a draft email to the company behind a bill, for the person to read, edit and
// copy into their own email. Omabilo never sends it. Built by PaymentDrafts from the saved bill only.

public partial class OmabiloApp
{
    private readonly ClientReactive<string?> _draftItem = new((string?)null);
    private readonly ClientReactive<string> _draftLang = new("en");
    private readonly ClientReactive<string> _draftDate = new("");

    private void OpenExtensionDraft(FinancialItem item)
    {
        _draftItem.Value = item.Id;
        _draftDate.Value = PaymentDrafts.DefaultNewDate(item, DateTime.UtcNow.Date).ToString("d.M.yyyy", CultureInfo.InvariantCulture);
    }

    private void RenderExtensionDraft(UIView view, FinancialItem item)
    {
        if (_draftItem.Value != item.Id)
        {
            return;
        }

        var today = DateTime.UtcNow.Date;
        var parsed = DateTime.TryParseExact(_draftDate.Value.Trim(), "d.M.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var newDate);
        var dateError = !parsed ? "Write the date as d.m.yyyy, for example 15.11.2026." : newDate.Date <= today ? "Pick a date after today." : null;
        var name = _me.Value.SignedIn ? _me.Value.Name : null;

        view.Box([Card.Elevated, "p-4 flex flex-col gap-3"], key: $"draft-{item.Id}", content: c =>
        {
            c.Row(["items-center justify-between gap-2 flex-wrap"], content: r =>
            {
                r.Row(["items-center gap-2"], content: t =>
                {
                    t.Icon([Icon.Sm, "text-brand-secondary"], name: "mail-plus");
                    t.Text([Text.Label, "text-foreground"], "Ask for more time to pay");
                });
                r.Button([Button.GhostSm], text: "Close", onClick: () => _draftItem.Value = null);
            });

            c.Text([Text.BodySm, "text-muted-foreground"], "A draft for you to read, edit and send from your own email. Omabilo doesn't send anything. It uses only this bill's saved details.");

            c.Row(["gap-3 flex-wrap items-end"], content: r =>
            {
                r.TextField(["w-44"], label: "New date (d.m.yyyy)", bind: _draftDate);
                r.ToggleGroupSingle([ToggleGroup.Root], value: _draftLang.Value, ariaLabel: "Draft language",
                    onValueChange: async v => { if (!string.IsNullOrEmpty(v)) _draftLang.Value = v; },
                    content: g =>
                    {
                        g.ToggleGroupItem([ToggleGroup.Item], value: "en", content: v => v.Text(text: "English"));
                        g.ToggleGroupItem([ToggleGroup.Item], value: "fi", content: v => v.Text(text: "Suomi"));
                    });
            });

            if (dateError != null)
            {
                c.Text([Text.BodySm, "text-error-600 dark:text-error-400"], dateError);
                return;
            }

            var draft = PaymentDrafts.Extension(item, newDate, name, _draftLang.Value);

            if (draft.Gaps.Count > 0)
            {
                c.Alert("Add these before sending", tone: SemanticTone.Warning,
                    description: $"The bill didn't say: {string.Join(", ", draft.Gaps).ToLowerInvariant()}. Find them on the original bill and add them to the draft.");
            }

            c.Column(["gap-1"], content: s =>
            {
                s.Text([Text.Caption, "text-muted-foreground"], "Subject");
                s.Text([Text.BodySm, "text-foreground font-semibold"], draft.Subject);
            });
            c.Text(["text-sm whitespace-pre-wrap break-words text-foreground rounded-xl bg-muted p-3"], draft.Body);

            c.Row(["gap-2 flex-wrap"], content: r =>
            {
                r.ActionButton([Button.PrimarySm], action: ActionKind.CopyToClipboard,
                    options: new CopyToClipboardActionOptions { Text = $"{draft.Subject}\n\n{draft.Body}" },
                    ariaLabel: "Copy draft",
                    content: b => b.Row(["items-center gap-2"], content: inner =>
                    {
                        inner.Icon([Icon.Sm], name: "copy");
                        inner.Text(text: "Copy draft");
                    }));
            });

            c.Text([Text.Caption, "text-muted-foreground"], "Asking for more time doesn't change the due date until the company agrees. Correct the due date here once they confirm.");
        });
    }
}
