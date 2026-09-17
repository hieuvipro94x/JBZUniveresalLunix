using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JBZUniveresalLunix.Views;

/// <summary>
/// Draws centered text with a real outline so wire-color codes remain readable
/// on both light and dark solid/striped backgrounds.
/// </summary>
public sealed class OutlinedTextBlock : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(
            1.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double thickness && double.IsFinite(thickness) && thickness >= 0);

    public OutlinedTextBlock()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        FormattedText text = CreateFormattedText();
        double outlineSpace = StrokeThickness * 2;
        return new Size(
            Math.Ceiling(text.WidthIncludingTrailingWhitespace + outlineSpace),
            Math.Ceiling(text.Height + outlineSpace));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (string.IsNullOrEmpty(Text))
            return;

        FormattedText text = CreateFormattedText();
        Point origin = new(
            Math.Max(StrokeThickness, (RenderSize.Width - text.WidthIncludingTrailingWhitespace) / 2),
            Math.Max(StrokeThickness, (RenderSize.Height - text.Height) / 2));
        Geometry glyphs = text.BuildGeometry(origin);
        Pen? outline = StrokeThickness > 0 && Stroke is not null
            ? new Pen(Stroke, StrokeThickness)
            : null;

        if (outline?.CanFreeze == true)
            outline.Freeze();

        drawingContext.DrawGeometry(Foreground, outline, glyphs);
    }

    private FormattedText CreateFormattedText() => new(
        Text ?? string.Empty,
        CultureInfo.CurrentUICulture,
        FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
        FontSize,
        Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
