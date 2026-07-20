using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProPdfReader.Text;
using ProPdfReader.Viewing;

namespace ProPdfReader.Controls;

public sealed class PdfPageView : Border
{
    private readonly Image _image;

    public PdfPageView()
    {
        Margin = new Thickness(24, 12, 24, 12);
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(85, 90, 98));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);
        HorizontalAlignment = HorizontalAlignment.Center;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        TextLayer = new TextSelectionLayer();
        AutomationProperties.SetName(TextLayer, "Selectable PDF text");

        var content = new Grid();
        content.Children.Add(_image);
        content.Children.Add(TextLayer);
        Child = content;
    }

    internal event Action<PdfPageView>? SelectionChanged;

    internal event Action<PdfPageView>? HighlightRequested;

    internal event Action<PdfPageView, Guid>? HighlightRemovalRequested;

    internal event Action<PdfPageView>? NoteRequested;

    internal event Action<PdfPageView, Guid>? NoteEditRequested;

    internal event Action<PdfPageView, Guid>? NoteRemovalRequested;

    internal event Action<PdfPageView, PageLink>? LinkRequested;

    internal TextSelectionLayer TextLayer { get; }

    internal PdfPageViewModel? Model => DataContext as PdfPageViewModel;

    internal BitmapSource? ImageSource => _image.Source as BitmapSource;

    internal bool EventsAttached { get; private set; }

    internal void AttachEvents()
    {
        if (EventsAttached)
        {
            return;
        }

        EventsAttached = true;
        TextLayer.SelectionChanged += () => SelectionChanged?.Invoke(this);
        TextLayer.HighlightRequested += () => HighlightRequested?.Invoke(this);
        TextLayer.HighlightRemovalRequested += id => HighlightRemovalRequested?.Invoke(this, id);
        TextLayer.NoteRequested += () => NoteRequested?.Invoke(this);
        TextLayer.NoteEditRequested += id => NoteEditRequested?.Invoke(this, id);
        TextLayer.NoteRemovalRequested += id => NoteRemovalRequested?.Invoke(this, id);
        TextLayer.LinkRequested += link => LinkRequested?.Invoke(this, link);
    }

    internal void SetImage(BitmapSource image)
    {
        _image.Source = image;
    }

    internal void SetRotation(int rotation)
    {
        LayoutTransform = rotation == 0 ? Transform.Identity : new RotateTransform(rotation);
    }
}
