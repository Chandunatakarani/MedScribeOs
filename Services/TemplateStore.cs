using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MedScribeOS.Models;

namespace MedScribeOS.Services;

/// <summary>
/// Local, per-doctor template persistence. No database anywhere - one indented
/// JSON file per doctor under <c>%LocalAppData%\MedScribeOS\Templates\</c>,
/// named with that doctor's id. <see cref="Load"/> only ever touches the file
/// for the id it's handed, which is the whole of the per-doctor isolation
/// guarantee.
/// </summary>
public interface ITemplateStore
{
    /// <summary>Reads the doctor's file, creating/repairing it if missing or corrupt. Never returns null; always has at least one template with one marked default.</summary>
    DoctorTemplateFile Load(string doctorId);

    /// <summary>Full read-modify-write already done by the caller - this persists the whole file atomically (temp file + replace).</summary>
    void Save(DoctorTemplateFile file);
}

public sealed class JsonTemplateStore : ITemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;

    public JsonTemplateStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MedScribeOS", "Templates");
    }

    public DoctorTemplateFile Load(string doctorId)
    {
        if (string.IsNullOrWhiteSpace(doctorId))
            throw new ArgumentException("A doctor id is required to load templates.", nameof(doctorId));

        Directory.CreateDirectory(_dir);
        var path = PathFor(doctorId);

        if (!File.Exists(path))
        {
            var fresh = FreshFileFor(doctorId);
            WriteAtomic(path, fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<DoctorTemplateFile>(json, JsonOptions);
            if (loaded == null || loaded.Templates == null)
                throw new JsonException("File deserialized to null.");

            loaded.DoctorId = doctorId; // trust the filename, not stale content
            NormalizeInvariants(loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt / unreadable - keep the bad copy for forensics, then
            // hand back a usable fresh file instead of crashing the app.
            TryQuarantine(path);
            var fresh = FreshFileFor(doctorId);
            WriteAtomic(path, fresh);
            Notify.Warning("Your saved templates couldn't be read and were reset to a fresh Standard template. The unreadable file was kept with a .corrupt suffix.");
            return fresh;
        }
    }

    public void Save(DoctorTemplateFile file)
    {
        if (string.IsNullOrWhiteSpace(file.DoctorId))
            throw new ArgumentException("DoctorTemplateFile.DoctorId must be set before saving.");

        Directory.CreateDirectory(_dir);
        NormalizeInvariants(file);
        WriteAtomic(PathFor(file.DoctorId), file);
    }

    // ── internals ────────────────────────────────────────────────────────────

    private string PathFor(string doctorId) => Path.Combine(_dir, $"templates_{Sanitize(doctorId)}.json");

    /// <summary>Doctor ids can be emails ("dr.smith@hfmg.org") - strip anything that isn't filename-safe so the path stays predictable.</summary>
    private static string Sanitize(string doctorId)
    {
        var cleaned = Regex.Replace(doctorId.Trim(), "[^A-Za-z0-9._-]", "_");
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    private void WriteAtomic(string path, DoctorTemplateFile file)
    {
        // Temp file in the same directory (so File.Replace stays on one volume),
        // fsync-ish flush, then atomic replace. A crash mid-write leaves either
        // the old file intact or the .tmp orphaned - never a half-written target.
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            var dest = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, dest);
        }
        catch { /* best effort - if we can't move it, WriteAtomic will overwrite it */ }
    }

    private static DoctorTemplateFile FreshFileFor(string doctorId) => new()
    {
        DoctorId = doctorId,
        Templates = { StandardTemplate() },
    };

    /// <summary>
    /// Enforces the two invariants everything else relies on: exactly one
    /// default, and never zero templates.
    /// </summary>
    private static void NormalizeInvariants(DoctorTemplateFile file)
    {
        file.Templates ??= new();

        if (file.Templates.Count == 0)
            file.Templates.Add(StandardTemplate());

        if (!file.Templates.Any(t => t.IsDefault))
            file.Templates[0].IsDefault = true;

        // If more than one got flagged (hand-edited file, merge, ...), keep the first.
        var seenDefault = false;
        foreach (var t in file.Templates)
        {
            if (t.IsDefault && !seenDefault) { seenDefault = true; continue; }
            t.IsDefault = false;
        }
    }

    /// <summary>
    /// The out-of-the-box template. Mirrors the HPI/ROS field set that was
    /// hardcoded in OpenAiClient/MainWindow before this feature, so a brand-new
    /// doctor gets identical extraction behaviour until they customise it.
    /// (Spec says "one empty default template" for the recovery case; a
    /// populated one is used instead so Analyze still works on first run -
    /// flagged in the deliverable notes.)
    /// </summary>
    public static NoteTemplate StandardTemplate() => new()
    {
        Name = "Standard HPI / ROS",
        IsDefault = true,
        Sections =
        {
            new TemplateSection
            {
                SectionKey = "HPI",
                Label = "History of Present Illness",
                Fields =
                {
                    F("chief_complaint", "Chief Complaint", "Main reason for the visit, in the patient's words."),
                    F("onset", "Onset", "When did the symptoms start?"),
                    F("location", "Location", "Where in the body is the symptom?"),
                    F("duration", "Duration", "How long do episodes last / how long has it been going on?"),
                    F("character", "Character", "Quality of the symptom - sharp, dull, throbbing, burning."),
                    F("severity", "Severity", "Severity, ideally on a 1-10 scale."),
                    F("aggravating_factors", "Aggravating Factors", "What makes it worse?"),
                    F("relieving_factors", "Relieving Factors", "What makes it better?"),
                    F("associated_symptoms", "Associated Symptoms", "Other symptoms alongside the main complaint."),
                    F("prior_episodes", "Prior Episodes", "Has this happened before?"),
                    F("medications_tried", "Medications Tried", "What has the patient taken for this?"),
                },
            },
            new TemplateSection
            {
                SectionKey = "ROS",
                Label = "Review of Systems",
                Fields =
                {
                    F("constitutional", "Constitutional", "Fever, chills, fatigue, weight loss/gain, night sweats."),
                    F("heent", "HEENT", "Headache, vision changes, ear pain, nasal congestion, sore throat."),
                    F("cardiovascular", "Cardiovascular", "Chest pain, palpitations, exertional dyspnea, leg swelling."),
                    F("respiratory", "Respiratory", "Dyspnea, cough, wheezing, hemoptysis."),
                    F("gastrointestinal", "Gastrointestinal", "Nausea, vomiting, diarrhea, constipation, abdominal pain, blood in stool."),
                    F("genitourinary", "Genitourinary", "Dysuria, frequency, urgency, hematuria."),
                    F("musculoskeletal", "Musculoskeletal", "Joint pain, swelling, stiffness, muscle weakness."),
                    F("neurological", "Neurological", "Dizziness, syncope, seizures, weakness, numbness, tingling."),
                    F("skin", "Skin", "Rash, lesions, itching, color changes."),
                    F("psychiatric", "Psychiatric", "Anxiety, depression, sleep disturbance, mood changes."),
                },
            },
        },
    };

    private static TemplateField F(string key, string label, string prompt) => new()
    {
        FieldKey = key,
        Label = label,
        Prompt = prompt,
    };
}
