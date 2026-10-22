using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Toolchains;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Settings;

public partial class CSharpSettingsViewModel
{
    private int _isRefreshingAll;

    [ObservableProperty]
    private LanguageSettingItemViewModel? _selectedLanguage;

    public ObservableCollection<LanguageSettingItemViewModel> Languages { get; } = new();
    public ObservableCollection<LanguageSettingItemViewModel> FilteredLanguages { get; } = new();

    private void InitializeLanguages()
    {
        SynchronizeLanguagesFromRegistry();
        _languageServices.Registry.Changed += OnLanguagesRegistryChanged;
        _ = SafeRefreshAllLanguagesAsync();
    }

    private async Task SafeRefreshAllLanguagesAsync()
    {
        try
        {
            await RefreshAllLanguagesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Settings] Background language initialization failed: {ex.Message}");
        }
    }

    private void OnLanguagesRegistryChanged()
    {
        UiDispatchHelper.RunOnUi(() =>
        {
            SynchronizeLanguagesFromRegistry();
        });
    }

    public void SynchronizeLanguagesFromRegistry()
    {
        var registered = _languageServices.Registry.All;
        var existingIds = Languages
            .Where(l => l?.Language?.Id != null)
            .Select(l => l.Language.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove any that are no longer registered
        var toRemove = Languages
            .Where(l => l?.Language?.Id == null || !registered.Any(r => r.IsNamed(l.Language.Id)))
            .ToList();
        foreach (var item in toRemove)
        {
            Languages.Remove(item);
            FilteredLanguages.Remove(item);
        }

        // Add any newly registered
        foreach (var language in registered)
        {
            if (existingIds.Contains(language.Id)) continue;

            IToolchainProvider? dotNetProvider = null;
            if (language.Id == LanguageIds.CSharp)
            {
                dotNetProvider = new PdfEditorApp.Plugins.CSharpEditor.Services.Languages.CSharp.CSharpToolchainProvider(
                    _languageServices.Host, _languageServices.Processes, _languageServices.ToolchainSettings);
            }

            var item = new LanguageSettingItemViewModel(language, provider: language.Toolchain, dotNetProvider: dotNetProvider, parent: this);
            if (item.IsCSharp)
            {
                item.CSharpExecutionEngine = _settingsStore.GetSettings().CSharpExecutionEngine;
            }

            Languages.Add(item);
            FilteredLanguages.Add(item);

            if (item.IsToolchainLanguage)
            {
                _ = RefreshLanguageToolchainAsync(item);
            }
        }

        if (SelectedLanguage == null || !Languages.Contains(SelectedLanguage))
        {
            SelectedLanguage = Languages.FirstOrDefault();
            if (SelectedLanguage != null) SelectedLanguage.IsSelected = true;
        }
    }

    partial void OnSelectedLanguageChanged(LanguageSettingItemViewModel? oldValue, LanguageSettingItemViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }

    [RelayCommand]
    public void SelectLanguageItem(LanguageSettingItemViewModel? item)
    {
        if (item == null) return;
        SelectedLanguage = item;
    }

    [RelayCommand]
    public void SelectCSharpEngine(string? mode)
    {
        if (SelectedLanguage?.IsCSharp == true && !string.IsNullOrWhiteSpace(mode))
        {
            SelectedLanguage.SetCSharpEngine(mode);
            HasPendingChanges = true;
        }
    }

    [RelayCommand]
    public async Task RefreshAllLanguagesAsync()
    {
        if (Interlocked.CompareExchange(ref _isRefreshingAll, 1, 0) != 0) return;

        try
        {
            var targets = Languages.ToArray();
            foreach (var item in targets)
            {
                if (!Languages.Contains(item)) continue;

                if (item.IsToolchainLanguage || item.IsCSharp)
                {
                    try
                    {
                        await RefreshLanguageToolchainAsync(item);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Settings] Failed to refresh toolchain for {item.Language.Id}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshingAll, 0);
        }
    }

    [RelayCommand]
    public async Task RefreshLanguageToolchainAsync(LanguageSettingItemViewModel item)
    {
        var provider = item.Provider ?? item.DotNetProvider;
        if (provider == null) return;

        UiDispatchHelper.RunOnUi(() =>
        {
            item.IsChecking = true;
            item.StatusBadge = "Scanning…";
            item.StatusColor = "#E3B341";
        });

        var query = new ToolchainQuery(null, null);
        try
        {
            provider.Refresh();
            var allFound = await Task.Run(() => provider.ListAsync(query));
            var resolution = await Task.Run(() => provider.ResolveAsync(query));

            UiDispatchHelper.RunOnUi(() =>
            {
                item.UpdateResolution(resolution, allFound);
            });
        }
        catch (Exception ex)
        {
            UiDispatchHelper.RunOnUi(() =>
            {
                item.IsChecking = false;
                item.IsFound = item.IsCSharp;
                item.StatusBadge = "Scan failed";
                item.StatusColor = "#F85149";
                item.MissingTitle = "Failed to inspect environment";
                item.MissingSummary = ex.Message;
            });
        }
    }

    [RelayCommand]
    public void SetAutoDetect(LanguageSettingItemViewModel item)
    {
        item.IsAutoDetect = true;
        item.CustomPath = string.Empty;
        var provider = item.Provider ?? item.DotNetProvider;
        if (provider != null)
        {
            provider.Select(null);
            _languageServices.ToolchainSettings.SetSelectedPath(item.Language.Id, null);
        }
        HasPendingChanges = true;
        _ = RefreshLanguageToolchainAsync(item);
    }

    [RelayCommand]
    public void SetCustomPath(LanguageSettingItemViewModel item)
    {
        item.IsAutoDetect = false;
        if (!string.IsNullOrWhiteSpace(item.CustomPath))
        {
            ApplyCustomPath(item, item.CustomPath);
        }
    }

    public void ApplyCustomPath(LanguageSettingItemViewModel item, string path)
    {
        item.IsAutoDetect = false;
        item.CustomPath = path.Trim();
        var provider = item.Provider ?? item.DotNetProvider;
        if (provider != null)
        {
            provider.Select(item.CustomPath);
            _languageServices.ToolchainSettings.SetSelectedPath(item.Language.Id, item.CustomPath);
        }
        HasPendingChanges = true;
        _ = RefreshLanguageToolchainAsync(item);
    }

    [RelayCommand]
    public void SelectDiscoveredToolchain(ToolchainInfo? info)
    {
        if (SelectedLanguage == null || info == null) return;
        SelectedLanguage.SelectedDiscoveredToolchain = info;
        ApplyCustomPath(SelectedLanguage, info.ExecutablePath);
    }

    [RelayCommand]
    public async Task RunToolchainActionAsync(string? actionId)
    {
        if (SelectedLanguage?.Provider is not { } provider || string.IsNullOrWhiteSpace(actionId)) return;

        SelectedLanguage.IsRunningAction = true;
        SelectedLanguage.ActionStatusMessage = "Running action…";
        SelectedLanguage.ActionOutputLog = string.Empty;

        var query = new ToolchainQuery(null, null);
        void AppendOutput(string text)
        {
            SelectedLanguage.ActionOutputLog += text;
        }

        try
        {
            var result = await Task.Run(() => provider.RunActionAsync(actionId, query, AppendOutput));
            SelectedLanguage.ActionStatusMessage = result.Success ? result.Message : $"⚠️ {result.Message}";
            SelectedLanguage.ActionOutputLog += $"\n[Finished: {result.Message}]\n";
            await RefreshLanguageToolchainAsync(SelectedLanguage);
        }
        catch (Exception ex)
        {
            SelectedLanguage.ActionStatusMessage = $"Action failed: {ex.Message}";
            SelectedLanguage.ActionOutputLog += $"\n[Error: {ex.Message}]\n";
        }
        finally
        {
            SelectedLanguage.IsRunningAction = false;
        }
    }

    private void SaveLanguageSettings()
    {
        foreach (var item in Languages.ToArray())
        {
            var provider = item.Provider ?? item.DotNetProvider;
            if (provider != null)
            {
                var path = item.IsAutoDetect ? null : item.CustomPath;
                provider.Select(path);
                _languageServices.ToolchainSettings.SetSelectedPath(item.Language.Id, path);
            }
            if (item.IsCSharp)
            {
                var settings = _settingsStore.GetSettings();
                settings.CSharpExecutionEngine = item.CSharpExecutionEngine;
                _settingsStore.SaveSettings(settings);
            }
        }
    }

    private void ResetLanguageSettingsToDefaults()
    {
        foreach (var item in Languages.ToArray())
        {
            var provider = item.Provider ?? item.DotNetProvider;
            if (provider != null)
            {
                item.IsAutoDetect = true;
                item.CustomPath = string.Empty;
                provider.Select(null);
                _languageServices.ToolchainSettings.SetSelectedPath(item.Language.Id, null);
                _ = RefreshLanguageToolchainAsync(item);
            }
            if (item.IsCSharp)
            {
                item.CSharpExecutionEngine = "internal";
            }
        }
    }

    private void ApplySearchFilter(string query)
    {
        FilteredLanguages.Clear();
        var trimmed = query?.Trim() ?? string.Empty;
        foreach (var item in Languages.ToArray())
        {
            if (string.IsNullOrEmpty(trimmed) ||
                item.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                item.RuntimeDescription.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                item.ActiveToolchainLabel.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                FilteredLanguages.Add(item);
            }
        }

        if (SelectedLanguage != null && !FilteredLanguages.Contains(SelectedLanguage))
        {
            SelectedLanguage = FilteredLanguages.FirstOrDefault();
        }
        else if (SelectedLanguage == null)
        {
            SelectedLanguage = FilteredLanguages.FirstOrDefault();
        }

        KeymapSearchQuery = trimmed;
    }
}
