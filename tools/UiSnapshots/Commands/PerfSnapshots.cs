using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Editor;
using PdfEditorApp.Plugins.CSharpEditor.Controls.Notebooks;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Problems.Catalogs.Blind75;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;
using PdfEditorApp.Plugins.CSharpEditor.Views;

namespace PdfEditorApp.Plugins.CSharpEditor.Tools.UiSnapshots.Commands;

/// <summary>
/// <c>perf</c>: times the real studio (host view, all six pages) over a generated workspace: the first visit of each page,
/// warm page switches, tab switches and file opens, plus how many times the workspace was re-scanned and how much memory
/// the visits leave behind. Numbers are milliseconds on the UI thread (view built, bindings run, layout done, one frame
/// rendered), so they are comparable between runs on one machine, not absolute.
/// </summary>
internal static class PerfSnapshots
{
    private readonly record struct Sample(double ToLayout, double ToFrame);

    public static void Run(Options options)
    {
        int files = Math.Max(5, options.Int("files", 200));
        int rounds = Math.Max(3, options.Int("rounds", 6));
        int notebooks = Math.Max(2, files / 20);

        var languages = new StudioLanguageServices(Snapshot.TempFolder("perf-languages"));
        var storage = new LocalScriptStorageService(Snapshot.TempFolder("perf-workspace"), languages.Registry);
        Console.WriteLine($"Workspace: {files} scripts, {notebooks} notebooks. Rounds: {rounds}.");
        for (int i = 0; i < files; i++) Pump(storage.CreateNewScriptAsync($"Script {i:D4}"));
        for (int i = 0; i < notebooks; i++) Pump(storage.CreateNewNotebookAsync($"Notebook {i:D3}"));

        var summaries = Pump(storage.LoadWorkspaceSummariesAsync());
        var scripts = summaries.Where(s => s.IsScript).Take(8).Select(s => Pump(storage.LoadScriptAsync(s.Id))!).ToList();
        var notebook = Pump(storage.LoadNotebookAsync(summaries.First(s => s.IsNotebook).Id))!;

        using var probe = new StallProbe();
        if (options.Int("table-rows", 0) is > 0 and var tableRows)
        {
            PerfLoading.TableOnly(probe, tableRows, options.Value("table-shot"));
            return;
        }

        var clock = Stopwatch.StartNew();
        var host = new CSharpStudioHostViewModel(
            blindProgress: new LocalBlindProgressService(Snapshot.TempFolder("perf-blind")),
            languages: languages,
            storageService: storage);
        Console.WriteLine($"Studio view model created (runs on the UI thread when the plugin opens): {clock.Elapsed.TotalMilliseconds:F0} ms");
        bool memoryTrace = options.Flag("memory-trace");
        void Heap(string where) { if (memoryTrace) Console.WriteLine($"  heap {where}: {GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:F0} MB"); }
        Heap("after the studio view model");

        clock.Restart();
        var view = new CSharpStudioHostView { DataContext = host };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using (window.CaptureRenderedFrame()) { }
        Console.WriteLine($"Studio window shown (Hub built, laid out, one frame): {clock.Elapsed.TotalMilliseconds:F0} ms");
        Heap("after the window");

        clock.Restart();
        // --open-early: open a script the moment the window is up, the way a user clicks a recent file at once.
        if (options.Flag("open-early"))
        {
            double earlyStall = 0;
            Sample early = default;
            earlyStall = probe.Measure(() => early = Navigate(window, () => host.NavigateToCodeStudio(scripts[0]), () => ReferenceEquals(host.CurrentPage, host.CodeStudioViewModel)));
            Console.WriteLine($"Code Studio opened during start-up: {early.ToFrame:F0} ms after the window; longest input wait {earlyStall:F0} ms");
        }

        if (options.Flag("theme-only"))
        {
            // Only the Hub and Settings built: what a theme costs without the other pages kept alive.
            Snapshot.WaitFor(() => host.CodeStudioViewModel != null && !host.ManagerViewModel.IsRefreshingList, TimeSpan.FromMinutes(3));
            PerfLoading.ThemeSwitch(window, host, probe);
            window.Close();
            return;
        }

        bool started = false;
        double startupStall = probe.Measure(() => started = Snapshot.WaitFor(() => host.CodeStudioViewModel != null && host.NotebookStudioViewModel != null && !host.ManagerViewModel.IsRefreshingList, TimeSpan.FromMinutes(3)));
        if (!started)
        {
            throw new InvalidOperationException("The studio never finished starting up.");
        }
        Console.WriteLine($"Engine and Hub data ready: {clock.Elapsed.TotalMilliseconds:F0} ms (background); longest input wait meanwhile {startupStall:F0} ms");
        // The engine's second stage (the first compilation) runs on in the background; keep it out of the page timings.
        double warmStall = probe.Measure(() => Pump(host.Engine.WarmReady));
        Console.WriteLine($"Engine stages: core {host.Engine.CoreElapsed.TotalMilliseconds:F0} ms, warm-up {host.Engine.WarmElapsed.TotalMilliseconds:F0} ms (state {host.Engine.State}); longest input wait during warm-up {warmStall:F0} ms");
        Heap("after the engine warmed up");

        if (options.Flag("style-cost"))
        {
            foreach (var file in new[] { "Styles/StudioStyles.axaml", "Views/CSharpManagerStyles.axaml" })
            {
                long before = GC.GetTotalMemory(forceFullCollection: true);
                var keep = new List<object>();
                var c = Stopwatch.StartNew();
                for (int i = 0; i < 5; i++)
                {
                    var include = new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://CSharpEditorPlugin/")) { Source = new Uri($"avares://CSharpEditorPlugin/{file}") };
                    keep.Add(include.Loaded);
                }
                long after = GC.GetTotalMemory(forceFullCollection: true);
                Console.WriteLine($"  style include {file}: {c.Elapsed.TotalMilliseconds / 5:F1} ms and {(after - before) / 5.0 / 1048576:F1} MB each");
                GC.KeepAlive(keep);
            }
        }

        if (options.Flag("memory-parts"))
        {
            // What each part of the studio holds once it is built and shown on its own (heap after a full collection).
            var parts = new (string Name, Func<Control> Make)[]
            {
                ("AI composer", () => new PdfEditorApp.Plugins.CSharpEditor.Controls.AI.StudioFloatingComposerControl { DataContext = host.AiComposer }),
                ("Hub page", () => new CSharpManagerView { DataContext = host.ManagerViewModel }),
                ("Settings page", () => new CSharpSettingsView { DataContext = host.SettingsViewModel }),
                ("Code Studio page", () => new CSharpCodeStudioView { DataContext = host.CodeStudioViewModel }),
                ("Notebook page", () => new CSharpNotebookStudioView { DataContext = host.NotebookStudioViewModel }),
                ("Docs page", () => new CSharpDocsView { DataContext = host.DocsViewModel }),
                ("Loading card", () => new PdfEditorApp.Plugins.CSharpEditor.Controls.Studio.StudioLoadingOverlayControl()),
            };
            if (options.Flag("memory-children"))
            {
                var studioView = new CSharpCodeStudioView { DataContext = host.CodeStudioViewModel };
                var holder = new Window { Width = 1400, Height = 900, Content = studioView };
                holder.Show();
                Snapshot.Settle(3);
                var types = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(studioView).OfType<UserControl>()
                    .Select(c => (c.GetType(), c.DataContext)).GroupBy(t => t.Item1).Select(g => g.First()).ToList();
                holder.Close();
                Snapshot.Settle(2);
                foreach (var (type, dataContext) in types)
                {
                    if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                    long before = GC.GetTotalMemory(forceFullCollection: true);
                    var control = (Control)Activator.CreateInstance(type)!;
                    control.DataContext = dataContext;
                    var w = new Window { Width = 1400, Height = 900, Content = control };
                    w.Show();
                    Snapshot.Settle(3);
                    long after = GC.GetTotalMemory(forceFullCollection: true);
                    Console.WriteLine($"  child {type.Name}: +{(after - before) / 1048576.0:F0} MB");
                    w.Close();
                    Snapshot.Settle(2);
                    GC.GetTotalMemory(forceFullCollection: true);
                }
            }

            foreach (var part in parts)
            {
                long before = GC.GetTotalMemory(forceFullCollection: true);
                var partWindow = new Window { Width = 1400, Height = 900, Content = part.Make() };
                partWindow.Show();
                Snapshot.Settle(3);
                long shown = GC.GetTotalMemory(forceFullCollection: true);
                Console.WriteLine($"  part {part.Name}: +{(shown - before) / 1048576.0:F0} MB");
                partWindow.Close();
                Snapshot.Settle(2);
            }
        }

        var pages = new (string Name, Action Go, Func<bool> Arrived)[]
        {
            ("Docs", () => host.NavigateToDocs(), () => ReferenceEquals(host.CurrentPage, host.DocsViewModel)),
            ("Blind 75", () => host.NavigateToBlindProblems(), () => ReferenceEquals(host.CurrentPage, host.BlindProblemsViewModel)),
            ("Settings", () => host.NavigateToSettings(), () => ReferenceEquals(host.CurrentPage, host.SettingsViewModel)),
            ("Code Studio", () => host.NavigateToCodeStudio(scripts[0]), () => ReferenceEquals(host.CurrentPage, host.CodeStudioViewModel)),
            ("Notebook", () => host.NavigateToNotebookStudio(notebook), () => ReferenceEquals(host.CurrentPage, host.NotebookStudioViewModel)),
            ("Hub", () => host.NavigateToManager(), () => ReferenceEquals(host.CurrentPage, host.ManagerViewModel)),
        };

        var samples = pages.ToDictionary(p => p.Name, _ => new List<Sample>());
        var stalls = pages.ToDictionary(p => p.Name, _ => new List<double>());
        long memoryAfterFirstRound = 0;
        int scansBefore = storage.WorkspaceScanCount;
        for (int round = 0; round < rounds; round++)
        {
            foreach (var page in pages)
            {
                Sample sample = default;
                stalls[page.Name].Add(probe.Measure(() => sample = Navigate(window, page.Go, page.Arrived)));
                samples[page.Name].Add(sample);
                if (round == 0) Heap($"after the first visit of {page.Name}");
                // --shots: what the page looks like in the studio host, last round only (kept out of the timing).
                if (options.Flag("shots") && round == rounds - 1) Snapshot.Save(window, $"host-{page.Name.ToLowerInvariant().Replace(' ', '-')}");
                // The Hub reloads on return; let that finish so it does not bleed into the next page's timing.
                if (page.Name == "Hub") Snapshot.WaitFor(() => !host.ManagerViewModel.IsRefreshingList, TimeSpan.FromMinutes(1));
            }

            if (round == 0) memoryAfterFirstRound = SettledMemory();
        }

        long memoryAtEnd = SettledMemory();
        int scansDuringPages = storage.WorkspaceScanCount - scansBefore;

        Console.WriteLine();
        Console.WriteLine($"{"page",-14}{"first visit",14}{"warm switch",14}{"warm + frame",14}{"max input wait",16}   (ms)");
        foreach (var page in pages)
        {
            var list = samples[page.Name];
            var warm = list.Skip(1).ToList();
            Console.WriteLine($"{page.Name,-14}{list[0].ToLayout,14:F1}{Median(warm.Select(s => s.ToLayout)),14:F1}{Median(warm.Select(s => s.ToFrame)),14:F1}{stalls[page.Name].Skip(1).DefaultIfEmpty().Max(),16:F1}");
        }

        // Tabs and file opens in the Code Studio.
        Navigate(window, () => host.NavigateToCodeStudio(scripts[0]), () => ReferenceEquals(host.CurrentPage, host.CodeStudioViewModel));
        var codeVm = host.CodeStudioViewModel!;
        var opens = new List<Sample>();
        double openStall = probe.Measure(() =>
        {
            foreach (var script in scripts.Skip(1))
            {
                opens.Add(Timed(window, () => Pump(codeVm.UpdateActiveScriptAsync(script))));
            }
        });

        var switches = new List<Sample>();
        int scansBeforeSwitches = storage.WorkspaceScanCount;
        var switchGcBefore = (Gen0: GC.CollectionCount(0), Gen1: GC.CollectionCount(1), Gen2: GC.CollectionCount(2), Pause: GC.GetTotalPauseDuration(),
            UiBytes: GC.GetAllocatedBytesForCurrentThread(), AllBytes: GC.GetTotalAllocatedBytes(precise: true));
        // --alloc-types: also which types the switches allocate and which full collections they ran into (and why).
        using var sampler = options.Flag("alloc-types") ? new AllocationSampler() : null;
        double switchStall = 0;
        void SwitchTabs() => switchStall = probe.Measure(() =>
        {
            for (int i = 0; i < rounds * codeVm.OpenTabs.Count; i++)
            {
                var tab = codeVm.OpenTabs[i % codeVm.OpenTabs.Count];
                switches.Add(Timed(window, () => Pump(codeVm.SwitchToTabAsync(tab))));
            }
        });
        // --jit-watch: what the JIT compiles over the switches (see JitWatcher: it decides when hot code gets optimized).
        using var jitWatcher = options.Flag("jit-watch") ? new JitWatcher() : null;
        if (sampler != null) sampler.Report("over the tab switches", SwitchTabs);
        else if (jitWatcher != null) jitWatcher.Report("over the tab switches", SwitchTabs);
        else SwitchTabs();

        int scansDuringSwitches = storage.WorkspaceScanCount - scansBeforeSwitches;

        // --editor-props: what the code editor's text is drawn with (font, features, sizes): what text shaping costs.
        if (options.Flag("editor-props") && window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().FirstOrDefault(e => e.IsEffectivelyVisible) is { } codeEditor)
        {
            var textView = codeEditor.TextArea.TextView;
            Console.WriteLine($"  editor: FontFamily '{codeEditor.FontFamily}' ({codeEditor.FontFamily.GetHashCode()}) size {codeEditor.FontSize} weight {codeEditor.FontWeight} style {codeEditor.FontStyle} stretch {codeEditor.FontStretch}; " +
                              $"text view FontFamily '{Avalonia.Controls.Documents.TextElement.GetFontFamily(textView)}' features '{Avalonia.Controls.Documents.TextElement.GetFontFeatures(textView)}' " +
                              $"letter spacing {Avalonia.Controls.Documents.TextElement.GetLetterSpacing(textView)}; visual lines {textView.VisualLines.Count}; bounds {textView.Bounds.Size}");
            var family = codeEditor.FontFamily;
            var typeface = new Avalonia.Media.Typeface(family);
            var g1 = typeface.GlyphTypeface;
            var g2 = new Avalonia.Media.Typeface(family).GlyphTypeface;
            Console.WriteLine($"  editor font family: names {string.Join("|", family.FamilyNames)}; key {family.Key?.GetType().Name} {family.Key}; " +
                              $"glyph typeface {g1.FamilyName} {g1.GetType().Name}, same object twice: {ReferenceEquals(g1, g2)}; " +
                              $"{Median(Repeat(200, () => new Avalonia.Media.Typeface(family).GlyphTypeface)) * 1000:F1} us to resolve");
            var line = "            var seen = new Dictionary<int, int>();   // value -> index where it was seen".AsMemory();
            var shaperOptions = new Avalonia.Media.TextFormatting.TextShaperOptions(g1, 13);
            Console.WriteLine($"  shaping one code line: {Median(Repeat(200, () => Avalonia.Media.TextFormatting.TextShaper.Current.ShapeText(line, shaperOptions).Length)) * 1000:F1} us; " +
                              $"a TextLayout of it: {Median(Repeat(100, () => new Avalonia.Media.TextFormatting.TextLayout(line.ToString(), typeface, 13, Avalonia.Media.Brushes.White).Width)) * 1000:F1} us");
        }

        Console.WriteLine();
        Console.WriteLine($"{"file open",-14}{Median(opens.Select(s => s.ToLayout)),14:F1}{"",14}{Median(opens.Select(s => s.ToFrame)),14:F1}   ({opens.Count} opens)");
        Console.WriteLine($"{"tab switch",-14}{Median(switches.Select(s => s.ToLayout)),14:F1}{"",14}{Median(switches.Select(s => s.ToFrame)),14:F1}   ({switches.Count} switches, {codeVm.OpenTabs.Count} tabs)");
        Console.WriteLine($"longest input wait: {openStall:F1} ms over the file opens, {switchStall:F1} ms over the tab switches");
        Console.WriteLine($"slowest tab switch {switches.Max(s => s.ToFrame):F1} ms; the garbage collector ran {GC.CollectionCount(0) - switchGcBefore.Gen0} gen-0, {GC.CollectionCount(1) - switchGcBefore.Gen1} gen-1 and {GC.CollectionCount(2) - switchGcBefore.Gen2} gen-2 collections over the switches, pausing for {(GC.GetTotalPauseDuration() - switchGcBefore.Pause).TotalMilliseconds:F0} ms in all; allocated per switch {(GC.GetAllocatedBytesForCurrentThread() - switchGcBefore.UiBytes) / 1048576.0 / switches.Count:F1} MB on the UI thread, {(GC.GetTotalAllocatedBytes(precise: true) - switchGcBefore.AllBytes) / 1048576.0 / switches.Count:F1} MB in all");

        // The same once the JIT has optimized the code these use. Until no new method has been compiled for a moment the
        // runtime keeps running hot code unoptimized, so the first opens and switches of a session cost up to twice as
        // much, and how far that warm-up has got by then varies from run to run: these are the numbers to hold to budget.
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(1500);
        Dispatcher.UIThread.RunJobs();
        var openedTabs = codeVm.OpenTabs.Skip(1).ToList();
        foreach (var tab in openedTabs) Pump(codeVm.CloseTabAsync(tab));
        Dispatcher.UIThread.RunJobs();
        var warmOpens = scripts.Skip(1).Select(script => Timed(window, () => Pump(codeVm.UpdateActiveScriptAsync(script)))).ToList();
        var warmSwitches = Enumerable.Range(0, codeVm.OpenTabs.Count * 2).Select(i => Timed(window, () => Pump(codeVm.SwitchToTabAsync(codeVm.OpenTabs[i % codeVm.OpenTabs.Count])))).ToList();
        Console.WriteLine($"warmed up: file open {Median(warmOpens.Select(s => s.ToLayout)):F1} ms (+frame {Median(warmOpens.Select(s => s.ToFrame)):F1}), " +
                          $"tab switch {Median(warmSwitches.Select(s => s.ToLayout)):F1} ms (+frame {Median(warmSwitches.Select(s => s.ToFrame)):F1})");
        Console.WriteLine();
        Console.WriteLine($"Workspace scans: {scansDuringPages} during {rounds} rounds of 6 page switches, {scansDuringSwitches} during {switches.Count} tab switches");
        Console.WriteLine($"Memory after round 1: {memoryAfterFirstRound / 1048576.0:F1} MB, after round {rounds}: {memoryAtEnd / 1048576.0:F1} MB " +
                          $"(+{(memoryAtEnd - memoryAfterFirstRound) / 1048576.0 / (rounds - 1):F2} MB per round)");
        // --big-kb n: one large source file among the open tabs, to see what a big document costs to open and switch to.
        if (options.Int("big-kb", 0) is > 0 and var bigKb)
        {
            var big = Pump(storage.CreateNewScriptAsync("Big file"));
            big.Code = BigCode(bigKb);
            Pump(storage.SaveScriptAsync(big));
            var bigOpen = Timed(window, () => Pump(codeVm.UpdateActiveScriptAsync(big)));
            var bigTab = codeVm.OpenTabs.First(t => t.Id == big.Id);
            var smallTab = codeVm.OpenTabs.First(t => t.Id != big.Id);
            var toBig = new List<Sample>();
            var fromBig = new List<Sample>();
            for (int i = 0; i < 5; i++)
            {
                fromBig.Add(Timed(window, () => Pump(codeVm.SwitchToTabAsync(smallTab))));
                toBig.Add(Timed(window, () => Pump(codeVm.SwitchToTabAsync(bigTab))));
            }

            // One keystroke at a time into the big document, the way typing reaches the studio (TextChanged -> Code).
            Pump(codeVm.SwitchToTabAsync(bigTab));
            var editor = (view.Pages.ViewFor(codeVm) as CSharpCodeStudioView)!.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
            var typing = new List<double>();
            var insertOnly = new List<double>();
            int gen2Before = GC.CollectionCount(2);
            int gen1Before = GC.CollectionCount(1);
            int gen0Before = GC.CollectionCount(0);
            var pauseBefore = GC.GetTotalPauseDuration();
            for (int i = 0; i < 10; i++)
            {
                var clock2 = Stopwatch.StartNew();
                editor.Document.Insert(editor.Document.TextLength - 2, "x");
                insertOnly.Add(clock2.Elapsed.TotalMilliseconds);
                Dispatcher.UIThread.RunJobs();
                typing.Add(clock2.Elapsed.TotalMilliseconds);
            }

            if (options.Flag("typing-experiments"))
            {
                // Where the queued work after a keystroke sits: run the jobs band by band, highest priority first.
                var bands = new (string Name, DispatcherPriority Priority)[]
                {
                    ("Render and above", DispatcherPriority.Render), ("Input", DispatcherPriority.Input),
                    ("Background", DispatcherPriority.Background), ("everything else", DispatcherPriority.SystemIdle)
                };
                var bandTimes = bands.ToDictionary(b => b.Name, _ => new List<double>());
                for (int i = 0; i < 10; i++)
                {
                    editor.Document.Insert(editor.Document.TextLength - 2, "x");
                    foreach (var band in bands)
                    {
                        var clock3 = Stopwatch.StartNew();
                        Dispatcher.UIThread.RunJobs(band.Priority);
                        bandTimes[band.Name].Add(clock3.Elapsed.TotalMilliseconds);
                    }
                }

                Console.WriteLine("  experiment: queued jobs after a keystroke, by priority: " +
                                  string.Join(", ", bands.Select(b => $"{b.Name} {Median(bandTimes[b.Name]):F1} ms")));

                // What one keystroke invalidates, and how its queued time splits between the layout pass and everything else.
                var reflected = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var layoutManager = typeof(TopLevel).GetProperty("LayoutManager", reflected)?.GetValue(window)
                                    ?? typeof(TopLevel).GetField("_layoutManager", reflected)?.GetValue(window);
                var executeLayoutPass = layoutManager?.GetType().GetMethod("ExecuteLayoutPass", reflected, Type.EmptyTypes);
                if (layoutManager != null && executeLayoutPass != null)
                {
                    var layoutTimes = new List<double>();
                    var restTimes = new List<double>();
                    var invalidated = new SortedDictionary<string, int>();
                    for (int i = 0; i < 10; i++)
                    {
                        editor.Document.Insert(editor.Document.TextLength - 2, "x");
                        foreach (var queueName in new[] { "_toMeasure", "_toArrange" })
                        {
                            if (layoutManager.GetType().GetField(queueName, reflected)?.GetValue(layoutManager) is System.Collections.IEnumerable queued)
                            {
                                foreach (var control in queued)
                                {
                                    var key = $"{queueName.TrimStart('_')}: {control.GetType().Name}#{(control as Control)?.Name}";
                                    invalidated[key] = invalidated.GetValueOrDefault(key) + 1;
                                }
                            }
                        }

                        var layoutClock = Stopwatch.StartNew();
                        executeLayoutPass.Invoke(layoutManager, null);
                        layoutTimes.Add(layoutClock.Elapsed.TotalMilliseconds);
                        var restClock = Stopwatch.StartNew();
                        Dispatcher.UIThread.RunJobs();
                        restTimes.Add(restClock.Elapsed.TotalMilliseconds);
                    }

                    Console.WriteLine($"  experiment: after a keystroke the layout pass takes {Median(layoutTimes):F1} ms, the remaining jobs {Median(restTimes):F1} ms");
                    Console.WriteLine("  experiment: queued for layout by 10 keystrokes: " + string.Join(", ", invalidated.Select(kv => $"{kv.Key} x{kv.Value}")));
                }

                double WithJobs() => Median(Repeat(10, () =>
                {
                    editor.Document.Insert(editor.Document.TextLength - 2, "x");
                    Dispatcher.UIThread.RunJobs();
                }));
                Console.WriteLine($"  experiment: keystroke + jobs, as is (sidebar {codeVm.SelectedActivityBarIndex}, visible {codeVm.IsSideBarVisible})  {WithJobs():F1} ms");
                codeVm.IsSideBarVisible = false;
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"  experiment: keystroke + jobs, sidebar hidden           {WithJobs():F1} ms");
                codeVm.IsSideBarVisible = true;
                foreach (var tool in new[] { 1, 2, 4, 0 })
                {
                    codeVm.SelectedActivityBarIndex = tool;
                    Dispatcher.UIThread.RunJobs();
                    Console.WriteLine($"  experiment: keystroke + jobs, sidebar tool {tool}            {WithJobs():F1} ms");
                }

                codeVm.IsBottomDeckExpanded = false;
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"  experiment: keystroke + jobs, deck collapsed           {WithJobs():F1} ms");

                // The same keystroke in a small file: is the cost about the big document at all?
                Pump(codeVm.SwitchToTabAsync(smallTab));
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"  experiment: keystroke + jobs, in a SMALL file          {Median(Repeat(15, () => { editor.Document.Insert(editor.Document.TextLength, "x"); Dispatcher.UIThread.RunJobs(); })):F1} ms");
                Console.WriteLine($"  experiment: keystroke alone, in a SMALL file           {Median(Repeat(15, () => editor.Document.Insert(editor.Document.TextLength, "x"))):F1} ms");

                // Typing INSIDE a method of an ordinary file (the enclosing foldings change length, and the editor redraws their whole range).
                foreach (var kb in new[] { 10, 40 })
                {
                    var ordinary = Pump(storage.CreateNewScriptAsync($"Ordinary {kb} KB"));
                    ordinary.Code = BigCode(kb);
                    Pump(storage.SaveScriptAsync(ordinary));
                    Pump(codeVm.UpdateActiveScriptAsync(ordinary));
                    Dispatcher.UIThread.RunJobs();
                    var ordinaryFoldings = (editor.TextArea.GetService(typeof(AvaloniaEdit.Folding.FoldingManager)) as AvaloniaEdit.Folding.FoldingManager)?.AllFoldings.Count();
                    var runs = Repeat(8, () => EditBelowTheScreen(editor, 18));
                    Console.WriteLine($"  experiment: an ordinary {kb} KB file ({ordinary.Code.Count(c => c == '\n')} lines, {ordinaryFoldings} foldings), one keystroke inside a method: {Median(runs):F1} ms (median of {runs.Count})");
                    var generatorOfOrdinary = editor.TextArea.TextView.ElementGenerators.OfType<AvaloniaEdit.Folding.FoldingElementGenerator>().FirstOrDefault();
                    if (generatorOfOrdinary != null)
                    {
                        editor.TextArea.TextView.ElementGenerators.Remove(generatorOfOrdinary);
                        var runsWithout = Repeat(8, () => EditBelowTheScreen(editor, 18));
                        Console.WriteLine($"  experiment: the same without the folding generator: {Median(runsWithout):F1} ms");
                        editor.TextArea.TextView.ElementGenerators.Insert(0, generatorOfOrdinary);
                    }
                }

                // Back to the big document: the editor is shared by the tabs, so the runs below would otherwise type into the small one.
                Pump(codeVm.SwitchToTabAsync(bigTab));
                Dispatcher.UIThread.RunJobs();
                double Keystrokes() => Median(Repeat(10, () =>
                {
                    editor.Document.Insert(editor.Document.TextLength - 2, "x");
                    Dispatcher.UIThread.RunJobs();
                }));
                Console.WriteLine($"  experiment: typing as is                       {Keystrokes():F1} ms");
                var highlighting = editor.SyntaxHighlighting;
                editor.SyntaxHighlighting = null;
                Console.WriteLine($"  experiment: without syntax highlighting        {Keystrokes():F1} ms");
                if (editor.TextArea.GetService(typeof(AvaloniaEdit.Folding.FoldingManager)) is AvaloniaEdit.Folding.FoldingManager folding)
                {
                    Console.WriteLine($"  experiment: foldings in the document: {folding.AllFoldings.Count()}");
                    var textView = editor.TextArea.TextView;
                    Console.WriteLine($"  experiment: first visible line {textView.VisualLines.FirstOrDefault()?.FirstDocumentLine.LineNumber}, caret on line {editor.TextArea.Caret.Line}, {textView.VisualLines.Count} visual lines");
                    foreach (var (where, offset) in new[] { ("at the end of the document", editor.Document.TextLength - 2), ("in the middle", editor.Document.TextLength / 2), ("just below the screen", editor.Document.GetLineByNumber(70).Offset + 2), ("in the last visible line", editor.Document.GetLineByNumber(47).Offset + 2), ("on the first line", 1) })
                    {
                        var linesBefore = textView.VisualLines.ToList();
                        var one = Stopwatch.StartNew();
                        editor.Document.Insert(offset, "x");
                        Dispatcher.UIThread.RunJobs();
                        var cost = one.Elapsed.TotalMilliseconds;
                        var linesAfter = textView.VisualLines.ToList();
                        Console.WriteLine($"  experiment: a keystroke {where} costs {cost:F1} ms and keeps {linesAfter.Count(l => linesBefore.Contains(l))} of {linesAfter.Count} visible lines");
                    }

                    var foldingStrategy = new CSharpFoldingStrategy();
                    Console.WriteLine($"  experiment: working out the foldings of the whole document takes {Median(Repeat(3, () => foldingStrategy.CreateNewFoldings(editor.Document, out _).Count())):F0} ms, applying them {Median(Repeat(3, () => foldingStrategy.UpdateFoldings(folding, editor.Document))) - Median(Repeat(3, () => foldingStrategy.CreateNewFoldings(editor.Document, out _).Count())):F0} ms more");
                    Console.WriteLine($"  experiment: GetNextFoldedFoldingStart(0) takes {Median(Repeat(20, () => folding.GetNextFoldedFoldingStart(0))):F3} ms, GetFoldingsContaining(middle) {Median(Repeat(20, () => folding.GetFoldingsContaining(editor.Document.TextLength / 2))):F3} ms, from the middle {Median(Repeat(20, () => folding.GetNextFoldedFoldingStart(editor.Document.TextLength / 2))):F3} ms");
                    Console.WriteLine($"  experiment: an edit below the screen, as is:      {EditBelowTheScreen(editor)}");
                    if (editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Folding.FoldingMargin>().FirstOrDefault() is { } foldingMargin)
                    {
                        editor.TextArea.LeftMargins.Remove(foldingMargin);
                        Console.WriteLine($"  experiment: + without the folding margin        {Keystrokes():F1} ms; below the screen {EditBelowTheScreen(editor)}");
                    }

                    if (editor.TextArea.TextView.ElementGenerators.OfType<AvaloniaEdit.Folding.FoldingElementGenerator>().FirstOrDefault() is { } foldingGenerator)
                    {
                        editor.TextArea.TextView.ElementGenerators.Remove(foldingGenerator);
                        Console.WriteLine($"  experiment: + without the folding generator     {Keystrokes():F1} ms; below the screen {EditBelowTheScreen(editor)}");
                    }

                    AvaloniaEdit.Folding.FoldingManager.Uninstall(folding);
                    Console.WriteLine($"  experiment: + manager uninstalled: below the screen {EditBelowTheScreen(editor)}");

                    // The same document in a bare editor: is this the studio's doing or the editor's own?
                    var bareEditor = new AvaloniaEdit.TextEditor { Document = new AvaloniaEdit.Document.TextDocument(big.Code) };
                    var bareWindow = new Window { Width = 1000, Height = 800, Content = bareEditor };
                    bareWindow.Show();
                    Dispatcher.UIThread.RunJobs();
                    Console.WriteLine($"  experiment: bare editor, no foldings:       {EditBelowTheScreen(bareEditor)}");
                    var bareFolding = AvaloniaEdit.Folding.FoldingManager.Install(bareEditor.TextArea);
                    new CSharpFoldingStrategy().UpdateFoldings(bareFolding, bareEditor.Document);
                    Dispatcher.UIThread.RunJobs();
                    Console.WriteLine($"  experiment: bare editor, {bareFolding.AllFoldings.Count()} foldings:  {EditBelowTheScreen(bareEditor)}");
                    if (bareEditor.TextArea.TextView.ElementGenerators.OfType<AvaloniaEdit.Folding.FoldingElementGenerator>().FirstOrDefault() is { } bareGenerator)
                    {
                        bareEditor.TextArea.TextView.ElementGenerators.Remove(bareGenerator);
                        Console.WriteLine($"  experiment: bare editor, generator removed:  {EditBelowTheScreen(bareEditor)}");
                    }

                    bareWindow.Close();
                    Console.WriteLine($"  experiment: + without foldings                 {Keystrokes():F1} ms");
                }

                editor.TextArea.TextView.LineTransformers.Clear();
                Console.WriteLine($"  experiment: + without line transformers        {Keystrokes():F1} ms ({editor.TextArea.TextView.BackgroundRenderers.Count} background renderers)");
                editor.TextArea.TextView.BackgroundRenderers.Clear();
                Console.WriteLine($"  experiment: + without background renderers     {Keystrokes():F1} ms");
                editor.SyntaxHighlighting = highlighting;

                var text = editor.Text;
                int unique = 0;
                Console.WriteLine($"  experiment: setting the view model's Code alone {Median(Repeat<string>(10, () => codeVm.Code = text + new string('y', ++unique))):F1} ms");
                Console.WriteLine($"  experiment: RefreshDocumentNuGetPackages alone  {Median(Repeat(10, () => codeVm.RefreshDocumentNuGetPackages())):F1} ms");
                Console.WriteLine($"  experiment: ScriptDocumentItem.Code set alone   {Median(Repeat<string>(10, () => codeVm.Script.Code = text + new string('z', ++unique))):F1} ms");
                var bare = new AvaloniaEdit.Document.TextDocument(text);
                Console.WriteLine($"  experiment: a bare TextDocument, no editor      {Median(Repeat(10, () => bare.Insert(bare.TextLength - 2, "x"))):F1} ms");
            }

            var textCopy = Repeat(10, () => editor.Text.Length);
            Console.WriteLine();
            Console.WriteLine($"  during those {typing.Count} keystrokes the garbage collector ran {GC.CollectionCount(0) - gen0Before} gen-0, {GC.CollectionCount(1) - gen1Before} gen-1 and {GC.CollectionCount(2) - gen2Before} gen-2 collections, pausing the program for {(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F0} ms in all");
            Console.WriteLine($"  running the queued UI jobs with nothing typed: {Median(Repeat(10, () => Dispatcher.UIThread.RunJobs())):F1} ms");
            Console.WriteLine($"  one rendered frame with nothing typed: {Median(Repeat(10, () => { using (window.CaptureRenderedFrame()) { } })):F1} ms");
            Console.WriteLine($"Typing into the big file: {Median(typing):F1} ms per keystroke on the UI thread (median of {typing.Count}); " +
                              $"of that the edit and its handlers {Median(insertOnly):F1} ms, queued UI jobs {Median(typing) - Median(insertOnly):F1} ms; " +
                              $"just reading editor.Text {Median(textCopy):F1} ms");
            Console.WriteLine($"Big file ({bigKb} KB, {big.Code!.Count(c => c == '\n')} lines): open {bigOpen.ToLayout:F0} ms (+frame {bigOpen.ToFrame:F0}), " +
                              $"switch to it {Median(toBig.Select(s => s.ToLayout)):F0} ms (+frame {Median(toBig.Select(s => s.ToFrame)):F0}), " +
                              $"switch away {Median(fromBig.Select(s => s.ToLayout)):F0} ms");
        }

        // What one tab switch is made of, measured one piece at a time.
        Console.WriteLine();
        Console.WriteLine("Breakdown (median of 5, ms):");
        Console.WriteLine($"  scan + read + parse the workspace (LoadWorkspaceSummariesAsync): {Median(Repeat(5, () => Pump(storage.LoadWorkspaceSummariesAsync()))),9:F1}");
        Console.WriteLine($"  list the folders (LoadFolderPathsAsync):                         {Median(Repeat(5, () => Pump(storage.LoadFolderPathsAsync()))),9:F1}");
        Console.WriteLine($"  RefreshExplorerAsync (scan + rebuild tree + layout):             {Median(Repeat(5, () => { Pump(codeVm.RefreshExplorerAsync()); Dispatcher.UIThread.RunJobs(); })),9:F1}");
        Console.WriteLine($"  RefreshExplorerAsync + one rendered frame:                       {Median(Repeat(5, () => { Pump(codeVm.RefreshExplorerAsync()); Dispatcher.UIThread.RunJobs(); using (window.CaptureRenderedFrame()) { } })),9:F1}");
        Console.WriteLine($"  one rendered frame, nothing changed:                             {Median(Repeat(5, () => { using (window.CaptureRenderedFrame()) { } })),9:F1}");

        // The Hub draws a capped number of cards; the button that reveals the rest must really be on screen.
        if (options.Flag("binding-census")) BindingCensus(window);

        // --hub-census: what the Hub draws, by control type, and what one frame of it costs with and without its shadows.
        if (options.Flag("hub-census"))
        {
            Navigate(window, () => host.NavigateToManager(), () => ReferenceEquals(host.CurrentPage, host.ManagerViewModel));
            Snapshot.WaitFor(() => !host.ManagerViewModel.IsRefreshingList, TimeSpan.FromMinutes(1));
            var hubView = (Control)view.Pages.ViewFor(host.ManagerViewModel)!;
            foreach (var (name, go, arrived) in new (string, Action, Func<bool>)[]
                     {
                         ("Hub", () => host.NavigateToManager(), () => ReferenceEquals(host.CurrentPage, host.ManagerViewModel)),
                         ("Docs", () => host.NavigateToDocs(), () => ReferenceEquals(host.CurrentPage, host.DocsViewModel)),
                     })
            {
                Navigate(window, () => host.NavigateToSettings(), () => ReferenceEquals(host.CurrentPage, host.SettingsViewModel));
                Navigate(window, go, arrived);
                var frames = Enumerable.Range(0, 6).Select(_ => { var c = Stopwatch.StartNew(); using (window.CaptureRenderedFrame()) { } return c.Elapsed.TotalMilliseconds; }).ToList();
                Console.WriteLine($"  frames after arriving on {name}: {string.Join(", ", frames.Select(f => f.ToString("F1")))} ms");
            }
            Navigate(window, () => host.NavigateToManager(), () => ReferenceEquals(host.CurrentPage, host.ManagerViewModel));
            var shown = hubView.GetVisualDescendants().Where(v => v.IsEffectivelyVisible).ToList();
            Console.WriteLine($"hub census: {shown.Count:N0} visible visuals, {shown.OfType<Avalonia.Controls.Shapes.Shape>().Count()} shapes, " +
                              $"{shown.OfType<Border>().Count(b => b.BoxShadow.Count > 0)} borders with a shadow, {shown.OfType<Material.Icons.Avalonia.MaterialIcon>().Count()} icons");
            foreach (var group in shown.GroupBy(v => v.GetType().Name).OrderByDescending(g => g.Count()).Take(12))
            {
                Console.WriteLine($"  {group.Key,-34} {group.Count(),6:N0}");
            }

            double FullFrame() => Median(Repeat(5, () => { hubView.InvalidateVisual(); foreach (var v in shown) v.InvalidateVisual(); using (window.CaptureRenderedFrame()) { } }));
            Console.WriteLine($"  hub frame, everything redrawn: {FullFrame():F1} ms");
            // What each part of the Hub costs to draw: the frame with that part hidden.
            var parts = shown.OfType<Control>()
                .Where(c => c.GetType().Namespace?.Contains(".Controls.Hub") == true || (c is Border b && b.Background is Avalonia.Media.IGradientBrush))
                .ToList();
            foreach (var part in parts)
            {
                part.IsVisible = false;
                Console.WriteLine($"  without {part.GetType().Name}{(part is Border ? " (gradient " + part.Bounds.Size + ")" : "")}: {FullFrame():F1} ms");
                part.IsVisible = true;
            }

            // What each piece costs to draw on its own (into a bitmap of its size, children included): the deepest
            // costly pieces are the ones to look at.
            var costs = new List<(Control Piece, double Ms)>();
            foreach (var piece in hubView.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is not TextBlock && c.Bounds.Width >= 8 && c.Bounds.Height >= 8))
            {
                var size = new Avalonia.PixelSize((int)Math.Ceiling(piece.Bounds.Width), (int)Math.Ceiling(piece.Bounds.Height));
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                costs.Add((piece, Median(Repeat(3, () => bitmap.Render(piece)))));
            }

            foreach (var (piece, ms) in costs.Where(c => c.Ms >= 2).OrderByDescending(c => c.Piece.GetVisualAncestors().Count()).ThenByDescending(c => c.Ms).Take(25))
            {
                Console.WriteLine($"    {ms,6:F1} ms  depth {piece.GetVisualAncestors().Count(),2}  {piece.GetType().Name} {piece.Name} [{string.Join('.', piece.Classes)}] {piece.Bounds.Size} in {piece.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.GetType().Namespace?.Contains(".Controls.Hub") == true)?.GetType().Name}");
            }

        }

        var hub = host.ManagerViewModel;
        var showMore = (view.Pages.ViewFor(hub) as Control)?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == hub.ShowMoreItemsCommand);
        // Its own flag (bound to HasHiddenItems): the Hub itself is hidden by now, another page is showing.
        Console.WriteLine($"Hub list: {hub.FilteredItems.Count} of {hub.FilteredItemCount} drawn; \"Show more\" button {(showMore == null ? "MISSING" : showMore.IsVisible ? "shown" : "hidden")}");

        var pageHost = view.GetType().GetProperty("Pages")?.GetValue(view);
        if (pageHost?.GetType().GetProperty("BuiltViewCount")?.GetValue(pageHost) is int built)
        {
            Console.WriteLine($"Page views built over the whole run: {built} (one per page)");
        }

        if (options.Flag("theme-switch")) PerfLoading.ThemeSwitch(window, host, probe, options.Flag("theme-shots"));

        if (options.Value("big-csv-mb") is { } csvMb && double.TryParse(csvMb, System.Globalization.CultureInfo.InvariantCulture, out var csvMegabytes) && csvMegabytes > 0)
        {
            Navigate(window, () => host.NavigateToCodeStudio(scripts[0]), () => ReferenceEquals(host.CurrentPage, host.CodeStudioViewModel));
            PerfLoading.BigCsv(window, view, codeVm, storage, probe, csvMegabytes, options.Flag("csv-experiments"));
        }

        if (options.Value("big-image-mp") is { } imageMp && double.TryParse(imageMp, System.Globalization.CultureInfo.InvariantCulture, out var megapixels) && megapixels > 0)
        {
            Navigate(window, () => host.NavigateToCodeStudio(scripts[0]), () => ReferenceEquals(host.CurrentPage, host.CodeStudioViewModel));
            PerfLoading.BigImage(window, codeVm, storage, probe, megapixels);
        }

        if (options.Int("cells", 0) is > 0 and var cellCount) LongNotebook(window, host, storage, cellCount, options.Flag("alloc-types"));
        if (options.Int("external", 0) is > 0 and var externalFiles) ExternalWorkspace(window, host, storage, externalFiles);

        window.Close();
        if (options.Flag("visuals")) VisualPerf.Run();
    }

    // --cells n: one notebook of n code cells, opened in the Notebook Studio: what a long notebook costs to show.
    private static void LongNotebook(Window window, CSharpStudioHostViewModel host, LocalScriptStorageService storage, int cells, bool allocTypes)
    {
        var notebook = Pump(storage.CreateNewNotebookAsync("Long notebook"));
        notebook.Cells.Clear();
        for (int i = 0; i < cells; i++)
        {
            notebook.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Code,
                Source = $"var value{i} = {i} * 2;\nConsole.WriteLine(value{i});",
                OutputText = $"{i * 2}",
                ExecutionCount = i + 1
            });
        }

        Pump(storage.SaveNotebookAsync(notebook));
        long before = SettledMemory();
        var opened = Navigate(window, () => host.NavigateToNotebookStudio(notebook), () => ReferenceEquals(host.CurrentPage, host.NotebookStudioViewModel) && host.NotebookStudioViewModel!.ActiveTab?.Notebook.Id == notebook.Id);
        long after = SettledMemory();
        var away = Navigate(window, () => host.NavigateToDocs(), () => ReferenceEquals(host.CurrentPage, host.DocsViewModel));
        var back = Navigate(window, () => host.NavigateToNotebookStudio(notebook), () => ReferenceEquals(host.CurrentPage, host.NotebookStudioViewModel));

        Console.WriteLine();
        Console.WriteLine($"Long notebook ({cells} cells): open {opened.ToLayout:F0} ms (+frame {opened.ToFrame:F0}), memory +{(after - before) / 1048576.0:F0} MB, " +
                          $"back to it {back.ToLayout:F0} ms (+frame {back.ToFrame:F0}); {host.NotebookStudioViewModel!.ActiveTab?.Cells.Count} cell view models");
        _ = away;

        // Scrolling: the canvas builds cells as they come into view, so a page at a time should stay quick, and the cells
        // that appear must show the right text.
        if (window.Content is CSharpStudioHostView hostView && hostView.Pages.ViewFor(host.NotebookStudioViewModel!) is Control notebookView)
        {
            Navigate(window, () => host.NavigateToNotebookStudio(notebook), () => ReferenceEquals(host.CurrentPage, host.NotebookStudioViewModel));
            var scroller = notebookView.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "NotebookCanvasScrollViewer");

            int Realized() => notebookView.GetVisualDescendants().OfType<BindableTextEditor>().Count();
            var steps = new List<double>();
            var gcBefore = (Gen2: GC.CollectionCount(2), Pause: GC.GetTotalPauseDuration(), Bytes: GC.GetAllocatedBytesForCurrentThread());
            // Cell views built over the scroll: a recycled cell keeps its view, so this counts the cells built from scratch.
            var cellViews = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void NoteCellViews() { foreach (var v in notebookView.GetVisualDescendants().OfType<NotebookCellOutputControl>()) cellViews.Add(v); }
            NoteCellViews();
            int cellViewsBefore = cellViews.Count;
            void PageDown()
            {
                for (int i = 0; i < 25; i++)
                {
                    steps.Add(Timed(window, () => { scroller.PageDown(); Dispatcher.UIThread.RunJobs(); }).ToFrame);
                    NoteCellViews();
                }
            }

            // --alloc-types: which types the cells that come into view allocate, and the collections they run into.
            using (var sampler = allocTypes ? new AllocationSampler() : null)
            {
                if (sampler != null) sampler.Report("while scrolling the notebook", PageDown);
                else PageDown();
            }

            Console.WriteLine($"  scrolling a page at a time: {Median(steps):F0} ms per page (slowest {steps.Max():F0} ms); code editors alive: {Realized()} of {cells}; " +
                              $"{GC.CollectionCount(2) - gcBefore.Gen2} full collections pausing {(GC.GetTotalPauseDuration() - gcBefore.Pause).TotalMilliseconds:F0} ms, {(GC.GetAllocatedBytesForCurrentThread() - gcBefore.Bytes) / 1048576.0 / steps.Count:F1} MB allocated per page; " +
                              $"{(cellViews.Count - cellViewsBefore) / (double)steps.Count:F1} cell views built per page, {notebookView.GetVisualDescendants().OfType<NotebookCellOutputControl>().Count()} in the tree");
            // The floating toolbar of a cell is built the first time the cell is selected or hovered (LazyContent): what that costs.
            var plainCells = notebookView.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Where(c => c.IsEffectivelyVisible && c.DataContext is PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.NotebookCellViewModel { IsSelected: false })
                .Select(c => (PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.NotebookCellViewModel)c.DataContext!).Take(3).ToList();
            if (plainCells.Count > 0)
            {
                int Visuals() => notebookView.GetVisualDescendants().Count();
                int visualsBefore = Visuals();
                var firstSelect = plainCells.Select(cell => Timed(window, () => cell.IsSelected = true).ToFrame).ToList();
                Console.WriteLine($"  ({Visuals() - visualsBefore} visuals built for {plainCells.Count} toolbars)");
                foreach (var cell in plainCells) cell.IsSelected = false;
                Console.WriteLine($"  selecting a cell for the first time (builds its toolbar): {Median(firstSelect):F1} ms with the frame");
            }

            var oneCell = notebookView.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault(c => c.DataContext is PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.NotebookCellViewModel && c.GetVisualParent() is VirtualizingStackPanel);
            if (oneCell != null)
            {
                var all = oneCell.GetVisualDescendants().ToList();
                var logical = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(oneCell).ToList();
                Console.WriteLine($"  one cell: {all.Count} visuals ({all.Count(v => v.IsEffectivelyVisible)} visible), {logical.Count} logical; most common: " +
                                  string.Join(", ", all.GroupBy(v => v.GetType().Name).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()}")));
                // How many styles each control of the cell carries (Avalonia keeps one frame per style whose selector could
                // match the control's type, active or not, plus control themes): what makes a control heavy to build.
                var valuesField = typeof(Avalonia.AvaloniaObject).GetField("_values", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var framesField = valuesField?.FieldType.GetField("_frames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (framesField != null)
                {
                    int Frames(Avalonia.Visual v) => (framesField.GetValue(valuesField!.GetValue(v)) as System.Collections.ICollection)?.Count ?? 0;
                    Console.WriteLine($"    style frames per control: {all.Average(Frames):F1} on average; by type: " +
                                      string.Join(", ", all.GroupBy(v => v.GetType().Name).OrderByDescending(g => g.Average(Frames)).Take(8).Select(g => $"{g.Key} {g.Average(Frames):F0}")));
                    if (all.OfType<Button>().FirstOrDefault() is { } button)
                    {
                        var frames = (framesField.GetValue(valuesField!.GetValue(button)) as System.Collections.IEnumerable)!.Cast<object>().ToList();
                        var selectors = frames.Select(f => (f.GetType().GetProperty("Source", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(f) as Avalonia.Styling.Style)?.Selector?.ToString()).Where(x => x != null).ToList();
                        Console.WriteLine($"    a cell Button carries {frames.Count} style frames ({selectors.Distinct().Count()} distinct styles; a style there twice means the studio's styles are included twice)");
                    }
                }
                var output = all.OfType<NotebookCellOutputControl>().FirstOrDefault();
                if (output != null)
                {
                    var inOutput = output.GetVisualDescendants().ToList();
                    Console.WriteLine($"    of which the output control: {inOutput.Count} visuals ({inOutput.Count(v => v.IsEffectivelyVisible)} visible); " +
                                      string.Join(", ", inOutput.GroupBy(v => v.GetType().Name).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()}")));
                }
            }

            scroller.ScrollToHome();
            Dispatcher.UIThread.RunJobs();
            var wheel = new List<double>();
            for (int i = 0; i < 60; i++)
            {
                wheel.Add(Timed(window, () => { scroller.Offset = new Avalonia.Vector(0, scroller.Offset.Y + 120); Dispatcher.UIThread.RunJobs(); }).ToFrame);
            }

            Console.WriteLine($"  a mouse-wheel notch (120 px) at a time: {Median(wheel):F0} ms median, {wheel.Max():F0} ms slowest, {wheel.Count(w => w > 100)} of {wheel.Count} over 100 ms");

            scroller.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            var lastRealized = notebookView.GetVisualDescendants().OfType<BindableTextEditor>()
                .Select(e => e.DataContext as NotebookCellViewModel).Where(c => c != null)
                .Select(c => host.NotebookStudioViewModel!.Cells.IndexOf(c!)).DefaultIfEmpty(-1).Max();
            Console.WriteLine($"  after Ctrl+End the bottom-most built cell is #{lastRealized} of {cells - 1}");
        }

        // The side panels that list every cell: what opening them costs on a long notebook.
        var notebookVm = host.NotebookStudioViewModel!;
        var outline = Timed(window, () => { notebookVm.SelectedActivityBarIndex = 1; Dispatcher.UIThread.RunJobs(); });
        var search = Timed(window, () => { notebookVm.SelectedActivityBarIndex = 3; Dispatcher.UIThread.RunJobs(); });
        notebookVm.SelectedActivityBarIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"  opening the Outline panel: {outline.ToFrame:F0} ms; the Search panel: {search.ToFrame:F0} ms ({cells} cells)");
        // Each TextMate model blocks a thread-pool thread: this must follow the editors on screen, not every editor ever built.
        Console.WriteLine($"  TextMate models alive: {PdfEditorApp.Plugins.CSharpEditor.Controls.Editor.SyntaxColoring.LiveInstallations}");

        // What one cell is made of: the two heaviest parts built on their own, 40 of each.
        Console.WriteLine($"  one code editor:        {Cost(40, () => new BindableTextEditor())}");
        Console.WriteLine($"  one cell output control: {Cost(40, () => new NotebookCellOutputControl())}");
        Console.WriteLine($"  TextMate models alive after building them off screen: {PdfEditorApp.Plugins.CSharpEditor.Controls.Editor.SyntaxColoring.LiveInstallations}");
    }

    // --external n: open a folder of n source files (25 per folder), the way a big project is opened, and see what the
    // Explorer and the Hub make of it.
    private static void ExternalWorkspace(Window window, CSharpStudioHostViewModel host, LocalScriptStorageService storage, int files)
    {
        var root = Snapshot.TempFolder("perf-external");
        int folders = files / 25 + 1;
        for (int f = 0; f < folders; f++)
        {
            var folder = Path.Combine(root, "src", $"module{f:D4}");
            Directory.CreateDirectory(folder);
            for (int i = 0; i < 25 && f * 25 + i < files; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"file{i:D2}.py"), $"print({f * 25 + i})\n");
            }
        }

        long before = SettledMemory();
        var clock = Stopwatch.StartNew();
        var result = Pump(storage.OpenExternalProjectAsync(root));
        double openMs = clock.Elapsed.TotalMilliseconds;

        var codeVm = host.CodeStudioViewModel!;
        var explorer = Timed(window, () => Pump(codeVm.RefreshExplorerAsync()));
        int inExplorer = CountFiles(codeVm.ExplorerRootItems);
        bool listedOnDemand = codeVm.ExplorerRootItems.Any(i => i.IsDirectory && !i.ChildrenLoaded);
        var hubReload = Timed(window, () => Pump(host.ManagerViewModel.LoadWorkspaceItemsAsync()));
        long after = SettledMemory();

        Console.WriteLine();
        Console.WriteLine($"External folder ({files} files in {folders} folders): open {openMs:F0} ms ({result.Message}); " +
                          $"Explorer refresh {explorer.ToLayout:F0} ms showing " +
                          (listedOnDemand ? $"{codeVm.ExplorerRows.Rows.Count} rows at the top (folders are listed as they are opened)" : $"{inExplorer:N0} of {files:N0} files") +
                          $"; Hub reload {hubReload.ToLayout:F0} ms; memory +{(after - before) / 1048576.0:F0} MB");

        if (listedOnDemand)
        {
            // A big workspace lists one folder at a time: what opening a folder costs, whatever the size of the project.
            var top = codeVm.ExplorerRootItems.First(i => i.IsDirectory);
            var openTop = Timed(window, () =>
            {
                top.IsExpanded = true;
                Snapshot.WaitFor(() => top.ChildrenLoaded, TimeSpan.FromSeconds(60));
            });
            var module = top.Children.First(c => c.IsDirectory);
            var openModule = Timed(window, () =>
            {
                module.IsExpanded = true;
                Snapshot.WaitFor(() => module.ChildrenLoaded, TimeSpan.FromSeconds(60));
            });
            var other = top.Children.Where(c => c.IsDirectory).Skip(1).First();
            var listClock = Stopwatch.StartNew();
            Pump(storage.ListFolderAsync(other.FullPath));
            double listMs = listClock.Elapsed.TotalMilliseconds;
            var listTopClock = Stopwatch.StartNew();
            Pump(storage.ListFolderAsync(top.FullPath));
            double listTopMs = listTopClock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"  opening '{top.Name}' ({top.Children.Count:N0} folders): {openTop.ToLayout:F0} ms; opening a folder of {module.Children.Count} files: {openModule.ToLayout:F0} ms " +
                              $"(reading a folder from disk alone: {listMs:F1} ms for {module.Children.Count} files, {listTopMs:F0} ms for {top.Children.Count:N0} folders)");
        }

        // The workspace file index behind Go to File: the background walk, then what a search costs.
        var index = storage.FileIndex;
        long beforeIndex = SettledMemory();
        var indexClock = Stopwatch.StartNew();
        Pump(index.RebuildAsync(storage.ActiveWorkspaceRootPath));
        double indexMs = indexClock.Elapsed.TotalMilliseconds;
        long afterIndex = SettledMemory();
        var queries = new[] { "file0042", "module0007", "f1", "py", "zzzz", "m0007/file" };
        var searchMs = queries.Select(q => Median(Repeat(15, () => index.Find(q, 30)))).ToList();
        Console.WriteLine($"  file index: {index.Count:N0} files indexed in {indexMs:F0} ms (in the background), {(afterIndex - beforeIndex) / 1048576.0:F1} MB; " +
                          $"a Go to File search takes {searchMs.Max():F1} ms at worst, {Median(searchMs):F1} ms typically" +
                          (index.IsTruncated ? " (workspace bigger than the index limit)" : string.Empty));

        // Find in Files: every file read on background threads. A word that is nowhere reads them all; a common one stops at the match limit.
        var paths = index.Paths;
        foreach (var (label, query) in new[] { ("a word in no file", "zzzz"), ("a word in every file", "print") })
        {
            var matcher = new TextMatcher(query, matchCase: false, wholeWord: false, useRegex: false);
            var searchClock = Stopwatch.StartNew();
            var summary = Pump(WorkspaceTextSearch.RunAsync(paths, storage.ActiveWorkspaceRootPath, matcher, null, _ => { }, CancellationToken.None));
            Console.WriteLine($"  find in files ({label}): {summary.FilesSearched:N0} files read, {summary.Matches:N0} matches in {searchClock.Elapsed.TotalMilliseconds:F0} ms" +
                              (summary.HitLimit ? " (stopped at the limit)" : string.Empty));
        }

        // Through the panel: the pause before it starts, the search, and the list filling up on the UI thread.
        codeVm.SearchAllFiles = true;
        foreach (var query in new[] { "print", "zzzz" })
        {
            var panelClock = Stopwatch.StartNew();
            codeVm.SearchQuery = query;
            Snapshot.WaitFor(() => !codeVm.IsSearching, TimeSpan.FromSeconds(120));
            Console.WriteLine($"  find in files through the Search panel for \"{query}\": {panelClock.Elapsed.TotalMilliseconds:F0} ms until done (incl. the 250 ms pause), {codeVm.SearchMatches.Count:N0} rows: {codeVm.SearchStatusText}");
        }

        codeVm.SearchQuery = string.Empty;
        codeVm.SearchAllFiles = false;
    }

    // Time and memory for building one control, averaged over `count` of them (kept alive while measuring).
    private static string Cost(int count, Func<object> build)
    {
        build(); // Warm-up: the first one pays for loading the XAML.
        long before = SettledMemory();
        var clock = Stopwatch.StartNew();
        var kept = new List<object>();
        for (int i = 0; i < count; i++) kept.Add(build());
        double ms = clock.Elapsed.TotalMilliseconds / count;
        long after = SettledMemory();
        GC.KeepAlive(kept);
        return $"{ms:F1} ms and {(after - before) / 1048576.0 / count:F2} MB each";
    }

    private static int CountFiles(IEnumerable<ExplorerItemViewModel> items) =>
        items.Sum(i => i.IsDirectory ? CountFiles(i.Children) : 1);

    /// <summary>One character typed just below the visible lines: how long the editor takes to settle and how many visible lines it keeps.</summary>
    private static string EditBelowTheScreen(AvaloniaEdit.TextEditor editor, int line = 70)
    {
        var textView = editor.TextArea.TextView;
        Dispatcher.UIThread.RunJobs();
        var linesBefore = textView.VisualLinesValid ? textView.VisualLines.ToList() : new List<AvaloniaEdit.Rendering.VisualLine>();
        var clock = Stopwatch.StartNew();
        editor.Document.Insert(editor.Document.GetLineByNumber(line).Offset + 2, "x");
        Dispatcher.UIThread.RunJobs();
        var cost = clock.Elapsed.TotalMilliseconds;
        var linesAfter = textView.VisualLinesValid ? textView.VisualLines.ToList() : new List<AvaloniaEdit.Rendering.VisualLine>();
        return $"{cost:F1} ms, keeps {linesAfter.Count(l => linesBefore.Contains(l))} of {linesAfter.Count} lines";
    }

    // One page switch: the call, the wait for the page to become current, the layout it triggers, then one rendered frame.
    private static Sample Navigate(Window window, Action go, Func<bool> arrived)
    {
        var clock = Stopwatch.StartNew();
        go();
        var limit = Stopwatch.StartNew();
        while (!arrived())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
            if (limit.Elapsed > TimeSpan.FromMinutes(1)) throw new TimeoutException("The page never became current.");
        }

        Dispatcher.UIThread.RunJobs();
        double toLayout = clock.Elapsed.TotalMilliseconds;
        using (window.CaptureRenderedFrame()) { }
        return new Sample(toLayout, clock.Elapsed.TotalMilliseconds);
    }

    private static Sample Timed(Window window, Action action)
    {
        var clock = Stopwatch.StartNew();
        action();
        Dispatcher.UIThread.RunJobs();
        double toLayout = clock.Elapsed.TotalMilliseconds;
        using (window.CaptureRenderedFrame()) { }
        return new Sample(toLayout, clock.Elapsed.TotalMilliseconds);
    }

    // Like Snapshot.Wait, but without its 5 ms sleep, so short operations are not rounded up to it.
    private static void Pump(Task task)
    {
        var limit = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
            if (limit.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("A task never finished.");
        }

        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.Result;
    }

    // Valid C# of about the requested size: a class of small methods, so highlighting and folding have real work to do.
    private static string BigCode(int kilobytes)
    {
        var code = new System.Text.StringBuilder("public class Generated\n{\n");
        for (int i = 0; code.Length < kilobytes * 1024; i++)
        {
            code.Append("    public int Method").Append(i).Append("(int value)\n    {\n        var doubled = value * 2;\n        return doubled + ").Append(i).Append(";\n    }\n\n");
        }

        return code.Append("}\n").ToString();
    }

    internal static long SettledMemory()
    {
        Snapshot.Settle(3);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static List<double> Repeat(int times, Action action)
    {
        var results = new List<double>();
        for (int i = 0; i < times; i++)
        {
            var clock = Stopwatch.StartNew();
            action();
            results.Add(clock.Elapsed.TotalMilliseconds);
        }

        return results;
    }

    private static List<double> Repeat<T>(int times, Func<T> action) => Repeat(times, () => { action(); });

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    // --binding-census: how many resource subscriptions (one per dynamic resource) each kind of control holds, every
    // page built. Compare two builds to see where bindings come from.
    private static void BindingCensus(Window window)
    {
        var field = typeof(Avalonia.StyledElement).GetField("ResourcesChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? typeof(Avalonia.StyledElement).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .FirstOrDefault(f => f.Name.Contains("resourcesChanged", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("ResourcesChanged"));
        if (field == null)
        {
            Console.WriteLine("binding census: ResourcesChanged backing field not found; fields: " +
                              string.Join(", ", typeof(Avalonia.StyledElement).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Select(f => f.Name)));
            return;
        }

        var byType = new Dictionary<string, (int Controls, int Subscribers)>();
        int controls = 0, subscribers = 0;
        foreach (var visual in window.GetVisualDescendants().Prepend(window))
        {
            if (visual is not Avalonia.StyledElement element) continue;
            controls++;
            var count = (field.GetValue(element) as Delegate)?.GetInvocationList().Length ?? 0;
            subscribers += count;
            var name = element.GetType().Name;
            var (c, n) = byType.GetValueOrDefault(name);
            byType[name] = (c + 1, n + count);
        }

        Console.WriteLine($"binding census: {controls:N0} controls, {subscribers:N0} resource subscriptions");
        foreach (var (name, (c, n)) in byType.OrderByDescending(kv => kv.Value.Subscribers).Take(15))
        {
            Console.WriteLine($"  {name,-34} {c,7:N0} controls {n,8:N0} subscriptions ({(double)n / c:F1} each)");
        }
    }
}
