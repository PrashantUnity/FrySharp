using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Display;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Visualizers.Models;
using PdfEditorApp.Plugins.CSharpEditor.Visualizers.Services;
using PdfEditorApp.Plugins.CSharpEditor.Visuals.Interaction;
using PdfEditorApp.Plugins.CSharpEditor.Visuals.Output;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Kernels;

public class KernelExecutionResult
{
    public bool Success { get; set; }
    public string ConsoleOutput { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
    public bool WasCancelled { get; set; }
    public IReadOnlyList<DiagnosticItem> Diagnostics { get; set; } = Array.Empty<DiagnosticItem>();

    /// <summary>A dependency the code tried to use that isn't installed (a Python module), so the cell can offer to install it.</summary>
    public string? MissingDependency { get; set; }

    /// <summary>A name the code used that isn't defined (C#'s CS0103, Python's NameError), for the "run the cell that defines it" hint.</summary>
    public string? MissingName { get; set; }

    /// <summary>The kind of error a kernel in another language reported (Python's <c>ZeroDivisionError</c>), when it did.</summary>
    public string? ErrorName { get; set; }

    /// <summary>The line of the cell the error was on, when the kernel said (a Python or JavaScript traceback names it).</summary>
    public int? ErrorLine { get; set; }
}

public class NotebookExecutionKernel : INotebookKernel, ISearchPathKernel
{
    private ScriptState<object>? _currentState;
    private ScriptOptions _scriptOptions;
    private InteractiveAssemblyLoader _assemblyLoader = new();
    private readonly NuGetReferenceResolver _nuGetResolver;
    private readonly List<MetadataReference> _additionalReferences = new();

    // Per-instance: protects this kernel's own _currentState/_scriptOptions/_additionalReferences from
    // concurrent mutation (e.g. two Run clicks racing in the same tab). Each notebook tab owns its own
    // kernel, so this intentionally no longer serializes execution *across* tabs — that used to happen
    // by accident because this was `static`. Console redirection is handled separately by
    // ConsoleRoutingContext, which is AsyncLocal-scoped per execution and needs no lock at all.
    // Not readonly: HardReset() below replaces it outright to recover from an abandoned execution
    // that never released it.
    private SemaphoreSlim _executionLock = new(1, 1);

    // Where callbacks for this kernel's visuals run: between cells, under the same lock, or when a cell asks.
    private VisualEventLoop _events;

    /// <summary>The loop this kernel's visual event callbacks run on.</summary>
    public VisualEventLoop Events => _events;

    public bool IsSessionActive => _currentState != null;

    private static readonly Lazy<ScriptOptions> CachedDefaultScriptOptions = new(CreateDefaultScriptOptionsInternal);

    public static ScriptOptions SharedDefaultScriptOptions => CachedDefaultScriptOptions.Value;

    public static void Warmup()
    {
        _ = CachedDefaultScriptOptions.Value;
        ConsoleRoutingContext.EnsureInstalled();
        CompileAsync(CSharpScript.Create<object>("new List<double> { 1 }.Sum()", CachedDefaultScriptOptions.Value), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    // Roslyn shares what it reads from the referenced assemblies between compilations. When cells compile at the same
    // time in a process that hasn't compiled one yet, some notebooks' submissions end up with .NET types of their own,
    // and a later cell of theirs then can't convert between those and everyone else's: "'List<double>' does not contain
    // a definition for 'Sum'" about a list an earlier cell made. So cells compile one at a time (they still run side
    // by side), and the first compilation is kept, so what it read stays shared.
    private static readonly SemaphoreSlim CompileGate = new(1, 1);
    private static Script? _firstCompiled;

    private static async Task<Script<object>> CompileAsync(Script<object> script, CancellationToken ct)
    {
        await CompileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            script.Compile(ct);
            _firstCompiled ??= script;
        }
        finally
        {
            CompileGate.Release();
        }

        return script;
    }

    // A cell's code as the next submission: the first, or one that continues from the state so far.
    private async Task<ScriptState<object>> RunSubmissionAsync(string code, CancellationToken ct)
    {
        // Stop ends its loops even when they never look at a cancellation token.
        code = LoopGuard.Instrument(code, Microsoft.CodeAnalysis.SourceCodeKind.Script);
        var priorState = _currentState;
        var submission = priorState == null
            ? CSharpScript.Create<object>(code, _scriptOptions, assemblyLoader: _assemblyLoader)
            : priorState.Script.ContinueWith<object>(code, _scriptOptions);

        var script = await CompileAsync(submission, ct);
        return priorState == null
            ? await script.RunAsync(cancellationToken: ct)
            : await script.RunFromAsync(priorState, cancellationToken: ct);
    }

    public NotebookExecutionKernel()
    {
        _events = NewEventLoop();
        _nuGetResolver = new NuGetReferenceResolver();
        _assemblyLoader = new InteractiveAssemblyLoader();
        RegisterCoreDependencies(_assemblyLoader);
        _scriptOptions = CachedDefaultScriptOptions.Value;
    }

    // The host's assemblies (Display, and on the desktop Avalonia and FrySharp.Sdk) load as themselves, not as copies.
    private static void RegisterCoreDependencies(InteractiveAssemblyLoader loader)
    {
        foreach (var assembly in ScriptHost.Profile.Assemblies)
        {
            loader.RegisterDependency(assembly);
        }
        loader.RegisterDependency(typeof(System.Data.DataTable).Assembly);
    }

    private static ScriptOptions CreateDefaultScriptOptionsInternal()
    {
        var references = new List<MetadataReference>(RoslynCompilerService.SharedDefaultReferences);

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(ScriptImports.ScriptingImports);
    }

    /// <param name="sourceId">Optional id (a notebook cell id) that [CallerFilePath] reports for code in this submission.</param>
    public async Task<KernelExecutionResult> ExecuteCellAsync(
        string code,
        Action<string>? onLiveConsole = null,
        Action<RichCellOutput>? onRichOutput = null,
        CancellationToken ct = default,
        string? sourceId = null,
        TextReader? stdin = null)
    {
        var result = new KernelExecutionResult();
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(code))
        {
            result.Success = true;
            return result;
        }

        // Everything below can throw OperationCanceledException before the innermost try/catch even
        // starts (e.g. an already-cancelled token failing the very first WaitAsync) — that must come
        // back as a normal WasCancelled result, not an unhandled exception out of this method.
        try
        {
            await ExecuteCellCoreAsync(code, sourceId, result, sw, onLiveConsole, onRichOutput, stdin, ct);
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
            result.ErrorMessage = "Execution was cancelled.";
            sw.Stop();
            result.Elapsed = sw.Elapsed;
        }

        return result;
    }

    private async Task ExecuteCellCoreAsync(
        string code,
        string? sourceId,
        KernelExecutionResult result,
        Stopwatch sw,
        Action<string>? onLiveConsole,
        Action<RichCellOutput>? onRichOutput,
        TextReader? stdin,
        CancellationToken ct)
    {
        // 1. Process #r "nuget: ..." directives
        var nugetResult = await _nuGetResolver.ProcessDirectivesAsync(code, ct);

        foreach (var msg in nugetResult.Messages)
        {
            onLiveConsole?.Invoke(msg + Environment.NewLine);
        }

        if (nugetResult.References.Count > 0)
        {
            RegisterMetadataReferences(nugetResult.References, nugetResult);
        }

        var cleanCode = CellUsingDeclarations.Rewrite(nugetResult.SanitizedCode);
        if (!string.IsNullOrEmpty(sourceId))
        {
            // Keeps line numbers 1:1 with the editor while naming the submission for caller-info attributes
            cleanCode = $"#line 1 \"{sourceId}\"{Environment.NewLine}{cleanCode}";
        }

        // Captured locally (not read again from the field) so that if HardReset() swaps
        // _executionLock out from under an abandoned execution, this call still releases the same
        // instance it acquired, rather than over-releasing whatever fresh semaphore replaced it.
        var executionLock = _executionLock;
        await executionLock.WaitAsync(ct);
        try
        {
            // 2. Intercept Console.Out and live writers. ConsoleRoutingContext routes Console.Out/
            // Error per-execution via an AsyncLocal scope (same pattern as InteractiveDisplayContext
            // just below) instead of a raw global swap — so unlike the old ConsoleRedirectionGate, this
            // never needs to hold a process-wide lock for the duration of the script run: a concurrent
            // Code Studio "Program" run or another tab's cell keeps its own output correctly isolated.
            var liveWriter = new KernelLiveStringWriter(text =>
            {
                onLiveConsole?.Invoke(text);
            });

            using (ConsoleRoutingContext.EnterScope(liveWriter, stdin))
            using (InteractiveCancellationContext.EnterScope(ct))
            using (_events.Enter())
            {
                // What the cell declared with `using` is disposed when it has run (see CellUsingDeclarations).
                await using var cellDisposables = CellDisposables.EnterScope();
                try
                {
                    using (InteractiveDisplayContext.EnterScope(richOutput =>
                    {
                        onRichOutput?.Invoke(richOutput);
                    }))
                    {
                        var newState = await RunSubmissionAsync(cleanCode, ct);

                        if (ReferenceEquals(executionLock, _executionLock) && !ct.IsCancellationRequested)
                        {
                            _currentState = newState;
                        }
                        result.Success = true;

                        // Inspect return value for rich media or expression output
                        if (newState.ReturnValue != null)
                        {
                            InspectAndEmitReturnValue(newState.ReturnValue, onLiveConsole, onRichOutput);
                        }
                    }
                }
                catch (CompilationErrorException cee)
                {
                    result.Success = false;
                    var diags = new List<DiagnosticItem>();

                    foreach (var diag in cee.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        var lineSpan = diag.Location.GetMappedLineSpan();
                        diags.Add(new DiagnosticItem
                        {
                            Id = diag.Id,
                            Message = diag.GetMessage(),
                            Severity = diag.Severity,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1
                        });
                    }

                    result.Diagnostics = diags;
                    result.ErrorMessage = string.Join("\n", diags.Select(d => $"Line {d.Line}: {d.Message}"));
                    liveWriter.WriteLine($"\n❌ Compilation Error:\n{result.ErrorMessage}");
                }
                catch (OperationCanceledException)
                {
                    result.WasCancelled = true;
                    result.ErrorMessage = "Execution was cancelled.";
                    liveWriter.WriteLine("\n⚠️ Execution cancelled.");
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    var root = ex;
                    while (root.InnerException != null &&
                           (root is TargetInvocationException || root is TypeInitializationException || root is AggregateException))
                    {
                        root = root.InnerException;
                    }

                    result.ErrorMessage = root.Message;
                    result.ErrorName = root.GetType().Name;
                    liveWriter.WriteLine($"\n❌ Runtime Error: {root.GetType().Name}: {root.Message}\n{root.StackTrace}");
                    if (root != ex && ex.InnerException != null && ex.InnerException != root)
                    {
                        liveWriter.WriteLine($"\n(Root cause of {ex.GetType().Name}: {ex.Message})");
                    }
                }
                finally
                {
                    sw.Stop();
                    result.Elapsed = sw.Elapsed;
                    result.ConsoleOutput = liveWriter.ToString();
                }
            }
        }
        finally
        {
            executionLock.Release();
        }
    }

    private void InspectAndEmitReturnValue(
        object returnValue,
        Action<string>? onLiveConsole,
        Action<RichCellOutput>? onRichOutput)
    {
        // A handle is a visual the call that returned it has already shown.
        if (returnValue is DisplayHandle)
        {
            return;
        }

        // An ECharts chart is shown, unless the cell already showed it (EChart.Line(x).Show() returns the chart).
        if (returnValue is EChart echart)
        {
            if (!echart.IsShown) onRichOutput?.Invoke(echart.ToOutput());
            return;
        }

        // A spec, or a C# model with a visual form (a chart, 3D plot or visualizer model), is shown as that visual.
        if (VisualOutputs.TryFromModel(returnValue, out var visual))
        {
            onRichOutput?.Invoke(visual);
            return;
        }

        if (TryEmitVisualizer(returnValue))
        {
            return;
        }

        var host = ScriptHost.Profile.Display;
        if (host?.TryCreateElementOutput(returnValue) is { } element)
        {
            onRichOutput?.Invoke(element);
            return;
        }

        if ((host?.TryEncodeImage(returnValue) ?? SkiaImages.TryEncodePng(returnValue)) is { } png)
        {
            onRichOutput?.Invoke(RichCellOutput.ForImage(png));
            return;
        }

        if (returnValue is byte[] bytes && bytes.Length > 8 &&
            (bytes[0] == 0x89 && bytes[1] == 0x50 || bytes[0] == 0xFF && bytes[1] == 0xD8))
        {
            onRichOutput?.Invoke(new RichCellOutput
            {
                Kind = CellOutputKind.Image,
                ImageBytes = bytes,
                ImageFormat = "PNG"
            });
            return;
        }

        // Bare string that looks like an image base64 data URI
        if (returnValue is string str && str.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            onRichOutput?.Invoke(new RichCellOutput
            {
                Kind = CellOutputKind.Html,
                HtmlContent = str
            });
            return;
        }

        // Direct DumpTableResult
        if (returnValue is DumpTableResult dumpTable)
        {
            onRichOutput?.Invoke(new RichCellOutput
            {
                Kind = CellOutputKind.Table,
                TableResult = dumpTable
            });
            return;
        }

        // Dedicated Tabular Data (e.g. DataFrame, DataTable, DataView)
        if (DumpTableBuilder.IsTabularObject(returnValue, out var tabularTable))
        {
            onRichOutput?.Invoke(new RichCellOutput
            {
                Kind = CellOutputKind.Table,
                TableResult = tabularTable
            });
            return;
        }

        // 1. If it's a 1D collection of scalars (e.g. List<int> -> [ 1, 3, 4, 5, 6, 7, 8, 10 ])
        if (ObjectInspectorBuilder.IsCollectionOfScalars(returnValue, out var inlineFormatted))
        {
            onLiveConsole?.Invoke(inlineFormatted + Environment.NewLine);
            return;
        }

        // 2. If it's a scalar primitive / string
        if (ObjectInspectorBuilder.IsScalarType(returnValue.GetType()))
        {
            var formattedScalar = FormatValue(returnValue);
            onLiveConsole?.Invoke(formattedScalar + Environment.NewLine);
            return;
        }

        // 3. If it's a complex object (e.g. var people = new People(); people)
        try
        {
            var inspectorNode = ObjectInspectorBuilder.Build(returnValue);
            onRichOutput?.Invoke(new RichCellOutput
            {
                Kind = CellOutputKind.ObjectInspector,
                InspectorNode = inspectorNode
            });
            return;
        }
        catch
        {
            // Fallback to plain string representation
            var formatted = FormatValue(returnValue);
            onLiveConsole?.Invoke(formatted + Environment.NewLine);
        }
    }

    // Trackers, recorders and plain data structures (a tree node, a list node, a 2D grid): Display.* turns them into a
    // visualizer's spec and emits it into the cell's output scope.
    private static bool TryEmitVisualizer(object value)
    {
        switch (value)
        {
            case VisualizerRecorder recorder:
                Scripting.Display.Visualizer(recorder);
                return true;
            case VisualizerSequence sequence:
                Scripting.Display.Visualizer(new VisualizerOptions { Sequence = sequence, Kind = sequence.CurrentStep?.Kind ?? VisualizerKind.Matrix });
                return true;
            case TreeTracker treeTracker:
                Scripting.Display.Visualizer(treeTracker);
                return true;
            case GraphTracker graphTracker:
                Scripting.Display.Visualizer(graphTracker);
                return true;
            case MatrixTracker matrixTracker:
                Scripting.Display.Visualizer(matrixTracker);
                return true;
            case LinkedListTracker listTracker:
                Scripting.Display.Visualizer(listTracker);
                return true;
            case RecursionTracker recursionTracker:
                Scripting.Display.Visualizer(recursionTracker);
                return true;
            case IntervalTracker intervalTracker:
                Scripting.Display.Visualizer(intervalTracker);
                return true;
            case TrieTracker trieTracker:
                Scripting.Display.Visualizer(trieTracker);
                return true;
        }

        switch (DataStructureDetector.Detect(value))
        {
            case DataStructureShape.Tree:
                Scripting.Display.Tree(value, title: value.GetType().Name);
                return true;
            case DataStructureShape.LinkedList:
                Scripting.Display.LinkedList(value, title: value.GetType().Name);
                return true;
            case DataStructureShape.Grid:
                Scripting.Display.Matrix(value);
                return true;
            default:
                return false;
        }
    }

    public IReadOnlyList<NotebookVariableInfo> GetActiveVariables()
    {
        if (_currentState == null)
        {
            return Array.Empty<NotebookVariableInfo>();
        }

        var variables = new List<NotebookVariableInfo>();

        // ScriptState.Variables lists a name once per cell that declared it; only the latest one is in scope.
        var latest = _currentState.Variables
            .GroupBy(v => v.Name)
            .Select(g => g.Last());

        foreach (var v in latest)
        {
            try
            {
                variables.Add(new NotebookVariableInfo
                {
                    Name = v.Name,
                    TypeName = v.Type == null ? "var" : DumpTableBuilder.GetFriendlyTypeName(v.Type),
                    ValueDisplay = FormatValue(v.Value),
                    Kind = DetermineKind(v.Type, v.Value)
                });
            }
            catch
            {
                // Ignore evaluation errors
            }
        }

        return variables;
    }

    public void ResetSession()
    {
        // The variables callbacks would read are gone, so the callbacks go too.
        _events.Stop();
        _events = NewEventLoop();
        _currentState = null;
        _additionalReferences.Clear();
        _assemblyLoader = new InteractiveAssemblyLoader();
        RegisterCoreDependencies(_assemblyLoader);
        _scriptOptions = CachedDefaultScriptOptions.Value;
    }

    /// <summary>
    /// ResetSession() plus recovery from a previous execution that was abandoned (its Task.Run
    /// never returned — e.g. a script statement synchronously blocked on a network call that never
    /// responds) and so never released _executionLock: without this, every future Run on this
    /// kernel would itself hang waiting for that lock. Swaps in a fresh semaphore instead of trying
    /// to acquire/reset the old one; the abandoned execution still holds a reference to the old
    /// instance and will Release() it harmlessly into the void whenever/if it ever returns.
    /// </summary>
    // Callbacks wait for the lock a cell takes (read when they need it: HardReset replaces it).
    private VisualEventLoop NewEventLoop() => new(async ct =>
    {
        var gate = _executionLock;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new LockRelease(gate);
    });

    private sealed class LockRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    public void HardReset()
    {
        ResetSession();
        _executionLock = new SemaphoreSlim(1, 1);
    }

    public Task AddSearchPathAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.CompletedTask;

        var dllFiles = new List<string>();
        if (File.Exists(path) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            dllFiles.Add(path);
        }
        else if (Directory.Exists(path))
        {
            var topDlls = Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
            dllFiles.AddRange(topDlls);
            if (dllFiles.Count == 0)
            {
                dllFiles.AddRange(Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)));
            }
        }

        var newRefs = new List<MetadataReference>();
        foreach (var dll in dllFiles)
        {
            try
            {
                newRefs.Add(MetadataReference.CreateFromFile(dll));
            }
            catch { }
        }

        if (newRefs.Count > 0)
        {
            RegisterMetadataReferences(newRefs);
        }

        return Task.CompletedTask;
    }

    private void RegisterMetadataReferences(IEnumerable<MetadataReference> references, NuGetResolutionResult? nugetResult = null)
    {
        var list = references.ToList();
        if (list.Count == 0) return;

        _additionalReferences.AddRange(list);
        _scriptOptions = _scriptOptions.AddReferences(list);
        foreach (var r in list)
        {
            if (r is PortableExecutableReference per && !string.IsNullOrEmpty(per.FilePath) && File.Exists(per.FilePath))
            {
                try
                {
                    var asm = Assembly.LoadFrom(per.FilePath);
                    _assemblyLoader.RegisterDependency(asm);
                    _nuGetResolver.EnsureNativeAssetsResolved(asm, nugetResult);
                }
                catch { }
            }
        }
    }

    string INotebookKernel.LanguageId => LanguageIds.CSharp;

    string INotebookKernel.DisplayName => ".NET (C#)";

    // A statement blocked inside the studio's own process can't be interrupted, so Stop may have to abandon it.
    bool INotebookKernel.CanForceStop => false;

    Task<KernelExecutionResult> INotebookKernel.ExecuteAsync(KernelExecutionRequest request, CancellationToken ct) =>
        ExecuteCellAsync(request.Code, request.OnConsole, request.OnRichOutput, ct, request.SourceId,
            request.OnInputRequest is { } ask ? new InputRequestReader(ask, ct) : null);

    Task<IReadOnlyList<NotebookVariableInfo>> INotebookKernel.GetVariablesAsync(CancellationToken ct) =>
        Task.FromResult(GetActiveVariables());

    async Task<string> INotebookKernel.GetValueJsonAsync(string name, CancellationToken ct)
    {
        var bareName = name.TrimStart('@');
        var executionLock = _executionLock;
        await executionLock.WaitAsync(ct);
        try
        {
            if (_currentState == null) throw new KernelValueException($"No C# cell has run yet, so there's no '{name}'.");
            var variable = _currentState.Variables.LastOrDefault(v => v.Name == bareName)
                           ?? throw new KernelValueException($"C# has no variable named '{name}'. Run the C# cell that declares it first.");
            return KernelValueSharing.ToJson(variable.Value, variable.Type, name);
        }
        finally
        {
            executionLock.Release();
        }
    }

    /// <summary>
    /// A shared value in C#: a variable of that name keeps its type (the value is converted to it) when it can; otherwise
    /// a new one is declared with the type that fits the value (<c>int[]</c>, <c>Dictionary&lt;string, object&gt;</c>…).
    /// </summary>
    async Task INotebookKernel.SetValueFromJsonAsync(string name, string json, CancellationToken ct)
    {
        var identifier = KernelValueSharing.Identifier(name);
        var bareName = identifier.TrimStart('@');
        var (inferredType, inferredValue) = KernelValueSharing.Infer(json);

        var executionLock = _executionLock;
        await executionLock.WaitAsync(ct);
        try
        {
            var existing = _currentState?.Variables.LastOrDefault(v => v.Name == bareName);
            if (existing is { IsReadOnly: false })
            {
                if (existing.Type == typeof(object))
                {
                    existing.Value = inferredValue;
                    return;
                }

                if (KernelValueSharing.TryFromJson(json, existing.Type, out var converted, out _))
                {
                    existing.Value = converted;
                    return;
                }
            }

            // Declared by a line of C# (so the variable is like any other a cell declares), then given the value. The line
            // names only .NET types: naming one of the plugin's own can make Roslyn load references mid-chain, after which
            // later cells can't bind LINQ over earlier cells' variables.
            var code = $"{KernelValueSharing.TypeName(inferredType)} {identifier} = default;";
            try
            {
                var state = await RunSubmissionAsync(code, ct);
                state.GetVariable(bareName)!.Value = inferredValue;
                _currentState = state;
            }
            catch (CompilationErrorException ex)
            {
                throw new KernelValueException($"Couldn't declare '{name}' in C#: {ex.Diagnostics.FirstOrDefault()?.GetMessage()}");
            }
        }
        finally
        {
            executionLock.Release();
        }
    }

    // Everything lives in the studio's own process; there's nothing to shut down.
    void IDisposable.Dispose()
    {
    }

    private static string FormatValue(object? val)
    {
        if (val == null) return "null";
        if (val is string s) return $"\"{s}\"";
        if (val is bool or int or long or double or float or decimal or DateTime or TimeSpan or Guid)
        {
            return val.ToString() ?? "";
        }
        if (ScriptHost.Profile.Display?.Describe(val) is { } host) return host.Text;

        var type = val.GetType();
        if (type.FullName?.Contains("SkiaSharp") == true)
        {
            return $"<{type.Name}>";
        }

        if (val is ICollection col)
        {
            return $"Count = {col.Count}";
        }

        // Object.ToString() gives the full runtime name ("Submission#2+Solution"); show the type the way C# wrote it.
        var text = val.ToString();
        return string.IsNullOrEmpty(text) || text == type.FullName || text == type.ToString()
            ? $"{{{DumpTableBuilder.GetFriendlyTypeName(type)}}}"
            : text;
    }

    private static string DetermineKind(Type? type, object? val)
    {
        if (type == null || val == null) return "Value";
        if (ScriptHost.Profile.Display?.Describe(val) is { } host) return host.Kind;
        if (SkiaImages.IsSkiaImage(val)) return "Image";
        if (val is IEnumerable and not string) return "Collection";
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)) return "Primitive";
        return "Object";
    }

    private sealed class KernelLiveStringWriter : StringWriter
    {
        private readonly Action<string> _onWrite;
        [ThreadStatic] private static bool _isWriting;

        public KernelLiveStringWriter(Action<string> onWrite) => _onWrite = onWrite;

        public override void Write(char value)
        {
            base.Write(value);
            if (!_isWriting)
            {
                _isWriting = true;
                try { _onWrite(value.ToString()); }
                finally { _isWriting = false; }
            }
        }

        public override void Write(string? value)
        {
            base.Write(value);
            if (value != null && !_isWriting)
            {
                _isWriting = true;
                try { _onWrite(value); }
                finally { _isWriting = false; }
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            base.Write(buffer, index, count);
            if (buffer != null && count > 0 && !_isWriting)
            {
                _isWriting = true;
                try { _onWrite(new string(buffer, index, count)); }
                finally { _isWriting = false; }
            }
        }
    }
}
