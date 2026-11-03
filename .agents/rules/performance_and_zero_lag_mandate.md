# Performance & Scale Mandate: zero-lag pages, tabs and files, at any workspace size

This rule applies strictly to `examples/CSharpEditorPlugin` (`com.frypdf.plugin.csharpeditor`). It is the plugin's counterpart of FryPDF's `performance_and_zero_lag_mandate.md`, and it exists because this studio is used on **very large codebases**: anything that is merely slow on a small workspace is unusable on a big one. All AI agents and developers must follow it.

---

## 1. Pages are built once and kept alive
- The studio host shows its pages through `Controls/KeepAlivePageHost`: a page's view is built the first time it is shown and only shown or hidden afterwards. **Never** show a page with a `ContentControl` + `DataTemplate` (that rebuilds the whole view, its styles and its editors on every switch, and the views it drops stay subscribed to the long-lived view models).
- A new page is registered in `CSharpStudioHostView`'s constructor. A page view model that owns timers, polling or animations implements `ViewModels/IPageLifecycle` and starts them in `OnActivated` and stops them in `OnDeactivated`. Hidden pages stay in the visual tree, so animations pause on `IsEffectivelyVisible`, not on detach.

## 2. Never block the UI thread
- No `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` on the UI thread; no file, process or network access there either. Heavy work goes through `Task.Run` with a `CancellationToken`.

## 3. Never scan the workspace on a hot path
- Switching a tab, switching a page, opening a file or typing must **not** walk the workspace or read its documents. Consumers remember `IScriptStorageService.StructureVersion` (tree shape) or `ContentVersion` (titles, modified times) and reload only when it moved (`RefreshExplorerIfStaleAsync`, `ReloadIfStaleAsync`), usually from `IPageLifecycle.OnActivated`.
- Every operation that changes the workspace bumps the versions (`LocalScriptStorageService.MarkChanged`); files changed by other programs are noticed by `WorkspaceWatcher` (`ExternalChangeDetected`). A new mutating storage method must call `MarkChanged`.
- Tests can prove it: `LocalScriptStorageService.WorkspaceScanCount` must not move on a tab switch (`WorkspaceStalenessTests`).

## 4. Virtualize every list that can grow
- A list that can exceed ~100 rows is virtualized (`VirtualizingStackPanel` as the items panel, with the `ScrollViewer` in the `ItemsControl` template, or a `ListBox`). Never nest `ItemsControl`s to draw a tree and never put an `ItemsControl` inside a `ScrollViewer` for a growing collection.
- The Explorer binds to `ExplorerRowList.Rows` (a flat list of the visible rows). Change many items through `Suspend()` and `RangeObservableCollection` (one notification), never with thousands of `Add` calls.
- Virtualized today: the Explorer, the notebook canvas (`NotebookCanvasRows`: a header row, a row per cell, a footer row), the notebook Outline and Search panels, and the Blind 75 table. `CanvasVirtualizationGuardTests` fails if the canvas or the Explorer loses its `VirtualizingStackPanel` (the canvas once did, and a 1,000-cell notebook took 3.5 minutes to open). A virtualizing panel treats a vertical margin on its presenter as part of the area it must fill, so keep vertical gaps out of the presenter (use header and footer rows) or the strip they cover stays empty while scrolling.
- Known debt: the Hub's workspace list (`HubWorkspacesPanelControl`) is not virtualized; it draws at most 100 cards and offers "Show more".

## 4b. Build the heavy parts of a repeated item on demand
- XAML builds everything it declares, and `IsVisible` does not stop that. A notebook cell used to build a table, a chart, a 3D plot, an inspector and an HTML view even when it only printed text. Put such a part in the template of `Controls/LazyContent` and bind its `IsActive` to "this kind of output is showing": it is built the first time it is needed.
- The same for parts that only show on hover, on selection or in a rare state: a cell's floating toolbar (`IsActive` = selected or `#CellCard.IsPointerOver`), the insert buttons between cells, the input prompt, the collapsed banners, the output kind tabs and the error hints are all `LazyContent`. That took a cell from 281 to 194 visuals and a page down in a 1,000-cell notebook from 126 MB to 59 MB of garbage. Opacity 0 is not "not built".
- Recycling does not save a cell's build: Avalonia 12's `VirtualizingStackPanel` takes a recycled container out of the tree, and a `ContentPresenter` that joins the tree again builds its template from scratch (and every control is styled again). Make the cell cheap to build instead.
- Hunt the cost with an experiment, not a guess: remove sections of a template one at a time and time the result (the output control was ~80% of a cell's cost; the menus everyone suspected were ~10%).

## 4c. Find files through the index, never by walking
- `IScriptStorageService.FileIndex` (`WorkspaceFileIndex`) holds the path of every file in the workspace: built by a cancellable background walk, searchable while it is still building, rebuilt when the workspace changes. "Go to File" (Ctrl+P) and any future search over files must take their file list from it, never walk the folder themselves, and never on the UI thread.
- The Hub's list (`LoadWorkspaceSummariesAsync`) stops at `WorkspaceFileLimit` files and says so (`IsWorkspaceTruncated`): a limit must never be silent.
- **The Explorer never stops at a limit: it lists a big workspace one folder at a time.** `IScriptStorageService.LoadExplorerListingAsync` lists the whole workspace when it fits `WorkspaceFileLimit`; otherwise it returns only the top folder (`WorkspaceListing.IsPartial`) and `ListFolderAsync(relativeFolder)` lists each folder when it is opened. `LazyExplorerTree` (shared by both studios) draws that: an unlisted folder holds a "Loading..." placeholder row, opening it lists it (`ExplorerItemViewModel.LoadChildrenRequested`), `RevealAsync` opens the folders down to the open document, and a rebuild opens the folders that were open again. Rows of a folder that is opened are patched in with `ExplorerRowList.ChangeChildren`, never by rebuilding the whole list. Code that edits the tree (new file or folder under a folder) must list an unlisted folder first, or the listing that follows adds the new item a second time.
- **The Explorer shows the folder as VS Code does.** Every file and folder of the workspace folder, by its own file name (`WorkspaceItemSummary.ExplorerName`), except VS Code's default exclusions (`.git`, `.svn`, `.hg`, `CVS`, `.DS_Store`, `Thumbs.db`: `WorkspaceWalker.IsHiddenInExplorer`/`IsExplorerFile`). Nothing from outside the folder: an open document that isn't one of its files gets no row. The folders the walk doesn't go into for speed (node_modules, bin, obj, .venv, `__pycache__`, links) are still listed, closed (`WorkspaceListing.ClosedFolders`, `LazyExplorerTree.MakeUnlisted`), and listed when opened; the file index, search and watcher keep skipping their contents. Renaming in an Explorer renames the file (`RenameDocumentFileAsync`); Duplicate gives a copied document its own id (`DuplicateFileAsync`). The Server Studio's Explorer is `StudioWorkspaceExplorer`.
- **Find in Files** (`WorkspaceTextSearch`, the Search panel's "search the whole workspace" toggle): reads `FileIndex.Paths` on background threads, several files at once, streams matches in file-list order, stops at `MaxMatches` (2,000) and `MaxMatchesPerFile`, skips binary and huge files, searches open documents as the editor holds them, and is cancelled by the next keystroke. Results reach the (virtualized) list in batches. Any other search over the workspace follows these rules.

## 5. Per-keystroke and per-switch work is independent of document size
- Do not copy or scan the whole document on the UI thread per keystroke or per tab switch: no `Document.Text` to compare (use a chunked compare), no whole-text regex (see `DirectiveScanInlineLimit`), at most one folding pass, and documents above `LargeDocumentLength` fold shortly after they are shown.
- Any new feature attached to text changes must be measured on the 2 MB case below.
- **Large documents give up what grows with size.** Above `CSharpCodeStudioViewModel.LargeDocumentLength` (500,000 chars) live diagnostics are off (a Roslyn compile of the whole file after every pause in typing made the garbage collector stall each keystroke; running the file still reports its problems), and foldings are capped at `FoldingLimits.MaxFoldings` (2,000, deepest blocks dropped first). AvaloniaEdit walks every folding for each visual line it builds and redraws a folding's whole range on every edit inside it (measured on a bare editor too): 18,000 foldings cost about 12 ms per keystroke, none cost 1 ms. Even an ordinary file pays about 3 ms per keystroke for its outermost folding; a replacement folding generator would be the next step.

## 6. Clean up what you hook up
- Every `+=` on a longer-lived object needs its `-=`. Views that hook a view model implement `IDisposable` (`CSharpCodeStudioView.Dispose`), and `KeepAlivePageHost.Dispose` calls it.
- Timers get an explicit `DispatcherPriority` (never `Render` for decoration) and stop while their page is hidden.
- **Never block thread-pool threads for the life of a control.** A TextMate model (TextMateSharp 2.0.4) blocks one pool thread for its tokenizer, so `SyntaxColoring` installs it only while its editor is attached to the visual tree and gives it back on detach. Before that, every editor ever built kept one (cells scrolled past, editors built off screen), the pool starved, and every await in the studio queued behind its slow growth: a folder took 80 s to open after scrolling a long notebook. `perf --cells` prints "TextMate models alive", which must follow the editors on screen. The studio host also raises the pool's minimum to 64 as a backstop.

## 6b. Loading and progress: report work, never block for it
- Work the user may wait for is reported to `Services/Activities/IActivityService` (`using var a = activities.Start(new ActivityOptions("Opening data.csv", ActivityLocation.Editor))`, or `RunAsync`). View models get the service through an optional constructor parameter (default `NullActivityService.Instance`); the studio host owns the one `ActivityService` and its `ActivityPresenterViewModel`. No view model owns loading state of its own, and nothing is static.
- **Producers say what, presenters decide how.** The rules live in `ActivityTiming`: nothing shows before 250 ms (a tab switch never shows anything), a thin line in the zone doing the work (`ActivityProgressLine`, `Location="Editor|Notebook|Explorer|Hub|Window"`) and the status-bar entry (`ActivityStatusItem`) after that, a toast with Cancel after 2 s when the work can be cancelled or reports progress, and once shown it stays 400 ms so it never flickers. Failures become error toasts (`IActivity.Fail`, `RunAsync`, `FireAndForget`), never only `Debug.WriteLine`.
- **The branded card (`StudioLoadingOverlayControl`) is for blocking work only**: `ActivityOptions.Blocking` (switching workspace, the first workspace list at cold start, which gives way after `YieldAfter`). It appears after 400 ms. Never use it for opening a file or a tab.
- **Never wait to make something visible**: no `Task.Delay` before or after work "so the spinner shows", no `await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Render)` to paint a loading state. Work that blocks the UI thread cannot show progress anyway; move it off the thread.
- **Latest wins**: opening document B while A is loading cancels A (`LatestOperation`); after every await, check the token before touching the screen. Two loads of the same document at once share one read (`SingleFlight`). Fire-and-forget goes through `FireAndForget(activities, "what")`, never `_ = ...` or `async void`.
- **Start-up is staged** (`Services/Startup/EngineReadiness`): Core (compiler service and studio view models) is all an open waits for; Warm (the first compilation) runs after it as a status-bar activity. A failed Core is started again by the next open.
- **Artifacts never decode or parse on the UI thread, and never in full when it is not shown**: images through `Services/Display/ImageDecoder` (header-only sizes, background decode, at most 4096 px wide, nothing over 256 MB), notebook image outputs decoded the first time they are shown, CSV previews parsed off the thread above 64 KB, capped at 100,000 rows and debounced while the text changes, tables virtualized (`DumpTableRowsList`), and `DumpTableResult` rows added in bulk (`AddRows`) or appended without re-filtering. Source files are sized and sniffed (8 KB) before reading; above `MaxEditorFileBytes` (50 MB) or binary, the editor gets a description that is never saved over the file.
- A code-built `ItemsControl` template must hand `ItemsPresenter.ItemsPanel` the `VirtualizingStackPanel` itself: the presenter otherwise makes a plain `StackPanel` and every item is built (the theme's ItemsControl style also overrides a default `ItemsPanel`).

## 7. Styles
- **`Styles/StudioStyles.axaml` (AvaloniaEdit's theme included) applies once on any branch of the tree, through `Controls/Common/StudioStyleSheet`.** The Runner's `App.axaml` applies it to the whole application; `CSharpStudioHostView` includes it with `IncludeUnlessApplicationHasIt`, so only inside FryPDF, whose application has none of it. Never include it in a page, a control, a row or a cell. Avalonia gives every control one style instance for **each style of its type in each copy, matched or not**: with the application and the host both including it, a notebook cell's Button carried 263 style instances. Applying it once cut the heap after a visit of every page from 794 to 548 MB, a page down in a 1,000-cell notebook from 282 to 188 ms and first page visits by about a third. Earlier, removing 36 per-control and per-page includes had halved the memory (1,021 → 546 MB). `perf --cells 1000` prints how many style frames a cell's Button carries and how many are distinct: any style there twice means a second copy.
- A window of its own (`AiComposerWindow`) calls `IncludeUnlessApplicationHasIt`; a control also shown in a window of its own (the visualizers) calls `IncludeWhereMissing`, which looks up the tree when it joins it. The editor's completion popup adds AvaloniaEdit's theme only when the application lacks the bundle. Flyouts, tooltips and context menus inherit from their owner and need nothing.
- **A class style for a common type costs every control of that type**, matched or not: each Button in the studio still carries ~133 style instances, one per Button style of every page (Hub, Docs, Settings, the composer...). Known debt: scope a page's styles to that page, or make its variants `ControlTheme`s that only the controls using them pay for.
- **No endless animations** (`IterationCount="Infinite"`) on anything that stays on screen. One pulsing status dot redrew the whole Hub on every frame (~60 ms of drawing per frame, headless) for as long as it was shown. A status is a still colour.
- **Bind an icon name kept as a string through `IconKindConverter`** (`Kind="{Binding X, Converter={x:Static common:IconKindConverter.Instance}}"`), or keep it as a `MaterialIconKind`. Left to Avalonia, each update looks the name up among 7,000+ icon names (a reflection binding copies the whole list to do it): 1 to 2 ms an update, on every Explorer row, Problems row and tab switch.
- **The palette (`Styles/Tokens/StudioPaletteTokens.axaml`) is also included once, by the host** (plus the style bundle, the Runner's `App.axaml` and windows of their own). A copy inside a page or control shadows the theme: colours set by the theme engine never showed there.
- **Themes, colours, fonts and density go through `DynamicThemeEngine`, which holds them in one theme layer** (`ThemeLayerProvider`) installed on top of the palette in each studio root (the host view and windows that call `TrackResourceRoot`). A change replaces the layer's content with **one** resource-change notification. Never write many entries into `Application.Resources` (or any resource dictionary above many controls) one by one: each write re-resolves every dynamic resource below it, so a theme of ~240 entries froze the studio for ~8 s. Never swap a dictionary inside `MergedDictionaries` either: that is a removal plus an addition, two notifications, with a flash of the palette in between.
- Settings lists that only change colour (token rows, swatches, contrast rows) are recoloured in place; a list is replaced (`RangeObservableCollection.ReplaceAll`) only when its members change. Slider and wheel changes are coalesced to one recomputation per UI turn. Measure with `perf --theme-switch` (`--theme-shots` saves each theme; `--theme-only` with only the Hub and Settings built): a theme apply ≤ 300 ms with every page built, a hue-wheel step ≤ 16 ms.
- **Palettes are generated off nothing heavier than arithmetic** (`PaletteGenerator` + `ThemeTokenMapper`, ~2 ms): a Generate press, a lock, a one-section regenerate or an engine switch recolours the section rows in place and never applies a theme by itself; only Apply swaps the theme layer. Editors follow a theme through `SyntaxPaletteApplier.EditorColorsChanged` (one event per burst, on the UI thread) and redraw; they never re-fetch or rebuild highlighting definitions per change. See `docs/themes-and-palettes.md`.
- **Layout values (font sizes, weights, radii, borders, spacing, shadows) are token keys, never literals and never `{DynamicResource}`**: `lt:Tokens.FontSize="DsFontSize200"` on an element, `<Setter Property="lt:Tokens.CornerRadius" Value="DsRadiusMD" />` in a style (fixed values as `Value="=0"`). A dynamic resource in a shared style's setter makes Avalonia build that style per control: ~140 MB across the studio, and every one is re-resolved on each theme switch. Keys cost what literals cost, and a layout change sets them from one weak registry. `DesignTokenLintTests` enforces it; see `docs/themes-and-palettes.md`.
- **Preferences are never written on the UI thread or per keystroke**: `StudioSettingsStore.Update` changes them in memory and writes once, 500 ms after the last change, atomically. Read-modify-write only; never build a new `StudioSettings` to save (it resets every field you didn't copy).
- To check that a change still looks right in the plugin (where FryPDF's application has none of the studio's styles), render the pages through the host with `FRY_PLUGIN_STYLES_ONLY=1 ... perf --files 20 --rounds 1 --shots` and compare with a normal `--shots` render (`images.py compare`); only the live memory readout may differ. (The single-view snapshot commands render a page without the host, so in that mode they come out unstyled.)

---

## Verification
Time the real studio, headless, on a generated workspace:

```bash
dotnet build tools/UiSnapshots
dotnet tools/UiSnapshots/bin/Debug/net10.0/UiSnapshots.dll perf --files 200 --rounds 3 --big-kb 2048 --cells 1000 --external 30000
dotnet tools/UiSnapshots/bin/Debug/net10.0/UiSnapshots.dll perf --files 200 --rounds 3 --open-early --big-csv-mb 1 --big-image-mp 24
dotnet tools/UiSnapshots/bin/Debug/net10.0/UiSnapshots.dll perf --table-rows 100000
```
Every scenario also prints the **longest input wait**: a background thread keeps a tiny job queued at input priority and records how long it waited, which is what a user feels as a hang.

Two things in the headless numbers are the harness, not the studio:
- **JIT warm-up.** The runtime optimizes hot code only after no new method has been compiled for a moment, and the harness never pauses, so the first file opens and tab switches of a run use unoptimized code and vary from run to run (file open 55 to 98 ms on the same code). The `warmed up:` line repeats them after a pause; hold that one to the budget. `--jit-watch` lists what was compiled over the switches.
- **Frame capture.** Each captured headless frame allocates a 5 MB framebuffer and reports it to the GC as memory pressure, which now and then forces a full collection: a single 400 to 700 ms "slowest tab switch" with the switch median unchanged. `--alloc-types` names each full collection's reason (7 = induced by memory pressure) and the types the switches allocate.

Other diagnostics: `--hub-census` (what the Hub draws and what each piece costs on its own, plus frames after arriving: an idle page must cost ~0.4 ms a frame), `--editor-props` (the code editor's font and how long shaping a line takes), `--memory-trace` and `--memory-parts` (the heap per page).

Budgets (Debug build, this machine class; compare runs against each other, not against a stopwatch):

| Measure | Budget |
| :--- | :--- |
| Warm page switch | ≤ 20 ms |
| Tab switch, small file (`warmed up:` line) | ≤ 30 ms |
| File open, small file (`warmed up:` line) | ≤ 50 ms |
| A frame of a page with nothing changing (`--hub-census`) | ≤ 1 ms |
| Workspace scans during tab and page switches | 0 |
| Explorer refresh at 2,000 files | ≤ 600 ms |
| Explorer refresh of a 30,000-file workspace (top folder only) | ≤ 200 ms |
| Keystroke in a 2 MB file, whole editor | ≤ 8 ms |
| Memory growth per round of page visits | ~0 |
| Open a notebook of 1,000 cells | ≤ 1.5 s, and the same at 4,000 |
| Page down in a 1,000-cell notebook | ≤ 250 ms, no mouse-wheel notch over 100 ms |
| Open the notebook Outline or Search panel at 2,000 cells | ≤ 300 ms |
| Index 200,000 files in the background | ≤ 2 s |
| A Go to File search over 200,000 files | ≤ 40 ms |
| Find in Files over 30,000 small files, nothing found | ≤ 2 s |
| Find in Files, a query that matches everywhere | ≤ 200 ms to the match limit |
| Longest input wait during a tab switch, a file open or a warm page switch | ≤ 100 ms |
| Show a table of 100,000 rows (`perf --table-rows 100000`) | ≤ 300 ms, ~25 rows built |
| Open a 1 MB CSV / a 24-megapixel image (`--big-csv-mb 1`, `--big-image-mp 24`): longest input wait | ≤ 300 ms |
| Open a script during start-up (`--open-early`) | waits for the engine's Core stage only, not its warm-up |
| Palette generate + token map (`perf --theme-switch`) | ≤ 10 ms |
| Palette Generate press, previewed and drawn, every page built | ≤ 30 ms |
| Apply a theme or a palette, every page built | ≤ 300 ms |
| Apply a layout (preset or lever release), every page built | ≤ 400 ms |

Also run the guard tests: `dotnet test Tests --filter "FullyQualifiedName~KeepAlivePageHostTests|FullyQualifiedName~WorkspaceStalenessTests|FullyQualifiedName~ExplorerRowList|FullyQualifiedName~ExternalChangeTests|FullyQualifiedName~TabSwitchStateTests|FullyQualifiedName~NotebookCanvas|FullyQualifiedName~LazyContent|FullyQualifiedName~WorkspaceFileIndex|FullyQualifiedName~GoToFile|FullyQualifiedName~WorkspaceTruncation|FullyQualifiedName~LazyExplorer|FullyQualifiedName~FindInFiles|FullyQualifiedName~TextMatcher|FullyQualifiedName~WorkspaceTextSearch|FullyQualifiedName~StudioSearchPanel|FullyQualifiedName~LargeDocument|FullyQualifiedName~CSharpEditorPlugin.Tests.Activities|FullyQualifiedName~StudioLoadingOverlayTests|FullyQualifiedName~TablePreviewScaleTests|FullyQualifiedName~LargeAndBinaryFileTests|FullyQualifiedName~RestartDebugTests|FullyQualifiedName~CanvasVirtualizationGuard|FullyQualifiedName~StyleCostLintTests"`.
