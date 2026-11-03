using Microsoft.CodeAnalysis;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Display;
using PdfEditorApp.Plugins.CSharpEditor.Services.Kernels;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Execution;

/// <summary>How a C# run ended.</summary>
public sealed record CSharpRunResult(
    bool Success,
    bool WasCancelled,
    TimeSpan Elapsed,
    IReadOnlyList<DiagnosticItem> Diagnostics,
    string? Error = null,
    bool WasAbandoned = false)
{
    /// <summary>The run as a process exit code: 0 when it succeeded, 130 when stopped, 1 otherwise.</summary>
    public int ExitCode => Success ? 0 : WasCancelled ? 130 : 1;
}

/// <summary>
/// Runs C# source in this process, as the desktop's Code Studio does: statements and expressions in a fresh
/// <see cref="NotebookExecutionKernel"/> (the last value is shown, <c>#r "nuget:…"</c> works), a whole program (a class with
/// a Main) compiled and run by <see cref="ScriptExecutionEngine"/>. Displays go to the <c>onRichOutput</c> callback of
/// <see cref="RunAsync"/>. Stop cancels; code that ignores it is abandoned after <see cref="ExecutionAbandonment.GracePeriod"/>,
/// because Roslyn can't preempt a blocked call.
/// </summary>
public sealed class InProcessCSharpRunner(RoslynCompilerService compiler)
{
    public async Task<CSharpRunResult> RunAsync(
        string code,
        Action<string> onConsole,
        Action<RichCellOutput> onRichOutput,
        TextReader? stdin,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(onConsole);
        ArgumentNullException.ThrowIfNull(onRichOutput);

        // Displays made by a program's code reach the caller through this scope; the kernel hands its own to the callback.
        using var displays = InteractiveDisplayContext.EnterScope(onRichOutput);
        using var cancellation = InteractiveCancellationContext.EnterScope(ct);

        return RoslynCompilerService.IsExplicitProgram(code)
            ? await RunProgramAsync(code, onConsole, stdin, ct).ConfigureAwait(false)
            : await RunStatementsAsync(code, onConsole, onRichOutput, stdin, ct).ConfigureAwait(false);
    }

    private async Task<CSharpRunResult> RunStatementsAsync(string code, Action<string> onConsole, Action<RichCellOutput> onRichOutput, TextReader? stdin, CancellationToken ct)
    {
        var kernel = new NotebookExecutionKernel();
        var execution = Task.Run(() => kernel.ExecuteCellAsync(code, onConsole, onRichOutput, ct, stdin: stdin), CancellationToken.None);
        if (!await ExecutionAbandonment.WaitWithGraceAsync(execution, ct).ConfigureAwait(false))
        {
            return Abandoned();
        }

        var result = await execution.ConfigureAwait(false);
        return new CSharpRunResult(result.Success, result.WasCancelled, result.Elapsed, result.Diagnostics,
            string.IsNullOrEmpty(result.ErrorMessage) ? null : result.ErrorMessage);
    }

    private async Task<CSharpRunResult> RunProgramAsync(string code, Action<string> onConsole, TextReader? stdin, CancellationToken ct)
    {
        // Stop ends its loops even when they never look at a cancellation token.
        var instrumented = LoopGuard.Instrument(code, Microsoft.CodeAnalysis.SourceCodeKind.Regular);
        var (success, bytes, diagnostics) = await Task.Run(() => compiler.CompileToAssembly(instrumented, ExecutionLanguageMode.Program), ct).ConfigureAwait(false);
        if (!success || bytes == null)
        {
            foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                onConsole($"{diagnostic.LocationString}: {diagnostic.Id} {diagnostic.Message}\n");
            }
            return new CSharpRunResult(false, false, TimeSpan.Zero, diagnostics, "Compilation failed.");
        }

        var execution = new ScriptExecutionEngine().ExecuteAsync(bytes, onConsole, ct, stdin);
        if (!await ExecutionAbandonment.WaitWithGraceAsync(execution, ct).ConfigureAwait(false))
        {
            return Abandoned();
        }

        var result = await execution.ConfigureAwait(false);
        return new CSharpRunResult(result.Success, result.WasCancelled, result.Elapsed, diagnostics,
            string.IsNullOrEmpty(result.Error) ? null : result.Error);
    }

    // The code ignored Stop: it keeps its thread until it returns, but the run is over for whoever waits on it.
    private static CSharpRunResult Abandoned() =>
        new(false, true, TimeSpan.Zero, [], "The code did not respond to Stop and was abandoned.", WasAbandoned: true);
}
