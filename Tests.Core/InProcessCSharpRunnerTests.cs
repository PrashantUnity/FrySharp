using PdfEditorApp.Plugins.CSharpEditor.Services.Display;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using Xunit;

namespace FrySharp.Core.Tests;

/// <summary>C# run headless (the web server, the CLI): statements, expressions, programs, displays, errors and Stop.</summary>
public sealed class InProcessCSharpRunnerTests
{
    [Fact]
    public void ScriptsCompileAgainstTheHeadlessProfile()
    {
        // No desktop plugin in this process: the profile is Core's, and it must be fixed by the first compile.
        Assert.Same(ScriptHostProfile.Headless, ScriptHost.Profile);
        Assert.Contains("PdfEditorApp.Plugins.CSharpEditor.Scripting", ScriptImports.Namespaces);
        Assert.DoesNotContain(ScriptImports.Namespaces, n => n.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Statements_PrintAndShowTheLastValue()
    {
        var (result, output, _) = await Headless.RunAsync("Console.WriteLine(\"hello\");\nvar x = 40 + 2;\nx");

        Assert.True(result.Success, output);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", output);
        Assert.Contains("42", output);
    }

    [Fact]
    public async Task ABareExpression_IsDumped()
    {
        var (result, output, _) = await Headless.RunAsync("1 + 1");

        Assert.True(result.Success, output);
        Assert.Contains("2", output);
    }

    [Fact]
    public async Task AProgramWithAMain_RunsAsAProgram()
    {
        var (result, output, _) = await Headless.RunAsync(
            "namespace Demo;\npublic static class P { public static void Main() { System.Console.WriteLine(\"program ran\"); } }");

        Assert.True(result.Success, output);
        Assert.Contains("program ran", output);
    }

    [Fact]
    public async Task ACompileError_FailsWithExitCodeOneAndALocatedDiagnostic()
    {
        var (result, output, _) = await Headless.RunAsync("int x = \"nope\";");

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0029", error.Id);
        Assert.Equal(1, error.Line);
        Assert.Contains("Line 1: Cannot implicitly convert type 'string' to 'int'", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpAndDisplay_ReachTheHostAsRichOutputs()
    {
        var (result, output, displays) = await Headless.RunAsync(
            "new[] { 1, 2, 3 }.Dump(\"numbers\");\nDisplay.Html(\"<b>hi</b>\");\nDisplay.Markdown(\"# Title\");");

        Assert.True(result.Success, output);
        Assert.Contains(displays, d => d.Kind == CellOutputKind.Table && d.TableResult!.Rows.Count == 3);
        Assert.Contains(displays, d => d.Kind == CellOutputKind.Html && d.HtmlContent == "<b>hi</b>");
        Assert.Contains(displays, d => d.Kind == CellOutputKind.Html && d.HtmlContent!.Contains("<h1>Title</h1>"));
    }

    [Fact]
    public async Task Stop_EndsCodeThatChecksForCancellation_WithExitCode130()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var (result, _, _) = await Headless.RunAsync(
            "while (true) { Display.ThrowIfCancellationRequested(); await Task.Delay(20); }", ct: stop.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(130, result.ExitCode);
    }

    [Fact]
    public async Task CodeThatIgnoresStop_IsAbandonedAfterTheGracePeriod()
    {
        // Stop is pressed once the code is running (on a busy machine the first compile can take longer than any fixed wait, and a Stop
        // that arrives before the code starts is a cancellation, not code that ignores it).
        using var stop = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (result, _, _) = await Headless.RunAsync("Console.WriteLine(\"started\");\nSystem.Threading.Thread.Sleep(60_000);", ct: stop.Token, observe: text =>
        {
            if (text.Contains("started")) stop.Cancel();
        });

        Assert.True(result.WasAbandoned);
        Assert.Equal(130, result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData("Console.WriteLine(\"started\");\nlong n = 0;\nwhile (true) { n++; }")]
    [InlineData("Console.WriteLine(\"started\");\nfor (var i = 0; ; i++) i--;")]
    [InlineData("namespace Demo;\npublic static class P { public static void Main() { System.Console.WriteLine(\"started\"); while (true) { } } }")]
    public async Task ALoopThatNeverLooksAtStop_StillEndsWhenStopped(string code)
    {
        using var stop = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (result, output, _) = await Headless.RunAsync(code, ct: stop.Token, observe: text =>
        {
            if (text.Contains("started")) stop.Cancel();
        });

        Assert.True(result.WasCancelled, output);
        Assert.False(result.WasAbandoned, "the loop kept its thread after Stop");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public void InstrumentedLoops_KeepTheirLineNumbers_AndBrokenCodeIsLeftAlone()
    {
        const string code = "var x = 0;\nfor (int i = 0; i < 3; i++)\n{\n    x++;\n}\nwhile (x > 0)\n    x--;\nforeach (var c in \"ab\") { x++; }";

        var instrumented = PdfEditorApp.Plugins.CSharpEditor.Services.Execution.LoopGuard.Instrument(code, Microsoft.CodeAnalysis.SourceCodeKind.Script);

        Assert.Equal(code.Split('\n').Length, instrumented.Split('\n').Length);
        Assert.Equal(3, instrumented.Split("LoopGuard.Tick()").Length - 1);
        Assert.Contains("    x++;", instrumented.Split('\n')[3]);

        const string broken = "while (true) { x++";
        Assert.Equal(broken, PdfEditorApp.Plugins.CSharpEditor.Services.Execution.LoopGuard.Instrument(broken, Microsoft.CodeAnalysis.SourceCodeKind.Script));
    }

    [Fact]
    public async Task UsingDeclarations_AreDisposedLastFirst_AndTheLastExpressionIsStillTheResult()
    {
        var (result, output, _) = await Headless.RunAsync("""
            class Noisy(string name) : IDisposable { public string Name => name; public void Dispose() => Console.WriteLine($"disposed {name}"); }
            using var a = new Noisy("a");
            using var b = new Noisy("b");
            a.Name + b.Name
            """);

        Assert.True(result.Success, output);
        var ab = output.IndexOf("\"ab\"", StringComparison.Ordinal);
        var disposedB = output.IndexOf("disposed b", StringComparison.Ordinal);
        var disposedA = output.IndexOf("disposed a", StringComparison.Ordinal);
        Assert.True(ab >= 0 && disposedB > ab && disposedA > disposedB, output);
    }
}
