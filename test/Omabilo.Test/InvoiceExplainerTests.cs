using Xunit;

public class InvoiceExplainerTests
{
    private static readonly DateTime Today = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static InvoiceExplanation Run(string key) =>
        InvoiceExplainer.Explain(InvoiceTestScenarios.All(Today).Single(s => s.Key == key).Text, Today);

    [Fact]
    public void Normal_invoice_reads_every_field_as_stated()
    {
        var r = Run("normal");

        Assert.True(r.LooksLikeInvoice);
        Assert.Equal("Revontuli Energy Oy", r.Field("Sender or company").Value);
        Assert.Equal(FieldSource.Found, r.Field("What it's for").Source);
        Assert.Equal(64.20m, r.Amount);
        Assert.Equal(Today.AddDays(12), r.DueDate);
        Assert.Equal(FieldSource.Found, r.Field("Reference number").Source);
        Assert.Equal("Unknown", r.Field("Payment status").Value);
        Assert.Equal(Urgency.Soon, r.Urgency);
        Assert.Empty(r.CheckBeforePaying);
        Assert.Contains("Pay 64,20 €", r.NextStep);
        Assert.False(r.SaysPaid);
    }

    [Fact]
    public void Reminder_is_recognised_with_its_fee_and_original_due_date()
    {
        var r = Run("reminder");

        Assert.True(r.IsReminder);
        Assert.Equal(44.90m, r.Amount);
        Assert.Equal(Urgency.ActNow, r.Urgency);
        Assert.Equal("Not paid yet, according to the sender", r.Field("Payment status").Value);
        Assert.Contains("12 days ago", r.Field("Due date").Note);
        Assert.Contains(r.PayAttentionTo, p => p.Contains("reminder fee of 5,00 €"));
        Assert.Contains("If you already paid", r.NextStep);
        Assert.Empty(r.CheckBeforePaying);
    }

    [Fact]
    public void Unclear_message_is_flagged_without_inventing_details_or_calling_it_fraud()
    {
        var r = Run("unclear");

        Assert.Equal(Urgency.CheckFirst, r.Urgency);
        Assert.Null(r.Amount);
        Assert.Null(r.DueDate);
        Assert.Equal(FieldSource.Missing, r.Field("Sender or company").Source);
        Assert.Equal(FieldSource.Missing, r.Field("Reference number").Source);
        Assert.Contains("several amounts", r.Field("Amount").Note);
        Assert.Equal(3, r.CheckBeforePaying.Count);
        Assert.Contains(r.MissingOrUncertain, m => m.Contains("due date"));
        Assert.DoesNotContain("fraud", r.WhatIsThis + r.Attention + r.NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scam", r.WhatIsThis + r.Attention + r.NextStep, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Overdue_invoice_without_reminder_words_is_overdue()
    {
        var r = InvoiceExplainer.Explain("Biller: Kuusi Water Oy\nFor: Water\nAmount due: 30,00 €\nDue date: 10.10.2026\nReference: 12345672", Today);

        Assert.Equal(Urgency.Overdue, r.Urgency);
        Assert.Equal("Overdue by 7 days", r.UrgencyLabel);
    }

    [Fact]
    public void Payment_is_called_paid_only_when_the_message_says_so()
    {
        var r = InvoiceExplainer.Explain("From: Kuusi Water <billing@kuusi.example>\nThank you for your payment of 30,00 € for water.", Today);

        Assert.True(r.SaysPaid);
        Assert.Equal(Urgency.NotUrgent, r.Urgency);
        Assert.Equal(FieldSource.Guessed, r.Field("Sender or company").Source);
        Assert.Equal(FieldSource.Guessed, r.Field("Amount").Source);
    }

    [Fact]
    public void A_wrong_reference_check_digit_is_flagged()
    {
        var r = InvoiceExplainer.Explain("Biller: Kuusi Water Oy\nAmount due: 30,00 €\nDue date: 30.10.2026\nReference: 12345671", Today);

        Assert.Contains(r.CheckBeforePaying, c => c.Contains("check digit"));
    }

    [Fact]
    public void Text_that_is_not_an_invoice_says_so()
    {
        var r = InvoiceExplainer.Explain("Hi! Are we still meeting for coffee on Friday afternoon?", Today);

        Assert.False(r.LooksLikeInvoice);
        Assert.Equal("Nothing to pay based on this message.", r.NextStep);
    }

    [Fact]
    public void Keywords_match_word_starts_only()
    {
        var r = InvoiceExplainer.Explain("Biller: Kuusi Oy\nYour current balance is shown below.\nAmount due: 30,00 €", Today);

        Assert.Equal(FieldSource.Missing, r.Field("What it's for").Source);
    }

    [Fact]
    public void Details_run_together_on_one_line_are_still_read()
    {
        var r = InvoiceExplainer.Explain("Biller: Kuusi Water Oy For: Water Amount due: 30,00 € Due date: 30.10.2026 Reference: 12345672", Today);

        Assert.Equal(30.00m, r.Amount);
        Assert.Equal(Today.AddDays(13), r.DueDate);
        Assert.Equal("Kuusi Water Oy", r.Field("Sender or company").Value);
        Assert.Equal(FieldSource.Found, r.Field("Reference number").Source);
    }

    [Theory]
    [InlineData("", "Paste a fictional")]
    [InlineData("   ", "Paste a fictional")]
    [InlineData("too short", "too short")]
    public void Invalid_input_is_explained(string text, string expected) =>
        Assert.Contains(expected, InvoiceExplainer.Validate(text));

    [Fact]
    public void Overlong_input_is_refused() =>
        Assert.NotNull(InvoiceExplainer.Validate(new string('a', InvoiceExplainer.MaxLength + 1)));

    [Fact]
    public void Demo_scenarios_are_valid_and_carry_no_sensitive_warnings()
    {
        foreach (var s in InvoiceTestScenarios.All(Today))
        {
            Assert.Null(InvoiceExplainer.Validate(s.Text));
            Assert.Empty(InvoiceExplainer.SensitiveWarnings(s.Text));
        }
    }

    [Fact]
    public void Identity_codes_and_card_numbers_trigger_a_warning()
    {
        Assert.NotEmpty(InvoiceExplainer.SensitiveWarnings("Customer 131052-308T owes money"));
        Assert.NotEmpty(InvoiceExplainer.SensitiveWarnings("Card 4111 1111 1111 1111"));
    }

    [Fact]
    public void Feedback_lists_unanswered_required_questions()
    {
        Assert.Equal(4, TesterFeedback.MissingAnswers(0, "", "", "").Count);
        Assert.Empty(TesterFeedback.MissingAnswers(4, "Yes", "Partly", "Maybe"));
        Assert.Null(TesterFeedback.Clean("   "));
        Assert.Equal(TesterFeedback.MaxTextLength, TesterFeedback.Clean(new string('x', 5000))!.Length);
    }
}
