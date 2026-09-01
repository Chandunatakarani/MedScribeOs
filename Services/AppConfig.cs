using System;
using System.IO;
using System.Text.Json;

namespace MedScribeOS.Services;

/// <summary>
/// One-shot read of <c>%AppData%\MedScribeOS\config.json</c>. Lets the LLM and
/// audio providers be repointed without a rebuild - a local OpenAI-compatible
/// server (LM Studio / Ollama) during development, OpenAI in production.
///
/// Every key is optional; the defaults are OpenAI, so an install that only
/// sets <c>OpenAiApiKey</c> behaves exactly as before.
///
/// The chat/extraction call works against any OpenAI-compatible endpoint.
/// The audio calls (dictation transcription, and the live Voice Analyzer's
/// speaker-diarized transcription) still need OpenAI - Ollama and LM Studio
/// don't do audio, and <c>gpt-4o-transcribe-diarize</c> has no local
/// equivalent.
/// </summary>
public sealed class AppConfig
{
    public const string OpenAiV1 = "https://api.openai.com/v1";

    // ── chat / HPI-ROS extraction (swappable to a local model) ──────────────
    public string ChatBaseUrl { get; private init; } = OpenAiV1;
    public string ChatModel { get; private init; } = "gpt-4o";
    public string? ChatApiKey { get; private init; }
    /// <summary>Send response_format={type:json_object}. Turn off if a local model rejects it.</summary>
    public bool ChatJsonMode { get; private init; } = true;

    // ── audio (OpenAI, or a Whisper-compatible server for dictation only) ───
    public string AudioBaseUrl { get; private init; } = OpenAiV1;
    public string? AudioApiKey { get; private init; }
    public string TranscribeModel { get; private init; } = "whisper-1";
    public string DiarizeModel { get; private init; } = "gpt-4o-transcribe-diarize";

    /// <summary>
    /// "openai" (default) = the Voice Analyzer uses gpt-4o-transcribe-diarize
    /// with the enrolled doctor voice. "off" = plain local transcription only;
    /// Doctor/Patient is inferred from turn-taking (works fully offline, less
    /// accurate on who-said-what, no voice enrollment needed).
    /// </summary>
    public string AudioDiarization { get; private init; } = "openai";

    public bool DiarizationEnabled =>
        !string.Equals(AudioDiarization, "off", StringComparison.OrdinalIgnoreCase);

    public bool ChatIsOpenAi => IsOpenAi(ChatBaseUrl);
    public bool AudioIsOpenAi => IsOpenAi(AudioBaseUrl);

    private static bool IsOpenAi(string url) => url.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MedScribeOS", "config.json");

    public static AppConfig Load()
    {
        JsonElement root = default;
        var haveJson = false;
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                root = doc.RootElement.Clone(); // clone so it outlives the using
                haveJson = root.ValueKind == JsonValueKind.Object;
            }
        }
        catch
        {
            // malformed config -> fall back to defaults + env
        }

        string? Str(string key)
        {
            if (!haveJson || !root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) return null;
            var v = el.GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        bool Bool(string key, bool fallback)
            => haveJson && root.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? el.GetBoolean()
                : fallback;

        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var sharedKey = Str("OpenAiApiKey") ?? (string.IsNullOrWhiteSpace(env) ? null : env.Trim());

        return new AppConfig
        {
            ChatBaseUrl = (Str("ChatBaseUrl") ?? OpenAiV1).TrimEnd('/'),
            ChatModel = Str("ChatModel") ?? "gpt-4o",
            ChatApiKey = Str("ChatApiKey") ?? sharedKey,
            ChatJsonMode = Bool("ChatJsonMode", true),
            AudioBaseUrl = (Str("AudioBaseUrl") ?? OpenAiV1).TrimEnd('/'),
            AudioApiKey = Str("AudioApiKey") ?? sharedKey,
            TranscribeModel = Str("TranscribeModel") ?? "whisper-1",
            DiarizeModel = Str("DiarizeModel") ?? "gpt-4o-transcribe-diarize",
            AudioDiarization = Str("AudioDiarization") ?? "openai",
        };
    }

    /// <summary>Writes a fully-commented template config on first run, so the fix for a setup error is "open this file and fill it in".</summary>
    public static void EnsureTemplate()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath)) return;

            var template = new
            {
                _help = new[]
                {
                    "Every key below is optional - blank/absent means use the default shown.",
                    "PRODUCTION (OpenAI): set OpenAiApiKey; leave the *BaseUrl / *Model keys as-is.",
                    "DEV - LM Studio: ChatBaseUrl = http://localhost:1234/v1 ; ChatModel = <the model id shown in LM Studio> ; no key needed.",
                    "DEV - Ollama:    ChatBaseUrl = http://localhost:11434/v1 ; ChatModel = llama3.1:8b (or your pulled model) ; no key needed.",
                    "If a local model rejects strict JSON mode, set ChatJsonMode = false.",
                    "DICTATION can run local: point AudioBaseUrl at a faster-whisper server (e.g. Speaches on http://localhost:8000/v1) and set TranscribeModel to its model id.",
                    "VOICE ANALYZER: gpt-4o-transcribe-diarize (speaker-anchored) is OpenAI only. To test fully offline set AudioDiarization = off - it then uses plain local transcription and infers Doctor/Patient from turn-taking (no voice enrollment needed, less accurate on who-said-what)."
                },
                OpenAiApiKey = "",
                ChatBaseUrl = OpenAiV1,
                ChatModel = "gpt-4o",
                ChatApiKey = "",
                ChatJsonMode = true,
                AudioBaseUrl = OpenAiV1,
                AudioApiKey = "",
                TranscribeModel = "whisper-1",
                DiarizeModel = "gpt-4o-transcribe-diarize",
                AudioDiarization = "openai",
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort - the thrown setup error still names the exact path
        }
    }
}
