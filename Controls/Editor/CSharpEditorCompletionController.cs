using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Editor;

public class CSharpEditorCompletionController : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<RoslynCompilerService>? _compilerProvider;
    private CSharpCompletionService? _completionService;
    private CompletionWindow? _completionWindow;
    private CancellationTokenSource? _queryCts;
    private readonly DispatcherTimer _debounceTimer;

    public ExecutionLanguageMode LanguageMode { get; set; } = ExecutionLanguageMode.Statements;

    /// <summary>
    /// Optional source of code that precedes this editor's own text but isn't in its document —
    /// e.g. earlier notebook cells, whose declared variables/usings a real execution kernel would
    /// already have in scope by the time this cell runs. Only feeds the semantic analysis below;
    /// window positioning stays in this editor's own local coordinates.
    /// </summary>
    public Func<string>? PrecedingContextProvider { get; set; }

    /// <summary>True while completion should stay off, e.g. when the editor shows a language other than C#.</summary>
    public Func<bool>? IsSuppressed { get; set; }

    /// <summary>
    /// Master on/off switch controlled by the user's "Auto Completion" setting.
    /// Defaults to true. Setting to false closes any open completion window immediately.
    /// </summary>
    public bool IsFeatureEnabled
    {
        get => _isFeatureEnabled;
        set
        {
            _isFeatureEnabled = value;
            if (!value) Close();
        }
    }
    private bool _isFeatureEnabled = true;

    /// <summary>Closes the completion list if it's open (the editor switched to another document or language).</summary>
    public void Close()
    {
        _debounceTimer.Stop();
        _queryCts?.Cancel();
        _completionWindow?.Close();
    }

    public CSharpEditorCompletionController(TextEditor editor, RoslynCompilerService compilerService)
        : this(editor, () => compilerService)
    {
    }

    public CSharpEditorCompletionController(TextEditor editor, Func<RoslynCompilerService> compilerProvider)
    {
        _editor = editor;
        _compilerProvider = compilerProvider;

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _debounceTimer.Tick += (s, e) =>
        {
            _debounceTimer.Stop();
            TriggerCompletion(explicitTrigger: false);
        };

        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.KeyDown += OnKeyDown;
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow != null && !string.IsNullOrEmpty(e.Text))
        {
            if (e.Text == ".")
            {
                _completionWindow.Close();
            }
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!_isFeatureEnabled || string.IsNullOrEmpty(e.Text) || IsSuppressed?.Invoke() == true) return;

        var ch = e.Text[0];

        if (ch == '.')
        {
            _debounceTimer.Stop();
            TriggerCompletion(explicitTrigger: true);
            return;
        }

        if (char.IsLetterOrDigit(ch) || ch == '_')
        {
            if (_completionWindow == null)
            {
                int offset = _editor.CaretOffset;
                var text = _editor.Text ?? string.Empty;
                int wordStart = offset - 1;
                while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
                {
                    wordStart--;
                }

                int wordLen = offset - wordStart;
                if (wordLen >= 1)
                {
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var isModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (isModifier && e.Key == Key.Space)
        {
            if (IsSuppressed?.Invoke() == true) return;
            _debounceTimer.Stop();
            TriggerCompletion(explicitTrigger: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _completionWindow != null)
        {
            _completionWindow.Close();
            e.Handled = true;
            return;
        }
    }

    public void TriggerCompletion(bool explicitTrigger = false)
    {
        if (!_isFeatureEnabled || IsSuppressed?.Invoke() == true) return;
        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        var token = _queryCts.Token;

        var text = _editor.Text ?? string.Empty;
        int caretOffset = _editor.CaretOffset;
        if (caretOffset < 0 || caretOffset > text.Length) return;

        bool isDot = caretOffset > 0 && text[caretOffset - 1] == '.';

        int startOffset;
        string initialQuery = string.Empty;

        if (isDot)
        {
            startOffset = caretOffset;
        }
        else
        {
            int i = caretOffset;
            while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
            {
                i--;
            }
            startOffset = i;
            if (caretOffset > startOffset)
            {
                initialQuery = text.Substring(startOffset, caretOffset - startOffset);
            }
        }

        var mode = LanguageMode;

        // Merge in preceding notebook cells (if any) purely for the semantic analyzer below — the
        // completion window itself is positioned using startOffset/text above, which stay in this
        // editor's own local document coordinates untouched.
        var precedingContext = PrecedingContextProvider?.Invoke();
        var analysisText = text;
        var analysisCaretOffset = caretOffset;
        if (!string.IsNullOrEmpty(precedingContext))
        {
            analysisText = precedingContext + "\n" + text;
            analysisCaretOffset = precedingContext.Length + 1 + caretOffset;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_completionService == null && _compilerProvider != null)
                {
                    var compiler = _compilerProvider();
                    _completionService = new CSharpCompletionService(compiler);
                }

                if (_completionService == null) return;

                var items = await _completionService.GetCompletionsAsync(analysisText, analysisCaretOffset, mode, token);

                if (token.IsCancellationRequested || items.Count == 0) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;

                    bool hasFocus = _editor.IsKeyboardFocusWithin || _editor.TextArea.IsFocused || explicitTrigger;
                    if (!hasFocus) return;

                    _completionWindow?.Close();

                    _completionWindow = new CompletionWindow(_editor.TextArea)
                    {
                        StartOffset = startOffset,
                        CloseAutomatically = true,
                        CloseWhenCaretAtBeginning = !isDot,
                        ExpectInsertionBeforeStart = false,
                        MaxHeight = 280,
                        MaxWidth = 480
                    };

                    // The popup is a root of its own: it has the editor's theme when the application has the studio's styles
                    // (the Runner); otherwise (FryPDF) it brings it.
                    if (!PdfEditorApp.Plugins.CSharpEditor.Controls.Common.StudioStyleSheet.ApplicationHasIt)
                    {
                        try
                        {
                            _completionWindow.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://CSharpEditorPlugin/"))
                            {
                                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
                            });
                        }
                        catch { }
                    }

                    bool isDark = ThemeService.IsDark;
                    _completionWindow.CompletionList.Background = new SolidColorBrush(Color.Parse(isDark ? "#14171F" : "#FFFFFF"));
                    _completionWindow.CompletionList.Foreground = new SolidColorBrush(Color.Parse(isDark ? "#D4D4D4" : "#1F2328"));
                    _completionWindow.CompletionList.BorderBrush = new SolidColorBrush(Color.Parse(isDark ? "#30363D" : "#D0D7DE"));
                    _completionWindow.CompletionList.BorderThickness = new Thickness(PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Theming.Layout.LayoutTokens.BorderWidth);
                    _completionWindow.CompletionList.CornerRadius = new CornerRadius(PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility.Theming.Layout.LayoutTokens.Radius("LG"));

                    var data = _completionWindow.CompletionList.CompletionData;
                    data.Clear();
                    foreach (var item in items)
                    {
                        data.Add(new CSharpCompletionData(item));
                    }

                    var window = _completionWindow;
                    var completionList = window.CompletionList;

                    void ApplySelection()
                    {
                        if (!ReferenceEquals(_completionWindow, window)) return;

                        var currentCaret = _editor.CaretOffset;
                        var currentText = _editor.Text ?? string.Empty;
                        var effectiveQuery = initialQuery;
                        if (currentCaret > startOffset && currentCaret <= currentText.Length)
                        {
                            effectiveQuery = currentText.Substring(startOffset, currentCaret - startOffset);
                        }

                        if (!string.IsNullOrEmpty(effectiveQuery))
                        {
                            completionList.SelectItem(effectiveQuery);
                        }
                        else if (data.Count > 0)
                        {
                            completionList.SelectedItem = data[0];
                        }
                    }

                    void OnTemplateApplied(object? s, TemplateAppliedEventArgs e)
                    {
                        completionList.TemplateApplied -= OnTemplateApplied;
                        ApplySelection();
                    }

                    window.Closed += (s, e) =>
                    {
                        completionList.TemplateApplied -= OnTemplateApplied;
                        if (ReferenceEquals(_completionWindow, window)) _completionWindow = null;
                    };

                    window.Show();

                    // AvaloniaEdit has a known upstream timing bug (AvaloniaUI/AvaloniaEdit
                    // issues #308 and #357): TemplatedControl.ApplyTemplate() silently no-ops
                    // if styling hasn't resolved CompletionList's ControlTemplate yet, leaving
                    // its internal ListBox null - Show() does not guarantee it's ready. That's
                    // harmless under light UI load (template resolves before the next frame)
                    // but under FryPDF's heavier UI thread it can still be unresolved right
                    // here, silently dropping the selection/highlight instead of crashing (the
                    // crash itself is now caught by Dispatcher.UIThread.UnhandledException, but
                    // that only stops the abort - it doesn't make the popup usable). Wait for
                    // the template to genuinely finish applying before touching ListBox-backed
                    // members if it isn't ready the instant Show() returns.
                    if (completionList.ListBox != null)
                    {
                        ApplySelection();
                    }
                    else
                    {
                        completionList.TemplateApplied += OnTemplateApplied;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorCompletionController] TriggerCompletion failed: {ex}");
            }
        }, token);
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _queryCts?.Cancel();
        _completionWindow?.Close();

        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.TextArea.TextEntering -= OnTextEntering;
        _editor.KeyDown -= OnKeyDown;
    }
}
