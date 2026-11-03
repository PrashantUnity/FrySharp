using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Problems;
using PdfEditorApp.Plugins.CSharpEditor.Services.Problems.Catalogs.Blind75;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

/// <summary>
/// The Test Cases panel. A case is a <c>Check("name", answer, "expected")</c> line in the script: adding one writes that
/// line, every run reads each case's ✅/❌ line back, and for a Blind 75 problem "Generate" brings in its verified
/// examples and edge cases.
/// </summary>
public partial class CSharpCodeStudioViewModel
{
    [ObservableProperty]
    private bool _isAddingTestCase;

    [ObservableProperty]
    private string _newTestCaseName = string.Empty;

    /// <summary>The C# expression whose value is checked, e.g. <c>sol.MaxArea(new[] { 1, 1 })</c>.</summary>
    [ObservableProperty]
    private string _newTestCaseCall = string.Empty;

    [ObservableProperty]
    private string _newTestCaseExpected = string.Empty;

    [ObservableProperty]
    private bool _newTestCaseAnyOrder;

    [ObservableProperty]
    private string? _testCaseFormError;

    /// <summary>A one-line note after Generate ("Added 4 cases") or when there is nothing to add.</summary>
    [ObservableProperty]
    private string? _testCasesStatus;

    public bool HasTestCases => TestCases.Count > 0;

    /// <summary>"3 of 7 passed" once cases have run (with how many didn't when a run stopped early), otherwise how many there are.</summary>
    public string TestCasesSummary
    {
        get
        {
            int total = TestCases.Count, run = TestCases.Count(t => t.HasResult);
            if (total == 0) return "No cases yet";
            if (run == 0) return $"{total} case{(total == 1 ? "" : "s")}";
            string passed = $"{TestCases.Count(t => t.IsPassed)} of {total} passed";
            return run < total ? $"{passed} · {total - run} not run" : passed;
        }
    }

    /// <summary>The Blind 75 problem this script was opened from, if any: it can generate verified cases.</summary>
    public BlindProblemItem? TestCaseProblem => SupportsTestCases ? Blind75CatalogService.FindForScript(Script) : null;

    public bool CanGenerateTestCases => TestCaseProblem != null;

    private void WatchTestCases()
    {
        TestCases.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (TestCaseItem item in e.NewItems)
                {
                    item.PropertyChanged -= OnTestCaseChanged; // a tab switch adds the same cases again
                    item.PropertyChanged += OnTestCaseChanged;
                }
            }
            OnPropertyChanged(nameof(HasTestCases));
            OnPropertyChanged(nameof(TestCasesSummary));
        };
    }

    private void OnTestCaseChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestCaseItem.Passed)) OnPropertyChanged(nameof(TestCasesSummary));
        // What the user wrote is an edit; what a run filled in (result, output, running) is not.
        if (e.PropertyName is nameof(TestCaseItem.Name) or nameof(TestCaseItem.Input) or nameof(TestCaseItem.ExpectedOutput) or nameof(TestCaseItem.Call))
        {
            MarkActiveDocumentEdited();
        }
    }

    partial void OnScriptChanged(ScriptDocumentItem value)
    {
        OnPropertyChanged(nameof(TestCaseProblem));
        OnPropertyChanged(nameof(CanGenerateTestCases));
        OnActiveLanguageChanged();
    }

    [RelayCommand]
    private void AddTestCase()
    {
        if (!SupportsTestCases) return;
        int next = TestCases.Count + 1;
        while (TestCases.Any(t => t.Name == $"Case {next}")) next++;
        NewTestCaseName = $"Case {next}";
        // Start from one of the problem's own calls, so only the values need changing.
        NewTestCaseCall = TestCaseProblem?.Tests.FirstOrDefault()?.Call ?? string.Empty;
        NewTestCaseExpected = string.Empty;
        NewTestCaseAnyOrder = false;
        TestCaseFormError = null;
        TestCasesStatus = null;
        IsAddingTestCase = true;
    }

    [RelayCommand]
    private void CancelAddTestCase()
    {
        IsAddingTestCase = false;
        TestCaseFormError = null;
    }

    [RelayCommand]
    private void ConfirmAddTestCase()
    {
        if (!SupportsTestCases) return;
        string name = NewTestCaseName.Trim();
        string call = NewTestCaseCall.Trim().TrimEnd(';');
        string expected = NewTestCaseExpected.Trim();
        TestCaseFormError =
            name.Length == 0 ? "Give the case a name." :
            TestCases.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)) ? $"There's already a case called \"{name}\"." :
            call.Length == 0 ? "Enter the C# expression whose value you want to check." :
            expected.Length == 0 ? "Enter the answer you expect, e.g. 49, true, [0,1] or \"bab\"." :
            null;
        if (TestCaseFormError != null) return;

        SetCode(TestCaseCode.AddCheck(Code, TestCaseCode.CheckLine(name, call, expected, NewTestCaseAnyOrder)));
        AddToTestCases(new TestCaseItem { Name = name, Call = call, ExpectedOutput = expected, AnyOrder = NewTestCaseAnyOrder });
        IsAddingTestCase = false;
        TestCasesStatus = $"Added \"{name}\": Run or F5 checks it.";
    }

    /// <summary>
    /// For a Blind 75 problem: adds its verified examples and edge cases that aren't listed yet, writing a Check line for
    /// any the script no longer has.
    /// </summary>
    [RelayCommand]
    private void GenerateTestCases()
    {
        var problem = TestCaseProblem;
        if (problem == null) return;

        int added = 0;
        string code = Code;
        foreach (var test in problem.Tests.Concat(problem.ExtraTests))
        {
            if (TestCases.Any(t => string.Equals(t.Name, test.Name, StringComparison.Ordinal))) continue;
            bool writeLine = !TestCaseCode.HasCheck(code, test.Name);
            if (writeLine) code = TestCaseCode.AddCheck(code, TestCaseCode.CheckLine(test.Name, test.Call, test.Expected, test.AnyOrder));
            AddToTestCases(new TestCaseItem
            {
                Name = test.Name,
                Input = test.Input,
                ExpectedOutput = test.Expected,
                AnyOrder = test.AnyOrder,
                Call = writeLine ? test.Call : string.Empty
            });
            added++;
        }

        if (code != Code) SetCode(code);
        TestCasesStatus = added == 0
            ? "Every example and edge case of this problem is already here."
            : $"Added {added} case{(added == 1 ? "" : "s")} from the problem's examples and edge cases.";
    }

    /// <summary>Takes the case off the list, and its Check line out of the script when the panel wrote it.</summary>
    [RelayCommand]
    private void DeleteTestCase(TestCaseItem? testCase)
    {
        if (testCase == null) return;
        if (testCase.IsFromPanel) SetCode(TestCaseCode.RemoveCheck(Code, testCase.Name));
        testCase.PropertyChanged -= OnTestCaseChanged;
        TestCases.Remove(testCase);
        Script.TestCases.Remove(testCase);
        MarkActiveDocumentEdited();
        TestCasesStatus = null;
    }

    /// <summary>
    /// Runs the script, which checks every case, and stays on this panel to show the results. A Blind 75 problem whose
    /// cases all pass is marked solved.
    /// </summary>
    [RelayCommand]
    private async Task RunAllTestCasesAsync()
    {
        if (IsExecuting || !SupportsTestCases) return;
        var script = Script;
        var problem = TestCaseProblem;
        var cases = TestCases.ToList();
        TestCasesStatus = null;

        await RunCodeAsync();

        if (problem != null && cases.Count > 0 && cases.All(t => t.Passed == true))
        {
            try
            {
                _ = _blindProgress.SetProblemSolvedAsync(problem.Number, true);
            }
            catch
            {
            }
        }
        if (!ReferenceEquals(Script, script)) return; // another tab is active now: its panel is left as it is
        ShowTestCasesTab();
        if (cases.Count > 0 && cases.All(t => !t.HasResult))
        {
            TestCasesStatus = ErrorCount > 0
                ? "No case ran: the script has errors. The Problems tab lists them."
                : "No case ran: the run stopped before the first Check line. The Terminal shows why.";
        }
    }

    /// <summary>
    /// After a run, on the cases of the script that ran: a case whose ✅/❌ line was printed shows it; one with a Check line
    /// in the code that never printed didn't run (the run stopped first). An older case with no Check line passes when a
    /// completed run's output contains its expected text.
    /// </summary>
    private static void UpdateTestCaseResults(IReadOnlyList<TestCaseItem> cases, string code, string output, bool completed)
    {
        foreach (var testCase in cases)
        {
            testCase.IsRunning = false;
            var line = Judge.LineFor(output, testCase.Name);
            if (line != null)
            {
                testCase.Passed = Judge.Verdict(output, testCase.Name);
                int arrow = line.IndexOf(" → ", StringComparison.Ordinal);
                testCase.ActualOutput = arrow >= 0 ? line[(arrow + 3)..] : line;
            }
            else if (completed && !string.IsNullOrEmpty(testCase.ExpectedOutput) && !TestCaseCode.HasCheck(code, testCase.Name))
            {
                testCase.Passed = output.Contains(testCase.ExpectedOutput, StringComparison.Ordinal);
                testCase.ActualOutput = testCase.Passed == true ? testCase.ExpectedOutput : "not found in the output";
            }
            else
            {
                testCase.Passed = null;
                testCase.ActualOutput = string.Empty;
            }
        }
    }

    private void AddToTestCases(TestCaseItem testCase)
    {
        TestCases.Add(testCase);
        Script.TestCases.Add(testCase);
        MarkActiveDocumentEdited();
    }

    // Code changed here, not in the editor: show it there too.
    private void SetCode(string code)
    {
        Code = code;
        RequestReloadEditorText?.Invoke();
    }
}
