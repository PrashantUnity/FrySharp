using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace PdfEditorApp.Plugins.CSharpEditor.Runner;

public partial class App : Application
{
    public override void Initialize()
    {
        // See the matching handler in the hosted FryPDF app's App.axaml.cs for why this is
        // needed: raw input is dispatched via Dispatcher.Send, which only offers this event
        // as a way to intercept an exception thrown while handling a key/pointer event before
        // it aborts the whole process.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Debug.WriteLine($"[Dispatcher.UnhandledException] {e.Exception}");
            e.Handled = true;
            ReportToTheUser(e.Exception);
        };

        AvaloniaXamlLoader.Load(this);
    }

    // Kept from ending the app, but never silent: a failed menu action (an open, a Save As) shows as an error toast.
    private static bool _reporting;

    private static void ReportToTheUser(Exception error)
    {
        if (_reporting || PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common.CSharpStudioHostViewModel.ActiveInstance?.Activities is not { } activities) return;
        _reporting = true;
        try
        {
            using var activity = activities.Start(new PdfEditorApp.Plugins.CSharpEditor.Services.Activities.ActivityOptions("Something went wrong"));
            activity.Fail(error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dispatcher.UnhandledException] Couldn't report it: {ex}");
        }
        finally
        {
            _reporting = false;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void AboutMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var about = new AboutWindow();
            about.OpenDocumentationAction = () =>
            {
                if (desktop.MainWindow is MainWindow mw && mw.StudioHostVm is { } host)
                {
                    host.NavigateToDocs();
                }
            };
            about.ShowDialog(desktop.MainWindow);
        }
    }

    private void SettingsMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow mw &&
            mw.StudioHostVm is { } host)
        {
            host.NavigateToSettings();
        }
    }

    private void CustomizationMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow mw &&
            mw.StudioHostVm is { } host)
        {
            host.NavigateToSettings("Customization");
        }
    }

    private async void CheckForUpdatesMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var updateDialog = new UpdateDialogWindow();
            await updateDialog.ShowDialog(desktop.MainWindow);
        }
    }
}
