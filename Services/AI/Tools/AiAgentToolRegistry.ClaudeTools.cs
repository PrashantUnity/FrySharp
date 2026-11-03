using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using PdfEditorApp.Plugins.CSharpEditor.Models.AI;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;
using PdfEditorApp.Plugins.CSharpEditor.Services.Processes;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.AI.Tools;

public partial class AiAgentToolRegistry
{
    private const int MaxToolOutputChars = 4000;
    private const int MaxGrepMatches = 50;

    [Description("Executes a shell command in the workspace directory (e.g. dotnet build, dotnet test, git, python) with timeout and output capture.")]
    public async Task<string> RunCommand(
        [Description("Shell command line to execute.")] string command,
        [Description("Optional working directory relative to workspace root (defaults to workspace root).")] string? directory = null,
        [Description("Timeout in seconds (default 60, maximum 300).")] int timeoutSeconds = 60)
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        var targetDir = string.IsNullOrWhiteSpace(directory) ? root : ResolvePath(directory);

        if (!Directory.Exists(targetDir))
        {
            return $"Error: Working directory does not exist: {targetDir}";
        }

        int clampedTimeout = Math.Clamp(timeoutSeconds, 1, 300);
        var step = new AgentStepItem
        {
            Title = $"Run: {command}",
            Status = AgentStepStatus.Running,
            ToolName = "run_command"
        };
        OnStepUpdate?.Invoke(step);

        var sw = Stopwatch.StartNew();
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        string shell;
        string[] args;

        if (OperatingSystem.IsWindows())
        {
            shell = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            args = ["/c", command];
        }
        else
        {
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
            args = ["-c", command];
        }

        try
        {
            var spec = new ProcessStartSpec
            {
                FileName = shell,
                Arguments = args,
                WorkingDirectory = targetDir
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(clampedTimeout));
            using var proc = _processLauncher.Start(
                spec,
                outChunk => { lock (stdoutBuilder) { stdoutBuilder.Append(outChunk); } },
                errChunk => { lock (stderrBuilder) { stderrBuilder.Append(errChunk); } });

            var exitCode = await proc.Completion.WaitAsync(cts.Token);
            sw.Stop();

            step.Status = exitCode == 0 ? AgentStepStatus.Completed : AgentStepStatus.Failed;
            step.Duration = sw.Elapsed;
            OnStepUpdate?.Invoke(step);

            var stdout = stdoutBuilder.ToString();
            var stderr = stderrBuilder.ToString();

            var combinedOutput = new StringBuilder();
            combinedOutput.AppendLine($"Exit Code: {exitCode} (Duration: {sw.ElapsedMilliseconds}ms)");

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                combinedOutput.AppendLine("Standard Output:");
                combinedOutput.AppendLine(TruncateOutput(stdout));
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                combinedOutput.AppendLine("Standard Error:");
                combinedOutput.AppendLine(TruncateOutput(stderr));
            }

            return combinedOutput.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            step.Status = AgentStepStatus.Failed;
            OnStepUpdate?.Invoke(step);
            return $"Command timed out after {clampedTimeout} seconds: {command}";
        }
        catch (Exception ex)
        {
            sw.Stop();
            step.Status = AgentStepStatus.Failed;
            OnStepUpdate?.Invoke(step);
            return $"Error executing command '{command}': {ex.Message}";
        }
    }

    [Description("Discovers files across the workspace matching a wildcard or glob pattern (e.g. **/*.cs, *.py, src/**/*.json), ignoring build and cache folders.")]
    public string GlobFiles(
        [Description("Glob pattern (e.g. **/*.cs, *.py, Tests/**).")] string pattern,
        [Description("Subdirectory path to search from, or empty for workspace root.")] string? directory = null)
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        var targetDir = string.IsNullOrWhiteSpace(directory) ? root : ResolvePath(directory);

        if (!Directory.Exists(targetDir)) return $"Directory not found: {targetDir}";

        try
        {
            var matchedFiles = new List<string>();
            foreach (var filePath in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                if (IsIgnoredPath(filePath)) continue;

                var relPath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
                if (MatchesGlob(relPath, pattern))
                {
                    matchedFiles.Add(relPath);
                    if (matchedFiles.Count >= 100) break;
                }
            }

            if (matchedFiles.Count == 0)
            {
                return $"No files matched pattern '{pattern}' in {targetDir}.";
            }

            var result = new StringBuilder();
            result.AppendLine($"Matched {matchedFiles.Count} file(s) for '{pattern}':");
            foreach (var file in matchedFiles)
            {
                result.AppendLine(file);
            }

            if (matchedFiles.Count >= 100)
            {
                result.AppendLine("[Results capped at first 100 files; refine pattern if needed]");
            }

            return result.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Glob error: {ex.Message}";
        }
    }

    [Description("Fast regex or text search across workspace files, returning line numbers and matching snippets.")]
    public string GrepSearch(
        [Description("Search query or regular expression pattern.")] string query,
        [Description("Optional relative subdirectory or specific file to search.")] string? path = null,
        [Description("Optional file pattern filter (e.g. *.cs, *.py).")] string? filePattern = null,
        [Description("Whether matching should be case sensitive.")] bool caseSensitive = false)
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        var targetPath = string.IsNullOrWhiteSpace(path) ? root : ResolvePath(path);

        if (File.Exists(targetPath))
        {
            return SearchSingleFile(targetPath, root, query, caseSensitive);
        }

        if (!Directory.Exists(targetPath))
        {
            return $"Path not found: {targetPath}";
        }

        var matches = new List<string>();
        Regex? regex = null;
        try
        {
            var regexOptions = RegexOptions.Compiled | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = new Regex(query, regexOptions);
        }
        catch
        {
            // Fallback to literal search if regex is invalid
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        try
        {
            var pattern = string.IsNullOrWhiteSpace(filePattern) ? "*.*" : filePattern;
            foreach (var file in Directory.EnumerateFiles(targetPath, pattern, SearchOption.AllDirectories))
            {
                if (IsIgnoredPath(file)) continue;

                var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
                int lineNum = 1;

                foreach (var line in File.ReadLines(file))
                {
                    bool isMatch = regex != null ? regex.IsMatch(line) : line.Contains(query, comparison);
                    if (isMatch)
                    {
                        matches.Add($"{relPath}:{lineNum}: {line.Trim()}");
                        if (matches.Count >= MaxGrepMatches) break;
                    }
                    lineNum++;
                }

                if (matches.Count >= MaxGrepMatches) break;
            }

            if (matches.Count == 0) return $"No matches found for '{query}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {matches.Count} match(es) for '{query}':");
            foreach (var m in matches) sb.AppendLine(m);

            if (matches.Count >= MaxGrepMatches)
            {
                sb.AppendLine($"[Results capped at first {MaxGrepMatches} matches]");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Grep search error: {ex.Message}";
        }
    }

    [Description("Reads workspace architectural rules, agent mandates, and coding guidelines from AGENTS.md, GEMINI.md, CLAUDE.md, or .cursorrules.")]
    public string ReadProjectRules(
        [Description("Optional directory path to read rules from, or empty for workspace root.")] string? directory = null)
    {
        var root = string.IsNullOrWhiteSpace(directory)
            ? (_storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory())
            : ResolvePath(directory);
        var candidates = new[] { "AGENTS.md", "GEMINI.md", "CLAUDE.md", ".cursorrules" };

        var sb = new StringBuilder();
        bool foundAny = false;

        foreach (var candidate in candidates)
        {
            var filePath = Path.Combine(root, candidate);
            if (File.Exists(filePath))
            {
                foundAny = true;
                sb.AppendLine($"=== {candidate} ===");
                var content = File.ReadAllText(filePath);
                sb.AppendLine(content.Length > 4000 ? content[..4000] + "\n...[truncated]..." : content);
                sb.AppendLine();
            }
        }

        var rulesDir = Path.Combine(root, ".agents", "rules");
        if (Directory.Exists(rulesDir))
        {
            foreach (var ruleFile in Directory.EnumerateFiles(rulesDir, "*.md"))
            {
                foundAny = true;
                sb.AppendLine($"=== {Path.GetFileName(ruleFile)} ===");
                var content = File.ReadAllText(ruleFile);
                sb.AppendLine(content.Length > 2000 ? content[..2000] + "\n...[truncated]..." : content);
                sb.AppendLine();
            }
        }

        return foundAny ? sb.ToString().TrimEnd() : "No project rules found in workspace root (checked AGENTS.md, GEMINI.md, CLAUDE.md, .cursorrules).";
    }

    [Description("Gets current git repository status including branch, staged, unstaged, and untracked files.")]
    public async Task<string> GitStatus()
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        var git = _gitService ?? _codeStudioViewModel?.Git.GitService ?? new GitService();

        try
        {
            if (!await git.IsGitInstalledAsync()) return "Git is not installed on this system.";
            if (!await git.IsGitRepositoryAsync(root)) return $"Not a git repository: {root}";

            var status = await git.GetStatusAsync(root);
            var sb = new StringBuilder();
            sb.AppendLine($"Branch: {status.CurrentBranch ?? "DETACHED"} (Ahead: {status.AheadCount}, Behind: {status.BehindCount})");

            if (status.StagedChanges.Count > 0)
            {
                sb.AppendLine($"\nStaged Changes ({status.StagedChanges.Count}):");
                foreach (var s in status.StagedChanges) sb.AppendLine($"  [{s.StatusKind}] {s.RelativePath}");
            }

            var unstaged = status.WorkingChanges.Where(w => w.StatusKind != GitFileStatusKind.Untracked).ToList();
            if (unstaged.Count > 0)
            {
                sb.AppendLine($"\nWorking Tree Changes ({unstaged.Count}):");
                foreach (var u in unstaged) sb.AppendLine($"  [{u.StatusKind}] {u.RelativePath}");
            }

            var untracked = status.WorkingChanges.Where(w => w.StatusKind == GitFileStatusKind.Untracked).ToList();
            if (untracked.Count > 0)
            {
                sb.AppendLine($"\nUntracked Files ({untracked.Count}):");
                foreach (var ut in untracked.Take(25)) sb.AppendLine($"  {ut.RelativePath}");
                if (untracked.Count > 25) sb.AppendLine($"  ... and {untracked.Count - 25} more");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error checking git status: {ex.Message}";
        }
    }

    [Description("Gets git diff for a specific file or the entire repository (staged or unstaged).")]
    public async Task<string> GitDiff(
        [Description("Optional relative file path to diff, or empty for entire repository.")] string? filePath = null,
        [Description("Set to true to inspect staged changes (--cached), false for working tree.")] bool staged = false)
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        var git = _gitService ?? _codeStudioViewModel?.Git.GitService ?? new GitService();

        try
        {
            if (!await git.IsGitRepositoryAsync(root)) return $"Not a git repository: {root}";

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var diff = await git.GetFileDiffAsync(root, filePath, staged);
                return string.IsNullOrWhiteSpace(diff) ? $"No {(staged ? "staged" : "unstaged")} changes in {filePath}." : TruncateOutput(diff);
            }

            var status = await git.GetStatusAsync(root);
            var list = staged ? status.StagedChanges : status.WorkingChanges;
            if (list.Count == 0) return $"No {(staged ? "staged" : "unstaged")} changes in repository.";

            var sb = new StringBuilder();
            foreach (var item in list.Take(10))
            {
                var d = await git.GetFileDiffAsync(root, item.RelativePath, staged);
                if (!string.IsNullOrWhiteSpace(d))
                {
                    sb.AppendLine($"--- {item.RelativePath} ---");
                    sb.AppendLine(d);
                    sb.AppendLine();
                }
            }

            return sb.Length > 0 ? TruncateOutput(sb.ToString()) : "No diff output generated.";
        }
        catch (Exception ex)
        {
            return $"Error getting git diff: {ex.Message}";
        }
    }

    private static string SearchSingleFile(string fullPath, string root, string query, bool caseSensitive)
    {
        var relPath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var matches = new List<string>();
        int lineNum = 1;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var line in File.ReadLines(fullPath))
        {
            if (line.Contains(query, comparison))
            {
                matches.Add($"{relPath}:{lineNum}: {line.Trim()}");
                if (matches.Count >= MaxGrepMatches) break;
            }
            lineNum++;
        }

        return matches.Count > 0 ? string.Join("\n", matches) : $"No matches found for '{query}' in {relPath}.";
    }

    private static bool IsIgnoredPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.git/") ||
               normalized.Contains("/bin/") ||
               normalized.Contains("/obj/") ||
               normalized.Contains("/node_modules/") ||
               normalized.Contains("/.idea/") ||
               normalized.Contains("/.vs/") ||
               normalized.Contains("/.venv/") ||
               normalized.Contains("/__pycache__/");
    }

    private static bool MatchesGlob(string relativePath, string pattern)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        pattern = pattern.Replace('\\', '/').TrimStart('/');

        if (pattern == "*" || pattern == "**" || pattern == "*.*" || pattern == "**/*") return true;

        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";

        return Regex.IsMatch(relativePath, regexPattern, RegexOptions.IgnoreCase);
    }

    private static string TruncateOutput(string output)
    {
        if (output.Length <= MaxToolOutputChars) return output;
        return output[..MaxToolOutputChars] + $"\n[Output truncated to first {MaxToolOutputChars} characters to preserve context]";
    }
}
