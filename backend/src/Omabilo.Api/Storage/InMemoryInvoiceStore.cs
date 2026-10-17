namespace Omabilo.Api.Storage;

/// <summary>Keeps records in memory only; everything is lost when the server stops.
/// For tests and quick local runs.</summary>
public sealed class InMemoryInvoiceStore : IInvoiceStore
{
    private readonly Dictionary<string, (List<CanonicalInvoice> Invoices, List<InvoiceSourceRecord> Sources, List<InvoiceAuditEvent> Audit)> _data = new();
    private readonly OwnerLocks _locks = new();
    private readonly object _sync = new();

    public async Task<InvoiceBook> LoadAsync(string ownerId, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        return Read(ownerId);
    }

    public async Task<T> UpdateAsync<T>(string ownerId, Func<InvoiceBook, T> change, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        var book = Read(ownerId);
        var result = change(book);

        lock (_sync)
        {
            // Only this owner's records are written back, whatever the change touched
            _data[ownerId] = (
                book.AllInvoices.Where(i => i.OwnerId == ownerId).ToList(),
                book.AllSources.Where(s => s.OwnerId == ownerId).ToList(),
                book.AllAudit.Where(a => a.OwnerId == ownerId).ToList());
        }

        return result;
    }

    private InvoiceBook Read(string ownerId)
    {
        lock (_sync)
        {
            return _data.TryGetValue(ownerId, out var d)
                ? new InvoiceBook(d.Invoices, d.Sources, d.Audit)
                : new InvoiceBook([], [], []);
        }
    }
}
