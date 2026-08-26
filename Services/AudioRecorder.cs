using System;
using System.IO;
using NAudio.Wave;

namespace MedScribeOS.Services;

/// <summary>
/// Records microphone audio to a WAV file for the duration of a visit.
/// This is the OS-level equivalent of the extension's MediaRecorder-based
/// audio capture (ConversationAnalyzer.jsx startRec/stopRec) - same
/// "record the whole visit, then process it" flow, just using NAudio
/// since there's no browser MediaRecorder API available here.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _outputPath;

    public bool IsRecording { get; private set; }

    public void Start()
    {
        if (IsRecording) return;

        _outputPath = Path.Combine(
            Path.GetTempPath(),
            $"medscribe_visit_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        // 16kHz mono is plenty for speech and keeps file size (and upload
        // time to Whisper) small - Whisper doesn't need high-fidelity audio.
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 1)
        };
        _writer = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);

        _waveIn.DataAvailable += (_, e) =>
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        };

        _waveIn.StartRecording();
        IsRecording = true;
    }

    /// <summary>Stops recording and returns the path to the recorded WAV file, or null if nothing was recorded.</summary>
    public string? Stop()
    {
        if (!IsRecording) return null;

        _waveIn?.StopRecording();
        _writer?.Flush();
        _writer?.Dispose();
        _waveIn?.Dispose();

        _writer = null;
        _waveIn = null;
        IsRecording = false;

        return _outputPath;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }
}
