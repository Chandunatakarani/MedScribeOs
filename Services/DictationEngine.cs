using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using NAudio.Wave;
// This project references both WPF and WinForms (WinForms is needed for the
// tray icon), and both frameworks have their own Timer/TextBox/Button classes.
// With implicit usings on, the compiler can't tell which "Timer" you mean
// without help - this alias pins it to the one we actually want here.
using Timer = System.Threading.Timer;

namespace MedScribeOS.Services;

/// <summary>
/// "Live" dictation implemented as rolling ~3 second chunks: record a chunk,
/// transcribe it with Whisper, inject the result into whatever eCW field
/// currently has focus, repeat. This is NOT instant per-word the way the
/// browser extension's Web Speech API was - Whisper is a batch transcription
/// API, not a streaming one, so there's necessarily a few seconds of latency
/// per phrase. Tradeoff for using the same accurate model everywhere instead
/// of a separate, less accurate always-on engine.
/// </summary>
public sealed class DictationEngine : IDisposable
{
    private readonly OpenAiClient _openAi;
    private readonly TimeSpan _chunkLength = TimeSpan.FromSeconds(3);

    // Carried over from the tail of one chunk into the start of the next so a
    // word spoken right at the chunk cut isn't split in half between two
    // separate Whisper calls (where each half tends to get dropped or
    // garbled). 750ms at 16kHz/mono/16-bit. This can occasionally duplicate
    // a word at the seam - acceptable, since a clinician spotting a stray
    // repeated word is far safer than one silently missing from the chart.
    private const int OverlapBytes = 16000 * 2 * 3 / 4;

    private readonly object _tailLock = new();
    private byte[] _tailBuffer = Array.Empty<byte>();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _chunkPath;
    private Timer? _flushTimer;
    private bool _flushing;

    public bool IsRunning { get; private set; }

    /// <summary>Fired on the UI thread's async continuation - hook this up via Dispatcher.Invoke in the caller.</summary>
    public event Action<string>? PhraseInjected;
    public event Action<string>? ErrorOccurred;

    public DictationEngine(OpenAiClient openAi)
    {
        _openAi = openAi;
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        lock (_tailLock) { _tailBuffer = Array.Empty<byte>(); }
        StartNewChunk();
        _flushTimer = new Timer(_ => _ = FlushChunkAsync(), null, _chunkLength, _chunkLength);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _flushTimer?.Dispose();
        _flushTimer = null;
        CloseCurrentChunk(deleteFile: true);
    }

    private void StartNewChunk(byte[]? overlapPrefix = null)
    {
        _chunkPath = Path.Combine(Path.GetTempPath(), $"medscribe_dict_{Guid.NewGuid():N}.wav");
        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
        _writer = new WaveFileWriter(_chunkPath, _waveIn.WaveFormat);

        if (overlapPrefix is { Length: > 0 })
        {
            _writer.Write(overlapPrefix, 0, overlapPrefix.Length);
        }

        _waveIn.DataAvailable += (_, e) =>
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            AppendToTailBuffer(e.Buffer, e.BytesRecorded);
        };
        _waveIn.StartRecording();
    }

    /// <summary>Keeps a rolling buffer of the last OverlapBytes of raw PCM seen, so it can be
    /// prefixed onto the next chunk when this one is flushed.</summary>
    private void AppendToTailBuffer(byte[] buffer, int count)
    {
        lock (_tailLock)
        {
            if (count >= OverlapBytes)
            {
                _tailBuffer = buffer[(count - OverlapBytes)..count];
                return;
            }

            int keep = OverlapBytes - count;
            var combined = new byte[keep + count];
            if (keep > 0 && _tailBuffer.Length > 0)
            {
                int copyFrom = Math.Max(0, _tailBuffer.Length - keep);
                int copyLength = Math.Min(keep, _tailBuffer.Length);
                Array.Copy(_tailBuffer, copyFrom, combined, keep - copyLength, copyLength);
            }
            Array.Copy(buffer, 0, combined, keep, count);
            _tailBuffer = combined;
        }
    }

    private void CloseCurrentChunk(bool deleteFile)
    {
        _waveIn?.StopRecording();
        _writer?.Flush();
        _writer?.Dispose();
        _waveIn?.Dispose();
        _writer = null;
        _waveIn = null;

        if (deleteFile && _chunkPath != null && File.Exists(_chunkPath))
        {
            try { File.Delete(_chunkPath); } catch { /* best effort cleanup */ }
        }
    }

    private async Task FlushChunkAsync()
    {
        if (!IsRunning || _flushing) return;
        _flushing = true;

        var pathToProcess = _chunkPath;
        byte[] tailSnapshot;
        lock (_tailLock) { tailSnapshot = _tailBuffer; }
        CloseCurrentChunk(deleteFile: false);

        if (IsRunning)
        {
            // Seed the next chunk with the tail of this one so a word spoken
            // right at the cut lands whole in at least one chunk instead of
            // being split across both.
            StartNewChunk(tailSnapshot);
        }

        try
        {
            // Skip near-silent chunks (roughly < ~0.15s of actual audio at this
            // format) rather than sending empty/near-empty clips to Whisper.
            // Real silence detection, not just "did some bytes get written."
            // A silent room still produces a full-size WAV file, and Whisper
            // is known to hallucinate text (often YouTube-caption-style
            // phrases) when given audio with no real speech in it - that
            // hallucinated text would otherwise get pasted straight into
            // whatever eCW field has focus. Better to occasionally skip a
            // real quiet phrase than risk fabricated text in a patient chart.
            if (pathToProcess != null && File.Exists(pathToProcess) && ChunkHasRealSpeech(pathToProcess))
            {
                var text = await _openAi.TranscribeAsync(pathToProcess);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Clipboard operations require an STA thread. This method
                    // runs on the Timer's thread-pool thread (not STA), which
                    // is very likely why pasted text came out garbled/mixed
                    // up - Clipboard.SetText() silently misbehaving off the
                    // UI thread rather than throwing a clean error. Marshal
                    // just this call onto the UI thread (which is STA) to fix it.
                    var phrase = text.Trim();
                    EcwInjector.InjectResult result = default!;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        result = EcwInjector.TryInject(phrase + " ");
                    });

                    if (result.Success)
                    {
                        PhraseInjected?.Invoke(phrase);
                    }
                    else
                    {
                        ErrorOccurred?.Invoke(result.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            if (pathToProcess != null && File.Exists(pathToProcess))
            {
                try { File.Delete(pathToProcess); } catch { /* best effort cleanup */ }
            }
            _flushing = false;
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Measures amplitude across ~100ms sliding windows and checks the loudest
    /// one against the threshold, rather than averaging the whole chunk - a
    /// silent room still produces a full-size WAV file, and a single real
    /// word surrounded by silence in the same chunk would otherwise get
    /// diluted below the threshold by the quiet padding around it. Threshold
    /// is approximate and mic-dependent; it's tuned conservatively (erring
    /// toward skipping) since a missed quiet phrase is far preferable to
    /// Whisper hallucinating text into a chart.
    /// </summary>
    private static bool ChunkHasRealSpeech(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var buffer = new byte[reader.Length];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read < 2) return false;

            const int windowSamples = 1600; // ~100ms at 16kHz mono
            int sampleCount = read / 2;
            long windowSum = 0;
            int windowCount = 0;
            long maxWindowAvg = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                windowSum += Math.Abs((int)sample);
                windowCount++;

                if (windowCount == windowSamples)
                {
                    maxWindowAvg = Math.Max(maxWindowAvg, windowSum / windowCount);
                    windowSum = 0;
                    windowCount = 0;
                }
            }
            if (windowCount > 0)
            {
                maxWindowAvg = Math.Max(maxWindowAvg, windowSum / windowCount);
            }

            return maxWindowAvg > 300;
        }
        catch
        {
            // Couldn't read it - err toward not sending it to Whisper.
            return false;
        }
    }
}