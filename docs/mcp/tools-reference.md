# FrySharp MCP Tools Reference

This document provides complete technical specifications, JSON schemas, parameters, and request/response examples for all 9 MCP tools exposed by FrySharp.

---

## 1. `csharp_execute_code`

Executes arbitrary C# code in-memory using Roslyn and returns standard output, compilation errors, and execution metrics.

### Use When:
- Running standalone calculations or benchmarks without opening an editor window.
- Testing expressions, algorithms, LINQ queries, or utility methods autonomously.
- Running unit assertions or verifying API behaviors.

### Parameters:
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `code` | `string` | **Yes** | — | The C# code (top-level statements, expressions, or full classes) to compile and execute. |
| `mode` | `string` | No | `"Statements"` | Execution mode: `"Statements"`, `"Expression"`, or `"Program"`. |

### Response Structure:
```json
{
  "Success": true,
  "Phase": "Execution",
  "Output": "Computed result or console stream",
  "Error": "",
  "DurationMs": 12.35,
  "WasCancelled": false
}
```

### Example:
**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "csharp_execute_code",
    "arguments": {
      "code": "int a = 15;\nint b = 27;\nConsole.WriteLine($\"Sum: {a + b}\");\nreturn a + b;"
    }
  },
  "id": 101
}
```
**Response:**
```json
{
  "jsonrpc": "2.0",
  "id": 101,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"Success\":true,\"Phase\":\"Execution\",\"Output\":\"Sum: 42\",\"Error\":\"\",\"DurationMs\":7.51,\"WasCancelled\":false}"
      }
    ],
    "isError": false
  }
}
```

---

## 2. `csharp_get_diagnostics`

Runs full Roslyn compiler diagnostics (syntax trees + semantic model) on source code without executing it.

### Parameters:
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `code` | `string` | No | `null` | Optional C# code to analyze. If omitted or null, analyzes the active document in Code Studio. |

### Response Structure:
```json
{
  "TotalCount": 1,
  "ErrorCount": 1,
  "WarningCount": 0,
  "Diagnostics": [
    {
      "Id": "CS0029",
      "Severity": "Error",
      "Message": "Cannot implicitly convert type 'int' to 'string'",
      "Line": 1,
      "Column": 15
    }
  ]
}
```

### Example:
**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "csharp_get_diagnostics",
    "arguments": {
      "code": "string badAssignment = 12345;"
    }
  },
  "id": 102
}
```

---

## 3. `csharp_get_active_code`

Retrieves the document title, language ID, file path, and source code of the active tab in Code Studio or the active cell in Notebook Studio.

### Parameters:
*None (`{}`)*

### Response Structure:
```json
{
  "Type": "Script",
  "Title": "MainScript.csx",
  "Language": "csharp",
  "FilePath": "/path/to/script.csx",
  "Code": "Console.WriteLine(\"Active code\");",
  "IsExecuting": false,
  "IsPaused": false
}
```

---

## 4. `csharp_set_active_code`

Replaces the source code in the active Code Studio editor or active Notebook cell.

### Parameters:
| Parameter | Type | Required | Description |
|---|---|---|---|
| `newCode` | `string` | **Yes** | The new code content to set into the active editor. |

### Response:
```json
{
  "Success": true,
  "Message": "Active script code updated successfully."
}
```

---

## 5. `csharp_new_script`

Creates a new script document in FrySharp and opens Code Studio in the workspace.

### Parameters:
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `title` | `string` | No | `"Script.csx"` | The filename/title for the document tab. |
| `initialCode` | `string` | No | Boilerplate | Initial C# code to populate the new script. |

### Response:
```json
{
  "Success": true,
  "Title": "Automation.csx",
  "Language": "csharp",
  "Message": "Created and opened script in Code Studio."
}
```

---

## 6. `csharp_open_file`

Opens a local script (`.cs`, `.csx`) or notebook (`.dib`, `.ipynb`) from disk into FrySharp.

### Parameters:
| Parameter | Type | Required | Description |
|---|---|---|---|
| `filePath` | `string` | **Yes** | Absolute file system path to the script or notebook file. |

### Response:
```json
{
  "Success": true,
  "Type": "Notebook",
  "FilePath": "/Users/user/Notebooks/DataScience.dib",
  "Title": "DataScience"
}
```

---

## 7. `csharp_run_script`

Starts execution of the active script or notebook cell in the studio, attaching interactive debugging and real-time console capture.

### Parameters:
*None (`{}`)*

### Response:
```json
{
  "Success": true,
  "Message": "Execution started in Code Studio."
}
```

---

## 8. `csharp_stop_script`

Interrupts and stops active script execution or notebook evaluation.

### Parameters:
*None (`{}`)*

### Response:
```json
{
  "Success": true,
  "Message": "Execution stopped."
}
```

---

## 9. `csharp_get_output`

Fetches real-time execution status, compiler status, stdout, stderr, and debugger trace from the studio console.

### Parameters:
*None (`{}`)*

### Response Structure:
```json
{
  "Type": "Script",
  "IsExecuting": false,
  "IsDebugging": false,
  "IsPaused": false,
  "CompilerStatus": "Completed",
  "Output": "🐞 Starting interactive C# debugging session...\n[Debug] C# debug session attached.\nOutput: 42\n🏁 Debugging finished in 850 ms"
}
```
