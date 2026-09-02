using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MedScribeOS.Models;

namespace MedScribeOS.Services;

/// <summary>
/// Builds Doctor/Patient turns for the File Analyzer, so an uploaded visit
/// shows up as the same two-sided chat the Voice Analyzer produces - without
/// any LLM call, so it adds essentially zero time on top of transcription.
///
/// Two sources:
///  - Whisper segments with timestamps (an uploaded recording): speaker
///    hand-offs are inferred from silence gaps between segments, the same
///    turn-taking assumption the live local mode uses, plus a question-mark
///    assist (a segment ending in "?" hands off on a much smaller pause -
///    that's how Q&amp;A actually flows in an exam room).
///  - Text that already carries speaker labels ("Doctor:", "Pt:", "Speaker 1:"):
///    parsed directly.
///
/// Both are heuristics - the ⇄ flip button on each bubble is the correction
/// path, exactly as in live local mode.
/// </summary>
public static class TranscriptTurns
{
    /// <summary>Silence this long between segments is read as a speaker hand-off (matches the live mode's chunk-boundary assumption).</summary>
    private const double HandoffGapSeconds = 0.9;

    /// <summary>After a question, the reply usually comes fast - hand off on a much smaller pause.</summary>
    private const double QuestionHandoffGapSeconds = 0.35;

    public static List<ConversationTurn> FromSegments(IReadOnlyList<RawDiarizedSegment> segments)
    {
        var clean = segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (clean.Count == 0) return new List<ConversationTurn>();

        // ── 1. gap-based alternation, re-anchored on questions ──────────────
        // Pure alternation has a failure mode: one intra-speaker pause flips
        // the phase and every later label stays inverted. The anchor that
        // stops the drift: in a clinical interview a substantive question
        // (ends in "?", 4+ words) is almost always the doctor - pinning those
        // to Doctor makes any inversion self-correct at the next question.
        var staged = new List<(SpeakerRole Role, RawDiarizedSegment Seg)>();
        for (var i = 0; i < clean.Count; i++)
        {
            SpeakerRole role;
            if (IsSubstantiveQuestion(clean[i].Text))
            {
                role = SpeakerRole.Doctor;
            }
            else if (i == 0)
            {
                role = SpeakerRole.Doctor; // doctor assumed to open the visit
            }
            else
            {
                var prev = clean[i - 1];
                var prevRole = staged[i - 1].Role;
                var gap = (clean[i].StartSeconds ?? 0) - (prev.EndSeconds ?? 0);
                var threshold = prev.Text.TrimEnd().EndsWith('?') ? QuestionHandoffGapSeconds : HandoffGapSeconds;
                role = gap >= threshold ? Flip(prevRole) : prevRole;
            }
            staged.Add((role, clean[i]));
        }

        // ── 1b. absorb question fragments ───────────────────────────────────
        // A doctor question split by a mid-question pause ("Anything that you
        // have done … tried since last night that's made the pain better?")
        // leaves its lead-in fragment mislabeled. A fragment with NO sentence-
        // ending punctuation sitting directly before an anchored question, with
        // no real silence between them, is part of that question. The
        // punctuation guard keeps a patient answer ("Not really." + question
        // start) from being swallowed.
        for (var i = 1; i < staged.Count; i++)
        {
            if (staged[i].Role != SpeakerRole.Doctor || !IsSubstantiveQuestion(staged[i].Seg.Text)) continue;
            for (var k = i - 1; k >= 0; k--)
            {
                if (staged[k].Role == SpeakerRole.Doctor) break;
                if (staged[k].Seg.Text.IndexOfAny(new[] { '.', '?', '!' }) >= 0) break;
                var gap = (staged[k + 1].Seg.StartSeconds ?? 0) - (staged[k].Seg.EndSeconds ?? 0);
                if (gap >= HandoffGapSeconds) break;
                staged[k] = (SpeakerRole.Doctor, staged[k].Seg);
            }
        }

        // ── 2. backchannel snap (same rule as the live refiner) ─────────────
        for (var i = 1; i < staged.Count - 1; i++)
        {
            if (SpeakerAttributionRefiner.IsBackchannel(staged[i].Seg.Text)
                && staged[i - 1].Role == staged[i + 1].Role
                && staged[i].Role != staged[i - 1].Role)
            {
                staged[i] = (staged[i - 1].Role, staged[i].Seg);
            }
        }

        // ── 3. merge adjacent same-role segments into one turn ──────────────
        // Timestamp = midnight + offset-into-recording, so the bubble's HH:mm
        // renders as position in the file (00:00, 00:03, …) instead of a
        // meaningless wall-clock load time repeated on every bubble.
        var midnight = new DateTimeOffset(DateTime.Today);
        var turns = new List<ConversationTurn>();
        foreach (var (role, seg) in staged)
        {
            if (turns.Count > 0 && turns[^1].Speaker == role)
                turns[^1] = turns[^1] with { Text = $"{turns[^1].Text} {seg.Text.Trim()}".Trim() };
            else
                turns.Add(new ConversationTurn(role, seg.Text.Trim(), midnight + TimeSpan.FromSeconds(seg.StartSeconds ?? 0)));
        }
        return turns;
    }

    // "Doctor:", "Dr.", "Provider -", "PT:", "Speaker 1:", "S2:" ... at line start.
    private static readonly Regex LabelRx = new(
        @"^\s*(?<who>doctor|dr\.?|provider|physician|clinician|md|patient|pt\.?|client|speaker\s*[ab12]|s[12])\s*[:\-–—]\s*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a transcript whose lines carry speaker labels into turns.
    /// Returns null when the text doesn't look labelled (fewer than two
    /// labelled lines) - the caller then keeps the plain-text view.
    /// </summary>
    public static List<ConversationTurn>? FromLabeledText(string text)
    {
        var now = DateTimeOffset.Now;
        var turns = new List<ConversationTurn>();
        var labeledLines = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var m = LabelRx.Match(line);
            if (m.Success)
            {
                labeledLines++;
                var role = RoleFor(m.Groups["who"].Value);
                var rest = m.Groups["rest"].Value.Trim();
                turns.Add(new ConversationTurn(role, rest, now));
            }
            else if (turns.Count > 0)
            {
                // continuation line - belongs to the previous speaker
                turns[^1] = turns[^1] with { Text = $"{turns[^1].Text} {line}".Trim() };
            }
            // text before the first label is dropped - it's headers/metadata
        }

        if (labeledLines < 2) return null;
        return turns.Where(t => t.Text.Length > 0).ToList();
    }

    /// <summary>Flattens turns back to labelled text - the "edit as text" view, and it round-trips through <see cref="FromLabeledText"/>.</summary>
    public static string ToLabeledText(IEnumerable<ConversationTurn> turns)
        => string.Join("\n\n", turns.Select(t => $"{t.SpeakerLabel}: {t.Text}"));

    private static SpeakerRole RoleFor(string who)
    {
        var w = who.ToLowerInvariant().Replace(".", "").Replace(" ", "");
        return w is "patient" or "pt" or "client" or "speaker2" or "speakerb" or "s2"
            ? SpeakerRole.Patient
            : SpeakerRole.Doctor;
    }

    /// <summary>A real question, not a one-word backchannel ("right?", "okay?") - those stay with whoever gap-logic assigned.</summary>
    private static bool IsSubstantiveQuestion(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith('?') && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
    }

    private static SpeakerRole Flip(SpeakerRole r) => r == SpeakerRole.Doctor ? SpeakerRole.Patient : SpeakerRole.Doctor;
}
