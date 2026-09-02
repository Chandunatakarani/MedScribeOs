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

    /// <summary>HTTP timeout for a chat/extraction call. Local models on CPU can be slow, so the default is generous for a non-OpenAI endpoint.</summary>
    public int ChatTimeoutSeconds { get; private init; } = 180;

    // ── audio (OpenAI, or a Whisper-compatible server for dictation only) ───
    public string AudioBaseUrl { get; private init; } = OpenAiV1;
    public string? AudioApiKey { get; private init; }
    public string TranscribeModel { get; private init; } = "whisper-1";
    public string DiarizeModel { get; private init; } = "gpt-4o-transcribe-diarize";

    /// <summary>HTTP timeout for one transcription request. A local model's first call has to load the model into RAM, which can take minutes on CPU - hence a large default for a non-OpenAI endpoint.</summary>
    public int AudioTimeoutSeconds { get; private init; } = 180;

    /// <summary>File Analyzer splits recordings longer than this into chunks of this length so each request stays fast and under the size limit.</summary>
    public int AudioChunkSeconds { get; private init; } = 45;

    /// <summary>
    /// How many chunk transcriptions to run at once in File Analyzer. Parallel
    /// requests cut wall-time a lot against a cloud endpoint; against a
    /// CPU-bound local model they mostly just contend for the same cores, so
    /// the default there is 1.
    /// </summary>
    public int AudioMaxParallel { get; private init; } = 1;

    /// <summary>
    /// "openai" (default) = the Voice Analyzer uses gpt-4o-transcribe-diarize
    /// with the enrolled doctor voice. "off" = plain local transcription only;
    /// Doctor/Patient is inferred from turn-taking (works fully offline, less
    /// accurate on who-said-what, no voice enrollment needed).
    /// </summary>
    public string AudioDiarization { get; private init; } = "openai";

    /// <summary>
    /// Passed to plain transcription (Whisper / local) as the "prompt" - primes
    /// the model with medical vocabulary and drug names so it mis-hears fewer
    /// clinical terms. Not used on the diarized OpenAI path.
    /// </summary>
    public string AudioPrompt { get; private init; } = DefaultAudioPrompt;

    /// <summary>Segments with a higher no-speech probability than this are dropped as silence/noise.</summary>
    public double AudioMaxNoSpeechProb { get; private init; } = 0.6;

    /// <summary>
    /// Segments whose text compresses better than this ratio are dropped as
    /// repetition loops ("a, a, a, a…") - the same signal Whisper itself uses
    /// to declare a decoding failure (its internal default is 2.4). Looping
    /// segments observed in practice score 4-20.
    /// </summary>
    public double AudioMaxCompressionRatio { get; private init; } = 2.4;

    /// <summary>
    /// Ask a local Whisper server to run voice-activity detection first, so
    /// long silences never reach the model - silence is the main trigger for
    /// repetition loops. Only sent to non-OpenAI endpoints (OpenAI's API does
    /// not accept the parameter).
    /// </summary>
    public bool AudioVadFilter { get; private init; } = true;

    /// <summary>
    /// Segments with a lower average token log-probability than this are dropped
    /// as likely hallucinations. Small local models score systematically lower
    /// than OpenAI Whisper, so the default is looser for a local endpoint
    /// (otherwise real speech gets discarded and the transcript looks "wrong").
    /// </summary>
    public double AudioMinAvgLogProb { get; private init; } = -1.0;

    private const string DefaultAudioPrompt =
        "A clinical consultation between a doctor and a patient. Possible terms: hypertension, " +
        "type 2 diabetes mellitus, hyperlipidemia, GERD, COPD, asthma, myocardial infarction, dyspnea, " +
        "metformin, lisinopril, atorvastatin, amlodipine, omeprazole, albuterol, blood pressure, HbA1c, " +
        "CBC, milligrams, twice daily, chief complaint, review of systems.";

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

        double? Num(string key)
            => haveJson && root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number
                ? el.GetDouble()
                : null;

        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var sharedKey = Str("OpenAiApiKey") ?? (string.IsNullOrWhiteSpace(env) ? null : env.Trim());
        var chatBaseUrl = (Str("ChatBaseUrl") ?? OpenAiV1).TrimEnd('/');
        var audioBaseUrl = (Str("AudioBaseUrl") ?? OpenAiV1).TrimEnd('/');
        var audioIsOpenAi = IsOpenAi(audioBaseUrl);

        return new AppConfig
        {
            ChatBaseUrl = chatBaseUrl,
            ChatModel = Str("ChatModel") ?? "gpt-4o",
            ChatApiKey = Str("ChatApiKey") ?? sharedKey,
            ChatJsonMode = Bool("ChatJsonMode", true),
            ChatTimeoutSeconds = (int)(Num("ChatTimeoutSeconds") ?? (IsOpenAi(chatBaseUrl) ? 180 : 600)),
            AudioBaseUrl = audioBaseUrl,
            AudioApiKey = Str("AudioApiKey") ?? sharedKey,
            TranscribeModel = Str("TranscribeModel") ?? "whisper-1",
            DiarizeModel = Str("DiarizeModel") ?? "gpt-4o-transcribe-diarize",
            AudioTimeoutSeconds = (int)(Num("AudioTimeoutSeconds") ?? (audioIsOpenAi ? 180 : 1200)),
            AudioChunkSeconds = Math.Max(10, (int)(Num("AudioChunkSeconds") ?? 45)),
            AudioMaxParallel = Math.Clamp((int)(Num("AudioMaxParallel") ?? (audioIsOpenAi ? 4 : 1)), 1, 12),
            AudioDiarization = Str("AudioDiarization") ?? "openai",
            AudioPrompt = Str("AudioPrompt") ?? DefaultAudioPrompt,
            AudioMaxNoSpeechProb = Num("AudioMaxNoSpeechProb") ?? 0.6,
            AudioMaxCompressionRatio = Num("AudioMaxCompressionRatio") ?? 2.4,
            AudioVadFilter = Bool("AudioVadFilter", true),
            // looser hallucination cutoff for small local models
            AudioMinAvgLogProb = Num("AudioMinAvgLogProb") ?? (audioIsOpenAi ? -1.0 : -2.2),
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
                    "VOICE ANALYZER: gpt-4o-transcribe-diarize (speaker-anchored) is OpenAI only. To test fully offline set AudioDiarization = off - it then uses plain local transcription and infers Doctor/Patient from turn-taking (no voice enrollment needed, less accurate on who-said-what).",
                    "ACCURACY: use TranscribeModel = deepdml/faster-whisper-large-v3-turbo-ct2 (or Systran/faster-whisper-medium) - the 'small' model is poor on medical terms. AudioPrompt primes clinical vocabulary. Loosen AudioMinAvgLogProb (e.g. -3.0) if words are being dropped.",
                    "TIMEOUTS: a local model's FIRST request loads it into RAM (minutes on CPU). AudioTimeoutSeconds / ChatTimeoutSeconds default high for a local endpoint; raise them further, or lower AudioChunkSeconds (e.g. 30), if you still hit timeouts.",
                    "SPEED: AudioMaxParallel transcribes that many chunks at once (default 4 for OpenAI, 1 for local - parallel barely helps a CPU-bound local model)."
                },
                OpenAiApiKey = "",
                ChatBaseUrl = OpenAiV1,
                ChatModel = "gpt-4o",
                ChatApiKey = "",
                ChatJsonMode = true,
                ChatTimeoutSeconds = 180,
                AudioBaseUrl = OpenAiV1,
                AudioApiKey = "",
                TranscribeModel = "whisper-1",
                DiarizeModel = "gpt-4o-transcribe-diarize",
                AudioTimeoutSeconds = 180,
                AudioChunkSeconds = 45,
                AudioMaxParallel = 4,
                AudioDiarization = "openai",
                AudioPrompt = "",
                AudioMaxNoSpeechProb = 0.6,
                AudioMinAvgLogProb = -1.0,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort - the thrown setup error still names the exact path
        }
    }
}
