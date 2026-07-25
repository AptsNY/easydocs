using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace EasyDocs.Api.Events;

// In-process SSE fan-out (spec §10.2). One instance, no interface: a document's live consoles each
// hold a bounded channel; Publish serializes once and pushes to every channel for that doc.
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Guid, List<Channel<(string, string)>>> _subs = new();

    public void Publish(Guid documentId, string type, object payload)
    {
        if (!_subs.TryGetValue(documentId, out var list)) return;
        var json = JsonSerializer.Serialize(payload);
        lock (list)
            foreach (var ch in list)
                ch.Writer.TryWrite((type, json)); // DropOldest bounded channel — never blocks
    }

    public async IAsyncEnumerable<(string Type, string Json)> Subscribe(
        Guid documentId, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<(string, string)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var list = _subs.GetOrAdd(documentId, _ => new List<Channel<(string, string)>>());
        lock (list) list.Add(channel);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct))
                yield return item;
        }
        finally
        {
            lock (list) list.Remove(channel);
        }
    }
}
