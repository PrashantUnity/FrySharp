using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

/// <summary>A button of the dialog on screen.</summary>
public sealed class StudioDialogChoiceViewModel
{
    public StudioDialogChoiceViewModel(DialogChoice choice) => Choice = choice;

    public DialogChoice Choice { get; }
    public string Label => Choice.Label;
    public bool IsPrimary => Choice.Kind == DialogChoiceKind.Primary;
    public bool IsDestructive => Choice.Kind == DialogChoiceKind.Destructive;
    public bool IsCancel => Choice.Kind == DialogChoiceKind.Cancel;
}

/// <summary>
/// The studio's one modal question (<see cref="IStudioDialogService"/>), drawn by <c>StudioDialogOverlayControl</c> over the
/// whole studio. Questions asked while one is showing wait their turn.
/// </summary>
public sealed partial class StudioDialogViewModel : ObservableObject, IStudioDialogService
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private TaskCompletionSource<string?>? _answer;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _details = [];

    [ObservableProperty]
    private IReadOnlyList<StudioDialogChoiceViewModel> _choices = [];

    public bool HasDetails => Details.Count > 0;

    partial void OnDetailsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasDetails));

    public async Task<string?> ShowChoiceAsync(DialogRequest request)
    {
        await _oneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await UiDispatchHelper.InvokeAsync(() =>
            {
                _answer = answer;
                Title = request.Title;
                Message = request.Message;
                Details = request.Details;
                Choices = request.Choices.Select(c => new StudioDialogChoiceViewModel(c)).ToList();
                IsOpen = true;
            }).ConfigureAwait(false);

            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Answers with <paramref name="choice"/> (a button, or Enter on the primary one).</summary>
    [RelayCommand]
    public void Choose(StudioDialogChoiceViewModel? choice)
    {
        if (choice == null || _answer is not { } answer) return;
        _answer = null;
        IsOpen = false;
        answer.TrySetResult(choice.Choice.Id);
    }

    /// <summary>Escape, the close button or a click outside the card: the cancel choice.</summary>
    [RelayCommand]
    public void Dismiss() => Choose(Choices.FirstOrDefault(c => c.IsCancel) ?? Choices.LastOrDefault());

    /// <summary>Enter: the primary choice.</summary>
    public void AcceptDefault() => Choose(Choices.FirstOrDefault(c => c.IsPrimary) ?? Choices.FirstOrDefault());
}
