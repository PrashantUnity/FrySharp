# FrySharp Model Context Protocol (MCP) Integration

FrySharp exposes its full development studio, compiler diagnostics pipeline, interactive debugging runtime, and Roslyn script execution engine to AI agents and external tools via the **Model Context Protocol (MCP)**.

When hosted inside **FryWorld**, FrySharp's tools are dynamically discovered and registered into the host AI subsystem and exposed over high-performance HTTP/SSE and JSON-RPC 2.0 endpoints.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                 MCP Clients & AI Agents                     │
│  (Antigravity IDE, Claude Desktop, Cursor, Custom Scripts)  │
└──────────────────────────────┬──────────────────────────────┘
                               │ JSON-RPC 2.0 / HTTP SSE
                               ▼
┌─────────────────────────────────────────────────────────────┐
│                    FryWorld Host Process                    │
│   ┌──────────────────────────────────────────────────────┐  │
│   │   McpHttpSseTransport (http://localhost:5242/sse)    │  │
│   └──────────────────────────┬───────────────────────────┘  │
│                              │ Dispatches JSON-RPC requests │
│                              ▼                              │
│   ┌──────────────────────────────────────────────────────┐  │
│   │         McpServer (Tool & Prompt Registry)           │  │
│   └────────────┬─────────────────────────────┬───────────┘  │
└────────────────┼─────────────────────────────┼──────────────┘
                 │                             │
                 ▼                             ▼
┌─────────────────────────────────┐   ┌───────────────────────┐
│     FrySharp Plugin (2.0.6)     │   │   FryCircuit Plugin   │
│   ┌──────────────────────────┐  │   │  (16 Circuit Tools)   │
│   │    CSharpHostAiTools     │  │   └───────────────────────┘
│   ├──────────────────────────┤  │
│   │ • Roslyn Execution Engine│  │
│   │ • Live Diagnostics CS*   │  │
│   │ • Code Studio Editor     │  │
│   │ • Interactive Debugger   │  │
│   └──────────────────────────┘  │
└─────────────────────────────────┘
```

---

## Server Endpoints

The default FryWorld MCP server listens on port `5242`:

| Endpoint | Method | Description |
|---|---|---|
| `http://localhost:5242/sse` | `GET` | SSE event stream. Sends `event: endpoint` handshake and streams execution events. |
| `http://localhost:5242/sse` | `POST` | Direct JSON-RPC 2.0 command dispatch. Responds immediately with JSON result. |
| `http://localhost:5242/message` | `POST` | JSON-RPC message endpoint for established SSE sessions (`?sessionId=...`). |

The runtime discovery file is maintained automatically at `~/.fryworld/mcp.json`.

---

## Client Configuration

### 1. Antigravity IDE (`~/.gemini/config/mcp_config.json`)
```json
{
  "mcpServers": {
    "fryworld": {
      "serverUrl": "http://localhost:5242/sse"
    }
  }
}
```

### 2. Claude Desktop (`claude_desktop_config.json`)
```json
{
  "mcpServers": {
    "frysharp": {
      "command": "curl",
      "args": ["-s", "-X", "POST", "http://localhost:5242/sse"]
    }
  }
}
```
*Alternatively, if running FryWorld in headless stdio bridge mode:*
```json
{
  "mcpServers": {
    "frysharp": {
      "command": "/path/to/FryWorld",
      "args": ["--mcp-stdio"]
    }
  }
}
```

### 3. Cursor (`.cursor/mcp.json`)
```json
{
  "mcpServers": {
    "fryworld": {
      "url": "http://localhost:5242/sse"
    }
  }
}
```

---

## Available FrySharp Tools (9 Tools)

| Tool Name | Scope | Description |
|---|---|---|
| [`csharp_execute_code`](tools-reference.md#1-csharp_execute_code) | In-Memory Engine | Compiles & executes arbitrary C# expressions, statements, or classes via Roslyn. |
| [`csharp_get_diagnostics`](tools-reference.md#2-csharp_get_diagnostics) | Compiler Pipeline | Analyzes code for syntax and semantic errors/warnings without running it. |
| [`csharp_get_active_code`](tools-reference.md#3-csharp_get_active_code) | Studio Editor | Reads current code, language, file path, and title from the active studio tab. |
| [`csharp_set_active_code`](tools-reference.md#4-csharp_set_active_code) | Studio Editor | Updates/replaces source code in the active editor document or notebook cell. |
| [`csharp_new_script`](tools-reference.md#5-csharp_new_script) | Studio Workspace | Creates a new automation script (`.csx`) in FrySharp and opens Code Studio. |
| [`csharp_open_file`](tools-reference.md#6-csharp_open_file) | File System | Opens a script (`.csx`) or notebook (`.dib`, `.ipynb`) from disk. |
| [`csharp_run_script`](tools-reference.md#7-csharp_run_script) | Studio Runtime | Launches execution of the active script with interactive debugging. |
| [`csharp_stop_script`](tools-reference.md#8-csharp_stop_script) | Studio Runtime | Halts or interrupts running script execution. |
| [`csharp_get_output`](tools-reference.md#9-csharp_get_output) | Studio Console | Retrieves compiler status, debugger trace, stdout/stderr, and timing. |

---

## Quick Example: Calling via cURL

### Execute C# Code via Roslyn:
```bash
curl -s -X POST http://localhost:5242/sse \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "csharp_execute_code",
      "arguments": {
        "code": "var names = new[] { \"Alpha\", \"Beta\", \"Gamma\" }; Console.WriteLine(string.Join(\", \", names));"
      }
    },
    "id": 1
  }'
```

### Response:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"Success\":true,\"Phase\":\"Execution\",\"Output\":\"Alpha, Beta, Gamma\",\"Error\":\"\",\"DurationMs\":8.42,\"WasCancelled\":false}"
      }
    ],
    "isError": false
  }
}
```

---

## Further Reading
- [Detailed Tool Reference](tools-reference.md): Complete parameter tables, input schemas, and error codes.
- [Interactive Workflows](interactive-workflows.md): Multi-step agent repair loops, notebook manipulation, and debugging automation.
