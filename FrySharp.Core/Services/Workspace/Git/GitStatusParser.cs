using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Plugins.CSharpEditor.Models.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;

/// <summary>
/// High-performance parser for Git status outputs, supporting porcelain v2 (preferred)
/// and porcelain v1 (fallback).
/// </summary>
public static class GitStatusParser
{
    public static GitRepositoryStatus Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return GitRepositoryStatus.EmptyClean();
        }

        string branch = "HEAD";
        string? upstream = null;
        int ahead = 0;
        int behind = 0;
        var staged = new List<GitFileChange>();
        var working = new List<GitFileChange>();

        var rawLines = output.Split('\n');

        foreach (var rawLine in rawLines)
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;

            // Porcelain v2 Headers
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = line["# branch.head ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line["# branch.upstream ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                ParseAheadBehind(line["# branch.ab ".Length..], out ahead, out behind);
                continue;
            }

            // Porcelain v1 Header Fallback: ## branch...upstream [ahead 1, behind 2]
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseV1BranchHeader(line[3..], out branch, out upstream, out ahead, out behind);
                continue;
            }

            // Porcelain v2: Untracked items
            if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                var path = UnquotePath(line[2..].Trim());
                working.Add(new GitFileChange(path, GitFileStatusKind.Untracked, IsStaged: false));
                continue;
            }

            // Porcelain v2: Ordinary changed entries
            // Format: 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
            if (line.StartsWith("1 ", StringComparison.Ordinal) && line.Length > 4)
            {
                char stagedCode = line[2];
                char workingCode = line[3];

                var path = ExtractPathAfterTokens(line, 8);
                if (string.IsNullOrEmpty(path)) continue;

                if (stagedCode != '.')
                {
                    staged.Add(new GitFileChange(path, MapStatus(stagedCode), IsStaged: true));
                }

                if (workingCode != '.')
                {
                    working.Add(new GitFileChange(path, MapStatus(workingCode), IsStaged: false));
                }

                continue;
            }

            // Porcelain v2: Renamed / copied entries
            // Format: 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <Xscore> <path>\t<origPath>
            if (line.StartsWith("2 ", StringComparison.Ordinal) && line.Length > 4)
            {
                char stagedCode = line[2];
                char workingCode = line[3];

                ExtractPathsFromV2RenamedLine(line, out var path, out var origPath);
                if (string.IsNullOrEmpty(path)) continue;

                if (stagedCode != '.')
                {
                    staged.Add(new GitFileChange(path, GitFileStatusKind.Renamed, IsStaged: true, OldRelativePath: origPath));
                }

                if (workingCode != '.')
                {
                    working.Add(new GitFileChange(path, MapStatus(workingCode), IsStaged: false, OldRelativePath: origPath));
                }

                continue;
            }

            // Porcelain v2: Unmerged / conflict entries
            // Format: u <XY> ... <path> (10 tokens before path)
            if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                var path = ExtractPathAfterTokens(line, 10);
                if (!string.IsNullOrEmpty(path))
                {
                    working.Add(new GitFileChange(path, GitFileStatusKind.Conflicted, IsStaged: false));
                }

                continue;
            }

            // Porcelain v1 fallback lines: XY path
            if (line.Length >= 4 && !line.StartsWith('#'))
            {
                char x = line[0];
                char y = line[1];

                if (x == '?' && y == '?')
                {
                    var p = UnquotePath(line[3..].Trim());
                    working.Add(new GitFileChange(p, GitFileStatusKind.Untracked, IsStaged: false));
                }
                else
                {
                    var p = UnquotePath(line[3..].Trim());
                    if (x != ' ') staged.Add(new GitFileChange(p, MapStatus(x), IsStaged: true));
                    if (y != ' ') working.Add(new GitFileChange(p, MapStatus(y), IsStaged: false));
                }
            }
        }

        return new GitRepositoryStatus
        {
            IsRepository = true,
            CurrentBranch = string.IsNullOrWhiteSpace(branch) ? "HEAD" : branch,
            UpstreamBranch = upstream,
            AheadCount = ahead,
            BehindCount = behind,
            StagedChanges = staged,
            WorkingChanges = working
        };
    }

    private static void ParseAheadBehind(string segment, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;

        var parts = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p.StartsWith('+') && int.TryParse(p[1..], out var a)) ahead = a;
            else if (p.StartsWith('-') && int.TryParse(p[1..], out var b)) behind = b;
        }
    }

    private static void ParseV1BranchHeader(string line, out string branch, out string? upstream, out int ahead, out int behind)
    {
        branch = "HEAD";
        upstream = null;
        ahead = 0;
        behind = 0;

        int dots = line.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0)
        {
            branch = line[..dots].Trim();
            var rest = line[(dots + 3)..].Trim();
            int bracket = rest.IndexOf('[');
            if (bracket >= 0)
            {
                upstream = rest[..bracket].Trim();
                var abPart = rest[(bracket + 1)..].TrimEnd(']');
                var tokens = abPart.Split(',', StringSplitOptions.TrimEntries);
                foreach (var token in tokens)
                {
                    if (token.StartsWith("ahead ", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(token[6..], out var a)) ahead = a;
                    else if (token.StartsWith("behind ", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(token[7..], out var b)) behind = b;
                }
            }
            else
            {
                upstream = rest;
            }
        }
        else
        {
            branch = line.Trim();
        }
    }

    private static string ExtractPathAfterTokens(string line, int tokenCount)
    {
        // Extracts the remaining string after the first N space-separated tokens
        int spaceCount = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ')
            {
                spaceCount++;
                if (spaceCount == tokenCount)
                {
                    return UnquotePath(line[(i + 1)..].Trim());
                }
            }
        }

        return string.Empty;
    }

    private static void ExtractPathsFromV2RenamedLine(string line, out string path, out string? origPath)
    {
        path = string.Empty;
        origPath = null;

        // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <Xscore> <path>\t<origPath>
        int spaceCount = 0;
        int pathStart = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ')
            {
                spaceCount++;
                if (spaceCount == 9)
                {
                    pathStart = i + 1;
                    break;
                }
            }
        }

        if (pathStart >= 0 && pathStart < line.Length)
        {
            var rest = line[pathStart..];
            int tab = rest.IndexOf('\t');
            if (tab >= 0)
            {
                path = UnquotePath(rest[..tab].Trim());
                origPath = UnquotePath(rest[(tab + 1)..].Trim());
            }
            else
            {
                path = UnquotePath(rest.Trim());
            }
        }
    }

    private static GitFileStatusKind MapStatus(char c) => c switch
    {
        'M' => GitFileStatusKind.Modified,
        'A' => GitFileStatusKind.Added,
        'D' => GitFileStatusKind.Deleted,
        'R' => GitFileStatusKind.Renamed,
        '?' => GitFileStatusKind.Untracked,
        'U' or 'u' => GitFileStatusKind.Conflicted,
        _ => GitFileStatusKind.Modified
    };

    /// <summary>
    /// Reads a path as git prints it: plain, or in double quotes with C escapes when it has unusual characters. Git escapes
    /// every byte of a non-ASCII name in octal ("caf\303\251.py" is café.py), so the escapes are bytes of UTF-8.
    /// </summary>
    internal static string UnquotePath(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"') return path;

        var bytes = new List<byte>(path.Length);
        var run = new System.Text.StringBuilder();
        void FlushRun()
        {
            if (run.Length == 0) return;
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(run.ToString()));
            run.Clear();
        }

        var end = path.Length - 1;
        for (var i = 1; i < end; i++)
        {
            var c = path[i];
            if (c != '\\' || i + 1 >= end)
            {
                run.Append(c);
                continue;
            }

            FlushRun();
            var n = path[++i];
            switch (n)
            {
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 't': bytes.Add(9); break;
                case 'n': bytes.Add(10); break;
                case 'v': bytes.Add(11); break;
                case 'f': bytes.Add(12); break;
                case 'r': bytes.Add(13); break;
                case >= '0' and <= '7' when i + 2 < end && path[i + 1] is >= '0' and <= '7' && path[i + 2] is >= '0' and <= '7':
                    bytes.Add((byte)(((n - '0') << 6) | ((path[i + 1] - '0') << 3) | (path[i + 2] - '0')));
                    i += 2;
                    break;
                default:
                    run.Append(n); // \" and \\ stand for themselves
                    break;
            }
        }

        FlushRun();
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
