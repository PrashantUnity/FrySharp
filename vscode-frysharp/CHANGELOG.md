# Changelog

## 2.0.7

- Synchronized with FrySharp v2.0.7 release.
- Added Model Context Protocol (MCP) tool integration and comprehensive AI subsystem tool documentation.
- Upgraded bundled headless `FrySharp.Sidecar` JSON-RPC engine to latest .NET 10 runtime.
- Enhanced notebook execution stability and live diagnostics across languages.

## 2.0.5

- Synchronized with FrySharp v2.0.5 release.
- Bundles the latest headless `FrySharp.Sidecar` JSON-RPC engine with multi-language execution improvements for SQL, Java, Go, Rust, and C++.
- Robust cross-platform process launching and argument escaping.
- Improved live Roslyn diagnostics and asynchronous session management.

## 1.0.0

First release.

### Notebooks (`.frynb`, and `.ipynb` on request)

- Cells in C#, F#, Python, JavaScript, Java, SQL, Rust, Go and C++, each run by its real toolchain through the FrySharp engine (a .NET 10 sidecar). What a cell declares stays for the cells after it.
- A `.frynb` file made by the desktop app opens, runs and saves back unchanged (charts, tables, inspectors, images and the fields this extension does not read are kept). A Jupyter file saves back byte for byte.
- `#!share` hands a value from one language to another. `%pip`, `%nuget`, `%maven`, `%cargo`, `%go` and `%vcpkg` lines install packages.
- Output as in the desktop app: the last expression is shown (scalars as text, a list of records as a table, an object in the Object Explorer), `Display.*` tables, charts, 3D plots, visualizers, ECharts, HTML, Markdown and images. A cell with several kinds of output draws them as tabs (Table, Console, Inspector, Chart, All ...; setting `frysharp.notebook.outputTabs`).
- The Object Explorer lists an object's members four levels down and a fifth level as a title, a reference back to an object above included, in every language, as the desktop does.
- Input: `Console.ReadLine`, `input()`, `Scanner`, `fry::input` and the rest ask in an input box; closing it ends the input.
- Errors are marked on the line and column of the cell, listed in Problems and shown as VS Code's own error output. Stop ends a running cell and keeps the session where the language allows it.
- C# completion and hover use the cells before; directive names, languages and `#!share` variables complete in every language; compile errors are marked as you type (setting `frysharp.notebook.liveDiagnostics`).
- Variables view (Panel > FrySharp), a note under a cell that runs as another language than the editor shows (a first line of `#!python`), a status bar item for the engine.

### Script runs

- Run, stop and re-run a file in a terminal (colour, input, Ctrl+C, clickable errors, Problems), from the editor title, the context menus or `Ctrl+Alt+R`; arguments, save before run, a scratchpad notebook and Send Selection.
- Optional Run and Stop CodeLens (setting `frysharp.codeLens.enabled`; never on plain `.cs` files).

### Everything else

- `FrySharp: Show Engine Info` lists the engine's version, the languages it can run, the toolchain it found for each and where its log is.
- Workspace Trust is required to run code; remote and WSL windows run the engine next to the files.
- The .NET 10 runtime is found on the PATH, in `frysharp.dotnetPath`, or installed by the .NET Install Tool.

### Known limits

- Rust, Go and C++ cells are built one at a time with what earlier cells declared written in front, so a variable of a type the cell defined is not kept (Rust), a function or a struct with lowercase fields is not kept (Go) and pointers, arrays and lambdas are not kept (C++). The README lists them.
- A plain read of standard input in Rust, Go and C++ sees the end of input; use `fry::input` / `fry.Input`.
- Live Avalonia controls (`Display.Control`) are desktop only.
