using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using PdfEditorApp.Plugins.CSharpEditor.Services.Common;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Editor;

/// <summary>
/// Universal code completion controller for external/compiled languages (Java, C++, etc.).
/// Manages debouncing, trigger detection (dot, arrow, scope resolution, Ctrl+Space),
/// and displays AvaloniaEdit's <see cref="CompletionWindow"/> off the UI thread.
/// </summary>
public sealed class LanguageCompletionController : IDisposable
{
    private readonly TextEditor _editor;
    private readonly EditorAssistantContext _context;
    private readonly ILanguageCompletionService _completionService;
    private readonly Func<char, string, int, bool> _isImmediateTrigger;
    private readonly DispatcherTimer _debounceTimer;

    private CompletionWindow? _completionWindow;
    private CancellationTokenSource? _queryCts;

    public LanguageCompletionController(
        TextEditor editor,
        EditorAssistantContext context,
        ILanguageCompletionService completionService,
        Func<char, string, int, bool>? isImmediateTrigger = null)
    {
        _editor = editor;
        _context = context;
        _completionService = completionService;
        _isImmediateTrigger = isImmediateTrigger ?? ((ch, _, _) => ch == '.');

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

    public void Close()
    {
        _debounceTimer.Stop();
        _queryCts?.Cancel();
        _completionWindow?.Close();
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow != null && !string.IsNullOrEmpty(e.Text))
        {
            var text = _editor.Text ?? string.Empty;
            int offset = _editor.CaretOffset;
            if (_isImmediateTrigger(e.Text[0], text, offset))
            {
                _completionWindow.Close();
            }
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _context.IsSuppressed?.Invoke() == true) return;

        var ch = e.Text[0];
        var text = _editor.Text ?? string.Empty;
        var offset = _editor.CaretOffset;

        if (_isImmediateTrigger(ch, text, offset))
        {
            _debounceTimer.Stop();
            TriggerCompletion(explicitTrigger: true);
            return;
        }

        if (char.IsLetterOrDigit(ch) || ch == '_')
        {
            if (_completionWindow == null)
            {
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
            if (_context.IsSuppressed?.Invoke() == true) return;
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
        if (_context.IsSuppressed?.Invoke() == true) return;

        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        var token = _queryCts.Token;

        var text = _editor.Text ?? string.Empty;
        int caretOffset = _editor.CaretOffset;
        if (caretOffset < 0 || caretOffset > text.Length) return;

        int startOffset;
        string initialQuery = string.Empty;

        // Check if cursor is right after an immediate trigger
        bool isImmediate = caretOffset > 0 && _isImmediateTrigger(text[caretOffset - 1], text, caretOffset);

        if (isImmediate)
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

        // Merge preceding notebook cell context if present
        var precedingContext = _context.PrecedingCode?.Invoke();
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
                var items = await _completionService.GetCompletionsAsync(analysisText, analysisCaretOffset, _context, token);
                if (token.IsCancellationRequested || items == null || items.Count == 0) return;

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
                        CloseWhenCaretAtBeginning = !isImmediate,
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LanguageCompletionController] Completion failed: {ex}");
            }
        }, token);
    }

    public void Dispose()
    {
        Close();
        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.TextArea.TextEntering -= OnTextEntering;
        _editor.KeyDown -= OnKeyDown;
    }
}
