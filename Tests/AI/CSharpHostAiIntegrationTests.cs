using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FryWorld.Sdk.Plugins;
using PdfEditorApp.Plugins.CSharpEditor;
using PdfEditorApp.Plugins.CSharpEditor.Services.AI.Providers;
using PdfEditorApp.Plugins.CSharpEditor.Services.AI.Tools;
using Xunit;

namespace CSharpEditorPlugin.Tests.AI;

[Collection(PluginLifecycleCollection.Name)]
public class CSharpHostAiIntegrationTests
{
    [Fact]
    public async Task ApplyAsync_RegistersAiSubsystemComponents()
    {
        var rootCtx = new FryPluginContext();
        var scope = new PluginScope();
        var scopedCtx = rootCtx.CreateScopedContext(scope);

        var plugin = new PdfEditorApp.Plugins.CSharpEditor.CSharpEditorPlugin();
        await plugin.ApplyAsync(scopedCtx);

        // 1. Verify AI Context Provider
        var contextProviders = scopedCtx.GetRegisteredAiContextProviders();
        Assert.NotEmpty(contextProviders);
        Assert.Contains(contextProviders, p => p.PluginId == "com.fryworld.csharpstudio");

        // 2. Verify all 9 AI Tools are registered
        var aiTools = scopedCtx.GetRegisteredAiTools();
        Assert.Equal(9, aiTools.Count);
        Assert.Contains(aiTools, t => t.Name == "csharp_get_active_code");
        Assert.Contains(aiTools, t => t.Name == "csharp_get_diagnostics");
        Assert.Contains(aiTools, t => t.Name == "csharp_set_active_code");
        Assert.Contains(aiTools, t => t.Name == "csharp_run_script");
        Assert.Contains(aiTools, t => t.Name == "csharp_stop_script");
        Assert.Contains(aiTools, t => t.Name == "csharp_get_output");
        Assert.Contains(aiTools, t => t.Name == "csharp_execute_code");
        Assert.Contains(aiTools, t => t.Name == "csharp_new_script");
        Assert.Contains(aiTools, t => t.Name == "csharp_open_file");

        // 3. Verify AI Prompts
        var aiPrompts = scopedCtx.GetRegisteredAiPrompts();
        Assert.NotEmpty(aiPrompts);
        Assert.Contains(aiPrompts, p => p.Id == "csharp.generate_script");
        Assert.Contains(aiPrompts, p => p.Id == "csharp.fix_diagnostics");
        Assert.Contains(aiPrompts, p => p.Id == "csharp.linq_benchmark");
        Assert.Contains(aiPrompts, p => p.Id == "csharp.explain_exception");
    }

    [Fact]
    public async Task CSharpAiContextProvider_ReturnsValidSnapshot()
    {
        var provider = new CSharpAiContextProvider(() => null);
        var snapshot = await provider.GetCurrentContextAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("com.fryworld.csharpstudio", snapshot.PluginId);
        Assert.NotEmpty(snapshot.Title);
        Assert.NotEmpty(snapshot.MimeType!);
        Assert.NotNull(snapshot.SystemPromptAugmentation);
    }

    [Fact]
    public async Task CSharpHostAiTools_ExecutesGracefullyWithoutActiveDocument()
    {
        var tools = new CSharpHostAiTools(() => null);

        var activeCode = tools.GetActiveCode();
        Assert.Contains("No active document", activeCode);

        var diagnostics = tools.GetDiagnostics();
        Assert.Contains("Active document is not a C# Code Studio script", diagnostics);

        var setResult = await tools.SetActiveCode("int x = 42;");
        Assert.Contains("No active editor document", setResult);

        var runResult = await tools.RunScript();
        Assert.Contains("No runnable script", runResult);

        var stopResult = await tools.StopScript();
        Assert.Contains("No running execution", stopResult);

        var outputResult = tools.GetExecutionOutput();
        Assert.Contains("No active script or notebook open", outputResult);
    }

    [Fact]
    public async Task CSharpHostAiTools_ExecuteCode_RunsTopLevelStatementsAndCapturesOutput()
    {
        var tools = new CSharpHostAiTools(() => null);

        var json = await tools.ExecuteCode("Console.WriteLine(\"Hello from FrySharp MCP!\");");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("Success").GetBoolean());
        Assert.Equal("Execution", root.GetProperty("Phase").GetString());
        Assert.Contains("Hello from FrySharp MCP!", root.GetProperty("Output").GetString());
    }

    [Fact]
    public async Task CSharpHostAiTools_ExecuteCode_ReturnsCompilationErrors()
    {
        var tools = new CSharpHostAiTools(() => null);

        var json = await tools.ExecuteCode("int number = \"this is not an integer\";");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("Success").GetBoolean());
        Assert.Equal("Compilation", root.GetProperty("Phase").GetString());
        Assert.True(root.GetProperty("ErrorCount").GetInt32() > 0);
    }

    [Fact]
    public void CSharpHostAiTools_GetDiagnostics_DirectCode_ReturnsErrorsAndWarnings()
    {
        var tools = new CSharpHostAiTools(() => null);

        var badCodeJson = tools.GetDiagnostics("int x = \"invalid assignment\";");
        using var badDoc = JsonDocument.Parse(badCodeJson);
        Assert.True(badDoc.RootElement.GetProperty("ErrorCount").GetInt32() > 0);

        var goodCodeJson = tools.GetDiagnostics("int x = 42;\nSystem.Console.WriteLine(x);");
        using var goodDoc = JsonDocument.Parse(goodCodeJson);
        Assert.Equal(0, goodDoc.RootElement.GetProperty("ErrorCount").GetInt32());
    }

    [Fact]
    public async Task ScopeDisposal_UnregistersAiSubsystemCleanly()
    {
        var rootCtx = new FryPluginContext();
        var scope = new PluginScope();
        var scopedCtx = rootCtx.CreateScopedContext(scope);

        var plugin = new PdfEditorApp.Plugins.CSharpEditor.CSharpEditorPlugin();
        await plugin.ApplyAsync(scopedCtx);

        Assert.NotEmpty(rootCtx.GetRegisteredAiContextProviders());
        Assert.NotEmpty(rootCtx.GetRegisteredAiTools());
        Assert.NotEmpty(rootCtx.GetRegisteredAiPrompts());

        scope.Dispose();

        Assert.Empty(rootCtx.GetRegisteredAiContextProviders());
        Assert.Empty(rootCtx.GetRegisteredAiTools());
        Assert.Empty(rootCtx.GetRegisteredAiPrompts());
    }
}
