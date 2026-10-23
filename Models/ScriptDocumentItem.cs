using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;

namespace PdfEditorApp.Plugins.CSharpEditor.Models;

public class ScriptDocumentItem
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Type { get; set; } = "script";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Script";
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string ExecutionMode { get; set; } = "Statements"; // Statements, Program, Expression
    public string Code { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<string> References { get; set; } = new();
    public List<TestCaseItem> TestCases { get; set; } = new();
    public List<int> Breakpoints { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public int ExecutionCount { get; set; }

    /// <summary>
    /// The document's language (<see cref="LanguageIds"/>). Set by storage from the file it was read from, never saved:
    /// a .frycs document is always C#, a source file's extension says what it is.
    /// </summary>
    [JsonIgnore]
    public string LanguageId { get; set; } = LanguageIds.CSharp;

    /// <summary>The plain source file this document is (e.g. /work/main.py), or null for a .frycs document.</summary>
    [JsonIgnore]
    public string? SourceFilePath { get; set; }

    /// <summary>True when this document is an unsaved scratchpad, starter template, or practice problem that should not auto-save to disk on execution.</summary>
    [JsonIgnore]
    public bool IsEphemeral { get; set; }

    /// <summary>True when this document is read-only (e.g. diff viewers or historical snapshots) and must never overwrite files on disk.</summary>
    [JsonIgnore]
    public bool IsReadOnly { get; set; }
}
