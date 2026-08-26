using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace MedScribeOS.Services;

/// <summary>
/// Records and stores a one-time reference sample of the provider's voice,
/// reused across every visit as the "known speaker" anchor for
/// gpt-4o-transcribe-diarize's audio-based diarization. Enrolling once and
/// reusing forever fits this app's actual use case - the same provider,
/// visit after visit - far better than re-diarizing from scratch each time:
/// speaker labeling becomes "does this voice match the enrolled doctor" (a
/// tractable verification problem) instead of guessing Doctor/Patient from
/// what a line of text happens to say, which is what made labels unreliable
/// before this.
/// </summary>
public static class DoctorVoiceEnrollment
{
    private static readonly WaveFormat Format = new(16000, 1);

    private static string ReferencePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MedScribeOS", "doctor_voice_reference.wav");

    public static bool IsEnrolled => File.Exists(ReferencePath);

    /// <summary>Records for the given duration from the default microphone and saves it as the permanent reference clip, overwriting any previous enrollment.</summary>
    public static async Task RecordAsync(TimeSpan duration)
    {
        var dir = Path.GetDirectoryName(ReferencePath)!;
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(Path.GetTempPath(), $"medscribe_enroll_{Guid.NewGuid():N}.wav");

        using (var waveIn = new WaveInEvent { WaveFormat = Format })
        using (var writer = new WaveFileWriter(tempPath, Format))
        {
            waveIn.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
            waveIn.StartRecording();
            await Task.Delay(duration);
            waveIn.StopRecording();
            // Let any in-flight DataAvailable callback land before the writer/waveIn dispose below.
            await Task.Delay(200);
        }

        File.Copy(tempPath, ReferencePath, overwrite: true);
        try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
    }

    /// <summary>The enrolled clip encoded the way gpt-4o-transcribe-diarize's known_speaker_references expects.</summary>
    public static string GetReferenceAsDataUrl()
    {
        var bytes = File.ReadAllBytes(ReferencePath);
        return $"data:audio/wav;base64,{Convert.ToBase64String(bytes)}";
    }
}
