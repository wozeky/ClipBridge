using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ClipBridge.App;

/// <summary>Samples the actual animated material behind this surface, with a small optical
/// enlargement, local blur, inset shade and a separate specular rim. Never captures the desktop.</summary>
public sealed class ChromeGlass : ContentControl
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(Visual), typeof(ChromeGlass), new PropertyMetadata(null, Rebuild));
    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(
        nameof(Region), typeof(Rect), typeof(ChromeGlass), new PropertyMetadata(new Rect(0, 0, 1, 1), Rebuild));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(ChromeGlass), new PropertyMetadata(32d, Rebuild));
    public Visual? Scene { get => (Visual?)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public Rect Region { get => (Rect)GetValue(RegionProperty); set => SetValue(RegionProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    private Grid? _material;
    private Border? _lens;

    public ChromeGlass()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var material = new FrameworkElementFactory(typeof(Grid), "Material");
        root.AppendChild(material);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        root.AppendChild(presenter);
        Template = new ControlTemplate(typeof(ChromeGlass)) { VisualTree = root };
        SizeChanged += (_, _) => UpdateMaterial();
        IsHitTestVisible = true;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _material = (Grid)GetTemplateChild("Material");
        UpdateMaterial();
    }

    private static void Rebuild(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ChromeGlass)d).UpdateMaterial();

    private void UpdateMaterial()
    {
        if (_material is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        _material.Children.Clear();
        _material.IsHitTestVisible = false;
        _material.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), Radius, Radius);
        var region = Region;
        region.Inflate(-region.Width * .012, -region.Height * .012);
        _lens = new Border
        {
            Background = new VisualBrush(Scene) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region,
                Stretch = Stretch.Fill },
            Effect = new BlurEffect { Radius = 4, RenderingBias = RenderingBias.Performance },
            Margin = new Thickness(-3), Opacity = .72
        };
        _material.Children.Add(_lens);
        var tint = new LinearGradientBrush();
        tint.StartPoint = new Point(0, 0); tint.EndPoint = new Point(.85, 1);
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(38, 170, 178, 183), 0));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(150, 5, 6, 6), .22));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(75, 3, 4, 4), .64));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(50, 128, 132, 132), 1));
        _material.Children.Add(new Border { CornerRadius = new CornerRadius(Radius), Background = tint });
        var rim = new LinearGradientBrush();
        rim.StartPoint = new Point(0, 0); rim.EndPoint = new Point(.85, 1);
        foreach (var stop in new[] { ("#DDD9DFDF",0d),("#284A4E4E",.24),("#05787B7B",.49),("#AA989E9E",.79),("#323E4242",1d) })
            rim.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stop.Item1), stop.Item2));
        _material.Children.Add(new Border { CornerRadius = new CornerRadius(Radius), BorderBrush = rim,
            BorderThickness = new Thickness(1.15), Margin = new Thickness(.6) });
        _material.Children.Add(new Border { CornerRadius = new CornerRadius(Math.Max(0, Radius-2)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(35, 230, 237, 237)),
            BorderThickness = new Thickness(.6, .6, 0, 0), Margin = new Thickness(3) });
    }
}
