using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MedScribeOS.Models;

namespace MedScribeOS.Services;

/// <summary>
/// Output of a template-driven extraction: section key -> (field key -> value).
/// Shape is entirely determined by the <see cref="NoteTemplate"/> that was
/// passed in, so a doctor's custom schema comes straight back out.
/// </summary>
public sealed class TemplateExtractionResult
{
    public Dictionary<string, Dictionary<string, string>> Sections { get; } = new();
}

/// <summary>
/// The app's LLM + audio calls. The chat/extraction call goes to whatever
/// OpenAI-compatible endpoint <see cref="AppConfig"/> points at (OpenAI in
/// production, a local LM Studio / Ollama server in development); the audio
/// calls still target OpenAI (Whisper + gpt-4o-transcribe-diarize).
/// </summary>
public sealed class OpenAiClient
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _chatHttp;
    private readonly HttpClient _audioHttp;

    public OpenAiClient(AppConfig? config = null)
    {
        _cfg = config ?? AppConfig.Load();

        // Only OpenAI endpoints need a key; a local model server usually doesn't.
        var missing = new List<string>();
        if (_cfg.ChatIsOpenAi && string.IsNullOrWhiteSpace(_cfg.ChatApiKey))
            missing.Add("the chat model is OpenAI but no API key is set");
        if (_cfg.AudioIsOpenAi && string.IsNullOrWhiteSpace(_cfg.AudioApiKey))
            missing.Add("audio (dictation / Voice Analyzer) uses OpenAI but no API key is set");

        if (missing.Count > 0)
        {
            AppConfig.EnsureTemplate();
            throw new InvalidOperationException(
                $"MedScribe isn't configured - {string.Join("; ", missing)}. Set the OPENAI_API_KEY " +
                $"environment variable, or open {AppConfig.FilePath} and fill in OpenAiApiKey, then restart. " +
                $"To run against a local model instead, point ChatBaseUrl at LM Studio or Ollama in that file.");
        }

        _chatHttp = MakeClient(_cfg.ChatBaseUrl, _cfg.ChatApiKey);
        _audioHttp = MakeClient(_cfg.AudioBaseUrl, _cfg.AudioApiKey);
    }

    private static HttpClient MakeClient(string baseUrl, string? apiKey)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // A local model on CPU can take a while for the first token.
            Timeout = TimeSpan.FromMinutes(3),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    /// <summary>Ports transcribe() - sends the recorded WAV to Whisper, then filters out hallucinated segments using Whisper's own confidence signals.</summary>
    public async Task<string> TranscribeAsync(string audioFilePath)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(audioFilePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(MimeForAudio(audioFilePath));

        form.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        form.Add(new StringContent(_cfg.TranscribeModel), "model");
        form.Add(new StringContent("en"), "language");
        // verbose_json instead of plain text - this gives us per-segment
        // no_speech_prob and avg_logprob, which is how we filter out
        // hallucinated output below instead of trusting whatever text
        // Whisper returns unconditionally.
        form.Add(new StringContent("verbose_json"), "response_format");
        // Prime the model with clinical vocabulary so it mis-hears fewer terms.
        if (!string.IsNullOrWhiteSpace(_cfg.AudioPrompt))
            form.Add(new StringContent(_cfg.AudioPrompt), "prompt");

        var response = await _audioHttp.PostAsync("audio/transcriptions", form);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Transcription failed ({_cfg.TranscribeModel} @ {_cfg.AudioBaseUrl}): {body}");
        }

        return ExtractRealSpeechFromVerboseJson(body);
    }

    /// <summary>
    /// Keeps only segments Whisper's own signals say are real speech -
    /// filtering hallucinated text (common on silence/background noise/
    /// non-speech sound) using the exact metrics OpenAI documents for this:
    /// no_speech_prob (its estimate that a segment is silence, not speech)
    /// and avg_logprob (how confident it was in the words it produced). This
    /// is far more reliable than guessing from raw audio amplitude beforehand,
    /// since it uses Whisper's own assessment of what it just transcribed.
    /// Thresholds are conservative on purpose - missing a real quiet phrase
    /// is far preferable to fabricating text in a patient's chart.
    /// </summary>
    private string ExtractRealSpeechFromVerboseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("segments", out var segmentsEl))
        {
            return root.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "";
        }

        var keptSegments = new List<string>();
        foreach (var segment in segmentsEl.EnumerateArray())
        {
            var text = segment.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (IsLikelyHallucination(segment)) continue;
            keptSegments.Add(text.Trim());
        }

        return string.Join(" ", keptSegments).Trim();
    }

    /// <summary>
    /// True if Whisper's own per-segment signals say this isn't real speech.
    /// Thresholds are config-driven (AppConfig.AudioMaxNoSpeechProb /
    /// AudioMinAvgLogProb) because small local models score confidence lower
    /// than OpenAI Whisper - too strict a cutoff silently drops real speech.
    /// </summary>
    private bool IsLikelyHallucination(JsonElement segment)
    {
        var noSpeechProb = segment.TryGetProperty("no_speech_prob", out var nsp) && nsp.ValueKind == JsonValueKind.Number ? nsp.GetDouble() : 0;
        var avgLogProb = segment.TryGetProperty("avg_logprob", out var alp) && alp.ValueKind == JsonValueKind.Number ? alp.GetDouble() : 0;
        return noSpeechProb > _cfg.AudioMaxNoSpeechProb || avgLogProb < _cfg.AudioMinAvgLogProb;
    }

    private static string MimeForAudio(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    /// <summary>True when the Voice Analyzer uses real (voice-anchored) diarization; false = local transcription only, roles inferred by turn-taking.</summary>
    public bool DiarizationEnabled => _cfg.DiarizationEnabled;

    /// <summary>
    /// Transcribes one live audio chunk. With diarization on it also labels
    /// each segment's speaker (gpt-4o-transcribe-diarize, anchored to the
    /// enrolled doctor voice); with it off it's plain transcription and the
    /// caller (<see cref="SpeakerAttributionRefiner"/>) assigns Doctor/Patient
    /// by turn-taking. Returns the model's segments raw. One chunk can yield
    /// several segments if a quick back-and-forth happened inside it.
    /// </summary>
    public async Task<List<RawDiarizedSegment>> TranscribeAndDiarizeRawAsync(string audioFilePath, string? doctorReferenceDataUrl)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(audioFilePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        if (_cfg.DiarizationEnabled)
        {
            // Voice-anchored diarization (OpenAI): returns per-segment "speaker".
            form.Add(new StringContent(_cfg.DiarizeModel), "model");
            form.Add(new StringContent("diarized_json"), "response_format");
            form.Add(new StringContent("auto"), "chunking_strategy");
            if (!string.IsNullOrWhiteSpace(doctorReferenceDataUrl))
            {
                form.Add(new StringContent("Doctor"), "known_speaker_names[]");
                form.Add(new StringContent(doctorReferenceDataUrl), "known_speaker_references[]");
            }
        }
        else
        {
            // Offline mode: plain transcription. No "speaker" field, so the
            // refiner falls back to turn-taking to assign Doctor/Patient.
            form.Add(new StringContent(_cfg.TranscribeModel), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("en"), "language");
            if (!string.IsNullOrWhiteSpace(_cfg.AudioPrompt))
                form.Add(new StringContent(_cfg.AudioPrompt), "prompt");
        }

        var response = await _audioHttp.PostAsync("audio/transcriptions", form);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var model = _cfg.DiarizationEnabled ? _cfg.DiarizeModel : _cfg.TranscribeModel;
            throw new InvalidOperationException($"Live transcription failed ({model} @ {_cfg.AudioBaseUrl}): {body}");
        }

        return ParseRawDiarizedSegments(body);
    }

    private List<RawDiarizedSegment> ParseRawDiarizedSegments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var segments = new List<RawDiarizedSegment>();

        if (!doc.RootElement.TryGetProperty("segments", out var segmentsEl))
            return segments;

        foreach (var segment in segmentsEl.EnumerateArray())
        {
            var text = segment.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Drop hallucinations on the plain-transcription path (diarized_json
            // carries no confidence fields, so this is a no-op for that path).
            if (IsLikelyHallucination(segment)) continue;

            var rawSpeaker = segment.TryGetProperty("speaker", out var s) ? s.GetString() ?? "" : "";
            double? start = segment.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetDouble() : null;
            double? end = segment.TryGetProperty("end", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetDouble() : null;

            segments.Add(new RawDiarizedSegment(rawSpeaker, text.Trim(), start, end));
        }

        return segments;
    }

    /// <summary>
    /// Extracts structured clinical information into EXACTLY the shape of the
    /// doctor's chosen <see cref="NoteTemplate"/> - sections, field keys, and
    /// per-field "prompt" guidance all come from the template. Takes the
    /// speaker-tagged live conversation.
    /// </summary>
    public Task<TemplateExtractionResult> ExtractStructuredAsync(List<ConversationTurn> turns, NoteTemplate template)
        => ExtractStructuredCoreAsync(string.Join("\n", turns.Select(t => $"{t.SpeakerLabel}: {t.Text}")), template);

    /// <summary>Same extraction, from a plain block of text (File Analyzer - a pasted transcript, a transcribed recording, or a document).</summary>
    public Task<TemplateExtractionResult> ExtractStructuredFromTextAsync(string sourceText, NoteTemplate template)
        => ExtractStructuredCoreAsync(sourceText, template);

    private async Task<TemplateExtractionResult> ExtractStructuredCoreAsync(string sourceText, NoteTemplate template)
    {
        var templateLines = new List<string>();
        var schemaSections = new List<string>();
        foreach (var section in template.Sections)
        {
            templateLines.Add($"{section.Label} [{section.SectionKey}]:");
            var pairs = new List<string>();
            foreach (var field in section.Fields)
            {
                var hint = string.IsNullOrWhiteSpace(field.Prompt) ? "" : $" — {field.Prompt}";
                templateLines.Add($"  - {field.Label} [{field.FieldKey}]{hint}");
                pairs.Add($"\"{field.FieldKey}\": \"\"");
            }
            schemaSections.Add($"\"{section.SectionKey}\": {{ {string.Join(", ", pairs)} }}");
        }

        var schema = "{\n  \"sections\": {\n    " + string.Join(",\n    ", schemaSections) + "\n  }\n}";

        var prompt = $$"""
            You are a board-certified medical scribe. Extract structured clinical information from the clinical source text below into EXACTLY the schema below - do not add, drop, or rename any key.

            TEMPLATE (each field shows its key and guidance on what to capture):
            {{string.Join("\n", templateLines)}}

            SOURCE TEXT:
            {{sourceText}}

            Return ONLY valid JSON in this exact shape (no markdown, no preamble):
            {{schema}}

            Use "Not discussed" for any field the source text does not cover. Be concise and clinically accurate.
            """;

        var json = await ChatJsonAsync(prompt);
        return ParseStructured(json, template);
    }

    private static TemplateExtractionResult ParseStructured(string json, NoteTemplate template)
    {
        var result = new TemplateExtractionResult();

        // All JsonElement reads must happen while `doc` is alive - a JsonElement
        // is just a cursor into the document, so touching one after dispose
        // throws "Cannot access a disposed object". Everything below stays
        // inside the using scope; on any parse failure every field falls back
        // to "Not discussed".
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Accept both { "sections": {...} } and a bare {...} of sections.
            var sectionsEl = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sections", out var s) ? s : root;
            var haveSections = sectionsEl.ValueKind == JsonValueKind.Object;

            foreach (var section in template.Sections)
            {
                var map = new Dictionary<string, string>();
                JsonElement secObj = default;
                var haveSec = haveSections
                              && sectionsEl.TryGetProperty(section.SectionKey, out secObj)
                              && secObj.ValueKind == JsonValueKind.Object;

                foreach (var field in section.Fields)
                {
                    map[field.FieldKey] =
                        haveSec && secObj.TryGetProperty(field.FieldKey, out var fv) && fv.ValueKind == JsonValueKind.String
                            ? (fv.GetString() ?? "").Trim()
                            : "Not discussed";
                }
                result.Sections[section.SectionKey] = map;
            }

            return result;
        }
        catch (JsonException)
        {
            return FillNotDiscussed(result, template);
        }
    }

    private static TemplateExtractionResult FillNotDiscussed(TemplateExtractionResult result, NoteTemplate template)
    {
        foreach (var section in template.Sections)
        {
            var map = new Dictionary<string, string>();
            foreach (var field in section.Fields)
                map[field.FieldKey] = "Not discussed";
            result.Sections[section.SectionKey] = map;
        }
        return result;
    }

    /// <summary>
    /// Chat-completion call for <see cref="ExtractStructuredAsync"/>. Targets
    /// whatever <see cref="AppConfig.ChatBaseUrl"/> / <see cref="AppConfig.ChatModel"/>
    /// name - OpenAI, or a local LM Studio / Ollama server.
    /// </summary>
    private async Task<string> ChatJsonAsync(string prompt)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _cfg.ChatModel,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["max_tokens"] = 3000,
            ["temperature"] = 0.1,
        };
        // Some local models reject JSON mode - AppConfig.ChatJsonMode disables it.
        if (_cfg.ChatJsonMode)
            requestBody["response_format"] = new { type = "json_object" };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await _chatHttp.PostAsync("chat/completions", content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Chat request failed ({_cfg.ChatModel} @ {_cfg.ChatBaseUrl}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var messageContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        // Local models often wrap JSON in ```json fences even in JSON mode.
        return StripCodeFence(messageContent ?? "{}");
    }

    private static string StripCodeFence(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```")) return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return t;
        t = t[(firstNewline + 1)..];
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? t[..lastFence] : t).Trim();
    }
}