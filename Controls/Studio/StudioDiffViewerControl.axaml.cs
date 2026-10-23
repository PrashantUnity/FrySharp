using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfEditorApp.Plugins.CSharpEditor.Controls.Studio;

public partial class StudioDiffViewerControl : UserControl
{
    private bool _isSyncingScroll;
    private ScrollViewer? _left;
    private ScrollViewer? _right;
    private bool _isSubscribed;

    public StudioDiffViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_isSubscribed) return;

        _left = this.FindControl<ScrollViewer>("LeftScrollViewer");
        _right = this.FindControl<ScrollViewer>("RightScrollViewer");

        if (_left != null && _right != null)
        {
            _left.ScrollChanged += OnLeftScrollChanged;
            _right.ScrollChanged += OnRightScrollChanged;
            _isSubscribed = true;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (!_isSubscribed) return;

        if (_left != null)
        {
            _left.ScrollChanged -= OnLeftScrollChanged;
        }
        if (_right != null)
        {
            _right.ScrollChanged -= OnRightScrollChanged;
        }
        _isSubscribed = false;
    }

    private void OnLeftScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || _left == null || _right == null) return;
        _isSyncingScroll = true;
        try
        {
            _right.Offset = new Vector(_right.Offset.X, _left.Offset.Y);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void OnRightScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || _left == null || _right == null) return;
        _isSyncingScroll = true;
        try
        {
            _left.Offset = new Vector(_left.Offset.X, _right.Offset.Y);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }
}
