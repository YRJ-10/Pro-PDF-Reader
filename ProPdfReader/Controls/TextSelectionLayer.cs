using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProPdfReader.State;
using ProPdfReader.Text;

namespace ProPdfReader.Controls;

public sealed class TextSelectionLayer : FrameworkElement
{
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(105, 48, 116, 214));
    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromArgb(105, 255, 210, 35));

    private readonly MenuItem _copyMenuItem;
    private readonly MenuItem _highlightMenuItem;
    private readonly MenuItem _removeHighlightMenuItem;
    private PageText? _page;
    private IReadOnlyList<HighlightState> _highlights = [];
    private double _pageWidth;
    private double _pageHeight;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;
    private Guid? _contextHighlightId;

    internal event Action? SelectionChanged;

    internal event Action? HighlightRequested;

    internal event Action<Guid>? HighlightRemovalRequested;

    public TextSelectionLayer()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;

        _copyMenuItem = new MenuItem { Header = "Copy" };
        _copyMenuItem.Click += (_, _) => CopySelection();

        _highlightMenuItem = new MenuItem { Header = "Highlight selection" };
        _highlightMenuItem.Click += (_, _) => HighlightRequested?.Invoke();

        _removeHighlightMenuItem = new MenuItem { Header = "Remove highlight" };
        _removeHighlightMenuItem.Click += (_, _) =>
        {
            if (_contextHighlightId is Guid highlightId)
            {
                HighlightRemovalRequested?.Invoke(highlightId);
            }
        };

        var selectAllMenuItem = new MenuItem { Header = "Select all" };
        selectAllMenuItem.Click += (_, _) => SelectAll();

        ContextMenu = new ContextMenu
        {
            Items =
            {
                _copyMenuItem,
                _highlightMenuItem,
                _removeHighlightMenuItem,
                new Separator(),
                selectAllMenuItem
            }
        };
        ContextMenuOpening += TextSelectionLayer_ContextMenuOpening;
    }

    public bool HasSelection => _selectionAnchor >= 0 && _selectionEnd >= 0;

    internal void SetPage(PageText page)
    {
        _page = page;
        _pageWidth = page.Width;
        _pageHeight = page.Height;
        ClearSelection();
    }

    internal void SetPageGeometry(
        double pageWidth,
        double pageHeight,
        IReadOnlyList<HighlightState> highlights)
    {
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _highlights = highlights;
        InvalidateVisual();
    }

    internal void SetHighlights(IReadOnlyList<HighlightState> highlights)
    {
        _highlights = highlights;
        InvalidateVisual();
    }

    public void ClearPage()
    {
        _page = null;
        _highlights = [];
        _pageWidth = 0;
        _pageHeight = 0;
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

        foreach (var highlight in _highlights)
        {
            foreach (var rectangle in highlight.Rectangles)
            {
                drawingContext.DrawRectangle(HighlightBrush, null, GetHighlightBounds(rectangle));
            }
        }

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
        SelectionChanged?.Invoke();

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
            SelectionChanged?.Invoke();
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
        return GetPageBounds(word.Left, word.Bottom, word.Right, word.Top);
    }

    private Rect GetHighlightBounds(HighlightRectangle rectangle)
    {
        return GetPageBounds(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);
    }

    private Rect GetPageBounds(double left, double bottom, double right, double top)
    {
        if (_pageWidth <= 0 || _pageHeight <= 0)
        {
            return Rect.Empty;
        }

        var scaleX = ActualWidth / _pageWidth;
        var scaleY = ActualHeight / _pageHeight;
        return new Rect(
            left * scaleX,
            (_pageHeight - top) * scaleY,
            Math.Max(1, (right - left) * scaleX),
            Math.Max(1, (top - bottom) * scaleY));
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
        SelectionChanged?.Invoke();
    }

    internal void ClearSelection()
    {
        var hadSelection = HasSelection;
        _selectionAnchor = -1;
        _selectionEnd = -1;
        InvalidateVisual();

        if (hadSelection)
        {
            SelectionChanged?.Invoke();
        }
    }

    private void CopySelection()
    {
        var selection = GetSelection();
        if (selection is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(selection.Text);
        }
        catch (Exception)
        {
            // Clipboard access can briefly fail when another process owns it.
        }
    }

    internal TextSelectionSnapshot? GetSelection()
    {
        if (_page is null || !HasSelection)
        {
            return null;
        }

        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Max(_selectionAnchor, _selectionEnd);
        var builder = new StringBuilder();
        var rectangles = new List<HighlightRectangle>();
        PageWord? previousWord = null;
        HighlightRectangle? currentRectangle = null;

        for (var index = first; index <= last; index++)
        {
            var word = _page.Words[index];

            if (previousWord is not null)
            {
                var overlap = Math.Min(previousWord.Top, word.Top) - Math.Max(previousWord.Bottom, word.Bottom);
                var minimumHeight = Math.Min(previousWord.Height, word.Height);
                var isSameLine = overlap >= minimumHeight * 0.3;
                builder.Append(isSameLine ? ' ' : Environment.NewLine);

                var horizontalGap = Math.Max(
                    word.Left - currentRectangle!.Right,
                    currentRectangle.Left - word.Right);
                var mergeDistance = Math.Max(previousWord.Height, word.Height) * 1.5;

                if (isSameLine && horizontalGap <= mergeDistance)
                {
                    currentRectangle = currentRectangle with
                    {
                        Left = Math.Min(currentRectangle.Left, word.Left),
                        Bottom = Math.Min(currentRectangle.Bottom, word.Bottom),
                        Right = Math.Max(currentRectangle.Right, word.Right),
                        Top = Math.Max(currentRectangle.Top, word.Top)
                    };
                    rectangles[^1] = currentRectangle;
                }
                else
                {
                    currentRectangle = CreateRectangle(word);
                    rectangles.Add(currentRectangle);
                }
            }
            else
            {
                currentRectangle = CreateRectangle(word);
                rectangles.Add(currentRectangle);
            }

            builder.Append(word.Text);
            previousWord = word;
        }

        return new TextSelectionSnapshot(builder.ToString(), rectangles);
    }

    private void TextSelectionLayer_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _copyMenuItem.IsEnabled = HasSelection;
        _highlightMenuItem.IsEnabled = HasSelection;
        _contextHighlightId = HitTestHighlight(Mouse.GetPosition(this));
        _removeHighlightMenuItem.Visibility = _contextHighlightId.HasValue
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Guid? HitTestHighlight(Point point)
    {
        for (var highlightIndex = _highlights.Count - 1; highlightIndex >= 0; highlightIndex--)
        {
            var highlight = _highlights[highlightIndex];
            if (highlight.Rectangles.Any(rectangle => GetHighlightBounds(rectangle).Contains(point)))
            {
                return highlight.Id;
            }
        }

        return null;
    }

    private static HighlightRectangle CreateRectangle(PageWord word)
    {
        return new HighlightRectangle(word.Left, word.Bottom, word.Right, word.Top);
    }
}

internal sealed record TextSelectionSnapshot(
    string Text,
    IReadOnlyList<HighlightRectangle> Rectangles);
