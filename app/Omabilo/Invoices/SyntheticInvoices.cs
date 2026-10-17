// Fictional, synthetic invoice material for the SIMULATED sources. Every company, address,
// reference and amount here is made up. Dates are relative to "today" so the demo stays current.

public static class SyntheticInvoices
{
    public record Sample(string Key, string Title, InboundEmail Email);

    private static DateTime Today => DateTime.UtcNow.Date;

    private static string D(DateTime d) => d.ToString("d.M.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    // Valid Finnish references (7-3-1 check digit), so extraction can verify them
    public static readonly string EnergyRef = PaymentReference.WithFinnishCheckDigit("1234561");
    public static readonly string WaterRef = PaymentReference.WithFinnishCheckDigit("8800231");
    public static readonly string InsuranceRef = PaymentReference.WithFinnishCheckDigit("4407719");
    public static readonly string DentalRef = PaymentReference.WithFinnishCheckDigit("5550142");

    /// <summary>Forwarded emails, in the order the "Simulate an email" button delivers them.
    /// The third repeats the first invoice from a different mailbox to show duplicate matching.</summary>
    public static IReadOnlyList<Sample> Emails()
    {
        var t = Today;
        return
        [
            new("email-energy", "Northwind Energy October invoice", new InboundEmail(
                "sim-msg-1001@example.invalid", "Northwind Energy <billing@northwind-energy.example>", "Your invoice 2026-1043", t.AddHours(-3),
                "Please find your invoice attached.",
                [new InboundAttachment("invoice-2026-1043.pdf", "application/pdf", $"""
                    Biller: Northwind Energy Oy (demo)
                    Invoice number: NE-2026-1043
                    Invoice date: {D(t.AddDays(-4))}
                    Due date: {D(t.AddDays(10))}
                    Reference: {EnergyRef}
                    Amount due: 58,20 €
                    """)])),
            new("email-water", "Harbor Water quarterly bill", new InboundEmail(
                "sim-msg-1002@example.invalid", "Harbor Water Utility <invoices@harbor-water.example>", "Lasku / Invoice Q4", t.AddHours(-2),
                "Lasku liitteenä. Invoice attached.",
                [new InboundAttachment("lasku-q4.pdf", "application/pdf", $"""
                    Laskuttaja: Harbor Water Utility (demo)
                    Laskun numero: HW-88231
                    Laskun päiväys: {D(t.AddDays(-2))}
                    Eräpäivä: {D(t.AddDays(16))}
                    Viitenumero: {WaterRef}
                    Yhteensä: 1 124,90 €
                    """)])),
            new("email-energy-again", "Same energy invoice, forwarded again", new InboundEmail(
                "sim-msg-2077@example.invalid", "Northwind Energy <billing@northwind-energy.example>", "Fwd: Your invoice 2026-1043", t.AddHours(-1),
                "Forwarding from my other mailbox.",
                [new InboundAttachment("invoice-2026-1043.pdf", "application/pdf", $"""
                    Biller: Northwind Energy Oy (demo)
                    Invoice number: NE-2026-1043
                    Due date: {D(t.AddDays(10))}
                    Reference: {EnergyRef}
                    Amount due: 58,20 €
                    """)])),
            new("email-insurance", "Lumo Insurance renewal", new InboundEmail(
                "sim-msg-1003@example.invalid", "Lumo Insurance <noreply@lumo-insurance.example>", "Home insurance renewal invoice", t.AddMinutes(-30),
                "Your home insurance renews next month.",
                [new InboundAttachment("renewal.pdf", "application/pdf", $"""
                    Seller: Lumo Insurance (demo)
                    Invoice no: LI-552019
                    Date: {D(t.AddDays(-1))}
                    Due: {D(t.AddDays(21))}
                    Ref: {InsuranceRef}
                    Total: 212.40 EUR
                    """)])),
        ];
    }

    /// <summary>Scanned documents for the simulated upload. The second is a poor scan with a
    /// mistyped reference and no amount label, so it lands in review with low confidence.</summary>
    public static IReadOnlyList<(string Key, string Title, InboundAttachment Document)> Documents()
    {
        var t = Today;
        return
        [
            ("doc-dental", "Dental clinic invoice (clear scan)", new InboundAttachment("dental-invoice.jpg", "image/jpeg", $"""
                From: Kallio Dental Clinic (demo)
                Invoice number: KD-30418
                Invoice date: {D(t.AddDays(-6))}
                Due date: {D(t.AddDays(8))}
                Reference: {DentalRef}
                Total: 96,00 €
                """)),
            ("doc-poor-scan", "Bike repair receipt (poor scan)", new InboundAttachment("bike-repair.jpg", "image/jpeg", $"""
                From: Spoke & Chain Repairs (demo)
                Due date: {D(t.AddDays(5))}
                Reference: 1234 5670
                Amount 74,5O €
                """)),
        ];
    }

    /// <summary>Demo invoices, already confirmed, for the "Load demo invoices" button.</summary>
    public static IReadOnlyList<InvoiceSubmission> DemoInvoices()
    {
        var t = Today;

        InvoiceSubmission Demo(string key, string provider, string number, string reference, int dueIn, decimal amount) =>
            new(InvoiceSourceType.Demo, $"demo:{key}", true,
                new ExtractedInvoiceFields(provider, number, reference, t.AddDays(dueIn - 14), t.AddDays(dueIn), amount, "EUR",
                    new Dictionary<string, double> { ["provider"] = 1, ["amount"] = 1, ["dueDate"] = 1, ["invoiceNumber"] = 1, ["reference"] = 1 }),
                1.0, "Demo invoice");

        return
        [
            // Providers the Financial Inbox demo bills don't use, so nothing is counted twice
            Demo("music", "Northern Lights Music School (demo)", "NL-7731", PaymentReference.WithFinnishCheckDigit("7731004"), 12, 85.00m),
            Demo("sauna", "Koivu Housing sauna shift (demo)", "KH-2209", PaymentReference.WithFinnishCheckDigit("2209140"), 9, 15.00m),
            Demo("library", "City Library late fees (demo)", "CL-5512", PaymentReference.WithFinnishCheckDigit("5512033"), 6, 4.50m),
        ];
    }
}
