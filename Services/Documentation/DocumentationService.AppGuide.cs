using Material.Icons;
using PdfEditorApp.Plugins.CSharpEditor.Models;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Documentation;

public partial class DocumentationService
{
    private DocCategory BuildAppGuideCategory()
    {
        return new DocCategory
        {
            Id = "app_guide",
            Title = "Application Guide",
            IconKind = MaterialIconKind.ApplicationOutline,
            AccentColor = "#38BDF8",
            Badge = "Getting Started",
            Description = "Learn how to use FrySharp, navigate the 5-zone VS Code layout, manage scripts, and debug .NET code.",
            Articles = new List<DocArticle>
            {
                CreateWelcomeArticle(),
                CreateVsCodeLayoutArticle(),
                CreateScriptsVsNotebooksArticle(),
                CreateDebuggingGuideArticle(),
                CreateNuGetGuideArticle(),
                CreateLanguagesGuideArticle()
            }
        };
    }

    private DocArticle CreateWelcomeArticle()
    {
        return new DocArticle
        {
            Id = "welcome_guide",
            Title = "Welcome to FrySharp",
            Subtitle = "A high-performance .NET 10 document scripting, algorithm, and interactive notebook IDE.",
            ReadingTime = "3 min read",
            Summary = "FrySharp brings Visual Studio Code ergonomics to FryPDF for document scripting, algorithmic exploration, and dynamic data visualization.",
            Keywords = new List<string> { "overview", "intro", "architecture", "roslyn", "net10", "welcome", "getting started" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Core Paradigm & Purpose",
                    Content = "FrySharp is a premier development environment built for .NET 10 within FryPDF. It provides Roslyn-powered C# scripting, interactive polyglot-style notebooks, algorithm visualizers, and interactive inspection decks without requiring an external IDE.",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "All compilation and reflection operations execute off the UI thread via background task workers, guaranteeing zero UI-thread freezes."
                },
                new()
                {
                    Heading = "Key Capabilities",
                    Content = "Everything you need for rapid C# document automation and algorithm prototyping is integrated:",
                    BulletPoints = new List<string>
                    {
                        "Roslyn C# 13 & .NET 10 script execution engine with live diagnostics.",
                        "Interactive Polyglot Notebooks (.ipynb / .csnb) with cell reordering, collapsing, and rich outputs.",
                        "VisualizerRecorder framework for step-by-step playback of sorting, trees, matrices, and graphs.",
                        "One-click .Dump() inspection for collections, objects, dataframes, images, and tables.",
                        "Visual Studio Code Dark+ theme, gutter breakpoints, stepping debugger, and hover data inspection."
                    }
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_hello_world",
                    Title = "Your First FrySharp Script",
                    Description = "Write modern top-level C# code and use .Dump() to view output in the bottom deck.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = @"// Welcome to FrySharp!
var message = ""Hello from .NET 10 Roslyn Scripting!"";
Console.WriteLine(message);

var numbers = Enumerable.Range(1, 10).Select(n => new { Number = n, Square = n * n, Cube = n * n * n });
numbers.Dump(""Number Powers Table"");"
                }
            }
        };
    }

    private DocArticle CreateVsCodeLayoutArticle()
    {
        return new DocArticle
        {
            Id = "vscode_layout",
            Title = "The 5-Zone VS Code Layout",
            Subtitle = "Master the authentic Visual Studio Code chrome, zones, panels, and shortcuts.",
            ReadingTime = "4 min read",
            Summary = "Every studio window adheres to a strict 5-zone layout structure mirroring VS Code for familiar, lightning-fast navigation.",
            Keywords = new List<string> { "layout", "5-zone", "activity bar", "sidebar", "deck", "status bar", "dock", "chrome" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Zone 1: Activity Bar (Leftmost 48px)",
                    Content = "The leftmost vertical rail hosts tool icons: Explorer (file tree), Search (workspace grep), Run & Debug, Dependencies & NuGet, Scratchpad & Notes, and Problems (with error badges). The bottom features Settings and Return to Hub.",
                    CalloutType = DocCalloutType.Info,
                    CalloutText = "Clicking the currently active icon toggles the Primary Side Bar closed or open (Ctrl+B / Cmd+B)."
                },
                new()
                {
                    Heading = "Zone 2: Primary Side Bar (Resizable ~270px)",
                    Content = "Dynamically renders the view selected in the Activity Bar. The Explorer includes New File, New Folder, Open Project, Refresh, and Collapse All controls.",
                    BulletPoints = new List<string>
                    {
                        "Explorer: Full workspace directory tree with inline rename and delete.",
                        "Search: Workspace-wide text search with regex and match navigation.",
                        "Debug: Call stack, local variables, and watch expressions.",
                        "NuGet: Live package search, install, and dependency tree inspection.",
                        "Problems: Diagnostic errors and warnings with click-to-line navigation."
                    }
                },
                new()
                {
                    Heading = "Zone 3: Editor Area",
                    Content = "The central canvas hosts the multi-tab bar (dirty status indicators, close buttons, tab switching), breadcrumbs trail, and the AvaloniaEdit syntax canvas with line numbers, code folding, and breakpoint gutter. Rest the pointer on any symbol (or press Ctrl+K Ctrl+I) to see its signature, where it's declared and its documentation."
                },
                new()
                {
                    Heading = "Zone 4: Bottom Tool Deck (Ctrl+J / Cmd+J)",
                    Content = "Collapsible and resizable bottom panel featuring 6 specialized tabs:",
                    BulletPoints = new List<string>
                    {
                        "PROBLEMS: Roslyn diagnostic errors, warnings, and code analysis.",
                        "OUTPUT: Compiler messages and build logs.",
                        "TERMINAL / CONSOLE: Standard stdout/stderr streams.",
                        "DEBUG CONSOLE (REPL): Immediate expression evaluation prompt.",
                        "RESULTS (.DUMP): Interactive tables, HTML viewer, and object tree inspector.",
                        "TEST CASES: Unit test discovery, execution runners, and assertions."
                    }
                },
                new()
                {
                    Heading = "Zone 5: Status Bar (Bottom 22px)",
                    Content = "Displays workspace status, diagnostic problem count, Roslyn compiler state, execution elapsed timer, cursor line/col, encoding (UTF-8), and language mode."
                }
            }
        };
    }

    private DocArticle CreateScriptsVsNotebooksArticle()
    {
        return new DocArticle
        {
            Id = "scripts_vs_notebooks",
            Title = "Scripts (.csx) vs Interactive Notebooks",
            Subtitle = "Understand the differences between single-file scripts and cell-based notebooks.",
            ReadingTime = "3 min read",
            Summary = "Choose between linear script automation and interactive exploratory computing with live cell execution.",
            Keywords = new List<string> { "scripts", "notebooks", "cells", "csx", "ipynb", "interactive", "polyglot" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "C# Scripts (.csx / .linq)",
                    Content = "Scripts are ideal for document automation pipelines, batch PDF utilities, algorithm implementations, and standalone tools. They execute from top to bottom and support top-level statements without boilerplate classes.",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "Press F5 or Ctrl+F5 to compile and run the active script immediately."
                },
                new()
                {
                    Heading = "Interactive Notebooks (.ipynb / .csnb)",
                    Content = "Notebooks divide code into independent, sequential cells. You can mix rich Markdown documentation with executable C# code cells. Variables and imports defined in one cell persist into subsequent cell executions.",
                    BulletPoints = new List<string>
                    {
                        "Run individual cell: Shift+Enter",
                        "Run all cells: Click 'Run All' in the notebook toolbar.",
                        "Cell collapsing: Double-click a cell header or click the collapse chevron to tuck away large code blocks.",
                        "Rich outputs: Visualizers, charts, tables, and images render directly beneath each cell."
                    }
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_notebook_cell",
                    Title = "Interactive Notebook Cell Example",
                    Description = "Define data in one cell, compute in another, and display rich results.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Notebook,
                    Code = @"// Cell 1: Define dataset
var sales = new[]
{
    new { Quarter = ""Q1"", Revenue = 125000, Growth = 0.12 },
    new { Quarter = ""Q2"", Revenue = 142000, Growth = 0.14 },
    new { Quarter = ""Q3"", Revenue = 168000, Growth = 0.18 },
    new { Quarter = ""Q4"", Revenue = 210000, Growth = 0.25 },
};

// Render interactive table
sales.Dump(""2026 Fiscal Performance"");"
                }
            }
        };
    }

    private DocArticle CreateDebuggingGuideArticle()
    {
        return new DocArticle
        {
            Id = "debugging_guide",
            Title = "Debugging & Diagnostics",
            Subtitle = "Set breakpoints, step through code, inspect variables, and evaluate expressions.",
            ReadingTime = "4 min read",
            Summary = "Full stepping debugger with breakpoints, hover data tips, watch variables, and immediate REPL.",
            Keywords = new List<string> { "debugging", "breakpoints", "f5", "f10", "f11", "step", "repl", "variables", "watch" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Setting Breakpoints",
                    Content = "Click in the editor gutter margin next to any executable line to set a breakpoint (red circular dot), or place your cursor on a line and press F9.",
                    CalloutType = DocCalloutType.Info,
                    CalloutText = "When debugging starts (F5), execution automatically pauses when hitting a breakpoint, highlighting the current line in amber."
                },
                new()
                {
                    Heading = "Stepping Controls",
                    Content = "Use standard VS Code stepping shortcuts:",
                    BulletPoints = new List<string>
                    {
                        "F5: Start Debugging / Continue Execution to next breakpoint.",
                        "F10: Step Over current statement.",
                        "F11: Step Into method call.",
                        "Shift+F5: Stop Debugging / Terminate execution process."
                    }
                },
                new()
                {
                    Heading = "Variable Inspection & Immediate REPL",
                    Content = "While paused, hover over any variable in the code canvas to see its current value in a floating inspection tip. The Run & Debug sidebar lists all local variables, and the Bottom Deck's REPL prompt lets you evaluate arbitrary expressions in the current scope."
                }
            }
        };
    }

    private DocArticle CreateNuGetGuideArticle()
    {
        return new DocArticle
        {
            Id = "nuget_dependencies",
            Title = "NuGet Packages & Directives",
            Subtitle = "Reference external packages, assemblies, and shared scripts using Roslyn directives.",
            ReadingTime = "3 min read",
            Summary = "Import packages directly into scripts and notebooks with automatic resolution and assembly metadata.",
            Keywords = new List<string> { "nuget", "packages", "dependencies", "r directive", "load", "restore" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Referencing NuGet Packages (#r directive)",
                    Content = "You can add any NuGet package directly in your script or notebook cell using the standard Roslyn directive at the top of your document:",
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "The background NuGet resolver automatically fetches and restores dependencies upon compilation."
                },
                new()
                {
                    Heading = "Referencing Local Assemblies & Scripts",
                    Content = "Reference local DLLs or include shared script files using:",
                    BulletPoints = new List<string>
                    {
                        "#r \"nuget: Newtonsoft.Json, 13.0.3\" — Pulls package from NuGet.org.",
                        "#r \"path/to/MyCustomLibrary.dll\" — References a precompiled assembly.",
                        "#load \"common/Helpers.csx\" — Inlines another script file before execution."
                    }
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_nuget_usage",
                    Title = "Using NuGet Packages in Scripts",
                    Description = "Reference external libraries like Newtonsoft.Json or Humanizer directly in your script.",
                    Language = "csharp",
                    TargetKind = WorkspaceItemKind.Script,
                    Code = @"#r ""nuget: Newtonsoft.Json, 13.0.3""
using Newtonsoft.Json;

var payload = new { Project = ""FryPDF"", Version = 10.0, Features = new[] { ""Roslyn"", ""Visualizer"", ""Notebooks"" } };
string json = JsonConvert.SerializeObject(payload, Formatting.Indented);

Console.WriteLine(json);"
                }
            }
        };
    }

    private DocArticle CreateLanguagesGuideArticle()
    {
        return new DocArticle
        {
            Id = "languages_python",
            Title = "Languages & Python",
            Subtitle = "Run .py files, mix Python and C# cells in one notebook, share values between them, and install packages.",
            ReadingTime = "5 min read",
            Summary = "The studio runs your installed Python, so numpy, pandas, matplotlib and every other package work. Notebooks are polyglot: each cell runs in its language's kernel.",
            Keywords = new List<string> { "python", "py", "numpy", "pandas", "matplotlib", "pip", "venv", "polyglot", "share", "kernel", "language", "toolchain", "input" },
            Sections = new List<DocSection>
            {
                new()
                {
                    Heading = "Python files in FrySharp",
                    Content = "Create one with New File → Python in the Explorer (or open any .py file in your workspace). The file is edited and saved as plain Python, in place:",
                    BulletPoints = new List<string>
                    {
                        "F5 or Ctrl+F5 runs it with Python (there's no Python debugger yet, so F5 runs too); Stop ends it.",
                        "Output appears in the Terminal as it's printed. When the program calls input(), type in the Terminal's input row and press Enter.",
                        "A traceback puts the failing line in Problems, with the column. A missing module (ModuleNotFoundError) gets an \"Install <package>\" fix.",
                        "The status bar shows which Python runs the file; click it to pick another or to create the studio environment."
                    }
                },
                new()
                {
                    Heading = "Which Python runs",
                    Content = "The studio uses the first of these it finds (Python 3.9 or newer):",
                    BulletPoints = new List<string>
                    {
                        "The one you picked in the status bar.",
                        "The project's virtual environment: a .venv, venv or env folder (with pyvenv.cfg) next to the file or above it, up to the workspace root.",
                        "The studio environment, once it exists (see Packages below).",
                        "What your terminal would run (python3 on your login shell's PATH), then the usual install places: Homebrew, python.org, pyenv, Conda, uv, the Windows launcher."
                    },
                    CalloutType = DocCalloutType.Tip,
                    CalloutText = "The Hub's STUDIO ENVIRONMENT card shows the Python the studio found, or what's missing and how to install it."
                },
                new()
                {
                    Heading = "Python cells in notebooks",
                    Content = "A notebook can mix languages, as in Polyglot Notebooks. Each cell runs in its language's kernel, and each kernel keeps its own variables:",
                    BulletPoints = new List<string>
                    {
                        "Pick a cell's language from the chip in its toolbar (C# / PY), or start the cell with a line such as #!python or #!csharp.",
                        "New cells are written in the language of the cell they're added next to; the kernel pill in the header sets the notebook's default.",
                        "Python cells show pandas DataFrames as tables, matplotlib figures inline (plt.show()), and objects with _repr_html_ or _repr_png_ as HTML and images.",
                        "input() asks in the cell; Stop interrupts the cell and the kernel keeps its variables. Restart kernels (on the kernel pill) starts every kernel afresh."
                    }
                },
                new()
                {
                    Heading = "Sharing values between languages (#!share)",
                    Content = "Put #!share --from <language> <name> [--as <new name>] at the top of a cell to copy a variable in from another language's kernel. Values travel as JSON:",
                    BulletPoints = new List<string>
                    {
                        "C# arrays and lists arrive in Python as lists; Python lists arrive in C# as the array that fits (int[], double[], string[], double[][]…).",
                        "Dictionaries and objects become dicts in Python and Dictionary<string, object> in C#; numbers, text, true/false and null map as you'd expect.",
                        "numpy arrays and pandas Series share their values; a DataFrame shares its rows as records.",
                        "A C# variable that already exists keeps its type and gets the value; otherwise it's declared with the type that fits."
                    },
                    CalloutType = DocCalloutType.Warning,
                    CalloutText = "Only data can be shared: functions, controls, images and streams can't, and the cell says so."
                },
                new()
                {
                    Heading = "Packages (%pip)",
                    Content = "Start a Python cell with %pip install <package> (or click Install on a missing-module error) to install into the Python the notebook runs:",
                    BulletPoints = new List<string>
                    {
                        "A Python that won't take packages (Homebrew's or Debian's, marked \"externally managed\") gets a studio environment instead: a virtual environment the studio creates once, which still sees that Python's own packages.",
                        "The running kernel sees the new package straight away; its variables are kept.",
                        "A project .venv takes precedence, so a project's own environment is always the one used."
                    }
                },
                new()
                {
                    Heading = "When Python isn't installed",
                    Content = "The Terminal, the notebook cell and the Hub say what's missing and how to get it:",
                    BulletPoints = new List<string>
                    {
                        "macOS: brew install python, or the installer from python.org.",
                        "Windows: winget install Python.Python.3.13 (the Microsoft Store shortcut named python.exe isn't a real Python).",
                        "Linux: sudo apt install python3 python3-venv (or your distribution's equivalent).",
                        "Then click Look again (the Hub, or the status bar's Python menu): no restart needed."
                    }
                },
                new()
                {
                    Heading = "Not there yet",
                    Content = "Python support runs real Python, but the editor's C# helpers don't cover it yet:",
                    BulletPoints = new List<string>
                    {
                        "No completion, hover or live error squiggles for Python, and no Python debugger or breakpoints.",
                        "A cell keeps one output of each kind, so of several figures the last one shows.",
                        "Values cross between languages as JSON only."
                    }
                }
            },
            CodeSnippets = new List<DocCodeSnippet>
            {
                new()
                {
                    Id = "snip_python_dataframe",
                    Title = "A Python cell with numpy and pandas",
                    Description = "Opens as a notebook with one Python cell. The DataFrame shows as a table; a missing package gets an Install button.",
                    Language = "python",
                    TargetKind = WorkspaceItemKind.Notebook,
                    NotRunnable = true, // requires external packages: numpy, pandas
                    Code = @"import numpy as np
import pandas as pd

values = np.array([3, 1, 4, 1, 5, 9, 2, 6])
frame = pd.DataFrame({""n"": values, ""square"": values ** 2})
frame"
                },
                new()
                {
                    Id = "snip_python_figure",
                    Title = "A matplotlib figure in a cell",
                    Description = "plt.show() draws the figure inline, under the cell.",
                    Language = "python",
                    TargetKind = WorkspaceItemKind.Notebook,
                    NotRunnable = true, // requires external package: matplotlib
                    Code = @"import matplotlib.pyplot as plt

plt.figure(figsize=(6, 2.5))
plt.plot([3, 1, 4, 1, 5, 9, 2, 6], marker=""o"")
plt.title(""A figure from Python"")
plt.show()"
                },
                new()
                {
                    Id = "snip_share_values",
                    Title = "Sharing a C# value with Python",
                    Description = "Run a C# cell with var nums = new[] { 3, 1, 4 }; first, then this Python cell reads it and makes squares, which a C# cell can take back with #!share --from python squares.",
                    Language = "python",
                    TargetKind = WorkspaceItemKind.Notebook,
                    NotRunnable = true, // reads a value another cell made
                    Code = @"#!share --from csharp nums
squares = [n * n for n in nums]
print(sum(squares))"
                }
            }
        };
    }
}
