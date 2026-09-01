using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MedScribeOS.Services;

/// <summary>
/// A bottom-right stack of toast cards that shows every <see cref="Notify"/>
/// call. Drop one into a Grid cell that spans the whole window - it aligns
/// itself into the corner and floats above everything else.
///
/// Success / Warning / Info cards fade out on their own after a few seconds.
/// Error cards stay until the provider clicks them (or the X), so a failure
/// can never scroll past unseen - they have to acknowledge it and move on.
/// </summary>
public sealed class ToastHost : ItemsControl
{
    private const int MaxVisible = 5;

    public ToastHost()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 16, 16);
        Panel.SetZIndex(this, 10_000);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        ItemsPanel = new ItemsPanelTemplate(stack);

        Loaded += (_, _) => Notify.Raised += OnRaised;
        Unloaded += (_, _) => Notify.Raised -= OnRaised;
    }

    private void OnRaised(Toast toast)
    {
        var card = BuildCard(toast);
        Items.Add(card);

        while (Items.Count > MaxVisible) Items.RemoveAt(0);

        FadeIn(card);

        if (toast.Kind != ToastKind.Error)
        {
            var linger = toast.Kind == ToastKind.Success ? 4.5 : 6.0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(linger) };
            timer.Tick += (_, _) => { timer.Stop(); Dismiss(card); };
            timer.Start();
        }
    }

    private Border BuildCard(Toast toast)
    {
        var accent = AccentFor(toast.Kind);

        var glyph = new TextBlock
        {
            Text = GlyphFor(toast.Kind),
            Foreground = accent,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(glyph, Dock.Left);

        var close = new Button
        {
            Content = "✕",
            Foreground = Brush("TextSecondaryBrush", Colors.Gray),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            FontSize = 12,
        };
        DockPanel.SetDock(close, Dock.Right);

        var message = new TextBlock
        {
            Text = toast.Message,
            Foreground = Brush("TextPrimaryBrush", Color.FromRgb(0x1C, 0x1C, 0x1E)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
        };

        var body = new DockPanel { LastChildFill = true };
        body.Children.Add(glyph);
        body.Children.Add(close);
        body.Children.Add(message);

        var card = new Border
        {
            Background = Brush("BgCardBrush", Colors.White),
            BorderBrush = Brush("BorderBrush2", Color.FromRgb(0xD2, 0xD2, 0xD7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = 400,
            Child = body,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.14 },
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        close.Click += (_, _) => Dismiss(card);
        // Click anywhere on an error card to acknowledge and clear it.
        card.MouseLeftButtonUp += (_, _) => Dismiss(card);

        return card;
    }

    private void FadeIn(UIElement card)
    {
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void Dismiss(Border card)
    {
        if (!Items.Contains(card)) return;

        var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (Items.Contains(card)) Items.Remove(card); };
        card.BeginAnimation(OpacityProperty, fade);
    }

    private static string GlyphFor(ToastKind kind) => kind switch
    {
        ToastKind.Success => "✓", // check
        ToastKind.Error => "✕",   // x
        ToastKind.Warning => "⚠", // warning triangle
        _ => "ℹ",                 // info
    };

    private Brush AccentFor(ToastKind kind) => kind switch
    {
        ToastKind.Error => Brush("RedBrush", Color.FromRgb(0xFF, 0x3B, 0x30)),          // Apple red
        ToastKind.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)),      // Apple orange
        ToastKind.Success => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),      // Apple green
        _ => Brush("AccentBrush", Color.FromRgb(0x00, 0x7A, 0xFF)),                     // Apple blue
    };

    /// <summary>App brush by key, with a hard-coded fallback so the control still renders if used outside the styled app.</summary>
    private Brush Brush(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush b) return b;
        return new SolidColorBrush(fallback);
    }
}
