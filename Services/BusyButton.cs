using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MedScribeOS.Services;

/// <summary>
/// Puts a button into a "working" state for the duration of an async
/// operation: disabled, with a small spinning arc + busy label in place of
/// its normal content. Dispose restores everything, so the call sites stay
/// a one-liner:
///
///     using var busy = BusyButton.Begin(BtnAnalyze, "Analyzing…");
///
/// The spinner inherits the button's Foreground, so it's white on primary
/// buttons and dark on ghost buttons without any extra wiring.
/// </summary>
public static class BusyButton
{
    public static IDisposable Begin(Button button, string busyText)
    {
        var scope = new Scope(button, button.Content, button.IsEnabled);
        button.IsEnabled = false;

        var spinner = new Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 2.4,
            Stroke = button.Foreground,
            // dash gap turns the ring into an arc, which is what reads as "spinning"
            StrokeDashArray = new DoubleCollection { 2.4, 1.6 },
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var rotate = new RotateTransform();
        spinner.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(spinner);
        panel.Children.Add(new TextBlock
        {
            Text = busyText,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        button.Content = panel;

        return scope;
    }

    private sealed class Scope : IDisposable
    {
        private readonly Button _button;
        private readonly object _originalContent;
        private readonly bool _wasEnabled;
        private bool _disposed;

        public Scope(Button button, object originalContent, bool wasEnabled)
        {
            _button = button;
            _originalContent = originalContent;
            _wasEnabled = wasEnabled;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _button.Content = _originalContent;
            _button.IsEnabled = _wasEnabled;
        }
    }
}
