using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MedScribeOS.Services;

public record ConversationTurn(string Speaker, string Text);

public record HpiRosResult(
    Dictionary<string, string> Hpi,
    Dictionary<string, string> Ros,
    string? Assessment,
    string? Plan,
    string? AdditionalNotes);

/// <summary>
/// Ports the three OpenAI calls from the extension's ConversationAnalyzer.jsx:
/// Whisper transcription, GPT-4o speaker diarization, and GPT-4o HPI/ROS
/// extraction. Same models, same prompts, same JSON response shapes as the
/// extension - just called from C# via HttpClient instead of the browser's
/// fetch().
/// </summary>
public sealed class OpenAiClient
{
    private readonly HttpClient _http;

    // Same HPI/ROS field template as DEFAULT_TEMPLATE in ConversationAnalyzer.jsx.
    public const string DefaultTemplate = """
        HPI Fields:
        - Chief Complaint (main reason for visit)
        - Onset (when symptoms started)
        - Location (where in the body)
        - Duration (how long symptoms last)
        - Character (quality - sharp, dull, throbbing, burning; severity 1-10)
        - Aggravating factors (what makes it worse)
        - Relieving factors (what makes it better)
        - Associated symptoms (other symptoms alongside main complaint)
        - Prior episodes (has this happened before)
        - Medications tried (what they've taken for this)

        ROS (Review of Systems):
        - Constitutional: fever, chills, fatigue, weight loss/gain, night sweats
        - HEENT: headache, vision changes, ear pain, nasal congestion, sore throat
        - Cardiovascular: chest pain, palpitations, shortness of breath on exertion, leg swelling
        - Respiratory: dyspnea, cough, wheezing, hemoptysis
        - Gastrointestinal: nausea, vomiting, diarrhea, constipation, abdominal pain, blood in stool
        - Genitourinary: dysuria, frequency, urgency, hematuria
        - Musculoskeletal: joint pain, swelling, stiffness, muscle weakness
        - Neurological: dizziness, syncope, seizures, weakness, numbness, tingling
        - Skin: rash, lesions, itching, color changes
        - Psychiatric: anxiety, depression, sleep disturbance, mood changes
        """;

    public OpenAiClient(string? apiKey = null)
    {
        // Reads from OPENAI_API_KEY by default rather than hardcoding it into
        // the app the way process.env.REACT_APP_OPENAI_API_KEY got bundled
        // into the extension's built JS (technically extractable from the
        // built files). An env var on the clinical machine is a meaningfully
        // better place for it - though for production you'll eventually want
        // this behind your own backend rather than the desktop app holding
        // an OpenAI key directly.
        //
        // Falls back to a local config file if the env var isn't visible -
        // Windows environment variable propagation to already-running
        // processes has proven unreliable on this machine (possibly due to
        // IT-managed group policy on a domain-joined workstation), and a
        // file this app reads directly sidesteps that class of problem
        // entirely.
        apiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        apiKey ??= TryReadKeyFromConfigFile();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            EnsureConfigTemplateExists();
            throw new InvalidOperationException(
                $"No OpenAI API key found. Either set the OPENAI_API_KEY environment variable, " +
                $"or open {ConfigFilePath} and paste your key into \"OpenAiApiKey\", then restart the app.");
        }

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static string ConfigFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MedScribeOS", "config.json");

    private static string? TryReadKeyFromConfigFile()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return null;
            var json = File.ReadAllText(ConfigFilePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("OpenAiApiKey", out var keyEl))
            {
                var key = keyEl.GetString();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
        }
        catch
        {
            // Any read/parse failure is treated the same as "no key found" -
            // the caller falls through to the clear error message instead.
        }
        return null;
    }

    /// <summary>Creates an empty template config file on first failure, so the fix is just "open this file and paste your key in" rather than needing to know the exact JSON shape from scratch.</summary>
    private static void EnsureConfigTemplateExists()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFilePath)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(ConfigFilePath))
            {
                var template = JsonSerializer.Serialize(new { OpenAiApiKey = "" }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, template);
            }
        }
        catch
        {
            // Best effort - if we can't write the template, the exception
            // message above still tells the user the exact path to create.
        }
    }

    /// <summary>Ports transcribe() - sends the recorded WAV to Whisper, then filters out hallucinated segments using Whisper's own confidence signals.</summary>
    public async Task<string> TranscribeAsync(string audioFilePath)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(audioFilePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent("en"), "language");
        // verbose_json instead of plain text - this gives us per-segment
        // no_speech_prob and avg_logprob, which is how we filter out
        // hallucinated output below instead of trusting whatever text
        // Whisper returns unconditionally.
        form.Add(new StringContent("verbose_json"), "response_format");

        var response = await _http.PostAsync("https://api.openai.com/v1/audio/transcriptions", form);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Whisper transcription failed: {body}");
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
    private static string ExtractRealSpeechFromVerboseJson(string json)
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

            var noSpeechProb = segment.TryGetProperty("no_speech_prob", out var nsp) ? nsp.GetDouble() : 0;
            var avgLogProb = segment.TryGetProperty("avg_logprob", out var alp) ? alp.GetDouble() : 0;

            var soundsLikeSilence = noSpeechProb > 0.5;
            var lowConfidence = avgLogProb < -1.0;

            if (!soundsLikeSilence && !lowConfidence)
            {
                keptSegments.Add(text.Trim());
            }
        }

        return string.Join(" ", keptSegments).Trim();
    }

    /// <summary>Ports diarize() - splits a raw transcript into Doctor/Patient turns.</summary>
    public async Task<List<ConversationTurn>> DiarizeAsync(string transcript)
    {
        var prompt = $$"""
            You are an expert medical transcriptionist. Split this doctor-patient conversation transcript into individual speaker turns.
            Identify who is speaking based on context: doctors ask clinical questions, give instructions, explain diagnoses; patients describe symptoms, answer questions, express concerns.

            TRANSCRIPT:
            {{transcript}}

            Return ONLY a valid JSON object in this exact format (no markdown, no preamble):
            {
              "turns": [
                { "speaker": "Doctor", "text": "..." },
                { "speaker": "Patient", "text": "..." }
              ]
            }

            Rules:
            - Use ONLY "Doctor" or "Patient" as speaker values
            - Doctor typically starts the conversation
            - Preserve all original words exactly, do not paraphrase
            """;

        var json = await ChatJsonAsync(prompt);

        using var doc = JsonDocument.Parse(json);
        var turns = new List<ConversationTurn>();
        if (doc.RootElement.TryGetProperty("turns", out var turnsEl))
        {
            foreach (var t in turnsEl.EnumerateArray())
            {
                var speaker = t.TryGetProperty("speaker", out var s) ? s.GetString() ?? "Doctor" : "Doctor";
                var text = t.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    turns.Add(new ConversationTurn(speaker, text));
                }
            }
        }

        if (turns.Count == 0)
        {
            throw new InvalidOperationException("No speaker turns extracted - check audio quality.");
        }

        return turns;
    }

    /// <summary>
    /// Transcribes one live turn's audio AND labels its speaker in a single
    /// call, using gpt-4o-transcribe-diarize's audio-based diarization
    /// anchored to the enrolled doctor's voice (DoctorVoiceEnrollment) - this
    /// is what actually distinguishes Doctor/Patient by VOICE instead of
    /// guessing from sentence content, which is what made an earlier
    /// text-based classifier unreliable regardless of which chat model ran
    /// it. Any speaker the model doesn't match to "Doctor" is treated as
    /// "Patient", since a visit is assumed to be exactly two parties. A
    /// single clip can come back as more than one turn if a quick
    /// back-and-forth happened within it.
    /// </summary>
    public async Task<List<ConversationTurn>> TranscribeAndDiarizeTurnAsync(string audioFilePath, string doctorReferenceDataUrl)
    {
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(audioFilePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        form.Add(new StringContent("gpt-4o-transcribe-diarize"), "model");
        form.Add(new StringContent("diarized_json"), "response_format");
        form.Add(new StringContent("auto"), "chunking_strategy");
        form.Add(new StringContent("Doctor"), "known_speaker_names[]");
        form.Add(new StringContent(doctorReferenceDataUrl), "known_speaker_references[]");

        var response = await _http.PostAsync("https://api.openai.com/v1/audio/transcriptions", form);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Live transcription+diarization failed: {body}");
        }

        return ParseDiarizedSegments(body);
    }

    private static List<ConversationTurn> ParseDiarizedSegments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var turns = new List<ConversationTurn>();

        if (!doc.RootElement.TryGetProperty("segments", out var segmentsEl))
        {
            return turns;
        }

        foreach (var segment in segmentsEl.EnumerateArray())
        {
            var text = segment.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            var speakerLabel = segment.TryGetProperty("speaker", out var s) ? s.GetString() ?? "" : "";
            var speaker = speakerLabel.Equals("Doctor", StringComparison.OrdinalIgnoreCase) ? "Doctor" : "Patient";

            turns.Add(new ConversationTurn(speaker, text.Trim()));
        }

        return turns;
    }

    /// <summary>Ports the analyze() call - extracts structured HPI/ROS from speaker turns.</summary>
    public async Task<HpiRosResult> ExtractHpiRosAsync(List<ConversationTurn> turns, string? template = null)
    {
        template ??= DefaultTemplate;
        var formatted = string.Join("\n", turns.ConvertAll(t => $"{t.Speaker}: {t.Text}"));

        var prompt = $$"""
            You are a board-certified medical scribe. Analyze this doctor-patient conversation and extract structured clinical information.

            ECW TEMPLATE TO FILL:
            {{template}}

            CONVERSATION:
            {{formatted}}

            Return ONLY valid JSON (no markdown, no preamble):
            {
              "hpi": {
                "chief_complaint": "", "onset": "", "location": "", "duration": "", "character": "",
                "severity": "", "aggravating_factors": "", "relieving_factors": "",
                "associated_symptoms": "", "prior_episodes": "", "medications_tried": ""
              },
              "ros": {
                "constitutional": "", "heent": "", "cardiovascular": "", "respiratory": "",
                "gastrointestinal": "", "genitourinary": "", "musculoskeletal": "",
                "neurological": "", "skin": "", "psychiatric": ""
              },
              "assessment": "",
              "plan": "",
              "additional_notes": ""
            }
            Use "Not discussed" for fields not mentioned. Be concise and clinically accurate.
            """;

        var json = await ChatJsonAsync(prompt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new HpiRosResult(
            Hpi: ParseStringDict(root, "hpi"),
            Ros: ParseStringDict(root, "ros"),
            Assessment: root.TryGetProperty("assessment", out var a) ? a.GetString() : null,
            Plan: root.TryGetProperty("plan", out var p) ? p.GetString() : null,
            AdditionalNotes: root.TryGetProperty("additional_notes", out var n) ? n.GetString() : null);
    }

    private static Dictionary<string, string> ParseStringDict(JsonElement root, string propertyName)
    {
        var result = new Dictionary<string, string>();
        if (root.TryGetProperty(propertyName, out var section))
        {
            foreach (var prop in section.EnumerateObject())
            {
                result[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        return result;
    }

    /// <summary>Shared GPT-4o JSON-mode call used by both DiarizeAsync and ExtractHpiRosAsync.</summary>
    private async Task<string> ChatJsonAsync(string prompt)
    {
        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[] { new { role = "user", content = prompt } },
            response_format = new { type = "json_object" },
            max_tokens = 3000,
            temperature = 0.1
        };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("https://api.openai.com/v1/chat/completions", content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI request failed: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var messageContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return messageContent ?? "{}";
    }
}