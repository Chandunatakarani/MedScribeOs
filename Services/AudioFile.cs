using System;
using System.Collections.Generic;
using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MedScribeOS.Services;

/// <summary>
/// Decodes an uploaded recording (wav / mp3 / m4a) and slices it into short
/// 16 kHz-mono WAV chunks so the File Analyzer can transcribe a long visit
/// piece by piece with a real progress bar - and stay under the transcription
/// provider's per-request size limit.
/// </summary>
public static class AudioFile
{
    private static readonly WaveFormat Target = new(16000, 1);

    public static TimeSpan Duration(string path)
    {
        using var reader = Open(path);
        return reader.TotalTime;
    }

    /// <summary>
    /// Returns temp WAV file paths, ~<paramref name="chunkSeconds"/> each,
    /// covering the whole recording in order. The caller deletes them.
    /// </summary>
    public static IReadOnlyList<string> SplitToWavChunks(string path, int chunkSeconds)
    {
        try { MediaFoundationApi.Startup(); } catch { /* already started */ }

        using var reader = Open(path);
        using var resampler = new MediaFoundationResampler(reader, Target) { ResamplerQuality = 60 };

        // Decode the whole thing to 16k mono PCM in memory (~2 MB per recorded
        // minute - fine for a visit-length file).
        using var pcm = new MemoryStream();
        var buffer = new byte[Target.AverageBytesPerSecond];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            pcm.Write(buffer, 0, read);
        var bytes = pcm.ToArray();

        var chunkBytes = Target.AverageBytesPerSecond * Math.Max(5, chunkSeconds);
        chunkBytes -= chunkBytes % Target.BlockAlign; // keep sample-aligned

        var files = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, bytes.Length - offset);
            var file = Path.Combine(Path.GetTempPath(), $"medscribe_fa_{files.Count:D3}_{Guid.NewGuid():N}.wav");
            using (var writer = new WaveFileWriter(file, Target))
                writer.Write(bytes, offset, length);
            files.Add(file);
        }
        return files;
    }

    private static WaveStream Open(string path)
        => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
}
