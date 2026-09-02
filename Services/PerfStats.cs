using System;
using System.IO;
using System.Text.Json;

namespace MedScribeOS.Services;

/// <summary>
/// Remembers how fast THIS machine's providers actually are, so the File
/// Analyzer can show a real ETA instead of an open-ended spinner. Two rates,
/// learned from completed operations and smoothed with an EMA (so one
/// cold-start outlier doesn't wreck the estimate):
///
///  - TranscribeSecPerAudioSec: processing seconds per second of audio.
///  - ChatCharsPerSec: source characters per second for an Analyze call.
///
/// Persisted to %LocalAppData%\MedScribeOS\perf.json across runs. Until a
/// rate has been observed once, it's null and the UI shows elapsed time only -
/// no made-up numbers.
/// </summary>
public static class PerfStats
{
    private const double EmaAlpha = 0.4; // weight of the newest observation

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MedScribeOS", "perf.json");

    private static double? _transcribeSecPerAudioSec;
    private static double? _chatCharsPerSec;
    private static bool _loaded;

    public static double? TranscribeSecPerAudioSec { get { Load(); return _transcribeSecPerAudioSec; } }
    public static double? ChatCharsPerSec { get { Load(); return _chatCharsPerSec; } }

    /// <summary>Record a finished transcription: how long it took for how much audio.</summary>
    public static void ObserveTranscribe(TimeSpan audio, TimeSpan elapsed)
    {
        if (audio.TotalSeconds < 1 || elapsed.TotalSeconds < 0.5) return;
        Load();
        _transcribeSecPerAudioSec = Ema(_transcribeSecPerAudioSec, elapsed.TotalSeconds / audio.TotalSeconds);
        Save();
    }

    /// <summary>Record a finished Analyze: how long the extraction took for how much source text.</summary>
    public static void ObserveChat(int sourceChars, TimeSpan elapsed)
    {
        if (sourceChars < 200 || elapsed.TotalSeconds < 0.5) return;
        Load();
        _chatCharsPerSec = Ema(_chatCharsPerSec, sourceChars / elapsed.TotalSeconds);
        Save();
    }

    private static double Ema(double? current, double observed)
        => current is { } c ? c + EmaAlpha * (observed - c) : observed;

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("TranscribeSecPerAudioSec", out var t) && t.ValueKind == JsonValueKind.Number)
                _transcribeSecPerAudioSec = t.GetDouble();
            if (doc.RootElement.TryGetProperty("ChatCharsPerSec", out var c) && c.ValueKind == JsonValueKind.Number)
                _chatCharsPerSec = c.GetDouble();
        }
        catch { /* corrupt stats are not worth an error - just relearn */ }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                TranscribeSecPerAudioSec = _transcribeSecPerAudioSec,
                ChatCharsPerSec = _chatCharsPerSec,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
