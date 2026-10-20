// Draft messages a person can copy into their own email: asking a company for more time to pay a
// bill. Built only from the bill's saved details and the date the person picks; nothing is invented
// and nothing is sent. Plain C# with no IKON types, so the screen and the tests agree.

using System.Globalization;

public sealed record PaymentDraft(string Subject, string Body, IReadOnlyList<string> Gaps);

public static class PaymentDrafts
{
    /// <summary>The new date suggested when the person hasn't picked one: two weeks after the later of the due date and today.</summary>
    public static DateTime DefaultNewDate(FinancialItem bill, DateTime today)
    {
        var from = bill.DueDate is { } due && due.Date > today.Date ? due.Date : today.Date;
        return from.AddDays(14);
    }

    /// <summary>A polite request for more time. English or Finnish ("fi"). Gaps lists what the bill
    /// didn't say, so the person can add it before sending; the draft never fills it with a guess.</summary>
    public static PaymentDraft Extension(FinancialItem bill, DateTime newDate, string? senderName, string language = "en")
    {
        var fi = language == "fi";
        var company = string.IsNullOrWhiteSpace(bill.Counterparty) ? null : bill.Counterparty!.Trim();
        var gaps = new List<string>();

        if (company == null) gaps.Add(fi ? "Laskuttajan nimi" : "The company's name");
        if (bill.InvoiceNumber == null && bill.Reference == null) gaps.Add(fi ? "Laskun numero tai viitenumero" : "The invoice number or payment reference");
        if (bill.Amount == null) gaps.Add(fi ? "Summa" : "The amount");
        if (bill.DueDate == null) gaps.Add(fi ? "Alkuperäinen eräpäivä" : "The original due date");

        var ids = new List<string>();
        if (bill.InvoiceNumber is { } number) ids.Add(fi ? $"laskunumero {number}" : $"invoice number {number}");
        if (bill.Reference is { } reference) ids.Add(fi ? $"viitenumero {reference}" : $"reference {reference}");
        var idText = ids.Count > 0 ? $" ({string.Join(", ", ids)})" : "";

        var amount = bill.Amount is { } a ? InvoiceExplainer.Money(a) : null;
        var due = bill.DueDate is { } d ? (fi ? Fi(d) : InvoiceExplainer.Day(d)) : null;
        var newDay = fi ? Fi(newDate) : InvoiceExplainer.Day(newDate);
        var name = string.IsNullOrWhiteSpace(senderName) ? (fi ? "[Nimesi]" : "[Your name]") : senderName.Trim();

        if (fi)
        {
            var what = amount != null ? $"laskustani{idText}, summa {amount}" : $"laskustani{idText}";
            var when = due != null ? $", eräpäivä {due}" : "";
            var body =
                $"Hei{(company != null ? $" {company}" : "")},\n\n" +
                $"Kirjoitan {what}{when}.\n\n" +
                $"Pyydän kohteliaasti lisää maksuaikaa {newDay} asti. Maksan laskun kokonaisuudessaan viimeistään silloin.\n\n" +
                "Voisitteko vahvistaa, sopiiko uusi eräpäivä, ja kertoa, aiheutuuko siitä viivästyskorkoa tai muita kuluja?\n\n" +
                $"Kiitos etukäteen.\n\nYstävällisin terveisin\n{name}";
            return new($"Maksuaikapyyntö{(bill.InvoiceNumber is { } n ? $", lasku {n}" : "")}", body, gaps);
        }
        else
        {
            var what = amount != null ? $"my invoice{idText} for {amount}" : $"my invoice{idText}";
            var when = due != null ? $", due {due}" : "";
            var body =
                $"Hello{(company != null ? $" {company}" : "")},\n\n" +
                $"I'm writing about {what}{when}.\n\n" +
                $"I'd like to ask for more time to pay, until {newDay}. I will pay the invoice in full by then.\n\n" +
                "Could you confirm whether the new date is possible, and let me know if any late-payment interest or fees would apply?\n\n" +
                $"Thank you.\n\nKind regards,\n{name}";
            return new($"Request for more time to pay{(bill.InvoiceNumber is { } n ? $", invoice {n}" : "")}", body, gaps);
        }
    }

    private static string Fi(DateTime d) => d.ToString("d.M.yyyy", CultureInfo.InvariantCulture);
}
