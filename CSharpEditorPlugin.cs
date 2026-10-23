using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using FryWorld.Sdk.AI;
using FryWorld.Sdk.Descriptors;
using FryWorld.Sdk.Models;
using FryWorld.Sdk.Plugins;
using FryWorld.Sdk.Services;
using FryWorld.Sdk.Shortcuts;
using Microsoft.Extensions.AI;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.AI.Providers;
using PdfEditorApp.Plugins.CSharpEditor.Services.AI.Tools;
using PdfEditorApp.Plugins.CSharpEditor.Services.Processes;
using PdfEditorApp.Plugins.CSharpEditor.Views;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels;
using CSharpCodeStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.CSharpCodeStudioViewModel;
using CSharpNotebookStudioViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks.CSharpNotebookStudioViewModel;
using CSharpStudioHostViewModel = PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common.CSharpStudioHostViewModel;

namespace PdfEditorApp.Plugins.CSharpEditor;

/// <summary>
/// FryPDF plugin entry point for FrySharp.
/// Mounts as a full-viewport Workspace Studio Navigation page, Status Bar widget,
/// Command Palette action, and Dynamic Ribbon action (No floating shell overlay).
/// </summary>
public class CSharpEditorPlugin : IFryPlugin
{
    public string Id => "com.fryworld.csharpstudio";
    public string Name => "FrySharp";
    public Version Version => new(2, 0, 6);
    public bool AutoOpenOverlay => false;
    public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();

    public IReadOnlyDictionary<string, PluginSettingDefinition>? SettingsSchema =>
        new Dictionary<string, PluginSettingDefinition>
        {
            ["DefaultTemplate"] = new()
            {
                Label = "Default Starter Script",
                Description = "Initial template loaded when creating a new script.",
                Type = PluginSettingType.Select,
                Options = new() { "Hello World Console", "PDF Document Automation", "LINQ Benchmark", "JSON Serializer" },
                DefaultValue = "Hello World Console"
            },
            ["EditorFontSize"] = new()
            {
                Label = "Editor Font Size (px)",
                Description = "Code typography size in points/pixels.",
                Type = PluginSettingType.Integer,
                DefaultValue = 13
            },
            ["ShowLineNumbers"] = new()
            {
                Label = "Show Line Numbers",
                Description = "Display gutter line numbers along the left margin.",
                Type = PluginSettingType.Boolean,
                DefaultValue = true
            },
            ["AutoCheckErrors"] = new()
            {
                Label = "Live Roslyn Diagnostics",
                Description = "Continuously analyze C# code in the background for syntax and semantic errors.",
                Type = PluginSettingType.Boolean,
                DefaultValue = true
            },
            ["ExecutionTimeoutSeconds"] = new()
            {
                Label = "Execution Timeout (seconds, 0 = no limit)",
                Description = "Maximum allowed run time before script execution is automatically stopped. 0 disables the automatic limit — Stop is always available regardless.",
                Type = PluginSettingType.Integer,
                DefaultValue = 0
            }
        };

    public Task ApplyAsync(IFryPluginContext ctx, CancellationToken ct = default)
    {
        // Scripts compile against the desktop API (Display, charts, Avalonia), so fix that before anything compiles.
        PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn.DesktopScriptHost.Configure();

        CSharpStudioHostViewModel? activeHost = null;

        if (ctx.TryGetService<FryWorld.Sdk.Theming.IThemeManager>(out var themeManager))
        {
            var tokenReg = themeManager.RegisterTokenContributor(new FryWorld.Sdk.Theming.Contributors.DeveloperStudioTokenContributor());
            ctx.RegisterDisposable(tokenReg);
        }

        // Opens the studio page in whichever shell hosts the plugin: FryWorld, FryPDF or the standalone runner.
        void NavigateToStudio()
        {
            if (ctx.TryGetService<IShellService>(out var shell)) shell.NavigateTo("com.fryworld.csharpstudio");
        }

        CSharpStudioHostViewModel GetHostViewModel()
        {
            if (activeHost == null)
            {
                ctx.TryGetService<ISettingsManager>(out var settingsStore);
                activeHost = CSharpStudioHostViewModel.ActiveInstance ?? new CSharpStudioHostViewModel(ctx, settingsStore);
            }
            return activeHost;
        }

        var navReg = ctx.RegisterNavigationItem(new NavigationItemDescriptor
        {
            Id = "com.fryworld.csharpstudio",
            Title = "FrySharp",
            Category = "Overview",
            IconKind = "CodeBraces",
            AccentColor = "#75D59A",
            Order = 38,
            ViewFactory = () =>
            {
                var hostVm = GetHostViewModel();
                var view = new CSharpStudioHostView { DataContext = hostVm };
                if (ctx.TryGetService<FryWorld.Sdk.Theming.IThemeManager>(out var tm))
                {
                    tm.TrackResourceRoot(view);
                }
                return view;
            }
        });

        var cmdReg = ctx.RegisterCommand(new CommandPaletteDescriptor
        {
            Id = "cmd.studio.csharpeditor",
            Title = "Open FrySharp",
            Subtitle = "Launch in-app C# development studio with project manager, editor, and Roslyn runner",
            Category = "Developer Tools",
            IconKind = "CodeBraces",
            ShortcutKey = OperatingSystem.IsMacOS() ? "Cmd+Alt+E" : "Ctrl+Alt+E",
            Command = new RelayCommand(NavigateToStudio)
        });

        var customizationManager = new PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.CustomizationManager();
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CustomizationManager = customizationManager;

        var extensionManager = new PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Extensions.ExtensionManager();
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ExtensionManager = extensionManager;

        // The look the user left the studio with (theme, density, colours), before their customization script runs, so
        // the script still has the last word.
        PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Theming.AppearanceRestorer.RestoreFrom(
            null, PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ThemeEngine);

        _ = Task.Run(async () =>
        {
            var ws = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.WorkspaceService.RootPath;
            await customizationManager.InitializeAsync(enableHotReload: true, ct);
            await extensionManager.DiscoverAndLoadFromDefaultLocationsAsync(workspacePath: ws, enableHotReload: true, ct);
        }, ct);

        var reloadCustomizationsCmd = ctx.RegisterCommand(new CommandPaletteDescriptor
        {
            Id = "cmd.studio.reload_customizations",
            Title = "Reload Customizations & Themes",
            Subtitle = "Recompile and apply ~/.frysharp/init.csx and active themes in real time",
            Category = "Developer Tools",
            IconKind = "Refresh",
            ShortcutKey = OperatingSystem.IsMacOS() ? "Cmd+Shift+R" : "Ctrl+Shift+R",
            Command = new RelayCommand(() => { _ = customizationManager.ReloadAsync(); })
        });

        var openInitScriptCmd = ctx.RegisterCommand(new CommandPaletteDescriptor
        {
            Id = "cmd.studio.open_init_script",
            Title = "Open Customization Script (init.csx)",
            Subtitle = "Edit in-app startup script, colors, themes, commands, and hooks",
            Category = "Developer Tools",
            IconKind = "Tune",
            Command = new RelayCommand(() =>
            {
                NavigateToStudio();
                _ = customizationManager.Storage.EnsureInitScriptExistsAsync().ContinueWith(async t =>
                {
                    if (activeHost?.CodeStudioViewModel != null)
                    {
                        var item = new ScriptDocumentItem
                        {
                            Id = "init.csx",
                            Title = "init.csx",
                            Code = await t,
                            LanguageId = "csharp",
                            SourceFilePath = customizationManager.Storage.GlobalInitScriptPath
                        };
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            activeHost.NavigateToCodeStudio(item);
                        });
                    }
                });
            })
        });

        var statusReg = ctx.RegisterStatusBarWidget(new StatusBarWidgetDescriptor
        {
            Id = "frypdf.status.csharpeditor",
            AlignRight = true,
            Order = 14,
            Tooltip = "FrySharp & Automation Hub",
            CustomControlFactory = () => new Button
            {
                Content = "{ } FrySharp",
                Classes = { "m3-tonal-btn" },
                Command = new RelayCommand(NavigateToStudio)
            }
        });

        var ribbonActionReg = ctx.RegisterRibbonTab(new RibbonTabDescriptor
        {
            Id = "plugins",
            Header = "Plugins",
            Actions = new List<RibbonActionDescriptor>
            {
                new RibbonActionDescriptor
                {
                    Id = "frypdf.ribbon.action.csharpeditor",
                    GroupId = "tools",
                    Label = "FrySharp",
                    Tooltip = "Open full-featured C# development studio and script automation workspace",
                    IconKind = "CodeBraces",
                    Order = 30,
                    Command = new RelayCommand(NavigateToStudio)
                }
            }
        });

        // 6. AI Subsystem: Real-time context provider for host Copilot & external MCP clients
        var aiProviderReg = ctx.RegisterAiContextProvider(new CSharpAiContextProvider(() => GetHostViewModel()));

        // 7. AI Subsystem: Callable tools for autonomous execution, inspection, and patching
        var hostAiTools = new CSharpHostAiTools(() => GetHostViewModel(), NavigateToStudio);
        var aiTools = new List<IDisposable>
        {
            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_get_active_code",
                Description = "Gets the file name, programming language, and complete source code of the active editor document or notebook cell in FrySharp.",
                Category = "C# Development",
                Function = AIFunctionFactory.Create(hostAiTools.GetActiveCode, "csharp_get_active_code", "Gets the file name, programming language, and source code of the active FrySharp document.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_get_diagnostics",
                Description = "Analyzes C# code using Roslyn compiler diagnostics and returns syntax and semantic errors and warnings. Accepts optional code parameter; if omitted, the active document is analyzed.",
                Category = "C# Development",
                Function = AIFunctionFactory.Create(hostAiTools.GetDiagnostics, "csharp_get_diagnostics", "Analyzes C# code using Roslyn compiler diagnostics.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_set_active_code",
                Description = "Updates or replaces the code content in the active FrySharp editor document or notebook cell.",
                Category = "C# Development",
                RequiresConfirmation = false,
                Function = AIFunctionFactory.Create(hostAiTools.SetActiveCode, "csharp_set_active_code", "Updates or replaces the code in the active editor.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_run_script",
                Description = "Executes the active script or notebook cell in FrySharp.",
                Category = "C# Development",
                RequiresConfirmation = false,
                Function = AIFunctionFactory.Create(hostAiTools.RunScript, "csharp_run_script", "Executes the active script or notebook cell in FrySharp.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_stop_script",
                Description = "Halts or interrupts active execution in FrySharp.",
                Category = "C# Development",
                RequiresConfirmation = false,
                Function = AIFunctionFactory.Create(hostAiTools.StopScript, "csharp_stop_script", "Halts or interrupts active script execution.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_get_output",
                Description = "Returns the latest execution console output, status, and compiler diagnostics from the active FrySharp editor or notebook.",
                Category = "C# Development",
                Function = AIFunctionFactory.Create(hostAiTools.GetExecutionOutput, "csharp_get_output", "Returns the latest execution console output, status, and compiler diagnostics.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_execute_code",
                Description = "Executes C# code directly in-memory using Roslyn and returns console output, compilation diagnostics, and execution telemetry.",
                Category = "C# Development",
                RequiresConfirmation = false,
                Function = AIFunctionFactory.Create(hostAiTools.ExecuteCode, "csharp_execute_code", "Executes C# code directly in-memory using Roslyn.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_new_script",
                Description = "Creates a new C# automation script in FrySharp and opens it in Code Studio.",
                Category = "C# Development",
                RequiresConfirmation = false,
                Function = AIFunctionFactory.Create(hostAiTools.NewScript, "csharp_new_script", "Creates a new C# automation script in FrySharp.")
            }),

            ctx.RegisterAiTool(new AiToolDescriptor
            {
                Name = "csharp_open_file",
                Description = "Opens a script or notebook file from disk into FrySharp Code Studio or Notebook Studio.",
                Category = "C# Development",
                RequiresConfirmation = false,
                Function = AIFunctionFactory.Create(hostAiTools.OpenFile, "csharp_open_file", "Opens a script or notebook file from disk into FrySharp.")
            })
        };

        // 8. AI Subsystem: Turnkey Engineering Prompts
        var aiPrompts = new List<IDisposable>
        {
            ctx.RegisterAiPrompt(new AiPromptDescriptor
            {
                Id = "csharp.generate_script",
                Title = "Create Automation Script",
                Description = "Generate a top-level C# automation script with Display(), Dump(), and LINQ.",
                Category = "C# Development",
                Template = "Create a robust, self-contained C# 13 script for FrySharp using top-level statements. Use Display() to show structured outputs or tables and include error handling."
            }),

            ctx.RegisterAiPrompt(new AiPromptDescriptor
            {
                Id = "csharp.fix_diagnostics",
                Title = "Fix Compiler Diagnostics",
                Description = "Diagnose active Roslyn compiler syntax and type errors and provide corrected code.",
                Category = "C# Development",
                Template = "Analyze the active C# compiler diagnostics in FrySharp. Identify why each error or warning occurs and provide a clean, idiomatic fix with minimal code changes."
            }),

            ctx.RegisterAiPrompt(new AiPromptDescriptor
            {
                Id = "csharp.linq_benchmark",
                Title = "Optimize LINQ & Memory",
                Description = "Refactor queries for zero-allocation and high performance using Span and Memory.",
                Category = "C# Development",
                Template = "Review the active C# code and optimize it for high performance and low memory allocation. Propose improvements using Memory<T>, ReadOnlySpan<T>, or optimized LINQ patterns."
            }),

            ctx.RegisterAiPrompt(new AiPromptDescriptor
            {
                Id = "csharp.explain_exception",
                Title = "Diagnose Runtime Exception",
                Description = "Analyze the stack trace and execution output to explain the root cause and remedy.",
                Category = "C# Development",
                Template = "Examine the latest execution failure or exception in the FrySharp script. Explain the root cause of the error and provide the exact patch needed to fix it."
            })
        };

        // 9. Keyboard Shortcuts with explicit Scope and Category
        var shortcuts = new List<IDisposable>
        {
            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.studio.launch",
                Title = "Launch FrySharp",
                Category = "Navigation",
                Description = "Open in-app C# development studio and script automation workspace.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Alt+E" : "Ctrl+Alt+E",
                Scope = ShortcutScope.Global,
                Command = new RelayCommand(NavigateToStudio)
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.run",
                Title = "Run / Debug Script",
                Category = "Code Studio",
                Description = "Execute active C# script or debug at current breakpoint.",
                KeyGesture = "F5",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is not CSharpCodeStudioViewModel codeVm) return;
                    if (codeVm.IsPaused)
                    {
                        codeVm.ContinueDebug();
                    }
                    else if (!codeVm.IsExecuting && !codeVm.IsDebugging)
                    {
                        codeVm.DebugCodeCommand.Execute(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.runWithoutDebug",
                Title = "Run Without Debugging",
                Category = "Code Studio",
                Description = "Execute active C# script or external language program directly without attaching the debugger.",
                KeyGesture = "Ctrl+F5",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is not CSharpCodeStudioViewModel codeVm) return;
                    if (!codeVm.IsExecuting && !codeVm.IsDebugging)
                    {
                        codeVm.RunCodeCommand.Execute(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.stop",
                Title = "Stop Script",
                Category = "Code Studio",
                Description = "Halt currently running C# background script, or interrupt the active notebook cell.",
                KeyGesture = "Shift+F5",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    switch (activeHost?.CurrentPage)
                    {
                        case CSharpCodeStudioViewModel { IsDebugging: true } codeVm:
                            codeVm.StopDebug();
                            break;
                        case CSharpCodeStudioViewModel codeVm:
                            codeVm.StopCommand.Execute(null);
                            break;
                        case CSharpNotebookStudioViewModel nbVm:
                            nbVm.InterruptExecutionCommand.Execute(null);
                            break;
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.save",
                Title = "Save Document",
                Category = "File",
                Description = "Save active C# script or notebook to local workspace storage.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+S" : "Ctrl+S",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    switch (activeHost?.CurrentPage)
                    {
                        case CSharpCodeStudioViewModel codeVm:
                            codeVm.SaveCommand.Execute(null);
                            break;
                        case CSharpNotebookStudioViewModel nbVm:
                            nbVm.SaveCommand.Execute(null);
                            break;
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.new",
                Title = "New Document",
                Category = "File",
                Description = "Create a new notebook when Notebook Studio is active, otherwise a new automation script.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+N" : "Ctrl+N",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
                    {
                        nbVm.NewNotebookTabCommand.Execute(null);
                    }
                    else
                    {
                        var newScriptTask = activeHost?.ManagerViewModel.CreateNewScriptAsync(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.show_references",
                Title = "Toggle References Tab",
                Category = "Code Studio",
                Description = "Switch to References tab and manage NuGet package dependencies.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Shift+P" : "Ctrl+Shift+P",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpCodeStudioViewModel codeVm)
                    {
                        codeVm.SelectedLeftTabIndex = codeVm.SelectedLeftTabIndex == 1 ? 0 : 1;
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.notebook.run_cell",
                Title = "Run Cell and Advance",
                Category = "Notebook",
                Description = "Execute current notebook code cell and advance cursor to next cell.",
                KeyGesture = "Shift+Enter",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
                    {
                        nbVm.RunCellAndSelectNextCommand.Execute(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.notebook.run_cell_stay",
                Title = "Run Cell in Place",
                Category = "Notebook",
                Description = "Execute current notebook cell without moving cursor focus.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Enter" : "Ctrl+Enter",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel { ActiveCell: not null } nbVm)
                    {
                        nbVm.RunSingleCellCommand.Execute(nbVm.ActiveCell);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.notebook.run_all",
                Title = "Run All Cells",
                Category = "Notebook",
                Description = "Restart notebook kernel and execute all cells in sequence.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Shift+Enter" : "Ctrl+Shift+Enter",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
                    {
                        nbVm.RunAllCellsCommand.Execute(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.notebook.insert_cell_below",
                Title = "Insert Cell Below",
                Category = "Notebook",
                Description = "Insert a new C# code cell below current cell.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Shift+B" : "Ctrl+Shift+B",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
                    {
                        nbVm.AddCodeCellCommand.Execute(nbVm.ActiveCell);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.notebook.insert_cell_above",
                Title = "Insert Cell Above",
                Category = "Notebook",
                Description = "Insert a new C# code cell above current cell.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Shift+A" : "Ctrl+Shift+A",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
                    {
                        nbVm.AddCellAboveCommand.Execute(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.notebook.delete_cell",
                Title = "Delete Cell",
                Category = "Notebook",
                Description = "Remove the selected notebook cell from document.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Shift+D" : "Ctrl+Shift+D",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
                    {
                        nbVm.DeleteActiveCellCommand.Execute(null);
                    }
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.zoom_in",
                Title = "Zoom In",
                Category = "View",
                Description = "Increase code canvas and terminal font size.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+=" : "Ctrl+=",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpCodeStudioViewModel codeVm) codeVm.ZoomInCommand.Execute(null);
                    else if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm) nbVm.ZoomInCommand.Execute(null);
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.zoom_out",
                Title = "Zoom Out",
                Category = "View",
                Description = "Decrease code canvas and terminal font size.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+-" : "Ctrl+-",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpCodeStudioViewModel codeVm) codeVm.ZoomOutCommand.Execute(null);
                    else if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm) nbVm.ZoomOutCommand.Execute(null);
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.editor.zoom_reset",
                Title = "Reset Zoom",
                Category = "View",
                Description = "Reset typography to default 100% (13px).",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+0" : "Ctrl+0",
                Scope = Id,
                Command = new RelayCommand(() =>
                {
                    if (activeHost?.CurrentPage is CSharpCodeStudioViewModel codeVm) codeVm.ResetZoomCommand.Execute(null);
                    else if (activeHost?.CurrentPage is CSharpNotebookStudioViewModel nbVm) nbVm.ResetZoomCommand.Execute(null);
                })
            }),

            ctx.RegisterShortcut(new ShortcutDescriptor
            {
                Id = "csharp.studio.reload_customizations",
                Title = "Reload Customizations",
                Category = "Developer Tools",
                Description = "Recompile and apply in-app customization scripts and theme tokens.",
                KeyGesture = OperatingSystem.IsMacOS() ? "Cmd+Shift+R" : "Ctrl+Shift+R",
                Scope = Id,
                Command = new RelayCommand(() => { _ = customizationManager.ReloadAsync(); })
            })
        };

        // Programs the studio starts (Python runs and notebook kernels) end with it: when the app exits, and when the plugin
        // is unloaded.
        var killProcessesOnExit = ProcessRegistry.KillAllOnExit();

        ctx.RegisterEffect(() =>
        {
            if (activeHost != null)
            {
                if (CSharpStudioHostViewModel.ActiveInstance == activeHost)
                {
                    CSharpStudioHostViewModel.ActiveInstance = null;
                }
                activeHost.Dispose();
                activeHost = null;
            }
            killProcessesOnExit.Dispose();
            ProcessRegistry.KillAll();
            navReg.Dispose();
            cmdReg.Dispose();
            reloadCustomizationsCmd.Dispose();
            openInitScriptCmd.Dispose();
            customizationManager.Dispose();
            extensionManager.Dispose();
            statusReg.Dispose();
            ribbonActionReg.Dispose();
            aiProviderReg.Dispose();
            foreach (var t in aiTools) t.Dispose();
            foreach (var p in aiPrompts) p.Dispose();
            foreach (var s in shortcuts) s.Dispose();
        });

        return Task.CompletedTask;
    }
}
