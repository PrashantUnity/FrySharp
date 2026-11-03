using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using PdfEditorApp.Plugins.CSharpEditor.Models;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Languages.Lsp;

/// <summary>
/// Lightweight, zero-dependency JSON-RPC 2.0 client for communicating with external
/// Language Server Protocol (LSP) binaries over standard input/output.
/// </summary>
public sealed class LspClient : IAsyncDisposable, IDisposable
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly string? _workingDirectory;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    private Process? _process;
    private Stream? _stdin;
    private CancellationTokenSource? _readCts;
    private Task? _readLoopTask;
    private int _nextId;
    private bool _initialized;
    private int _disposed;

    public event Action<string, IReadOnlyList<DiagnosticItem>>? DiagnosticsReceived;
    public event Action<string>? LogReceived;

    public bool IsRunning => _process is { HasExited: false };

    public LspClient(string command, IEnumerable<string>? args = null, string? workingDirectory = null)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _args = args != null ? new List<string>(args) : Array.Empty<string>();
        _workingDirectory = workingDirectory;
    }

    public async Task<bool> StartAsync(string rootPath, CancellationToken ct = default)
    {
        if (IsRunning) return true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _command,
                WorkingDirectory = _workingDirectory ?? rootPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in _args)
            {
                psi.ArgumentList.Add(arg);
            }

            _process = Process.Start(psi);
            if (_process == null) return false;

            _stdin = _process.StandardInput.BaseStream;
            _readCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_process.StandardOutput.BaseStream, _readCts.Token));

            // Forward stderr to logs
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = _process.StandardError;
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (!string.IsNullOrEmpty(line))
                        {
                            LogReceived?.Invoke($"[LSP stderr] {line}");
                        }
                    }
                }
                catch { }
            });

            // Send initialize request
            var rootUri = new Uri(rootPath).AbsoluteUri;
            var initParams = new
            {
                processId = Environment.ProcessId,
                rootUri,
                capabilities = new
                {
                    textDocument = new
                    {
                        completion = new { completionItem = new { snippetSupport = true } },
                        hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                        publishDiagnostics = new { relatedInformation = true }
                    }
                }
            };

            // A big project can take a server a while to load: starting up gets longer than an ordinary request.
            var initResult = await SendRequestAsync("initialize", initParams, ct, TimeSpan.FromSeconds(60));
            await SendNotificationAsync("initialized", new { });
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"[LSP Error] Failed to start LSP server '{_command}': {ex.Message}");
            return false;
        }
    }

    public async Task DidOpenAsync(string filePath, string languageId, string text)
    {
        if (!IsRunning || !_initialized) return;

        var uri = new Uri(filePath).AbsoluteUri;
        await SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                languageId,
                version = 1,
                text
            }
        });
    }

    public async Task DidChangeAsync(string filePath, int version, string text)
    {
        if (!IsRunning || !_initialized) return;

        var uri = new Uri(filePath).AbsoluteUri;
        await SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new object[]
            {
                new { text }
            }
        });
    }

    public async Task DidCloseAsync(string filePath)
    {
        if (!IsRunning || !_initialized) return;

        var uri = new Uri(filePath).AbsoluteUri;
        await SendNotificationAsync("textDocument/didClose", new
        {
            textDocument = new { uri }
        });
    }

    public async Task<IReadOnlyList<CSharpCompletionItem>> GetCompletionsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken ct = default)
    {
        if (!IsRunning || !_initialized) return Array.Empty<CSharpCompletionItem>();

        try
        {
            var uri = new Uri(filePath).AbsoluteUri;
            var result = await SendRequestAsync("textDocument/completion", new
            {
                textDocument = new { uri },
                position = new { line, character }
            }, ct);

            return ParseCompletionResult(result);
        }
        catch
        {
            return Array.Empty<CSharpCompletionItem>();
        }
    }

    public async Task<Controls.Editor.LanguageQuickInfoHit?> GetHoverAsync(
        string filePath,
        int line,
        int character,
        int offset,
        CancellationToken ct = default)
    {
        if (!IsRunning || !_initialized) return null;

        try
        {
            var uri = new Uri(filePath).AbsoluteUri;
            var result = await SendRequestAsync("textDocument/hover", new
            {
                textDocument = new { uri },
                position = new { line, character }
            }, ct);

            return ParseHoverResult(result, offset);
        }
        catch
        {
            return null;
        }
    }

    // A server that is busy or wedged must not keep a caller (a hover, a completion list) waiting forever.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (_stdin == null) throw new InvalidOperationException("The language server isn't running.");

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? RequestTimeout);
        using var reg = limit.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(id, out var removed))
            {
                removed.TrySetCanceled(limit.Token);
            }
        });

        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        };

        await SendMessageAsync(payload);
        return await tcs.Task;
    }

    private async Task SendNotificationAsync(string method, object? @params)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            method,
            @params
        };

        await SendMessageAsync(payload);
    }

    private async Task SendMessageAsync(object payload)
    {
        if (_stdin == null) return;

        string json = JsonSerializer.Serialize(payload);
        byte[] contentBytes = Encoding.UTF8.GetBytes(json);
        string header = $"Content-Length: {contentBytes.Length}\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        lock (_writeLock)
        {
            _stdin.Write(headerBytes, 0, headerBytes.Length);
            _stdin.Write(contentBytes, 0, contentBytes.Length);
            _stdin.Flush();
        }

        await Task.CompletedTask;
    }

    private async Task ReadLoopAsync(Stream stdout, CancellationToken ct)
    {
        try
        {
            await ReadMessagesAsync(stdout, ct);
        }
        finally
        {
            // The server is gone (it exited, or its output closed): nobody will ever answer what is still waiting.
            foreach (var id in _pendingRequests.Keys.ToList())
            {
                if (_pendingRequests.TryRemove(id, out var waiting)) waiting.TrySetException(new IOException("The language server stopped."));
            }
        }
    }

    private async Task ReadMessagesAsync(Stream stdout, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int contentLength = -1;
                while (true)
                {
                    string? headerLine = ReadAsciiLine(stdout);
                    if (headerLine == null) return; // EOF
                    if (headerLine.Length == 0) break; // End of headers

                    if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        var lengthStr = headerLine["Content-Length:".Length..].Trim();
                        _ = int.TryParse(lengthStr, out contentLength);
                    }
                }

                if (contentLength <= 0) continue;

                byte[] buffer = new byte[contentLength];
                int totalRead = 0;
                while (totalRead < contentLength)
                {
                    int bytesRead = await stdout.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
                    if (bytesRead == 0) return; // EOF
                    totalRead += bytesRead;
                }

                string json = Encoding.UTF8.GetString(buffer);
                DispatchMessage(json);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[LSP Read Error] {ex.Message}");
                break;
            }
        }
    }

    private static string? ReadAsciiLine(Stream stream)
    {
        var sb = new StringBuilder();
        int prev = -1;
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) return sb.Length > 0 ? sb.ToString() : null;

            if (b == '\n')
            {
                if (prev == '\r' && sb.Length > 0)
                {
                    sb.Length--; // Remove \r
                }
                return sb.ToString();
            }

            sb.Append((char)b);
            prev = b;
        }
    }

    private void DispatchMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("id", out var idElem) && idElem.TryGetInt32(out int id))
            {
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var errorElem))
                    {
                        tcs.TrySetException(new InvalidOperationException($"LSP Error: {errorElem.GetRawText()}"));
                    }
                    else if (root.TryGetProperty("result", out var resultElem))
                    {
                        tcs.TrySetResult(resultElem.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(root);
                    }
                }
            }
            else if (root.TryGetProperty("method", out var methodElem))
            {
                string method = methodElem.GetString() ?? string.Empty;
                if (method == "textDocument/publishDiagnostics" && root.TryGetProperty("params", out var paramsElem))
                {
                    HandleDiagnostics(paramsElem);
                }
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"[LSP Parse Error] {ex.Message}");
        }
    }

    private void HandleDiagnostics(JsonElement paramsElem)
    {
        if (!paramsElem.TryGetProperty("uri", out var uriElem) ||
            !paramsElem.TryGetProperty("diagnostics", out var diagsElem)) return;

        string uriStr = uriElem.GetString() ?? string.Empty;
        var list = new List<DiagnosticItem>();

        if (diagsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in diagsElem.EnumerateArray())
            {
                string message = d.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                int line = 1, col = 1, endLine = 1, endCol = 1;

                if (d.TryGetProperty("range", out var range))
                {
                    if (range.TryGetProperty("start", out var start))
                    {
                        line = (start.TryGetProperty("line", out var l) ? l.GetInt32() : 0) + 1;
                        col = (start.TryGetProperty("character", out var c) ? c.GetInt32() : 0) + 1;
                    }
                    if (range.TryGetProperty("end", out var end))
                    {
                        endLine = (end.TryGetProperty("line", out var el) ? el.GetInt32() : 0) + 1;
                        endCol = (end.TryGetProperty("character", out var ec) ? ec.GetInt32() : 0) + 1;
                    }
                }

                int sevInt = d.TryGetProperty("severity", out var s) ? s.GetInt32() : 1;
                var severity = sevInt switch
                {
                    1 => DiagnosticSeverity.Error,
                    2 => DiagnosticSeverity.Warning,
                    3 => DiagnosticSeverity.Info,
                    _ => DiagnosticSeverity.Hidden
                };

                list.Add(new DiagnosticItem
                {
                    Id = "LSP",
                    Message = message,
                    Line = line,
                    Column = col,
                    EndLine = endLine,
                    EndColumn = endCol,
                    Severity = severity
                });
            }
        }

        DiagnosticsReceived?.Invoke(uriStr, list);
    }

    private static IReadOnlyList<CSharpCompletionItem> ParseCompletionResult(JsonElement result)
    {
        var items = new List<CSharpCompletionItem>();
        JsonElement array = default;

        if (result.ValueKind == JsonValueKind.Array)
        {
            array = result;
        }
        else if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            array = itemsProp;
        }

        if (array.ValueKind != JsonValueKind.Array) return items;

        foreach (var item in array.EnumerateArray())
        {
            string label = item.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(label)) continue;

            string insertText = item.TryGetProperty("insertText", out var it) ? it.GetString() ?? label : label;
            string? detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
            string? doc = null;
            if (item.TryGetProperty("documentation", out var docElem))
            {
                doc = docElem.ValueKind == JsonValueKind.String
                    ? docElem.GetString()
                    : docElem.TryGetProperty("value", out var v) ? v.GetString() : null;
            }

            int kindInt = item.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
            var kind = MapCompletionKind(kindInt);

            items.Add(new CSharpCompletionItem
            {
                DisplayText = label,
                InsertionText = insertText,
                Kind = kind,
                Signature = detail,
                Documentation = doc
            });
        }

        return items;
    }

    private static Controls.Editor.LanguageQuickInfoHit? ParseHoverResult(JsonElement result, int offset)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
        {
            return null;
        }

        string text = string.Empty;
        if (contents.ValueKind == JsonValueKind.String)
        {
            text = contents.GetString() ?? string.Empty;
        }
        else if (contents.ValueKind == JsonValueKind.Object && contents.TryGetProperty("value", out var val))
        {
            text = val.GetString() ?? string.Empty;
        }
        else if (contents.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var c in contents.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String) sb.AppendLine(c.GetString());
                else if (c.TryGetProperty("value", out var v)) sb.AppendLine(v.GetString());
            }
            text = sb.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        return new Controls.Editor.LanguageQuickInfoHit(
            SpanStart: offset,
            SpanLength: 1,
            Signature: text,
            Summary: string.Empty);
    }

    private static CompletionItemKind MapCompletionKind(int lspKind) => lspKind switch
    {
        1 => CompletionItemKind.Snippet,   // Text
        2 => CompletionItemKind.Method,    // Method
        3 => CompletionItemKind.Method,    // Function
        4 => CompletionItemKind.Method,    // Constructor
        5 => CompletionItemKind.Field,     // Field
        6 => CompletionItemKind.Variable,  // Variable
        7 => CompletionItemKind.Class,     // Class
        8 => CompletionItemKind.Interface, // Interface
        9 => CompletionItemKind.Namespace, // Module
        10 => CompletionItemKind.Property, // Property
        13 => CompletionItemKind.Enum,     // Enum
        14 => CompletionItemKind.Keyword,  // Keyword
        15 => CompletionItemKind.Snippet,  // Snippet
        22 => CompletionItemKind.Struct,   // Struct
        _ => CompletionItemKind.Method
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _readCts?.Cancel();
            if (IsRunning)
            {
                try
                {
                    await SendRequestAsync("shutdown", new { });
                    await SendNotificationAsync("exit", new { });
                }
                catch { }

                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _readCts?.Dispose();
        }
    }

    public void Dispose()
    {
        _ = DisposeAsync().AsTask();
    }
}
