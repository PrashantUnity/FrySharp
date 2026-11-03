using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.BlindProblems;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Docs;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Hub;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Server;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Settings;
using PdfEditorApp.Plugins.CSharpEditor.Views;

namespace PdfEditorApp.Plugins.CSharpEditor.Runner;

public partial class MainWindow : Window
{
    public CSharpStudioHostViewModel StudioHostVm { get; }
    private readonly MainWindowMenuCoordinator _menuCoordinator;

    private CSharpCodeStudioViewModel? ActiveCodeStudio => StudioHostVm.CurrentPage as CSharpCodeStudioViewModel;
    private CSharpNotebookStudioViewModel? ActiveNotebookStudio => StudioHostVm.CurrentPage as CSharpNotebookStudioViewModel;
    private FryServerStudioViewModel? ActiveServerStudio => StudioHostVm.CurrentPage as FryServerStudioViewModel;
    private CSharpManagerViewModel? ActiveManager => StudioHostVm.CurrentPage as CSharpManagerViewModel;
    private CSharpDocsViewModel? ActiveDocs => StudioHostVm.CurrentPage as CSharpDocsViewModel;
    private CSharpSettingsViewModel? ActiveSettings => StudioHostVm.CurrentPage as CSharpSettingsViewModel;
    private CSharpBlindProblemsViewModel? ActiveBlindProblems => StudioHostVm.CurrentPage as CSharpBlindProblemsViewModel;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(CSharpStudioHostViewModel? hostVm)
    {
        InitializeComponent();

        // The look the user left the studio with, before any page is built and before the customization script runs.
        if (hostVm == null)
        {
            PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Theming.AppearanceRestorer.RestoreFrom(
                null, PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ThemeEngine);
        }

        if (hostVm != null)
        {
            StudioHostVm = hostVm;
        }
        else
        {
            var settingsStore = new StandaloneSettingsStore();
            var sp = new StandaloneServiceProvider(settingsStore);
            StudioHostVm = new CSharpStudioHostViewModel(sp);
        }

        StudioHostVm.RequestClose = () => Close();
        Closing += OnWindowClosing;
        DataContext = StudioHostVm;
        StudioHost.DataContext = StudioHostVm;

        _menuCoordinator = new MainWindowMenuCoordinator(NativeMenu.GetMenu(this) ?? MainWindowMenuCoordinator.CreateDefaultMenu(), StudioHostVm);
        _menuCoordinator.HookActivePageEvents();
        _menuCoordinator.UpdateMenuStates();

        InitializeExtensibility();
    }

    // Set once the user answered the unsaved-changes question, so the second Close() goes through.
    private bool _closeApproved;

    // Quitting with unsaved edits asks first (Save All / Don't Save / Cancel), as VS Code does; the window stays open meanwhile.
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        StudioHostVm.FlushSettings();
        if (_closeApproved || StudioHostVm.UnsavedDocumentTitles.Count == 0) return;

        e.Cancel = true;
        try
        {
            if (!await StudioHostVm.ConfirmCloseStudioAsync()) return;
            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Closing failed: {ex}");
        }
    }

    private void InitializeExtensibility()
    {
        var uiService = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.UiService;
        uiService.ActiveTopLevelResolver = () => this;
        uiService.MainWindowResolver = () => this;
        uiService.DialogsService.PickFileHandler = async (title, exts) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
        uiService.DialogsService.PickFolderHandler = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };

        var customizationManager = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.CustomizationManager;
        var extensionManager = PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.StudioAppContext.Instance.ExtensionManager;
        var activeWorkspace = StudioHostVm?.StorageService?.ActiveWorkspaceRootPath;

        _ = Task.Run(async () =>
        {
            try
            {
                await customizationManager.InitializeAsync(enableHotReload: true);
                var results = await extensionManager.DiscoverAndLoadFromDefaultLocationsAsync(workspacePath: activeWorkspace, enableHotReload: true);
                Console.WriteLine($"[MainWindow] Extensibility loaded {results.Count(r => r.Success)} extensions: {string.Join(", ", results.Where(r => r.Success).Select(r => r.ExtensionId))}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MainWindow] Extensibility initialization error: {ex.Message}");
            }
        });
    }

    // ── Context-Aware Native Menu State Synchronization ──
    public NativeMenuItem? GetSubItem(string menuHeader, string itemHeader, bool exact = false) =>
        _menuCoordinator.GetSubItem(menuHeader, itemHeader, exact);

    public void UpdateMenuStates() => _menuCoordinator.UpdateMenuStates();

}
