using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using FrySharpPlugin = PdfEditorApp.Plugins.CSharpEditor.CSharpEditorPlugin;
using FryWorld.Sdk.Testing;
using Xunit;

namespace CSharpEditorPlugin.Tests.AI;

[Collection(PluginLifecycleCollection.Name)]
public class CSharpMcpProtocolIntegrationTests
{
    [Fact]
    public async Task MountAsync_RegistersAllMcpToolsAndPrompts()
    {
        await using var host = await FryPluginTestHost.MountAsync<FrySharpPlugin>();

        // 1. Verify Context Providers
        var providers = host.Context.GetRegisteredAiContextProviders();
        Assert.Contains(providers, p => p.PluginId == "com.fryworld.csharpstudio");

        // 2. Verify all 9 MCP Tools are registered with valid metadata
        var tools = host.Context.GetRegisteredAiTools();
        Assert.Equal(9, tools.Count);

        var toolNames = new[]
        {
            "csharp_get_active_code",
            "csharp_get_diagnostics",
            "csharp_set_active_code",
            "csharp_run_script",
            "csharp_stop_script",
            "csharp_get_output",
            "csharp_execute_code",
            "csharp_new_script",
            "csharp_open_file"
        };

        foreach (var name in toolNames)
        {
            var tool = tools.FirstOrDefault(t => t.Name == name);
            Assert.NotNull(tool);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal("C# Development", tool.Category);
            Assert.NotNull(tool.Function);
        }

        // 3. Verify Turnkey Engineering Prompts
        var prompts = host.Context.GetRegisteredAiPrompts();
        Assert.Equal(4, prompts.Count);
        Assert.Contains(prompts, p => p.Id == "csharp.generate_script" && p.Category == "C# Development");
        Assert.Contains(prompts, p => p.Id == "csharp.fix_diagnostics" && p.Category == "C# Development");
        Assert.Contains(prompts, p => p.Id == "csharp.linq_benchmark" && p.Category == "C# Development");
        Assert.Contains(prompts, p => p.Id == "csharp.explain_exception" && p.Category == "C# Development");

        await host.AssertCleanUnmountAsync();
    }

    [Fact]
    public async Task McpTool_ExecuteCode_RunsDirectlyViaMcpFunctionInvocation()
    {
        await using var host = await FryPluginTestHost.MountAsync<FrySharpPlugin>();

        var tools = host.Context.GetRegisteredAiTools();
        var executeTool = tools.First(t => t.Name == "csharp_execute_code");

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["code"] = "int x = 20; int y = 22; Console.WriteLine($\"Answer={x + y}\");",
            ["mode"] = "Statements"
        });

        var result = await executeTool.Function.InvokeAsync(args);
        Assert.NotNull(result);

        var resultText = result.ToString();
        Assert.NotNull(resultText);

        using var doc = JsonDocument.Parse(resultText);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("Success").GetBoolean());
        Assert.Equal("Execution", root.GetProperty("Phase").GetString());
        Assert.Contains("Answer=42", root.GetProperty("Output").GetString());

        await host.AssertCleanUnmountAsync();
    }

    [Fact]
    public async Task McpTool_ExecuteCode_HandlesCompilationErrorGracefully()
    {
        await using var host = await FryPluginTestHost.MountAsync<FrySharpPlugin>();

        var tools = host.Context.GetRegisteredAiTools();
        var executeTool = tools.First(t => t.Name == "csharp_execute_code");

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["code"] = "string s = 12345;",
            ["mode"] = "Statements"
        });

        var result = await executeTool.Function.InvokeAsync(args);
        Assert.NotNull(result);

        var resultText = result.ToString()!;
        using var doc = JsonDocument.Parse(resultText);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("Success").GetBoolean());
        Assert.Equal("Compilation", root.GetProperty("Phase").GetString());
        Assert.True(root.GetProperty("ErrorCount").GetInt32() > 0);

        await host.AssertCleanUnmountAsync();
    }

    [Fact]
    public async Task McpContextProvider_ProvidesAccurateGroundingContext()
    {
        await using var host = await FryPluginTestHost.MountAsync<FrySharpPlugin>();

        var providers = host.Context.GetRegisteredAiContextProviders();
        var provider = providers.First(p => p.PluginId == "com.fryworld.csharpstudio");

        var snapshot = await provider.GetCurrentContextAsync();
        Assert.NotNull(snapshot);
        Assert.Equal("com.fryworld.csharpstudio", snapshot.PluginId);
        Assert.Contains("Display(object)", snapshot.SystemPromptAugmentation);
        Assert.Contains("Dump(object)", snapshot.SystemPromptAugmentation);
        Assert.Contains("Roslyn scripting", snapshot.SystemPromptAugmentation);
        Assert.NotNull(snapshot.Metadata);

        await host.AssertCleanUnmountAsync();
    }

    [Fact]
    public async Task McpTool_GetOutput_ReturnsValidSchemaWhenIdle()
    {
        await using var host = await FryPluginTestHost.MountAsync<FrySharpPlugin>();

        var tools = host.Context.GetRegisteredAiTools();
        var outputTool = tools.First(t => t.Name == "csharp_get_output");

        var result = await outputTool.Function.InvokeAsync();
        Assert.NotNull(result);

        var resultText = result.ToString()!;
        Assert.Contains("Status", resultText);

        await host.AssertCleanUnmountAsync();
    }

    [Fact]
    public async Task McpTool_GetDiagnostics_WithExplicitCodeArgument_ReturnsDiagnostics()
    {
        await using var host = await FryPluginTestHost.MountAsync<FrySharpPlugin>();

        var tools = host.Context.GetRegisteredAiTools();
        var diagTool = tools.First(t => t.Name == "csharp_get_diagnostics");

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["code"] = "int a = \"not a number\";"
        });

        var result = await diagTool.Function.InvokeAsync(args);
        Assert.NotNull(result);

        var resultText = result.ToString()!;
        using var doc = JsonDocument.Parse(resultText);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ErrorCount").GetInt32() > 0);
        Assert.True(root.GetProperty("TotalCount").GetInt32() > 0);

        await host.AssertCleanUnmountAsync();
    }
}
