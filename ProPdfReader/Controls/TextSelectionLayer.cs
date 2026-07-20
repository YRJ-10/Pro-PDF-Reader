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
    private static readonly Pen UnderlinePen = new(new SolidColorBrush(Color.FromRgb(242, 190, 60)), 2);
    private static readonly Brush NoteMarkerBrush = new SolidColorBrush(Color.FromRgb(224, 112, 62));
    private static readonly Pen NoteUnderlinePen = new(new SolidColorBrush(Color.FromRgb(224, 112, 62)), 2);
    private static readonly Pen NoteMarkerPen = new(new SolidColorBrush(Color.FromRgb(142, 68, 38)), 1);
    private static readonly Pen NoteMarkerTextPen = new(Brushes.White, 1);

    private readonly MenuItem _copyMenuItem;
    private readonly MenuItem _highlightMenuItem;
    private readonly MenuItem _underlineMenuItem;
    private readonly MenuItem _addNoteMenuItem;
    private readonly MenuItem _editNoteMenuItem;
    private readonly MenuItem _removeNoteMenuItem;
    private readonly MenuItem _removeHighlightMenuItem;
    private PageText? _page;
    private IReadOnlyList<HighlightState> _highlights = [];
    private IReadOnlyList<NoteState> _notes = [];
    private IReadOnlyList<PageLink> _links = [];
    private double _pageWidth;
    private double _pageHeight;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;
    private Guid? _contextHighlightId;
    private Guid? _contextNoteId;
    private PageLink? _pressedLink;
    private Point _pressedPoint;

    internal event Action? SelectionChanged;

    internal event Action? HighlightRequested;

    internal event Action? UnderlineRequested;

    internal event Action<Guid>? HighlightRemovalRequested;

    internal event Action? NoteRequested;

    internal event Action<Guid>? NoteEditRequested;

    internal event Action<Guid>? NoteRemovalRequested;

    internal event Action<PageLink>? LinkRequested;

    public TextSelectionLayer()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;

        _copyMenuItem = new MenuItem { Header = "Copy" };
        _copyMenuItem.Click += (_, _) => CopySelection();

        _highlightMenuItem = new MenuItem { Header = "Highlight selection" };
        _highlightMenuItem.Click += (_, _) => HighlightRequested?.Invoke();

        _underlineMenuItem = new MenuItem { Header = "Underline selection" };
        _underlineMenuItem.Click += (_, _) => UnderlineRequested?.Invoke();

        _addNoteMenuItem = new MenuItem { Header = "Add note" };
        _addNoteMenuItem.Click += (_, _) => NoteRequested?.Invoke();

        _editNoteMenuItem = new MenuItem { Header = "Edit note" };
        _editNoteMenuItem.Click += (_, _) =>
        {
            if (_contextNoteId is Guid noteId)
            {
                NoteEditRequested?.Invoke(noteId);
            }
        };

        _removeNoteMenuItem = new MenuItem { Header = "Remove note" };
        _removeNoteMenuItem.Click += (_, _) =>
        {
            if (_contextNoteId is Guid noteId)
            {
                NoteRemovalRequested?.Invoke(noteId);
            }
        };

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
                _underlineMenuItem,
                _addNoteMenuItem,
                _editNoteMenuItem,
                _removeNoteMenuItem,
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
        _links = page.Links;
        _pageWidth = page.Width;
        _pageHeight = page.Height;
        ClearSelection();
    }

    internal void SetPageGeometry(
        double pageWidth,
        double pageHeight,
        IReadOnlyList<HighlightState> highlights,
        IReadOnlyList<NoteState> notes)
    {
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _highlights = highlights;
        _notes = notes;
        InvalidateVisual();
    }

    internal void SetHighlights(IReadOnlyList<HighlightState> highlights)
    {
        _highlights = highlights;
        InvalidateVisual();
    }

    internal void SetNotes(IReadOnlyList<NoteState> notes)
    {
        _notes = notes;
        InvalidateVisual();
    }

    public void ClearPage()
    {
        _page = null;
        _highlights = [];
        _notes = [];
        _links = [];
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
                var bounds = GetHighlightBounds(rectangle);
                if (highlight.Style == HighlightStyle.Underline)
                {
                    drawingContext.DrawLine(
                        UnderlinePen,
                        new Point(bounds.Left, bounds.Bottom - 1),
                        new Point(bounds.Right, bounds.Bottom - 1));
                }
                else
                {
                    drawingContext.DrawRectangle(HighlightBrush, null, bounds);
                }
            }
        }

        foreach (var note in _notes)
        {
            foreach (var anchor in note.Anchors)
            {
                var bounds = GetHighlightBounds(anchor);
                drawingContext.DrawLine(
                    NoteUnderlinePen,
                    new Point(bounds.Left, bounds.Bottom - 1),
                    new Point(bounds.Right, bounds.Bottom - 1));
            }

            var markerBounds = GetNoteMarkerBounds(note);
            drawingContext.DrawRectangle(NoteMarkerBrush, NoteMarkerPen, markerBounds);
            drawingContext.DrawLine(
                NoteMarkerTextPen,
                new Point(markerBounds.Left + 3, markerBounds.Top + 5),
                new Point(markerBounds.Right - 3, markerBounds.Top + 5));
            drawingContext.DrawLine(
                NoteMarkerTextPen,
                new Point(markerBounds.Left + 3, markerBounds.Top + 8),
                new Point(markerBounds.Right - 5, markerBounds.Top + 8));
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

        _pressedPoint = e.GetPosition(this);
        _pressedLink = HitTestLink(_pressedPoint);

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
            Cursor = HitTestLink(e.GetPosition(this)) is null ? Cursors.IBeam : Cursors.Hand;
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

        var releasedPoint = e.GetPosition(this);
        if (_pressedLink is not null &&
            (releasedPoint - _pressedPoint).Length <= SystemParameters.MinimumHorizontalDragDistance &&
            HitTestLink(releasedPoint) == _pressedLink)
        {
            LinkRequested?.Invoke(_pressedLink);
        }

        _pressedLink = null;
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

    private PageLink? HitTestLink(Point point)
    {
        return _links.LastOrDefault(link =>
            GetPageBounds(link.Left, link.Bottom, link.Right, link.Top).Contains(point));
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

    internal bool SelectRange(int startWordIndex, int endWordIndex)
    {
        if (_page is null ||
            startWordIndex < 0 ||
            endWordIndex < startWordIndex ||
            endWordIndex >= _page.Words.Count)
        {
            return false;
        }

        _selectionAnchor = startWordIndex;
        _selectionEnd = endWordIndex;
        InvalidateVisual();
        SelectionChanged?.Invoke();
        return true;
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
        _underlineMenuItem.IsEnabled = HasSelection;
        _addNoteMenuItem.IsEnabled = HasSelection;
        _contextHighlightId = HitTestHighlight(Mouse.GetPosition(this));
        _contextNoteId = HitTestNote(Mouse.GetPosition(this));
        _removeHighlightMenuItem.Visibility = _contextHighlightId.HasValue
            ? Visibility.Visible
            : Visibility.Collapsed;
        _editNoteMenuItem.Visibility = _contextNoteId.HasValue
            ? Visibility.Visible
            : Visibility.Collapsed;
        _removeNoteMenuItem.Visibility = _contextNoteId.HasValue
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

    private Guid? HitTestNote(Point point)
    {
        for (var noteIndex = _notes.Count - 1; noteIndex >= 0; noteIndex--)
        {
            var note = _notes[noteIndex];
            if (GetNoteMarkerBounds(note).Contains(point) ||
                note.Anchors.Any(anchor => GetHighlightBounds(anchor).Contains(point)))
            {
                return note.Id;
            }
        }

        return null;
    }

    private Rect GetNoteMarkerBounds(NoteState note)
    {
        if (note.Anchors.Count == 0)
        {
            return Rect.Empty;
        }

        const double markerSize = 13;
        var anchorBounds = GetHighlightBounds(note.Anchors[0]);
        var left = Math.Clamp(anchorBounds.Right + 4, 0, Math.Max(0, ActualWidth - markerSize));
        var top = Math.Clamp(anchorBounds.Top - 2, 0, Math.Max(0, ActualHeight - markerSize));
        return new Rect(left, top, markerSize, markerSize);
    }

    private static HighlightRectangle CreateRectangle(PageWord word)
    {
        return new HighlightRectangle(word.Left, word.Bottom, word.Right, word.Top);
    }
}

internal sealed record TextSelectionSnapshot(
    string Text,
    IReadOnlyList<HighlightRectangle> Rectangles);
