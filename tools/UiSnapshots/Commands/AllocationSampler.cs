using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace PdfEditorApp.Plugins.CSharpEditor.Tools.UiSnapshots.Commands;

/// <summary>
/// Which types a stretch of the run allocates, from the runtime's allocation sampling (one event per ~100 KB allocated,
/// naming the type that crossed the line). Cheap enough to leave on for a whole measurement; turned on by --alloc-types.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private const int GCStartEventId = 1;
    private const int GCRestartEEEndEventId = 3;
    private const int GCSuspendEEBeginEventId = 9;
    private const int GCAllocationTickEventId = 10;
    private const EventKeywords GCKeyword = (EventKeywords)0x1;

    private readonly ConcurrentDictionary<string, long> _bytesByType = new();
    private readonly ConcurrentQueue<string> _fullCollections = new();
    private readonly ConcurrentQueue<(double Ms, string Gc)> _pauses = new();
    private DateTime _suspendedAt;
    private string _lastGc = "?";
    private volatile bool _recording;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, GCKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_recording || e.PayloadNames == null) return;
        if (e.EventId == GCSuspendEEBeginEventId)
        {
            _suspendedAt = e.TimeStamp;
            return;
        }

        if (e.EventId == GCRestartEEEndEventId)
        {
            if (_suspendedAt != default) _pauses.Enqueue(((e.TimeStamp - _suspendedAt).TotalMilliseconds, _lastGc));
            return;
        }

        if (e.EventId == GCStartEventId)
        {
            // Reason: 0 small allocation, 1 induced, 4 large allocation, 5/6 out of space, 7 induced (not forced; also
            // what GC.AddMemoryPressure asks for); Type: 0 blocking, 1 background, 2 foreground.
            object? Field(string name) => e.PayloadNames.IndexOf(name) is >= 0 and var i ? e.Payload![i] : null;
            _lastGc = $"#{Field("Count")} gen {Field("Depth")} reason {Field("Reason")} type {Field("Type")}";
            if (Convert.ToInt32(Field("Depth")) == 2) _fullCollections.Enqueue(_lastGc);
            return;
        }

        if (e.EventId != GCAllocationTickEventId) return;

        int typeIndex = e.PayloadNames.IndexOf("TypeName");
        int amountIndex = e.PayloadNames.IndexOf("AllocationAmount64");
        if (typeIndex < 0 || amountIndex < 0) return;

        var type = e.Payload![typeIndex] as string ?? "?";
        var bytes = Convert.ToInt64(e.Payload[amountIndex]);
        _bytesByType.AddOrUpdate(type, bytes, (_, total) => total + bytes);
    }

    /// <summary>Runs <paramref name="action"/> and prints the types it allocated most, by sampled bytes.</summary>
    public void Report(string what, Action action, int top = 15)
    {
        _bytesByType.Clear();
        _fullCollections.Clear();
        _pauses.Clear();
        _recording = true;
        try { action(); }
        finally { _recording = false; }

        Console.WriteLine($"  full collections {what}: {(_fullCollections.IsEmpty ? "none" : string.Join(", ", _fullCollections))}");
        Console.WriteLine($"  longest pauses {what}: {string.Join(", ", _pauses.OrderByDescending(p => p.Ms).Take(3).Select(p => $"{p.Ms:F0} ms ({p.Gc})"))}");
        Console.WriteLine($"  allocations {what} (sampled, by type):");
        foreach (var (type, bytes) in _bytesByType.OrderByDescending(kv => kv.Value).Take(top))
        {
            Console.WriteLine($"    {bytes / 1048576.0,8:F1} MB  {type}");
        }
    }
}
