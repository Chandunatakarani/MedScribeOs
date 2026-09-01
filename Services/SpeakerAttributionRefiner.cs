using System;
using System.Collections.Generic;
using System.Linq;
using MedScribeOS.Models;

namespace MedScribeOS.Services;

/// <summary>One segment straight from the diarization model, before role resolution / cleanup.</summary>
public sealed record RawDiarizedSegment(string RawSpeaker, string Text, double? StartSeconds, double? EndSeconds);

/// <summary>
/// Turns the diarization model's raw, per-chunk speaker labels into a clean,
/// conversation-wide Doctor/Patient stream. One instance per recording -
/// it carries state across chunks.
///
/// Why this layer exists: gpt-4o-transcribe-diarize is called once per VAD
/// chunk (1-15s of audio) in isolation, so its speaker *identity* mapping can
/// drift between chunks and its per-chunk labels are noisy on short
/// utterances. This refiner keeps the model's *relative* diarization (it's
/// good at "the speaker changed here") but re-derives *which* speaker is the
/// doctor conversation-wide, using, in order:
///
///   1. Voice anchor (primary): any segment the model matched to the enrolled
///      "Doctor" voice reference is Doctor; the other party is Patient.
///   2. Turn-taking fallback: for a chunk with no anchored label, assume a
///      chunk boundary (>=~0.9s of silence) means the speaker changed, and
///      follow the model's own within-chunk speaker changes after that.
///   3. Backchannel snap: a 1-2 word segment ("mm-hm", "right", "okay") between
///      two turns of the same speaker is given to that speaker - the single
///      most common diarization mistake.
///   4. Merge: adjacent same-speaker segments inside one chunk become one turn.
/// </summary>
public sealed class SpeakerAttributionRefiner
{
    // Inside one chunk, segments closer than this are treated as the model
    // over-segmenting one continuous speaker rather than a real hand-off.
    private const double ContiguousGapSeconds = 0.45;

    private static readonly string[] Backchannels =
    {
        "mm", "mmm", "mhm", "mm-hm", "mmhm", "uh-huh", "uhhuh", "uh huh",
        "yeah", "yep", "yes", "ok", "okay", "right", "sure", "got it",
        "i see", "gotcha", "no", "nope", "correct", "exactly",
    };

    private SpeakerRole? _lastRole;
    private bool _anchorEverMatched;

    /// <summary>
    /// True once at least one segment anywhere in the conversation matched the
    /// enrolled doctor voice. If this stays false the voice reference probably
    /// isn't working and every label came from the turn-taking fallback.
    /// </summary>
    public bool VoiceAnchorMatched => _anchorEverMatched;

    /// <summary>Resolve one chunk's raw segments into finished turns, in speaking order. Called once per diarization response.</summary>
    public IReadOnlyList<ConversationTurn> Refine(IReadOnlyList<RawDiarizedSegment> rawSegments, DateTimeOffset arrival)
    {
        var clean = rawSegments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (clean.Count == 0) return Array.Empty<ConversationTurn>();

        var chunkHasAnchor = clean.Any(IsDoctorAnchor);
        if (chunkHasAnchor) _anchorEverMatched = true;

        // ── 1. raw label -> provisional role ────────────────────────────────
        var staged = new List<(SpeakerRole Role, RawDiarizedSegment Seg)>();
        for (var i = 0; i < clean.Count; i++)
        {
            var seg = clean[i];
            SpeakerRole role;

            if (IsDoctorAnchor(seg))
            {
                role = SpeakerRole.Doctor;
            }
            else if (chunkHasAnchor)
            {
                // the model made a real doctor match in this chunk, so a
                // different label here is genuinely the other person
                role = SpeakerRole.Patient;
            }
            else if (i == 0)
            {
                // new chunk, no anchor: a chunk boundary is ~0.9s+ of silence,
                // which in a two-person exam room almost always means the
                // other person is now talking
                role = _lastRole is { } last ? Flip(last) : SpeakerRole.Doctor; // doctor assumed to open (see deliverable notes)
            }
            else
            {
                // mid-chunk, no anchor: trust the model's own speaker-change
                // signal (raw label differs from the previous segment), but
                // ignore hand-offs that are really just over-segmentation
                var prev = clean[i - 1];
                var prevRole = staged[i - 1].Role;
                var modelSaysChanged = !seg.RawSpeaker.Equals(prev.RawSpeaker, StringComparison.OrdinalIgnoreCase);
                var gap = (seg.StartSeconds ?? 0) - (prev.EndSeconds ?? 0);
                role = (modelSaysChanged && gap >= ContiguousGapSeconds) ? Flip(prevRole) : prevRole;
            }

            staged.Add((role, seg));
        }

        // ── 2. backchannel snap ────────────────────────────────────────────
        for (var i = 1; i < staged.Count - 1; i++)
        {
            if (IsBackchannel(staged[i].Seg.Text)
                && staged[i - 1].Role == staged[i + 1].Role
                && staged[i].Role != staged[i - 1].Role)
            {
                staged[i] = (staged[i - 1].Role, staged[i].Seg);
            }
        }

        // ── 3. merge adjacent same-role segments in this chunk ─────────────
        var turns = new List<ConversationTurn>();
        foreach (var (role, seg) in staged)
        {
            if (turns.Count > 0 && turns[^1].Speaker == role)
                turns[^1] = turns[^1] with { Text = $"{turns[^1].Text} {seg.Text.Trim()}".Trim() };
            else
                turns.Add(new ConversationTurn(role, seg.Text.Trim(), arrival));
        }

        if (turns.Count > 0) _lastRole = turns[^1].Speaker;
        return turns;
    }

    private static bool IsDoctorAnchor(RawDiarizedSegment seg) =>
        seg.RawSpeaker.Contains("doctor", StringComparison.OrdinalIgnoreCase);

    private static SpeakerRole Flip(SpeakerRole r) => r == SpeakerRole.Doctor ? SpeakerRole.Patient : SpeakerRole.Doctor;

    private static bool IsBackchannel(string text)
    {
        var t = new string(text.Where(c => !char.IsPunctuation(c)).ToArray()).Trim().ToLowerInvariant();
        if (t.Length == 0) return true;
        if (Backchannels.Contains(t)) return true;
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2 && Backchannels.Any(b => t == b || t.StartsWith(b + " "));
    }
}
