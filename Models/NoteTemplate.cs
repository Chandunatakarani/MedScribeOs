using System;
using System.Collections.Generic;

namespace MedScribeOS.Models;

/// <summary>
/// Root shape of <c>%LocalAppData%\MedScribeOS\Templates\templates_{doctorId}.json</c>.
/// One file per doctor - that file IS the per-doctor isolation boundary: the
/// store only ever opens the file whose name contains the signed-in doctor's id.
/// </summary>
public sealed class DoctorTemplateFile
{
    public string DoctorId { get; set; } = "";
    public List<NoteTemplate> Templates { get; set; } = new();
}

/// <summary>One named note layout a doctor can apply to a Conversation Analyzer / Dictation session.</summary>
public sealed class NoteTemplate
{
    public string TemplateId { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public List<TemplateSection> Sections { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A section of the note - "HPI", "ROS", or any custom name the doctor types.</summary>
public sealed class TemplateSection
{
    public string SectionKey { get; set; } = "";
    public string Label { get; set; } = "";
    public List<TemplateField> Fields { get; set; } = new();
}

/// <summary>One extracted field. <see cref="Prompt"/> is the free-text instruction that steers GPT-4o for this field.</summary>
public sealed class TemplateField
{
    public string FieldKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Prompt { get; set; }
}
