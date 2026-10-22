# CSharpPlayground (FrySharp)

[![CI](https://github.com/PrashantUnity/CSharpPlayground/actions/workflows/ci.yml/badge.svg)](https://github.com/PrashantUnity/CSharpPlayground/actions/workflows/ci.yml)
[![Microsoft Store](https://img.shields.io/badge/Microsoft_Store-FrySharp-0078D4?logo=microsoft&logoColor=white)](https://apps.microsoft.com/detail/9mx55lk7c843?hl=en-US&gl=IN)
[![Homebrew](https://img.shields.io/badge/Homebrew_Cask-frysharp-FBB040?logo=homebrew&logoColor=black)](https://github.com/PrashantUnity/homebrew-tap)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-12.1.2-red.svg)](https://avaloniaui.net/)
[![Sponsor](https://img.shields.io/badge/Sponsor-%E2%9D%A4-ea4aaa?logo=github-sponsors&logoColor=white)](https://github.com/sponsors/PrashantUnity)

**CSharpPlayground** (standalone executable: **FrySharp**) is an interactive multi-language code studio, polyglot Jupyter-style notebook environment for .NET 10, Python, Rust, and extensible language toolchains built with Avalonia UI.

It operates both as:
1. **A Standalone Desktop Application (`FrySharp`)**: Native desktop app for macOS (`.dmg`) and Windows (`.msix` / `.exe` installer) with VS Code-inspired layout.
2. **A FryPDF Ecosystem Plugin (`com.frypdf.plugin.csharpeditor`)**: Edge-to-edge full-viewport workspace and code automation plugin for FryPDF.

---

## 🎬 Product Demo

[![FrySharp Demo: Run C#, Python, Rust, Go, Java, C++, JS, F# & SQL in ONE Unified Notebook & IDE](Assets/frysharp-thumbnail.jpg)](https://www.youtube.com/watch?v=g1Y4RSxxqIc)

**[Click here to watch the FrySharp Demo on YouTube](https://www.youtube.com/watch?v=g1Y4RSxxqIc)** — *Supports C#, Python, Rust, Go, Java, C++, JavaScript, F# & SQL*

---

## 📦 Installation

### macOS (via Homebrew)
```bash
# Add the tap and install FrySharp
brew install --cask prashantunity/tap/frysharp
```

To update when a new version is released:
```bash
brew upgrade --cask frysharp
```

### Windows (via Microsoft Store)
Get FrySharp from the official Microsoft Store:
- **[Download on Microsoft Store](https://apps.microsoft.com/detail/9mx55lk7c843?hl=en-US&gl=IN)**

---

## 🚀 Quick Start (Build from Source)

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: [Python 3.9+](https://www.python.org/downloads/) for `.py` files and Python notebook cells

### Run Standalone Desktop App (FrySharp)
```bash
# Clone the repository
git clone https://github.com/PrashantUnity/CSharpPlayground.git
cd CSharpPlayground

# Run standalone desktop app
dotnet run --project Runner/CSharpEditorPlugin.Runner.csproj
```

### Run Automated Unit Tests
```bash
# Run the unit tests (the real-Python ones run when Python 3.9+ is installed)
dotnet test CSharpEditorPlugin.slnx
```

---


## 🏗 Architecture: Stateful Notebook Execution

```mermaid
flowchart TD
    subgraph Notebook UI
        C1["Cell 1: var m = 10;"]
        C2["Cell 2: Console.WriteLine(m * 2);"]
        C3["Cell 3: Display.Image(surface.Snapshot());"]
    end

    subgraph "NotebookExecutionKernel (Persistent Session)"
        Init["ScriptOptions\n(System Refs + NuGet Refs + Usings)"]
        S0["Submission #0: ScriptState\n(Creates 'm' field, Output = 10)"]
        S1["Submission #1: ScriptState.ContinueWithAsync\n(References S0, Reads 'm', Output = 20)"]
        S2["Submission #2: ScriptState.ContinueWithAsync\n(References S1, Rich Media Display)"]
        VarExp["Variable Inspector State\n[m : int = 10]"]
    end

    subgraph Rich Cell Output
        Out1["Text Output / Badge"]
        Out2["Console Output: 20"]
        Out3["Avalonia Image Control\n(Zoom, Copy, Save PNG)"]
    end

    C1 -->|"Execute Cell"| S0
    S0 -->|"State Chaining"| S1
    C2 -->|"Execute Cell"| S1
    S1 -->|"State Chaining"| S2
    C3 -->|"Execute Cell"| S2
    
    S0 --> VarExp
    S1 --> VarExp
    S2 --> VarExp

    S0 --> Out1
    S1 --> Out2
    S2 --> Out3
```

---

## 🤖 Model Context Protocol (MCP) Integration

FrySharp provides 9 native tools to autonomous AI agents over the Model Context Protocol (MCP) for direct code execution, compiler error diagnosis, editor injection, and live debugging.

See the complete documentation:
- 📖 [MCP Integration Guide](docs/mcp/README.md)
- 🛠️ [MCP Tools Reference](docs/mcp/tools-reference.md)
- ⚡ [Interactive Workflows & Client Examples](docs/mcp/interactive-workflows.md)

---

## 💖 Sponsor & Support

[![How Your Sponsorship Works: A Scientific Flowchart](Assets/sponsorship-flowchart.jpg)](https://github.com/sponsors/PrashantUnity)

> **The Scientific Sponsorship Loop:**
> 1. **You Fund** 💸 ➔ 2. **I Buy Food** 🍕 ➔ 3. **Eat & Stay Healthy** 🍜 ➔ 4. **Code & Ship Features** 🚀
> *(Alternative timeline: No food ➔ Dev dies 💀 ➔ Zero commits 🪦)*

If you find **FrySharp** (CSharpPlayground) helpful, consider keeping the developer fueled and alive:

👉 **[Sponsor @PrashantUnity on GitHub](https://github.com/sponsors/PrashantUnity)**

---

## 📜 License
MIT License. See [LICENSE](LICENSE) for details.
