using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace PdfEditorApp.Plugins.CSharpEditor.Tools.UiSnapshots.Commands;

/// <summary>
/// Which methods the JIT compiles during a stretch of the run. The runtime only optimizes hot code once no new method has
/// been compiled for a moment (tiered compilation's call-counting delay), so code that keeps compiling new methods while
/// the user works keeps everything else running unoptimized. Turned on by --jit-watch.
/// </summary>
internal sealed class JitWatcher : EventListener
{
    private const int MethodJittingStartedEventId = 145;
    private const EventKeywords JitKeyword = (EventKeywords)0x10;

    private readonly ConcurrentQueue<(DateTime At, string Method)> _compiled = new();
    private volatile bool _recording;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, JitKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_recording || e.EventId != MethodJittingStartedEventId || e.PayloadNames == null) return;
        string Field(string name) => e.PayloadNames.IndexOf(name) is >= 0 and var i ? e.Payload![i]?.ToString() ?? "" : "";
        _compiled.Enqueue((e.TimeStamp, $"{Field("MethodNamespace")}::{Field("MethodName")}"));
    }

    /// <summary>Runs <paramref name="action"/> and prints what was compiled meanwhile, by type, and how it was spread in time.</summary>
    public void Report(string what, Action action, int top = 25)
    {
        _compiled.Clear();
        var started = DateTime.UtcNow;
        _recording = true;
        try { action(); }
        finally { _recording = false; }

        var items = _compiled.ToList();
        var seconds = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
        // The longest stretch without a new compilation: under 100 ms, hot code was never optimized meanwhile.
        var gaps = items.Select(i => i.At).OrderBy(t => t).Zip(items.Select(i => i.At).OrderBy(t => t).Skip(1), (a, b) => (b - a).TotalMilliseconds).DefaultIfEmpty(0);
        Console.WriteLine($"  JIT {what}: {items.Count} methods compiled in {seconds:F1} s; longest pause between two compilations {gaps.Max():F0} ms");
        foreach (var group in items.GroupBy(i => i.Method.Split("::")[0]).OrderByDescending(g => g.Count()).Take(top))
        {
            Console.WriteLine($"    {group.Count(),5}  {group.Key}  (e.g. {group.First().Method.Split("::")[1]})");
        }
    }
}
