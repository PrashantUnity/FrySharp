using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Display;
using PdfEditorApp.Plugins.CSharpEditor.Services.Kernels;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Storage;

/// <summary>The formats a notebook file can be in.</summary>
public enum NotebookFileKind
{
    /// <summary>FrySharp's own notebook (<c>.frynb</c>, and the older <c>.csnb</c>): every output kept.</summary>
    Frynb,

    /// <summary>A Jupyter notebook (<c>.ipynb</c>, nbformat 4), as JupyterLab, VS Code and GitHub read it.</summary>
    Ipynb,
}

/// <summary>
/// Reads and writes notebook files, the same for every host. A <c>.frynb</c> is written with the same options the desktop
/// always used, so a notebook saved here is byte for byte what the desktop writes. A <c>.ipynb</c> stays a Jupyter notebook
/// when saved: code, Markdown, text, image and HTML outputs and each cell's language survive; Jupyter metadata FrySharp
/// has no use for (cell tags, widget state) does not.
/// </summary>
public static partial class NotebookFileFormat
{
    /// <summary>How <c>.frynb</c> files are written. Changing it rewrites every notebook a user saves.</summary>
    public static JsonSerializerOptions FrynbOptions { get; } = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions IpynbOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>True for a file a notebook is kept in.</summary>
    public static bool IsNotebookPath(string? path) =>
        Path.GetExtension(path ?? string.Empty).ToLowerInvariant() is ".frynb" or ".csnb" or ".ipynb";

    /// <summary>The format of the notebook file at <paramref name="path"/>, from its extension.</summary>
    public static NotebookFileKind KindOf(string path) =>
        string.Equals(Path.GetExtension(path), ".ipynb", StringComparison.OrdinalIgnoreCase) ? NotebookFileKind.Ipynb : NotebookFileKind.Frynb;

    /// <summary>Reads a notebook file's text in the format its extension names.</summary>
    /// <exception cref="JsonException">The text is not a notebook of that format.</exception>
    public static NotebookDocumentItem Read(string path, string text, ILanguageCatalog languages)
    {
        var notebook = KindOf(path) == NotebookFileKind.Ipynb ? ReadIpynb(text, languages) : ReadFrynb(text);
        if (KindOf(path) == NotebookFileKind.Ipynb)
        {
            notebook.Id = Path.GetFileName(path);
        }
        if (string.IsNullOrWhiteSpace(notebook.Title) || notebook.Title == new NotebookDocumentItem().Title)
        {
            notebook.Title = Path.GetFileNameWithoutExtension(path);
        }
        return notebook;
    }

    /// <summary>A notebook as the text of a file of <paramref name="kind"/>.</summary>
    public static string Write(NotebookDocumentItem notebook, NotebookFileKind kind, ILanguageCatalog languages) =>
        kind == NotebookFileKind.Ipynb ? WriteIpynb(notebook, languages) : WriteFrynb(notebook);

    public static NotebookDocumentItem ReadFrynb(string json) =>
        JsonSerializer.Deserialize<NotebookDocumentItem>(json, FrynbOptions)
        ?? throw new JsonException("The file holds no notebook.");

    public static string WriteFrynb(NotebookDocumentItem notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        return JsonSerializer.Serialize(notebook, FrynbOptions);
    }

    // ── Jupyter → FrySharp ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads a Jupyter notebook (nbformat 4).</summary>
    /// <exception cref="JsonException">The text is not a Jupyter notebook.</exception>
    public static NotebookDocumentItem ReadIpynb(string json, ILanguageCatalog languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        try
        {
            return ReadIpynbCore(json, languages);
        }
        catch (InvalidOperationException ex)
        {
            // A part of the wrong shape (a list where an object belongs).
            throw new JsonException($"The file is not a valid Jupyter notebook: {ex.Message}", ex);
        }
    }

    private static NotebookDocumentItem ReadIpynbCore(string json, ILanguageCatalog languages)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("A Jupyter notebook is a JSON object.");
        if (root["cells"] is not JsonArray cells) throw new JsonException("A Jupyter notebook has a \"cells\" list.");

        var metadata = root["metadata"] as JsonObject;
        var defaultLanguage = NotebookLanguage(metadata, languages);
        var notebook = new NotebookDocumentItem { Kernel = defaultLanguage?.Id ?? LanguageIds.CSharp };

        foreach (var node in cells.OfType<JsonObject>())
        {
            var type = node["cell_type"].AsText();
            var cell = new NotebookCellItem
            {
                Type = type == "code" ? CellType.Code : CellType.Markdown,
                Source = JoinText(node["source"]),
            };
            if (node["id"].AsText() is { Length: > 0 } id) cell.Id = id;

            if (cell.Type == CellType.Code)
            {
                if (node["execution_count"] is JsonValue count && count.TryGetValue<int>(out var executionCount))
                {
                    cell.ExecutionCount = executionCount;
                }

                var cellLanguage = CellKernel(node["metadata"] as JsonObject, languages);
                if (cellLanguage != null && !string.Equals(cellLanguage.Id, notebook.Kernel, StringComparison.OrdinalIgnoreCase))
                {
                    cell.Language = cellLanguage.Id;
                }

                if (node["outputs"] is JsonArray outputs) ReadOutputs(outputs, cell);
            }

            notebook.Cells.Add(cell);
        }

        notebook.ExecutionCount = notebook.Cells.Max(c => c.ExecutionCount) ?? 0;
        return notebook;
    }

    // The notebook's language: Polyglot Notebooks' default kernel, then the kernelspec, then language_info.
    private static ILanguageEngine? NotebookLanguage(JsonObject? metadata, ILanguageCatalog languages)
    {
        if (metadata == null) return null;
        var polyglotDefault = metadata["polyglot_notebook"]?["kernelInfo"]?["defaultKernelName"].AsText();
        return Find(languages, polyglotDefault)
               ?? Find(languages, metadata["kernelspec"]?["language"].AsText())
               ?? Find(languages, metadata["language_info"]?["name"].AsText())
               ?? Find(languages, metadata["kernelspec"]?["name"].AsText());
    }

    // A cell's own kernel, as Polyglot Notebooks (and .NET Interactive before it) wrote it.
    private static ILanguageEngine? CellKernel(JsonObject? metadata, ILanguageCatalog languages) =>
        Find(languages, metadata?["polyglot_notebook"]?["kernelName"].AsText())
        ?? Find(languages, metadata?["dotnet_interactive"]?["language"].AsText());

    // A language named the Jupyter way ("python", "python3", "C#", ".net-csharp") or the FrySharp way ("csharp").
    private static ILanguageEngine? Find(ILanguageCatalog languages, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (languages.FindLanguage(name) is { } named) return named;
        return languages.NotebookCellLanguages.FirstOrDefault(l => l.Jupyter is { } j &&
            (string.Equals(j.LanguageName, name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(j.KernelLanguage, name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(j.KernelName, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static void ReadOutputs(JsonArray outputs, NotebookCellItem cell)
    {
        var text = new StringBuilder();
        foreach (var output in outputs.OfType<JsonObject>())
        {
            switch (output["output_type"].AsText())
            {
                case "stream":
                    text.Append(JoinText(output["text"]));
                    if (output["name"].AsText() == "stderr") cell.HasError = true;
                    break;

                case "execute_result":
                case "display_data":
                    if (output["data"] is not JsonObject data) break;
                    if (cell.ImageBytes == null && TryImage(data, out var bytes, out var format))
                    {
                        cell.ImageBytes = bytes;
                        cell.ImageFormat = format;
                    }
                    else if (cell.HtmlContent == null && data["text/html"] is { } html)
                    {
                        cell.HtmlContent = JoinText(html);
                    }
                    else if (cell.HtmlContent == null && data["text/markdown"] is { } markdown)
                    {
                        var markdownText = JoinText(markdown);
                        cell.MarkdownContent = markdownText;
                        cell.HtmlContent = MarkdownOutput.ToBasicHtml(markdownText);
                    }
                    else if (data["text/plain"] is { } plain)
                    {
                        AppendLine(text, JoinText(plain));
                    }
                    break;

                case "error":
                    cell.HasError = true;
                    if (output["traceback"] is JsonArray traceback && traceback.Count > 0)
                    {
                        foreach (var line in traceback) AppendLine(text, AnsiEscape().Replace(line.AsText() ?? string.Empty, string.Empty));
                    }
                    else
                    {
                        AppendLine(text, $"{output["ename"].AsText()}: {output["evalue"].AsText()}");
                    }
                    break;
            }
        }

        cell.OutputText = text.ToString();
    }

    private static bool TryImage(JsonObject data, out byte[] bytes, out string format)
    {
        foreach (var (mime, name) in new[] { ("image/png", "PNG"), ("image/jpeg", "JPEG"), ("image/gif", "GIF") })
        {
            if (data[mime] is { } node)
            {
                try
                {
                    bytes = Convert.FromBase64String(JoinText(node).Replace("\n", string.Empty));
                    format = name;
                    return true;
                }
                catch (FormatException)
                {
                    // Not base64: try the next image type, then text.
                }
            }
        }

        bytes = [];
        format = string.Empty;
        return false;
    }

    // Jupyter keeps multi-line text as a string or as a list of lines (each but the last ending in "\n").
    private static string JoinText(JsonNode? node) => node switch
    {
        JsonArray lines => string.Concat(lines.Select(l => l.AsText())),
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => string.Empty
    };

    private static void AppendLine(StringBuilder text, string line)
    {
        if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
        text.Append(line);
    }

    // A string value, or null for anything else: a malformed notebook loses that value instead of failing to open.
    private static string? AsText(this JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    // ── FrySharp → Jupyter ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A notebook as a Jupyter notebook (nbformat 4.5), for JupyterLab, VS Code and GitHub. A notebook of several
    /// languages is written as Polyglot Notebooks does: the kernel of each cell in its metadata.
    /// </summary>
    public static string WriteIpynb(NotebookDocumentItem notebook, ILanguageCatalog languages)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        ArgumentNullException.ThrowIfNull(languages);
        var defaultLanguage = DefaultLanguage(notebook, languages);
        var used = notebook.Cells.Where(c => c.Type == CellType.Code)
            .Select(c => CellLanguage(c, defaultLanguage, languages))
            .DistinctBy(l => l.Id)
            .ToList();
        var polyglot = used.Count > 1;

        var cells = new List<object>();
        foreach (var cell in notebook.Cells)
        {
            var sourceLines = SplitToLinesWithNewlines(cell.Source);
            if (cell.Type == CellType.Markdown)
            {
                cells.Add(new
                {
                    cell_type = "markdown",
                    id = cell.Id,
                    metadata = new Dictionary<string, object>(),
                    source = sourceLines
                });
                continue;
            }

            var metadata = new Dictionary<string, object>();
            if (polyglot) metadata["polyglot_notebook"] = new { kernelName = CellLanguage(cell, defaultLanguage, languages).Id };
            cells.Add(new
            {
                cell_type = "code",
                execution_count = cell.ExecutionCount,
                id = cell.Id,
                metadata,
                outputs = WriteOutputs(cell),
                source = sourceLines
            });
        }

        // The notebook's kernel: its default language's, or the one language all its cells are in.
        var main = (used.Count == 1 ? used[0] : defaultLanguage).Jupyter
                   ?? languages.FindLanguage(LanguageIds.CSharp)?.Jupyter
                   ?? new JupyterLanguageInfo(".net-csharp", ".NET (C#)", "C#", "csharp", ".cs", "text/x-csharp", "13.0");
        var notebookMetadata = new Dictionary<string, object>
        {
            ["language_info"] = main.Version != null
                ? new { name = main.LanguageName, version = main.Version, file_extension = main.FileExtension, mimetype = main.MimeType }
                : new { name = main.LanguageName, file_extension = main.FileExtension, mimetype = main.MimeType },
            ["kernelspec"] = new { name = main.KernelName, display_name = main.KernelDisplayName, language = main.KernelLanguage }
        };
        if (polyglot)
        {
            notebookMetadata["polyglot_notebook"] = new
            {
                kernelInfo = new
                {
                    defaultKernelName = defaultLanguage.Id,
                    items = used.Select(l => new { name = l.Id, languageName = l.Jupyter?.LanguageName ?? l.Id, aliases = l.Aliases }).ToList()
                }
            };
        }

        return JsonSerializer.Serialize(new { cells, metadata = notebookMetadata, nbformat = 4, nbformat_minor = 5 }, IpynbOptions);
    }

    private static List<object> WriteOutputs(NotebookCellItem cell)
    {
        var outputs = new List<object>();
        if (!string.IsNullOrEmpty(cell.OutputText))
        {
            outputs.Add(new
            {
                output_type = "stream",
                name = cell.HasError ? "stderr" : "stdout",
                text = SplitToLinesWithNewlines(cell.OutputText)
            });
        }

        if (cell.ImageBytes is { Length: > 0 } image)
        {
            var mime = (cell.ImageFormat ?? "PNG").ToUpperInvariant() switch
            {
                "JPEG" or "JPG" => "image/jpeg",
                "GIF" => "image/gif",
                _ => "image/png"
            };
            outputs.Add(new
            {
                output_type = "display_data",
                data = new Dictionary<string, object> { [mime] = Convert.ToBase64String(image) },
                metadata = new Dictionary<string, object>()
            });
        }

        // A Markdown output is written as its Markdown, which Jupyter renders itself.
        if (!string.IsNullOrEmpty(cell.MarkdownContent))
        {
            outputs.Add(new
            {
                output_type = "display_data",
                data = new Dictionary<string, object> { ["text/markdown"] = SplitToLinesWithNewlines(cell.MarkdownContent) },
                metadata = new Dictionary<string, object>()
            });
        }
        else if (!string.IsNullOrEmpty(cell.HtmlContent))
        {
            outputs.Add(new
            {
                output_type = "display_data",
                data = new Dictionary<string, object> { ["text/html"] = SplitToLinesWithNewlines(cell.HtmlContent) },
                metadata = new Dictionary<string, object>()
            });
        }

        return outputs;
    }

    // The notebook's default language (NotebookDocumentItem.Kernel), C# when it names none the host knows.
    private static ILanguageEngine DefaultLanguage(NotebookDocumentItem notebook, ILanguageCatalog languages) =>
        languages.FindLanguage(notebook.Kernel)
        ?? languages.FindLanguage(LanguageIds.CSharp)
        ?? languages.NotebookCellLanguages.FirstOrDefault()
        ?? throw new InvalidOperationException("No notebook language is registered.");

    // A code cell's language: its #!python (or other) directive, its own, or the notebook's.
    private static ILanguageEngine CellLanguage(NotebookCellItem cell, ILanguageEngine defaultLanguage, ILanguageCatalog languages)
    {
        var own = languages.FindLanguage(cell.Language) ?? defaultLanguage;
        return languages.FindLanguage(NotebookCellDirectives.LanguageOf(cell.Source, languages, own.Id)) ?? own;
    }

    private static string[] SplitToLinesWithNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        // Jupyter's multiline strings: every line keeps its '\n' except the last, and text ending in a newline has no empty
        // last line ("hi\n" is ["hi\n"], not ["hi\n", ""]).
        var rawLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var count = rawLines[^1].Length == 0 ? rawLines.Length - 1 : rawLines.Length;
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = i < rawLines.Length - 1 ? rawLines[i] + "\n" : rawLines[i];
        }
        return result;
    }
}
