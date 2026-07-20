using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProPdfReader.Viewing;

internal sealed class PdfPageViewModel : INotifyPropertyChanged
{
    private double _sourceWidth;
    private double _sourceHeight;
    private double _displayWidth;
    private double _displayHeight;

    internal PdfPageViewModel(uint pageIndex, double sourceWidth, double sourceHeight)
    {
        PageIndex = pageIndex;
        SetSourceSize(sourceWidth, sourceHeight);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal uint PageIndex { get; }

    internal double SourceWidth => _sourceWidth;

    internal double SourceHeight => _sourceHeight;

    public double DisplayWidth
    {
        get => _displayWidth;
        private set => SetField(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        private set => SetField(ref _displayHeight, value);
    }

    internal void SetSourceSize(double width, double height)
    {
        _sourceWidth = width;
        _sourceHeight = height;
    }

    internal void UpdateDisplay(double scale)
    {
        var baseWidth = Math.Min(Math.Max(_sourceWidth, 520), 1200);
        DisplayWidth = baseWidth * scale;
        DisplayHeight = baseWidth * _sourceHeight / _sourceWidth * scale;
    }

    private void SetField(ref double field, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.01)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
