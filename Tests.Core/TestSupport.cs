using PdfEditorApp.Plugins.CSharpEditor.Services.Display;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Toolchains;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FrySharp.Core.Tests;

/// <summary>A fresh folder under the system temp folder, deleted with the test.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = Directory.CreateTempSubdirectory("frysharp-core-tests-").FullName;
    }

    public string Path { get; }

    public string File(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A process the test started may still hold a file; the OS cleans temp.
        }
    }
}

/// <summary>Runs C# the way a headless host does, collecting what it prints and shows.</summary>
public static class Headless
{
    private static readonly RoslynCompilerService Compiler = new();

    public static async Task<(CSharpRunResult Result, string Output, List<RichCellOutput> Displays)> RunAsync(string code, TimeSpan? timeout = null, CancellationToken ct = default, Action<string>? observe = null)
    {
        var output = new System.Text.StringBuilder();
        var displays = new List<RichCellOutput>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(1));
        var result = await new InProcessCSharpRunner(Compiler).RunAsync(
            code,
            text =>
            {
                lock (output) output.Append(text);
                observe?.Invoke(text);
            },
            display => { lock (displays) displays.Add(display); },
            stdin: null,
            limit.Token);
        return (result, output.ToString(), displays);
    }

    public static IReadOnlyList<PdfEditorApp.Plugins.CSharpEditor.Models.DiagnosticItem> Diagnostics(string code) => Compiler.CheckDiagnostics(code);
}

/// <summary>A test that needs a language's program (Python, Node.js) installed; skipped where it isn't.</summary>
public sealed class ToolchainFactAttribute : FactAttribute
{
    private static readonly LanguageRegistry<ILanguageEngine> Languages =
        HeadlessLanguages.Create(LanguageEngineServices.Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frysharp-core-tests-data")));

    public ToolchainFactAttribute(string languageId)
    {
        var toolchain = Languages.Get(languageId)?.Toolchain;
        var found = toolchain?.ResolveAsync(new ToolchainQuery()).GetAwaiter().GetResult().Toolchain;
        if (found == null) Skip = $"Needs {languageId} installed.";
    }

    public static LanguageRegistry<ILanguageEngine> Registry => Languages;
}
