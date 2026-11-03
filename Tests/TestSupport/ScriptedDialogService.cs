using PdfEditorApp.Plugins.CSharpEditor.Services.Common;

namespace CSharpEditorPlugin.Tests.TestSupport;

/// <summary>A studio dialog that answers every question with the next scripted answer, and remembers what it was asked.</summary>
public sealed class ScriptedDialogService : IStudioDialogService
{
    private readonly Queue<string?> _answers;

    public ScriptedDialogService(params string?[] answers) => _answers = new Queue<string?>(answers);

    public List<DialogRequest> Asked { get; } = new();

    public Task<string?> ShowChoiceAsync(DialogRequest request)
    {
        Asked.Add(request);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : StudioQuestions.Cancel);
    }
}
