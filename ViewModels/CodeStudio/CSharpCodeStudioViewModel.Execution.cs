using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.CodeAnalysis;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Display;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Kernels;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

public partial class CSharpCodeStudioViewModel
{
    [ObservableProperty]
    private int _selectedBottomTabIndex = 0;

    [ObservableProperty]
    private bool _isBottomDeckExpanded = true;

    [ObservableProperty]
    private bool _isDeckDockedToRight;

    [ObservableProperty]
    private Avalonia.Controls.GridLength _bottomDeckGridLength = new(280, Avalonia.Controls.GridUnitType.Pixel);

    [ObservableProperty]
    private Avalonia.Controls.GridLength _rightDeckGridLength = new(0, Avalonia.Controls.GridUnitType.Pixel);

    private double _savedBottomDeckHeight = 280;
    private double _savedRightDeckWidth = 520;

    public bool ShowBottomDeck => !IsDeckDockedToRight && IsBottomDeckExpanded;
    public bool ShowRightDeck => IsDeckDockedToRight && IsBottomDeckExpanded;
    public bool ShowBottomDeckSplitter => !IsDeckDockedToRight && IsBottomDeckExpanded;
    public bool ShowRightDeckSplitter => IsDeckDockedToRight && IsBottomDeckExpanded;
    public string DeckPositionTooltip => IsDeckDockedToRight ? "Dock Panel to Bottom" : "Dock Panel to Right";

    private bool _userExplicitlySetDeckPosition = true; // bottom is the default; user can toggle to right

    public void UpdateAdaptiveDeckWidth(double viewWidth)
    {
        if (viewWidth <= 0) return;

        // Auto-adapt deck position on widescreen displays (>= 1350px) unless user explicitly toggled it
        if (!_userExplicitlySetDeckPosition)
        {
            bool shouldDockRight = viewWidth >= 1350;
            if (IsDeckDockedToRight != shouldDockRight)
            {
                IsDeckDockedToRight = shouldDockRight;
            }
        }

        double targetWidth;
        if (viewWidth < 1400)
        {
            targetWidth = Math.Min(480, viewWidth * 0.45);
        }
        else if (viewWidth < 2000)
        {
            targetWidth = Math.Min(680, viewWidth * 0.42);
        }
        else if (viewWidth < 3000)
        {
            targetWidth = Math.Min(1100, viewWidth * 0.45);
        }
        else
        {
            targetWidth = Math.Min(1750, viewWidth * 0.48);
        }

        _savedRightDeckWidth = targetWidth;
        if (IsDeckDockedToRight && IsBottomDeckExpanded)
        {
            RightDeckGridLength = new Avalonia.Controls.GridLength(targetWidth, Avalonia.Controls.GridUnitType.Pixel);
        }
        else
        {
            RightDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
        }
    }

    [RelayCommand]
    public void ToggleDeckPosition()
    {
        _userExplicitlySetDeckPosition = true;
        IsDeckDockedToRight = !IsDeckDockedToRight;
    }

    partial void OnIsDeckDockedToRightChanged(bool value)
    {
        if (value)
        {
            if (BottomDeckGridLength.IsAbsolute && BottomDeckGridLength.Value > 60)
            {
                _savedBottomDeckHeight = BottomDeckGridLength.Value;
            }
            BottomDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            if (IsBottomDeckExpanded)
            {
                RightDeckGridLength = new Avalonia.Controls.GridLength(_savedRightDeckWidth > 60 ? _savedRightDeckWidth : 520, Avalonia.Controls.GridUnitType.Pixel);
            }
            else
            {
                RightDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            }
        }
        else
        {
            if (RightDeckGridLength.IsAbsolute && RightDeckGridLength.Value > 60)
            {
                _savedRightDeckWidth = RightDeckGridLength.Value;
            }
            RightDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            if (IsBottomDeckExpanded)
            {
                BottomDeckGridLength = new Avalonia.Controls.GridLength(_savedBottomDeckHeight > 60 ? _savedBottomDeckHeight : 280, Avalonia.Controls.GridUnitType.Pixel);
            }
            else
            {
                BottomDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            }
        }

        OnPropertyChanged(nameof(ShowBottomDeck));
        OnPropertyChanged(nameof(ShowRightDeck));
        OnPropertyChanged(nameof(ShowBottomDeckSplitter));
        OnPropertyChanged(nameof(ShowRightDeckSplitter));
        OnPropertyChanged(nameof(DeckPositionTooltip));
    }

    partial void OnIsBottomDeckExpandedChanged(bool value)
    {
        if (value)
        {
            if (IsDeckDockedToRight)
            {
                RightDeckGridLength = new Avalonia.Controls.GridLength(_savedRightDeckWidth > 60 ? _savedRightDeckWidth : 520, Avalonia.Controls.GridUnitType.Pixel);
                BottomDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            }
            else
            {
                BottomDeckGridLength = new Avalonia.Controls.GridLength(_savedBottomDeckHeight > 60 ? _savedBottomDeckHeight : 280, Avalonia.Controls.GridUnitType.Pixel);
                RightDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            }
        }
        else
        {
            if (BottomDeckGridLength.IsAbsolute && BottomDeckGridLength.Value > 60)
            {
                _savedBottomDeckHeight = BottomDeckGridLength.Value;
            }
            if (RightDeckGridLength.IsAbsolute && RightDeckGridLength.Value > 60)
            {
                _savedRightDeckWidth = RightDeckGridLength.Value;
            }
            BottomDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            RightDeckGridLength = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
        }

        OnPropertyChanged(nameof(ShowBottomDeck));
        OnPropertyChanged(nameof(ShowRightDeck));
        OnPropertyChanged(nameof(ShowBottomDeckSplitter));
        OnPropertyChanged(nameof(ShowRightDeckSplitter));
    }

    [ObservableProperty]
    private bool _isExecuting;

    partial void OnIsExecutingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNormalExecuting));
        OnPropertyChanged(nameof(ShowDebugButton));
    }

    public bool IsNormalExecuting => IsExecuting && !IsDebugging;

    [ObservableProperty]
    private string _executionTimeText = string.Empty;

    [ObservableProperty]
    private string _compilerStatusText = "Ready";

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private string _consoleOutput = string.Empty;

    [ObservableProperty]
    private string _consoleHeader = string.Empty;

    [ObservableProperty]
    private string _consoleBody = string.Empty;

    [ObservableProperty]
    private string _consoleFooter = string.Empty;

    [ObservableProperty]
    private int? _consoleExitCode;

    public bool IsConsoleExitSuccess => ConsoleExitCode == 0;
    public bool IsConsoleExitError => ConsoleExitCode != null && ConsoleExitCode != 0;

    partial void OnConsoleExitCodeChanged(int? value)
    {
        OnPropertyChanged(nameof(IsConsoleExitSuccess));
        OnPropertyChanged(nameof(IsConsoleExitError));
    }

    partial void OnConsoleOutputChanged(string value)
    {
        if (string.IsNullOrEmpty(_consoleBody) && string.IsNullOrEmpty(_consoleHeader) && string.IsNullOrEmpty(_consoleFooter))
        {
            ConsoleBody = value;
        }
    }

    public RangeObservableCollection<DumpTableResult> DumpResults { get; } = new();
    public RangeObservableCollection<RichCellOutput> RichOutputs { get; } = new();

    /// <summary>
    /// True when the Results tab has nothing to draw, so it shows its "No Visual Dumps Yet" hint: no table dumps and no
    /// image, HTML, chart, visualizer, control or object-inspector output. Text and error outputs aren't drawn there.
    /// </summary>
    public bool HasNoResults => DumpResults.Count == 0 && !RichOutputs.Any(IsDrawnInResults);

    // The kinds the Results tab's RichOutputs template draws (StudioBottomDeckControl.axaml).
    private static bool IsDrawnInResults(RichCellOutput output) =>
        output.IsImageKind || output.IsHtmlKind || output.IsControlKind || output.IsInspectorKind || output.IsVisualKind;
    public RangeObservableCollection<Common.DiagnosticItemViewModel> Diagnostics { get; } = new();
    public ObservableCollection<Common.AssemblyReferenceViewModel> References { get; } = new();
    public ObservableCollection<TestCaseItem> TestCases { get; } = new();

    /// <summary>
    /// Characters. Above this a document is a "large file": its foldings are worked out after it is shown, and it is not analysed
    /// as you type. Each pause in typing would start a multi-second Roslyn compile of the whole file, whose allocations keep the
    /// garbage collector pausing the editor (with a big workspace open, a keystroke in a 2 MB file waited about 25 ms for it).
    /// Running the file still reports its problems.
    /// </summary>
    public const int LargeDocumentLength = 500_000;

    /// <summary>What the status bar says while the open document is too large to be analysed as you type.</summary>
    public const string LargeDocumentStatus = "Live diagnostics are off for a very large file; run it to check for problems";

    private void TriggerDiagnosticsCheck()
    {
        if (!ActiveLanguage.Has(LanguageCapabilities.LiveDiagnostics))
        {
            // Problems of such a document come from its runs; they stay until the next one.
            _diagnosticsCts?.Cancel();
            return;
        }

        if (Code.Length > LargeDocumentLength)
        {
            _diagnosticsCts?.Cancel();
            // What was found before describes text that has changed since.
            if (Diagnostics.Count > 0) Diagnostics.Clear();
            OpenTabs.FirstOrDefault(t => t.Id == Script.Id)?.Diagnostics.Clear();
            ErrorCount = 0;
            WarningCount = 0;
            if (!IsExecuting) CompilerStatusText = LargeDocumentStatus;
            return;
        }

        _diagnosticsCts?.Cancel();
        _diagnosticsCts = new CancellationTokenSource();
        var token = _diagnosticsCts.Token;

        var targetScriptId = Script.Id;
        var codeSnapshot = Code;
        var mode = CurrentLanguageMode;

        _ = Task.Run((Func<Task?>)(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;

                if (Script.Id == targetScriptId)
                {
                    RunOnUiThread(() =>
                    {
                        if (Script.Id == targetScriptId)
                        {
                            CompilerStatusText = "Analyzing...";
                        }
                    });
                }
                var items = _compilerService.CheckDiagnostics(codeSnapshot, mode);

                if (token.IsCancellationRequested) return;

                Action applyDiagnostics = () =>
                {
                    var targetTab = OpenTabs.FirstOrDefault(t => t.Id == targetScriptId);
                    if (targetTab != null)
                    {
                        targetTab.Diagnostics.Clear();
                        foreach (var item in items)
                        {
                            targetTab.Diagnostics.Add(new Common.DiagnosticItemViewModel(item, (l, c) =>
                            {
                                RequestNavigateToCaret?.Invoke(l, c);
                            }));
                        }
                    }

                    if (Script.Id == targetScriptId)
                    {
                        Diagnostics.Clear();
                        foreach (var item in items)
                        {
                            Diagnostics.Add(new Common.DiagnosticItemViewModel(item, (l, c) =>
                            {
                                SetCaretPosition(l, c);
                                RequestNavigateToCaret?.Invoke(l, c);
                            }));
                        }

                        ErrorCount = items.Count(i => i.Severity == DiagnosticSeverity.Error);
                        WarningCount = items.Count(i => i.Severity == DiagnosticSeverity.Warning);

                        if (ErrorCount > 0)
                        {
                            CompilerStatusText = $"{ErrorCount} Error{(ErrorCount > 1 ? "s" : "")}";
                        }
                        else if (WarningCount > 0)
                        {
                            CompilerStatusText = $"{WarningCount} Warning{(WarningCount > 1 ? "s" : "")}";
                        }
                        else
                        {
                            CompilerStatusText = "Ready";
                        }
                    }
                };

                if (Avalonia.Application.Current != null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(applyDiagnostics);
                }
                else
                {
                    applyDiagnostics();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }), token);
    }

    private void DisposeRichOutputControls()
    {
        foreach (var output in RichOutputs)
        {
            InteractiveControlLifecycle.DisposeIfNeeded(output.InteractiveControl);
        }
    }

    [RelayCommand]
    public void ClearResults()
    {
        DisposeRichOutputControls();
        DumpResults.Clear();
        RichOutputs.Clear();
    }

    [RelayCommand]
    public async Task CopyTableTsvAsync(DumpTableResult? table)
    {
        if (table == null) return;
        var text = table.ToTsv();
        await SetClipboardTextAsync(text);
        CompilerStatusText = $"Copied {table.FullHeaderTitle} as TSV to clipboard";
    }

    [RelayCommand]
    public async Task CopyTableCsvAsync(DumpTableResult? table)
    {
        if (table == null) return;
        var text = table.ToCsv();
        await SetClipboardTextAsync(text);
        CompilerStatusText = $"Copied {table.FullHeaderTitle} as CSV to clipboard";
    }

    [RelayCommand]
    public async Task CopyTableJsonAsync(DumpTableResult? table)
    {
        if (table == null) return;
        var text = table.ToJson();
        await SetClipboardTextAsync(text);
        CompilerStatusText = $"Copied {table.FullHeaderTitle} as JSON to clipboard";
    }

    private static async Task SetClipboardTextAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
            }
        }
    }

    [RelayCommand]
    private async Task RunCodeAsync()
    {
        if (IsExecuting) return;

        if (ActiveLanguage.Id == LanguageIds.Text || ActiveLanguage.Capabilities == LanguageCapabilities.None)
        {
            CompilerStatusText = $"ℹ️ '{Script?.Title ?? "File"}' is a text/asset file and cannot be executed directly.";
            return;
        }
        if (ActiveLanguage.ScriptRunner != null)
        {
            await RunWithScriptRunnerAsync(OpenTabs.FirstOrDefault(t => t.Id == Script.Id), ActiveLanguage);
            return;
        }

        if (UseExternalDotNetRunner)
        {
            var externalRunner = new Services.Languages.CSharp.CSharpBuildAndRunScriptRunner(_languages.Host);
            var externalToolchains = new Services.Languages.CSharp.CSharpToolchainProvider(_languages.Host, _languages.Processes, _languages.ToolchainSettings);
            await RunWithScriptRunnerAsync(OpenTabs.FirstOrDefault(t => t.Id == Script.Id), ActiveLanguage, "⚡ Running with External .NET SDK (dotnet CLI)...", externalRunner, externalToolchains);
            return;
        }

        var runningTab = OpenTabs.FirstOrDefault(t => t.Id == Script.Id);

        var beforeHook = new FrySharp.Sdk.ExecutionHookContext
        {
            LanguageId = ActiveLanguage.Id,
            DocumentPath = Script.SourceFilePath,
            SourceCode = Code
        };
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.HookRegistry.InvokeBeforeScriptRun(beforeHook);
        if (beforeHook.CancelExecution)
        {
            var cancelReason = beforeHook.CancellationReason ?? "Execution cancelled by extension hook.";
            var cancelMsg = $"⚠️ {cancelReason}\n";
            if (runningTab != null) runningTab.ConsoleOutput = cancelMsg;
            ConsoleOutput = cancelMsg;
            CompilerStatusText = "Cancelled by Hook";
            return;
        }

        // Every run checks the script's test cases; their results land on them even if another tab is active by the end.
        var runningCases = TestCases.ToList();
        var runningCode = beforeHook.SourceCode;
        bool completed = false;
        foreach (var testCase in runningCases) testCase.IsRunning = true;
        var csharpHeader = "🚀 Running C# code (.Dump enabled)...\n";
        if (runningTab != null)
        {
            runningTab.IsExecuting = true;
            runningTab.ConsoleHeader = csharpHeader;
            runningTab.ConsoleBody = string.Empty;
            runningTab.ConsoleFooter = string.Empty;
            runningTab.ConsoleExitCode = null;
            runningTab.ConsoleOutput = csharpHeader;
            runningTab.DumpResults.Clear();
            runningTab.RichOutputs.Clear();
        }

        DisposeRichOutputControls();
        DumpResults.Clear();
        RichOutputs.Clear();
        SelectedBottomTabIndex = 0;
        IsBottomDeckExpanded = true;
        ConsoleHeader = csharpHeader;
        ConsoleBody = string.Empty;
        ConsoleFooter = string.Empty;
        ConsoleExitCode = null;
        ConsoleOutput = csharpHeader;
        CompilerStatusText = "Executing...";
        IsExecuting = true;

        _executionCts?.Cancel();
        _executionCts = new CancellationTokenSource();
        if (runningTab != null) runningTab.ExecutionCts = _executionCts;

        // 0 = no automatic timeout (the default) — execution runs until Stop is clicked, matching
        // Jupyter. A positive value is an opt-in ceiling for whoever configures one.
        var timeoutSeconds = _getTimeoutSeconds();
        using var timeoutCts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_executionCts.Token, timeoutCts.Token);
        var token = linkedCts.Token;

        // Identifies this specific run so callbacks/continuations from an execution we later give up
        // on (see ExecutionAbandonment usage below) can never write stale output/status after a
        // newer run has already started.
        var myRunId = ++_executionRunId;

        // Everything the script prints reaches the Terminal once and in order, however its queued live writes and the
        // end of the run interleave (see RunConsoleRelay).
        var terminal = new RunConsoleRelay(text =>
        {
            if (myRunId != _executionRunId) return;
            if (runningTab != null)
            {
                runningTab.ConsoleBody += text;
                runningTab.ConsoleOutput += text;
                if (runningTab.IsActive)
                {
                    ConsoleBody = runningTab.ConsoleBody;
                    ConsoleOutput = runningTab.ConsoleOutput;
                }
            }
            else
            {
                ConsoleBody += text;
                ConsoleOutput += text;
            }
        }, _postToUiThread);

        // What the run displays, from the script's thread: drawn in the Results of the tab that ran it (and the studio's,
        // while that tab is shown), or said in the Terminal when it couldn't be drawn (a spec with a mistake in it).
        void ShowOutput(RichCellOutput rich)
        {
            if (rich.Kind == CellOutputKind.Error)
            {
                terminal.Write($"⚠️ {rich.Text}\n");
                return;
            }

            _postToUiThread(() =>
            {
                if (myRunId != _executionRunId) return;
                if (runningTab != null)
                {
                    runningTab.RichOutputs.Add(rich);
                    if (rich.TableResult != null)
                    {
                        runningTab.DumpResults.Add(rich.TableResult);
                    }
                }
                if (runningTab == null || runningTab.IsActive)
                {
                    RichOutputs.Add(rich);
                    if (rich.TableResult != null)
                    {
                        DumpResults.Add(rich.TableResult);
                        SelectedBottomTabIndex = 0;
                    }
                }
            });
        }

        // A compiled program's displays; statements run in the kernel, which hands them to its own callback.
        using var scope = InteractiveDisplayContext.EnterScope(ShowOutput);
        using var cancellationScope = InteractiveCancellationContext.EnterScope(token);

        var stdin = new Services.Processes.InteractiveStdinReader(token);
        if (runningTab != null) runningTab.InProcessStdin = stdin;
        if (ActiveLanguage.Has(Services.Languages.LanguageCapabilities.StandardInput))
        {
            IsAcceptingProgramInput = true;
        }

        try
        {
            if (CurrentLanguageMode == ExecutionLanguageMode.Statements || CurrentLanguageMode == ExecutionLanguageMode.Expression)
            {
                _kernel.HardReset();

                var codeToRun = Code;
                if (CurrentLanguageMode == ExecutionLanguageMode.Expression)
                {
                    var expr = Code.Trim().TrimEnd(';');
                    codeToRun = $"({expr}).Dump();";
                }

                // Roslyn script execution must never run inline on the UI thread: if the user's code
                // does something like `httpClient.GetStringAsync(url).Result`, its continuation
                // needs to resume on the same UI thread that's blocked waiting for it — an
                // unrecoverable deadlock that freezes the whole app (and makes Stop unclickable).
                // Task.Run keeps it on a thread-pool thread with no captured SynchronizationContext,
                // matching the same pattern ScriptExecutionEngine.ExecuteAsync already uses safely.
                var kernelExecutionTask = Task.Run(() => _kernel.ExecuteCellAsync(
                    codeToRun,
                    ct: token,
                    onLiveConsole: terminal.Write,
                    stdin: stdin,
                    onRichOutput: ShowOutput));

                KernelExecutionResult kernelResult;
                if (await ExecutionAbandonment.WaitWithGraceAsync(kernelExecutionTask, token))
                {
                    kernelResult = await kernelExecutionTask;
                }
                else
                {
                    // Stop/timeout fired and execution didn't respond within the grace period —
                    // almost always because it's synchronously blocked mid-statement (e.g. a hung
                    // HttpClient call), which Roslyn's cancellation can never preempt. Give up
                    // waiting rather than hang this command forever; the thread-pool thread keeps
                    // running in the background until it naturally returns or the process exits.
                    kernelResult = new KernelExecutionResult
                    {
                        WasCancelled = true,
                        ErrorMessage = "Execution did not respond to Stop and was abandoned."
                    };

                    var abandonedRunId = myRunId;
                    _ = kernelExecutionTask.ContinueWith((Task<KernelExecutionResult> t) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (abandonedRunId != _executionRunId) return; // a newer run already took over
                            var statusText = t.IsFaulted
                                ? "Background task (previously abandoned) finished with an error"
                                : "Background task (previously abandoned) finished";
                            if (runningTab != null) runningTab.CompilerStatusText = statusText;
                            if (runningTab == null || runningTab.IsActive) CompilerStatusText = statusText;
                        });
                    }, TaskScheduler.Default);
                }

                // The kernel's ConsoleOutput repeats what it wrote live; it only adds text that never arrived live.
                terminal.Complete(kernelResult.Success ? kernelResult.ConsoleOutput : null);
                completed = kernelResult.Success;

                var timeText = $"{kernelResult.Elapsed.TotalMilliseconds:N0} ms";
                var exitCode = kernelResult.Success ? 0 : 1;
                var footerText = kernelResult.Success
                    ? $"— completed in {timeText}"
                    : (kernelResult.WasCancelled ? "— cancelled" : "— execution failed");

                if (runningTab != null)
                {
                    runningTab.ConsoleFooter = footerText;
                    runningTab.ConsoleExitCode = exitCode;
                }
                if (runningTab == null || runningTab.IsActive)
                {
                    ConsoleFooter = footerText;
                    ConsoleExitCode = exitCode;
                }

                if (kernelResult.Success)
                {
                    var statusText = DumpResults.Count > 0
                        ? $"Completed • {DumpResults.Count} visual dump{(DumpResults.Count == 1 ? "" : "s")}"
                        : "Completed";
                    Script.ExecutionCount++;
                    _ = _storageService.SaveScriptAsync(Script);

                    if (runningTab != null)
                    {
                        runningTab.ExecutionTimeText = timeText;
                        runningTab.CompilerStatusText = statusText;
                    }
                    if (runningTab == null || runningTab.IsActive)
                    {
                        if (runningTab != null)
                        {
                            ConsoleOutput = runningTab.ConsoleOutput;
                        }
                        ExecutionTimeText = timeText;
                        CompilerStatusText = statusText;
                        SelectedBottomTabIndex = HasNoResults ? 1 : 0; // Results when the run drew anything there, as for every language
                    }
                }
                else if (kernelResult.WasCancelled)
                {
                    CompilerStatusText = timeoutCts.IsCancellationRequested
                        ? $"⏱️ Timed out after {timeoutSeconds}s"
                        : "🛑 Cancelled";
                }
                else
                {
                    CompilerStatusText = "Execution Failed";
                    if (kernelResult.Diagnostics.Count > 0)
                    {
                        Diagnostics.Clear();
                        foreach (var d in kernelResult.Diagnostics)
                        {
                            Diagnostics.Add(new Common.DiagnosticItemViewModel(d, (l, c) => RequestNavigateToCaret?.Invoke(l, c)));
                        }
                        // Counted from the result, not from the shown list: a live check that finishes now changes that list
                        // while it is being enumerated.
                        ErrorCount = kernelResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
                        WarningCount = kernelResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
                        SelectedBottomTabIndex = 2;
                    }
                    else
                    {
                        SelectedBottomTabIndex = 1;
                    }
                }
            }
            else
            {
                var (success, bytes, diagnostics) = await Task.Run(() =>
                    _compilerService.CompileToAssembly(Code, CurrentLanguageMode));

                if (!success || bytes == null)
                {
                    var failMsg = "❌ Compilation failed. Check the Problems tab for details.\n";
                    foreach (var diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        failMsg += $"  • {diag.LocationString}: {diag.Id} {diag.Message}\n";
                    }
                    if (runningTab != null) runningTab.ConsoleOutput += failMsg;
                    if (runningTab == null || runningTab.IsActive)
                    {
                        ConsoleOutput += failMsg;
                        CompilerStatusText = "Build Failed";
                        SelectedBottomTabIndex = 2;
                    }
                    return;
                }

                var startMsg = "[Build] Succeeded. Executing in-memory...\n--------------------------------------------------\n";
                if (runningTab != null) runningTab.ConsoleOutput += startMsg;
                if (runningTab == null || runningTab.IsActive)
                {
                    ConsoleOutput += startMsg;
                    CompilerStatusText = "Running...";
                }

                var executionTask = _executionEngine.ExecuteAsync(bytes, terminal.Write, token, stdin);

                ExecutionResult result;
                if (await ExecutionAbandonment.WaitWithGraceAsync(executionTask, token))
                {
                    result = await executionTask;
                }
                else
                {
                    // See the Statements/Expression branch above: cancellation only stops a
                    // compiled Main() *before* it starts (ScriptExecutionEngine.ExecuteAsync checks
                    // the token once, then invokes the entry point) — a synchronously-blocked call
                    // inside it can't be preempted either. Give up waiting rather than hang forever.
                    result = new ExecutionResult { WasCancelled = true };

                    var abandonedRunId = myRunId;
                    _ = executionTask.ContinueWith((Task<ExecutionResult> t) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (abandonedRunId != _executionRunId) return; // a newer run already took over
                            var statusText = t.IsFaulted
                                ? "Background task (previously abandoned) finished with an error"
                                : "Background task (previously abandoned) finished";
                            if (runningTab != null) runningTab.CompilerStatusText = statusText;
                            if (runningTab == null || runningTab.IsActive) CompilerStatusText = statusText;
                        });
                    }, TaskScheduler.Default);
                }

                // Before the closing lines, so they always come after the program's own output.
                terminal.Complete();
                completed = result.Success;

                var endMsg = "\n--------------------------------------------------\n";
                if (result.Success)
                {
                    endMsg += $"✅ Execution finished in {result.Elapsed.TotalMilliseconds:N0} ms\n";
                    var timeText = $"{result.Elapsed.TotalMilliseconds:N0} ms";
                    var statusText = DumpResults.Count > 0
                        ? $"Completed • {DumpResults.Count} visual dump{(DumpResults.Count == 1 ? "" : "s")}"
                        : "Completed";
                    Script.ExecutionCount++;
                    _ = _storageService.SaveScriptAsync(Script);

                    if (runningTab != null)
                    {
                        runningTab.ConsoleOutput += endMsg;
                        runningTab.ExecutionTimeText = timeText;
                        runningTab.CompilerStatusText = statusText;
                    }
                    if (runningTab == null || runningTab.IsActive)
                    {
                        ConsoleOutput += endMsg;
                        ExecutionTimeText = timeText;
                        CompilerStatusText = statusText;
                        SelectedBottomTabIndex = HasNoResults ? 1 : 0; // Results when the run drew anything there, as for every language
                    }
                }
                else if (result.WasCancelled)
                {
                    var cancelMsg = timeoutCts.IsCancellationRequested
                        ? $"⏱️ Execution timed out after {timeoutSeconds}s.\n"
                        : "⚠️ Execution was cancelled.\n";
                    var statusText = timeoutCts.IsCancellationRequested
                        ? $"⏱️ Timed out after {timeoutSeconds}s"
                        : "🛑 Cancelled";

                    if (runningTab != null)
                    {
                        runningTab.ConsoleOutput += cancelMsg;
                        runningTab.CompilerStatusText = statusText;
                    }
                    if (runningTab == null || runningTab.IsActive)
                    {
                        ConsoleOutput += cancelMsg;
                        CompilerStatusText = statusText;
                    }
                }
                else
                {
                    var errText = $"❌ Execution failed: {result.Error}\n";
                    if (runningTab != null)
                    {
                        runningTab.ConsoleOutput += errText;
                        runningTab.CompilerStatusText = "Runtime Error";
                    }
                    if (runningTab == null || runningTab.IsActive)
                    {
                        ConsoleOutput += errText;
                        CompilerStatusText = "Runtime Error";
                    }
                }
            }
        }
        finally
        {
            stdin.Complete();
            if (runningTab != null)
            {
                runningTab.InProcessStdin = null;
                runningTab.IsExecuting = false;
                runningTab.ExecutionTimeText = ExecutionTimeText;
                runningTab.CompilerStatusText = CompilerStatusText;
            }
            if (runningTab == null || runningTab.IsActive)
            {
                IsExecuting = false;
                IsAcceptingProgramInput = false;
            }
            UpdateTestCaseResults(runningCases, runningCode, runningTab?.ConsoleOutput ?? ConsoleOutput, completed);
            
            PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.HookRegistry.InvokeAfterScriptRun(
                new FrySharp.Sdk.ExecutionFinishedHookContext
                {
                    LanguageId = ActiveLanguage.Id,
                    DocumentPath = Script.SourceFilePath,
                    Success = completed,
                    Output = runningTab?.ConsoleOutput ?? ConsoleOutput,
                    Error = completed ? string.Empty : (CompilerStatusText ?? "Execution failed")
                });
        }
    }

    // How a running script's output reaches the UI thread unless the constructor was given another way: queued when it
    // comes from another thread in a live UI app, right away on the UI thread or in unit tests.
    private static void RunOnUiThread(Action action)
    {
        Services.Common.UiDispatchHelper.RunOnUi(action);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsExecuting) return;
        var targetTab = OpenTabs.FirstOrDefault(t => t.Id == Script.Id);
        targetTab?.InProcessStdin?.Complete();
        if (targetTab?.ActiveRun != null)
        {
            // A program of this tab (a Python run): end it and what it started, and leave other tabs' runs alone.
            targetTab.ActiveRun.Stop();
        }
        else
        {
            targetTab?.ExecutionCts?.Cancel();
            _executionCts?.Cancel();
        }
        if (targetTab != null)
        {
            targetTab.ConsoleOutput += "\n🛑 Cancellation requested by user...\n";
            targetTab.CompilerStatusText = "Stopping...";
        }
        ConsoleOutput += "\n🛑 Cancellation requested by user...\n";
        CompilerStatusText = "Stopping...";
    }

    [RelayCommand]
    private void ClearConsole()
    {
        ConsoleHeader = string.Empty;
        ConsoleBody = string.Empty;
        ConsoleFooter = string.Empty;
        ConsoleExitCode = null;
        ConsoleOutput = string.Empty;
        var tab = OpenTabs.FirstOrDefault(t => t.Id == Script.Id);
        if (tab != null)
        {
            tab.ConsoleHeader = string.Empty;
            tab.ConsoleBody = string.Empty;
            tab.ConsoleFooter = string.Empty;
            tab.ConsoleExitCode = null;
            tab.ConsoleOutput = string.Empty;
        }
    }

    [RelayCommand]
    private void SetBottomTab(string index)
    {
        if (int.TryParse(index, out var idx))
        {
            SelectedBottomTabIndex = idx;
            IsBottomDeckExpanded = true;
        }
    }

    [RelayCommand]
    public void ToggleBottomDeck()
    {
        IsBottomDeckExpanded = !IsBottomDeckExpanded;
    }

    [RelayCommand]
    public void ShowProblemsTab()
    {
        SelectedBottomTabIndex = 2;
        IsBottomDeckExpanded = true;
    }

    [RelayCommand]
    public void ShowConsoleTab()
    {
        SelectedBottomTabIndex = 1;
        IsBottomDeckExpanded = true;
    }

    [RelayCommand]
    public void ShowDumpResultsTab()
    {
        SelectedBottomTabIndex = 0;
        IsBottomDeckExpanded = true;
    }

    [RelayCommand]
    public void ShowTestCasesTab()
    {
        SelectedBottomTabIndex = 3;
        IsBottomDeckExpanded = true;
    }

    [RelayCommand]
    public void ShowDebuggerTab()
    {
        SelectedBottomTabIndex = 4;
        IsBottomDeckExpanded = true;
    }
}
