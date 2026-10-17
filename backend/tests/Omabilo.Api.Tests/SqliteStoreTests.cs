using Omabilo.Api.Storage;
using Xunit;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"omabilo-test-{Guid.NewGuid():N}.db");

    private static InvoiceSubmission Manual(string provider) =>
        ManualInvoiceSource.Submit(new ExtractedInvoiceFields(provider, "N-1", null, null, DateTime.UtcNow.Date.AddDays(5), 42.5m, "EUR"), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Records_survive_a_restart()
    {
        var store = new SqliteInvoiceStore(_path);
        await store.UpdateAsync("demo-alice", b => b.Ingest("demo-alice", Manual("Fiction Utility (demo)"), DateTime.UtcNow));

        var reopened = new SqliteInvoiceStore(_path);
        var book = await reopened.LoadAsync("demo-alice");

        var inv = Assert.Single(book.InvoicesOf("demo-alice"));
        Assert.Equal("Fiction Utility (demo)", inv.Provider);
        Assert.Equal(42.5m, inv.Amount);
        Assert.Single(book.AuditOf("demo-alice"));
    }

    [Fact]
    public async Task Each_owner_is_loaded_and_saved_separately()
    {
        var store = new SqliteInvoiceStore(_path);
        await store.UpdateAsync("demo-alice", b => b.Ingest("demo-alice", Manual("Alice Co (demo)"), DateTime.UtcNow));
        await store.UpdateAsync("demo-bob", b => b.Ingest("demo-bob", Manual("Bob Co (demo)"), DateTime.UtcNow));

        var alice = await store.LoadAsync("demo-alice");
        Assert.Single(alice.AllInvoices);
        Assert.Equal("Alice Co (demo)", alice.AllInvoices[0].Provider);

        await store.UpdateAsync("demo-alice", b => b.DeleteAllFor("demo-alice", DateTime.UtcNow));
        Assert.Single((await store.LoadAsync("demo-bob")).InvoicesOf("demo-bob"));
    }

    [Fact]
    public async Task Concurrent_updates_for_one_owner_are_not_lost()
    {
        var store = new SqliteInvoiceStore(_path);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.UpdateAsync("demo-alice", b => b.Ingest("demo-alice", Manual($"Provider {i} (demo)"), DateTime.UtcNow))));

        Assert.Equal(20, (await store.LoadAsync("demo-alice")).InvoicesOf("demo-alice").Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
