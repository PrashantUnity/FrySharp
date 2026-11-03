using System.Diagnostics;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio.Explorer;

/// <summary>
/// When an Explorer lists the workspace, the same for every studio. Never while the studio is being built: that ran on
/// the engine's start-up path and read every document while the Hub was reading them too. Instead:
/// <list type="bullet">
/// <item>on the page's first visit;</item>
/// <item>when the host lists it in the background once the engine is warm;</item>
/// <item>when the workspace changed (<see cref="RefreshIfStaleAsync"/>);</item>
/// <item>when asked (<see cref="RefreshAsync"/>: the Refresh button, another folder opened).</item>
/// </list>
/// The newest listing wins: one that finishes after a later one began is dropped, so a slow listing of the folder just
/// left never lands over the new one. A staleness check that comes while a listing runs waits for it and then lists only
/// if the workspace changed meanwhile, so the background listing and a first visit never read the workspace twice
/// (concurrent listings at the same versions also share one walk in the storage).
/// </summary>
/// <param name="storage">The workspace.</param>
/// <param name="activities">Where a slow listing shows (a line in the Explorer) and a failed one is reported.</param>
/// <param name="show">Draws a listing. Runs where the listing was asked for (the UI thread in the studio), one at a time.</param>
public sealed class ExplorerRefresher(IScriptStorageService storage, IActivityService activities, Action<WorkspaceListing> show)
{
    private readonly Lock _gate = new();
    private long _generation;
    private Task _running = Task.CompletedTask;

    // The storage's StructureVersion the tree was last drawn at (-1: never).
    private long _listedVersion = -1;

    /// <summary>True when the tree shows the workspace as it is now (its files and folders).</summary>
    public bool IsCurrent => Interlocked.Read(ref _listedVersion) == storage.StructureVersion;

    /// <summary>Lists the workspace now and draws it, whether or not it changed.</summary>
    public Task RefreshAsync() => StartListing(reuseCurrent: false);

    private Task StartListing(bool reuseCurrent)
    {
        long generation;
        lock (_gate) generation = ++_generation;

        var listing = ListAsync(generation, reuseCurrent);
        lock (_gate)
        {
            if (generation == _generation) _running = listing;
        }
        return listing;
    }

    /// <summary>Lists the workspace only if it changed since the tree was drawn, or never was. True when it listed.</summary>
    public async Task<bool> RefreshIfStaleAsync()
    {
        Task running;
        lock (_gate) running = _running;
        await running;

        if (IsCurrent) return false;
        // Stale only because this tree is behind: a listing the other studio just made of the same workspace will do.
        await StartListing(reuseCurrent: true);
        return true;
    }

    private async Task ListAsync(long generation, bool reuseCurrent)
    {
        using var activity = activities.Start(new ActivityOptions("Listing the workspace", ActivityLocation.Explorer));
        try
        {
            // Taken before the walk: a change that lands while it runs leaves the tree stale, so the next check lists again.
            var version = storage.StructureVersion;
            var listing = await storage.LoadExplorerListingAsync(reuseCurrent);
            lock (_gate)
            {
                if (generation != _generation) return;
                show(listing);
                Interlocked.Exchange(ref _listedVersion, version);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Listing the workspace for the Explorer failed: {ex}");
            activity.Fail(ex);
        }
    }
}
