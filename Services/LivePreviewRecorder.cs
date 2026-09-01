using System;
using NAudio.Wave;

namespace MedScribeOS.Services;

/// <summary>
/// Runs alongside the main visit recording (AudioRecorder) purely to give the
/// provider live reassurance that audio is actually being captured - this is
/// NOT used for the final HPI/ROS pipeline, which still uses the complete,
/// continuously-recorded session file for accuracy when End Conversation is
/// pressed.
///
/// This used to run a second, parallel Whisper transcription over rolling
/// chunks just to print italic placeholder text that the live transcript threw
/// away the moment the real (diarized) turns came back - paying for a full
/// second transcription of the entire visit for a result nobody ever saw
/// used. A local peak-amplitude meter gives the same "yes, it's hearing you"
/// reassurance with zero API cost.
/// </summary>
public sealed class LivePreviewRecorder : IDisposable
{
    private WaveInEvent? _waveIn;

    public bool IsRunning { get; private set; }

    /// <summary>Fires roughly whenever audio arrives with a 0.0-1.0 peak level for that buffer.</summary>
    public event Action<double>? LevelChanged;
    public event Action<string>? ErrorOccurred;

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        try
        {
            _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    public void Stop()
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
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        int peak = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            int abs = Math.Abs((int)sample);
            if (abs > peak) peak = abs;
        }

        LevelChanged?.Invoke(peak / (double)short.MaxValue);
    }

    public void Dispose() => Stop();
}
