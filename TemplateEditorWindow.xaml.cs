using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MedScribeOS.Models;
using MedScribeOS.Services;

namespace MedScribeOS;

/// <summary>
/// Add / rename / reorder / delete sections and their fields for one template.
/// Edits happen on a deep copy, so Cancel throws everything away; Save
/// validates, folds the copy back into the doctor's <see cref="DoctorTemplateFile"/>,
/// and persists the whole file atomically through <see cref="JsonTemplateStore"/>.
/// </summary>
public partial class TemplateEditorWindow : Window
{
    private readonly ITemplateStore _store = new JsonTemplateStore();
    private readonly DoctorTemplateFile _file;
    private readonly bool _isNew;
    private readonly NoteTemplate _working;

    public TemplateEditorWindow(DoctorTemplateFile file, NoteTemplate? template)
    {
        InitializeComponent();
        _file = file;
        _isNew = template == null;

        _working = template == null
            ? NewBlankTemplate()
            : JsonSerializer.Deserialize<NoteTemplate>(JsonSerializer.Serialize(template))!;

        HeaderText.Text = _isNew ? "New Template" : "Edit Template";
        NameBox.Text = _working.Name;
        RebuildSections();
    }

    private static NoteTemplate NewBlankTemplate() => new()
    {
        Name = "",
        IsDefault = false,
        Sections =
        {
            new TemplateSection
            {
                SectionKey = "HPI",
                Label = "History of Present Illness",
                Fields = { new TemplateField { FieldKey = "onset", Label = "Onset", Prompt = "When did symptoms begin?" } },
            },
        },
    };

    // ── section / field UI ──────────────────────────────────────────────────

    private void RebuildSections()
    {
        SectionsPanel.Children.Clear();
        for (var i = 0; i < _working.Sections.Count; i++)
            SectionsPanel.Children.Add(BuildSectionCard(_working.Sections[i], i));
    }

    private UIElement BuildSectionCard(TemplateSection section, int index)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("BgCardBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var outer = new StackPanel();

        // header row: label + key + reorder/delete
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBox = LabeledBox("SECTION LABEL", section.Label, v => section.Label = v);
        Grid.SetColumn(labelBox, 0);
        head.Children.Add(labelBox);

        var keyBox = LabeledBox("KEY", section.SectionKey, v => section.SectionKey = v);
        keyBox.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(keyBox, 1);
        head.Children.Add(keyBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(MiniButton("▲", enabled: index > 0, () => MoveSection(index, -1)));
        buttons.Children.Add(MiniButton("▼", enabled: index < _working.Sections.Count - 1, () => MoveSection(index, +1)));
        buttons.Children.Add(MiniButton("✕", enabled: true, () => { _working.Sections.RemoveAt(index); RebuildSections(); }));
        Grid.SetColumn(buttons, 2);
        head.Children.Add(buttons);

        outer.Children.Add(head);

        // fields
        var fieldsHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        for (var f = 0; f < section.Fields.Count; f++)
            fieldsHost.Children.Add(BuildFieldRow(section, f));
        outer.Children.Add(fieldsHost);

        var addField = new Button
        {
            Content = "＋ Add Field",
            Style = (Style)FindResource("GhostButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11,
        };
        addField.Click += (_, _) =>
        {
            section.Fields.Add(new TemplateField { FieldKey = "", Label = "", Prompt = "" });
            RebuildSections();
        };
        outer.Children.Add(addField);

        card.Child = outer;
        return card;
    }

    private UIElement BuildFieldRow(TemplateSection section, int fieldIndex)
    {
        var field = section.Fields[fieldIndex];

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBox = MakeBox(field.Label, "Field label", v => field.Label = v);
        Grid.SetColumn(labelBox, 0);
        grid.Children.Add(labelBox);

        var promptBox = MakeBox(field.Prompt ?? "", "Prompt that steers GPT-4o for this field (optional)", v => field.Prompt = v);
        promptBox.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(promptBox, 1);
        grid.Children.Add(promptBox);

        var del = MiniButton("✕", enabled: true, () => { section.Fields.RemoveAt(fieldIndex); RebuildSections(); });
        del.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(del, 2);
        grid.Children.Add(del);

        return grid;
    }

    private void MoveSection(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _working.Sections.Count) return;
        (_working.Sections[index], _working.Sections[target]) = (_working.Sections[target], _working.Sections[index]);
        RebuildSections();
    }

    // ── small control builders ─────────────────────────────────────────────

    private static StackPanel LabeledBox(string caption, string value, Action<string> onChanged)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = caption, Foreground = Brushes.Gray, FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(MakeBox(value, "", onChanged));
        return panel;
    }

    private static TextBox MakeBox(string value, string tooltip, Action<string> onChanged)
    {
        var box = new TextBox { Text = value, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(tooltip)) box.ToolTip = tooltip;
        box.TextChanged += (_, _) => onChanged(box.Text);
        return box;
    }

    private Button MiniButton(string glyph, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            Style = (Style)FindResource("GhostButtonStyle"),
            Width = 34,
            Height = 30,
            Margin = new Thickness(4, 0, 0, 0),
            IsEnabled = enabled,
            FontSize = 11,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ── commands ───────────────────────────────────────────────────────────

    private void BtnAddSection_Click(object sender, RoutedEventArgs e)
    {
        _working.Sections.Add(new TemplateSection { SectionKey = "", Label = "", Fields = { new TemplateField() } });
        RebuildSections();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _working.Name = NameBox.Text.Trim();

        // Fill in any blank keys from labels before validating.
        foreach (var section in _working.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.SectionKey))
                section.SectionKey = Slug(section.Label);
            foreach (var field in section.Fields)
                if (string.IsNullOrWhiteSpace(field.FieldKey))
                    field.FieldKey = Slug(field.Label);
        }

        var error = Validate();
        if (error != null)
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        _working.UpdatedAt = DateTimeOffset.UtcNow;

        var existing = _file.Templates.FindIndex(t => t.TemplateId == _working.TemplateId);
        if (existing >= 0) _file.Templates[existing] = _working;
        else _file.Templates.Add(_working);

        if (_working.IsDefault)
            foreach (var t in _file.Templates)
                t.IsDefault = t.TemplateId == _working.TemplateId;

        _store.Save(_file);
        Notify.Success(_isNew ? $"Template \"{_working.Name}\" created." : $"Template \"{_working.Name}\" saved.");

        DialogResult = true;
        Close();
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(_working.Name))
            return "Template name is required.";

        if (_file.Templates.Any(t => t.TemplateId != _working.TemplateId
                                     && string.Equals(t.Name, _working.Name, StringComparison.OrdinalIgnoreCase)))
            return $"You already have a template named \"{_working.Name}\". Names must be unique.";

        if (_working.Sections.Count == 0)
            return "Add at least one section.";

        foreach (var section in _working.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Label))
                return "Every section needs a label.";
            if (string.IsNullOrWhiteSpace(section.SectionKey))
                return $"Section \"{section.Label}\" needs a key (letters/numbers).";
            if (section.Fields.Count == 0)
                return $"Section \"{section.Label}\" has no fields - add one or remove the section.";
            foreach (var field in section.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Label))
                    return $"A field in \"{section.Label}\" is missing its label.";
                if (string.IsNullOrWhiteSpace(field.FieldKey))
                    return $"Field \"{field.Label}\" in \"{section.Label}\" needs a key.";
            }

            var dupField = section.Fields.GroupBy(f => f.FieldKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dupField != null)
                return $"Section \"{section.Label}\" has two fields with the key \"{dupField.Key}\".";
        }

        var dupSection = _working.Sections.GroupBy(s => s.SectionKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupSection != null)
            return $"Two sections share the key \"{dupSection.Key}\" - section keys must be unique.";

        return null;
    }

    /// <summary>label -> "history_of_present_illness"; safe as a JSON key and a prompt token.</summary>
    private static string Slug(string text)
    {
        var lowered = (text ?? "").Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, "[^a-z0-9]+", "_").Trim('_');
        return string.IsNullOrEmpty(slug) ? "field" : slug;
    }
}
