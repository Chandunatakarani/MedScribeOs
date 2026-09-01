using System;

namespace MedScribeOS.Models;

public enum SpeakerRole
{
    Doctor,
    Patient,
}

/// <summary>
/// One attributed utterance in a doctor-patient conversation. Immutable - the
/// live chat view holds these in an ObservableCollection and replaces the item
/// when a turn is re-attributed (speaker flipped) so the UI refreshes.
/// </summary>
public record ConversationTurn(SpeakerRole Speaker, string Text, DateTimeOffset Timestamp)
{
    public bool IsDoctor => Speaker == SpeakerRole.Doctor;

    /// <summary>"Doctor" / "Patient" - the wire label the HPI/ROS extraction prompt expects.</summary>
    public string SpeakerLabel => Speaker == SpeakerRole.Doctor ? "Doctor" : "Patient";
}
