namespace Omabilo.Api.Storage;

/// <summary>
/// Where each person's invoice records live. Replaces IKON's per-user persistent state.
/// Every call is for ONE owner: implementations load and save only that owner's records,
/// and serialize changes per owner so two requests can't overwrite each other.
/// </summary>
public interface IInvoiceStore
{
    /// <summary>Reads the owner's records into a book (read-only use).</summary>
    Task<InvoiceBook> LoadAsync(string ownerId, CancellationToken ct = default);

    /// <summary>Loads the owner's book, applies the change and saves it, all under the owner's lock.</summary>
    Task<T> UpdateAsync<T>(string ownerId, Func<InvoiceBook, T> change, CancellationToken ct = default);
}

/// <summary>Per-owner locks shared by the store implementations.</summary>
internal sealed class OwnerLocks
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(string ownerId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
