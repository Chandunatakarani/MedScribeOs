using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace MedScribeOS.Services;

/// <summary>
/// Live replacement for the old "record everything, transcribe once at the
/// end" flow: cuts the conversation into turns as they're spoken (on natural
/// pauses) and runs each one through gpt-4o-transcribe-diarize immediately,
/// so accurately speaker-labeled Doctor/Patient turns appear on screen
/// during the visit instead of only after End Conversation. Diarization is
/// anchored to the enrolled doctor's voice (DoctorVoiceEnrollment) - a real
/// audio-based speaker match, not a guess from sentence content - which is
/// what makes the labels trustworthy. AudioRecorder's full-session WAV keeps
/// recording in parallel purely as a backup; it is no longer re-transcribed
/// at End Conversation; these live turns become the final record directly.
/// </summary>
public sealed class LiveConversationTranscriber : IDisposable
{
    private static readonly WaveFormat Format = new(16000, 1);

    // A turn ends once ~900ms of silence follows real speech - long enough
    // that a mid-sentence breath doesn't split a turn in two, short enough
    // that the next line appears well within a natural conversational beat.
    private const int SilenceGapMsToCutTurn = 900;

    // Safety valve for a long uninterrupted monologue (e.g. a patient's full
    // history) - cut anyway so one turn's transcription call doesn't keep
    // growing and delaying everything behind it in the queue.
    private const int MaxSegmentMs = 15000;

    // Below this, treat it as a cough/mic bump/silence, not a turn worth a
    // transcription call.
    private const int MinSpeechMsToKeep = 300;

    // Same peak-amplitude threshold ChunkHasRealSpeech/LivePreviewRecorder
    // use elsewhere in this codebase for "is this actually speech."
    private const short SilenceAmplitudeThreshold = 300;

    private readonly OpenAiClient _openAi;
    private readonly object _bufferLock = new();
    private readonly MemoryStream _segmentBuffer = new();
    private int _silenceMs;
    private int _speechMs;
    private int _segmentMs;

    private string? _doctorReferenceDataUrl;
    private Channel<string>? _segmentQueue;
    private Task? _consumerTask;
    private WaveInEvent? _waveIn;

    public bool IsRunning { get; private set; }

    /// <summary>Fired (off the UI thread) whenever a turn finishes transcription + voice-based speaker labeling, in speaking order.</summary>
    public event Action<ConversationTurn>? TurnAdded;
    public event Action<string>? ErrorOccurred;

    public LiveConversationTranscriber(OpenAiClient openAi) => _openAi = openAi;

    public void Start()
    {
        if (IsRunning) return;

        if (!DoctorVoiceEnrollment.IsEnrolled)
        {
            ErrorOccurred?.Invoke("Doctor's voice isn't enrolled yet - speaker labels would be unreliable, so live transcription was not started.");
            return;
        }
        _doctorReferenceDataUrl = DoctorVoiceEnrollment.GetReferenceAsDataUrl();

        lock (_bufferLock)
        {
            _segmentBuffer.SetLength(0);
            _silenceMs = 0;
            _speechMs = 0;
            _segmentMs = 0;
        }

        _segmentQueue = Channel.CreateUnbounded<string>();
        _consumerTask = Task.Run(ConsumeSegmentsAsync);

        try
        {
            _waveIn = new WaveInEvent { WaveFormat = Format };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            IsRunning = true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Stops capturing, flushes whatever's left of the in-progress turn into
    /// the processing queue, and waits for every queued turn (including ones
    /// still mid-transcription) to finish. Awaiting this matters: since live
    /// turns ARE the final transcript now, dropping the last few queued ones
    /// would silently lose the end of the visit.
    /// </summary>
    public async Task StopAndFlushAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        lock (_bufferLock)
        {
            if (_speechMs >= MinSpeechMsToKeep) CutSegmentLocked();
        }

        _segmentQueue?.Writer.TryComplete();
        if (_consumerTask != null)
        {
            await _consumerTask;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        long amplitudeSum = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            amplitudeSum += Math.Abs((int)sample);
        }
        var averageAmplitude = amplitudeSum / sampleCount;
        var bufferMs = sampleCount * 1000 / Format.SampleRate;
        var hasSpeech = averageAmplitude > SilenceAmplitudeThreshold;

        lock (_bufferLock)
        {
            _segmentBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            _segmentMs += bufferMs;

            if (hasSpeech)
            {
                _speechMs += bufferMs;
                _silenceMs = 0;
            }
            else
            {
                _silenceMs += bufferMs;
            }

            var naturalTurnBoundary = _speechMs >= MinSpeechMsToKeep && _silenceMs >= SilenceGapMsToCutTurn;
            var monologueSafetyValve = _segmentMs >= MaxSegmentMs && _speechMs >= MinSpeechMsToKeep;

            if (naturalTurnBoundary || monologueSafetyValve)
            {
                CutSegmentLocked();
            }
            else if (_speechMs == 0 && _segmentMs >= 3000)
            {
                // Sustained silence with nothing worth keeping - drop it so
                // the buffer doesn't grow unboundedly during quiet stretches
                // (patient changing, provider stepped out, etc).
                _segmentBuffer.SetLength(0);
                _segmentMs = 0;
                _silenceMs = 0;
            }
        }
    }

    /// <summary>Must be called with _bufferLock held.</summary>
    private void CutSegmentLocked()
    {
        if (_segmentBuffer.Length == 0) return;

        var bytes = _segmentBuffer.ToArray();
        _segmentBuffer.SetLength(0);
        _segmentMs = 0;
        _speechMs = 0;
        _silenceMs = 0;

        var path = Path.Combine(Path.GetTempPath(), $"medscribe_turn_{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(path, Format))
        {
            writer.Write(bytes, 0, bytes.Length);
        }

        _segmentQueue!.Writer.TryWrite(path);
    }

    /// <summary>
    /// Processes queued turn audio strictly one at a time and in order, so
    /// turns can never appear out of speaking order even if one Whisper/GPT
    /// call happens to take longer than the next.
    /// </summary>
    private async Task ConsumeSegmentsAsync()
    {
        await foreach (var path in _segmentQueue!.Reader.ReadAllAsync())
        {
            try
            {
                var turns = await _openAi.TranscribeAndDiarizeTurnAsync(path, _doctorReferenceDataUrl!);
                foreach (var turn in turns)
                {
                    TurnAdded?.Invoke(turn);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex.Message);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
            }
        }
    }

    public void Dispose()
    {
        if (!IsRunning) return;
        IsRunning = false;

        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        _segmentQueue?.Writer.TryComplete();
    }
}
