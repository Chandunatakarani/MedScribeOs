using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MedScribeOS.Models;

namespace MedScribeOS.Services;

// A single chat-bubble DataTemplate drives both sides; these converters,
// keyed off ConversationTurn.Speaker, flip alignment / colour / corner /
// header so there's no copy-pasted per-speaker template.

/// <summary>Doctor turns hug the right edge, patient turns the left - messaging-app style.</summary>
public sealed class SpeakerToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IsDoctor(value) ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static bool IsDoctor(object? value) => value is ConversationTurn { IsDoctor: true };

    internal static Brush AppBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>Doctor bubble = accent blue (the "me" side); patient bubble = light gray.</summary>
public sealed class SpeakerToBubbleBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SpeakerToAlignmentConverter.IsDoctor(value)
            ? SpeakerToAlignmentConverter.AppBrush("AccentBrush", Color.FromRgb(0x00, 0x67, 0xE0))
            : SpeakerToAlignmentConverter.AppBrush("BubbleOtherBrush", Color.FromRgb(0xE5, 0xE5, 0xEA));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Text colour that stays readable on each bubble background.</summary>
public sealed class SpeakerToForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SpeakerToAlignmentConverter.IsDoctor(value)
            ? Brushes.White
            : SpeakerToAlignmentConverter.AppBrush("TextPrimaryBrush", Color.FromRgb(0x1D, 0x1D, 0x1F));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Speech-bubble corner: the "tail" corner (bottom, toward the speaker's edge) is squared off.</summary>
public sealed class SpeakerToCornerConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SpeakerToAlignmentConverter.IsDoctor(value)
            ? new CornerRadius(14, 14, 4, 14)
            : new CornerRadius(14, 14, 14, 4);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"Dr. [Name]" for the doctor (name from the current session), "Patient" otherwise.</summary>
public sealed class SpeakerToHeaderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConversationTurn turn) return "";
        if (!turn.IsDoctor) return "Patient";
        var name = SessionService.Instance.DoctorDisplayName;
        return string.IsNullOrWhiteSpace(name) ? "Doctor" : $"Dr. {name}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
