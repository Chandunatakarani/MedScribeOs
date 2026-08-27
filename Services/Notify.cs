using System;
using System.Collections.Generic;
using System.Windows;

namespace MedScribeOS.Services;

public enum ToastKind { Success, Error, Warning, Info }

/// <summary>One user-facing notification: a severity plus the text to show.</summary>
public sealed record Toast(ToastKind Kind, string Message);

/// <summary>
/// App-wide, fire-and-forget user notifications. Any layer - including a
/// service running on a background thread - calls <see cref="Success"/> /
/// <see cref="Error"/> / <see cref="Warning"/> / <see cref="Info"/> and a
/// toast appears in the main window via <see cref="ToastHost"/>.
///
/// The point is that nothing fails (or succeeds) silently: every outcome the
/// provider needs to know about is stated plainly so they acknowledge it and
/// proceed, instead of guessing whether something worked.
///
/// Notifications raised before a <see cref="ToastHost"/> exists (e.g. during
/// startup, before the main window is opened from the tray) are queued and
/// delivered as soon as one subscribes.
/// </summary>
public static class Notify
{
    private const int MaxQueued = 20;

    private static readonly object Gate = new();
    private static readonly List<Toast> Pending = new();
    private static Action<Toast>? _handler;

    /// <summary>Raised on the UI thread once per notification. <see cref="ToastHost"/> subscribes; subscribing also drains anything queued so far.</summary>
    public static event Action<Toast>? Raised
    {
        add
        {
            lock (Gate)
            {
                _handler += value;
                if (value != null && Pending.Count > 0)
                {
                    foreach (var queued in Pending) value(queued);
                    Pending.Clear();
                }
            }
        }
        remove
        {
            lock (Gate) { _handler -= value; }
        }
    }

    public static void Success(string message) => Post(ToastKind.Success, message);
    public static void Error(string message) => Post(ToastKind.Error, message);
    public static void Warning(string message) => Post(ToastKind.Warning, message);
    public static void Info(string message) => Post(ToastKind.Info, message);

    private static void Post(ToastKind kind, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var toast = new Toast(kind, message.Trim());

        void Deliver()
        {
            lock (Gate)
            {
                if (_handler != null)
                {
                    _handler(toast);
                }
                else
                {
                    Pending.Add(toast);
                    if (Pending.Count > MaxQueued) Pending.RemoveAt(0);
                }
            }
        }

        // Toasts are UI; marshal onto the dispatcher so callers on NAudio /
        // Timer / Task threads (DictationEngine, LiveConversationTranscriber,
        // AuthService continuations) don't have to think about it.
        var app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess())
            Deliver();
        else
            app.Dispatcher.BeginInvoke((Action)Deliver);
    }
}
