# FrySharp MCP Interactive Workflows

This guide provides end-to-end code examples and interaction patterns for using FrySharp's MCP tools from automated agents, IDE extensions, and client scripts.

---

## Workflow 1: Autonomous Code Diagnosis & Auto-Fix Loop

Agents can read code from Code Studio, inspect Roslyn compiler errors, generate the correct fix, update the editor, and run the script:

```
┌─────────────────┐       ┌──────────────────────┐       ┌─────────────────┐
│ Get Active Code │ ────▶ │ Get Diagnostics (CS*)│ ────▶ │ AI Computes Fix │
└─────────────────┘       └──────────────────────┘       └────────┬────────┘
                                                                  │
┌─────────────────┐       ┌──────────────────────┐                │
│ Check Output OK │ ◀──── │  Run Script & Debug  │ ◀──────────────┘
└─────────────────┘       └──────────────────────┘   Set Active Code
```

### Python Implementation:

```python
import json
import urllib.request
import time

MCP_URL = "http://localhost:5242/sse"

def call_tool(tool_name, arguments=None):
    payload = {
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": arguments or {}
        },
        "id": 1
    }
    req = urllib.request.Request(
        MCP_URL,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req) as resp:
        res = json.loads(resp.read().decode("utf-8"))
        raw = res["result"]["content"][0]["text"]
        return json.loads(raw)

# 1. Inspect what the user has in Code Studio
active_doc = call_tool("csharp_get_active_code")
print(f"Active Document: {active_doc.get('Title')}")
code = active_doc.get("Code", "")

# 2. Analyze with Roslyn compiler
diagnostics = call_tool("csharp_get_diagnostics", {"code": code})
if diagnostics.get("ErrorCount", 0) > 0:
    for diag in diagnostics["Diagnostics"]:
        print(f"Error [{diag['Id']}]: {diag['Message']} at line {diag['Line']}")
    
    # 3. Apply corrected code
    fixed_code = code.replace("string text = 12345;", "string text = \"12345\";")
    call_tool("csharp_set_active_code", {"newCode": fixed_code})
    print("Fixed code injected into Code Studio.")

# 4. Run the script inside the studio
call_tool("csharp_run_script")
time.sleep(1.5)

# 5. Fetch output
output = call_tool("csharp_get_output")
print("Execution Output:\n", output.get("Output"))
```

---

## Workflow 2: TypeScript / Node.js Client with MCP SDK

Using `@modelcontextprotocol/sdk`:

```typescript
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { SSEClientTransport } from "@modelcontextprotocol/sdk/client/sse.js";

async function main() {
  const transport = new SSEClientTransport(new URL("http://localhost:5242/sse"));
  const client = new Client({ name: "NodeAgent", version: "1.0.0" }, { capabilities: {} });

  await client.connect(transport);

  // 1. Discover all FrySharp and FryWorld tools
  const tools = await client.listTools();
  console.log("Discovered tools:", tools.tools.map(t => t.name));

  // 2. Execute C# code via Roslyn in memory
  const execResult = await client.callTool({
    name: "csharp_execute_code",
    arguments: {
      code: `
        var fib = new List<int> { 0, 1 };
        for (int i = 2; i < 10; i++) fib.Add(fib[i-1] + fib[i-2]);
        Console.WriteLine("Fibonacci: " + string.Join(", ", fib));
      `
    }
  });

  console.log("Result:", execResult.content[0].text);
}

main().catch(console.error);
```

---

## Workflow 3: Multi-Studio Orchestration (Circuit + C#)

In FryWorld, agents can orchestrate multiple studios simultaneously:

1. **Simulate a circuit in Circuit Studio**:
   - Call `circuit_load_preset` with `"555 Timer Oscillator"`.
   - Call `circuit_step_simulation` to run 100 time-steps.
   - Call `circuit_get_readings` to extract numerical voltage readings across nodes.

2. **Process readings in FrySharp C# Studio**:
   - Call `fryworld_navigate` to `"com.fryworld.csharpstudio"`.
   - Call `csharp_new_script` with a LINQ script that analyzes the waveform harmonics.
   - Call `csharp_run_script` to plot FFT or summarize output.
