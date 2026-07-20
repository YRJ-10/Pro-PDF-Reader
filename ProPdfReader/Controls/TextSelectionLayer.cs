using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProPdfReader.Text;

namespace ProPdfReader.Controls;

public sealed class TextSelectionLayer : FrameworkElement
{
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(105, 48, 116, 214));

    private readonly MenuItem _copyMenuItem;
    private PageText? _page;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;

    public TextSelectionLayer()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;

        _copyMenuItem = new MenuItem { Header = "Copy" };
        _copyMenuItem.Click += (_, _) => CopySelection();

        var selectAllMenuItem = new MenuItem { Header = "Select all" };
        selectAllMenuItem.Click += (_, _) => SelectAll();

        ContextMenu = new ContextMenu
        {
            Items = { _copyMenuItem, selectAllMenuItem }
        };
        ContextMenuOpening += (_, _) => _copyMenuItem.IsEnabled = HasSelection;
    }

    public bool HasSelection => _selectionAnchor >= 0 && _selectionEnd >= 0;

    internal void SetPage(PageText page)
    {
        _page = page;
        ClearSelection();
    }

    public void ClearPage()
    {
        _page = null;
        ClearSelection();
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        if (_page is null || !HasSelection)
        {
            return;
        }

        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Max(_selectionAnchor, _selectionEnd);

        for (var index = first; index <= last; index++)
        {
            drawingContext.DrawRectangle(SelectionBrush, null, GetWordBounds(_page.Words[index]));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var wordIndex = HitTestWord(e.GetPosition(this), allowNearest: false);
        if (wordIndex < 0)
        {
            ClearSelection();
            return;
        }

        _selectionAnchor = wordIndex;
        _selectionEnd = wordIndex;
        InvalidateVisual();

        if (e.ClickCount == 1)
        {
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed || _page is null)
        {
            return;
        }

        var wordIndex = HitTestWord(e.GetPosition(this), allowNearest: true);
        if (wordIndex >= 0 && wordIndex != _selectionEnd)
        {
            _selectionEnd = wordIndex;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopySelection();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private int HitTestWord(Point point, bool allowNearest)
    {
        if (_page is null || _page.Words.Count == 0)
        {
            return -1;
        }

        var nearestIndex = -1;
        var nearestDistance = double.MaxValue;

        for (var index = 0; index < _page.Words.Count; index++)
        {
            var bounds = GetWordBounds(_page.Words[index]);
            var hitBounds = bounds;
            hitBounds.Inflate(2, 2);

            if (hitBounds.Contains(point))
            {
                return index;
            }

            if (!allowNearest)
            {
                continue;
            }

            var horizontalDistance = Math.Max(bounds.Left - point.X, Math.Max(0, point.X - bounds.Right));
            var verticalDistance = Math.Max(bounds.Top - point.Y, Math.Max(0, point.Y - bounds.Bottom));
            var distance = (horizontalDistance * horizontalDistance) + (verticalDistance * verticalDistance);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }

        return nearestIndex;
    }

    private Rect GetWordBounds(PageWord word)
    {
        if (_page is null || _page.Width <= 0 || _page.Height <= 0)
        {
            return Rect.Empty;
        }

        var scaleX = ActualWidth / _page.Width;
        var scaleY = ActualHeight / _page.Height;
        return new Rect(
            word.Left * scaleX,
            (_page.Height - word.Top) * scaleY,
            Math.Max(1, (word.Right - word.Left) * scaleX),
            Math.Max(1, word.Height * scaleY));
    }

    private void SelectAll()
    {
        if (_page is null || _page.Words.Count == 0)
        {
            return;
        }

        _selectionAnchor = 0;
        _selectionEnd = _page.Words.Count - 1;
        InvalidateVisual();
    }

    private void ClearSelection()
    {
        _selectionAnchor = -1;
        _selectionEnd = -1;
        InvalidateVisual();
    }

    private void CopySelection()
    {
        if (_page is null || !HasSelection)
        {
            return;
        }

        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Max(_selectionAnchor, _selectionEnd);
        var builder = new StringBuilder();
        PageWord? previousWord = null;

        for (var index = first; index <= last; index++)
        {
            var word = _page.Words[index];

            if (previousWord is not null)
            {
                var overlap = Math.Min(previousWord.Top, word.Top) - Math.Max(previousWord.Bottom, word.Bottom);
                var minimumHeight = Math.Min(previousWord.Height, word.Height);
                builder.Append(overlap >= minimumHeight * 0.3 ? ' ' : Environment.NewLine);
            }

            builder.Append(word.Text);
            previousWord = word;
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (Exception)
        {
            // Clipboard access can briefly fail when another process owns it.
        }
    }
}
