using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MedScribeOS.Models;
using MedScribeOS.Services;

namespace MedScribeOS;

/// <summary>
/// Post-login template manager. Shows only the signed-in doctor's templates
/// (the store is keyed by <see cref="ISessionService.DoctorId"/>), with New /
/// Edit / Duplicate / Delete / Set-as-Default. Every mutation is a full
/// read-modify-write of that doctor's JSON file via <see cref="JsonTemplateStore"/>.
/// </summary>
public partial class TemplateListWindow : Window
{
    private readonly ITemplateStore _store = new JsonTemplateStore();
    private readonly ISessionService _session = SessionService.Instance;

    private DoctorTemplateFile _file = new();

    public TemplateListWindow()
    {
        InitializeComponent();
        GlassChrome.Apply(this);
        Reload();
    }

    private void Reload()
    {
        _file = _store.Load(_session.DoctorId);
        SubHeader.Text = $"Dr. {_session.DoctorDisplayName}  ·  {_file.Templates.Count} template(s)";
        RebuildList();
    }

    private void RebuildList()
    {
        ListPanel.Children.Clear();

        foreach (var template in _file.Templates.OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            ListPanel.Children.Add(BuildRow(template));
        }
    }

    private UIElement BuildRow(NoteTemplate template)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("BgCardBrush"),
            BorderBrush = (Brush)FindResource(template.IsDefault ? "AccentBrush" : "BorderBrush2"),
            BorderThickness = new Thickness(template.IsDefault ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── left: name + summary ──────────────────────────────────────────
        var info = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = template.Name,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (template.IsDefault)
        {
            titleRow.Children.Add(new Border
            {
                Background = (Brush)FindResource("AccentBrush"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "DEFAULT", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold },
            });
        }
        info.Children.Add(titleRow);

        var fieldCount = template.Sections.Sum(s => s.Fields.Count);
        info.Children.Add(new TextBlock
        {
            Text = $"{template.Sections.Count} section(s) · {fieldCount} field(s)   —   {string.Join(", ", template.Sections.Select(s => s.SectionKey))}",
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontSize = 12.5,
            Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // ── right: actions ────────────────────────────────────────────────
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ActionButton("Edit", primary: false, () => EditTemplate(template)));
        actions.Children.Add(ActionButton("Duplicate", primary: false, () => DuplicateTemplate(template)));
        if (!template.IsDefault)
            actions.Children.Add(ActionButton("Set Default", primary: false, () => SetDefault(template)));
        actions.Children.Add(ActionButton("Delete", primary: false, () => DeleteTemplate(template)));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private Button ActionButton(string text, bool primary, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "PrimaryButtonStyle" : "GhostButtonStyle"),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ── actions ─────────────────────────────────────────────────────────────

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TemplateEditorWindow(_file, template: null) { Owner = this };
        if (editor.ShowDialog() == true) Reload();
    }

    private void EditTemplate(NoteTemplate template)
    {
        var editor = new TemplateEditorWindow(_file, template) { Owner = this };
        if (editor.ShowDialog() == true) Reload();
    }

    private void DuplicateTemplate(NoteTemplate template)
    {
        var copy = DeepClone(template);
        copy.TemplateId = Guid.NewGuid().ToString();
        copy.IsDefault = false;
        copy.CreatedAt = DateTimeOffset.UtcNow;
        copy.UpdatedAt = DateTimeOffset.UtcNow;
        copy.Name = UniqueName(template.Name + " (copy)");

        _file.Templates.Add(copy);
        _store.Save(_file);
        Notify.Success($"Duplicated as \"{copy.Name}\".");
        Reload();
    }

    private void SetDefault(NoteTemplate template)
    {
        foreach (var t in _file.Templates) t.IsDefault = ReferenceEquals(t, template);
        _store.Save(_file);
        Notify.Success($"\"{template.Name}\" is now the default template.");
        Reload();
    }

    private void DeleteTemplate(NoteTemplate template)
    {
        if (_file.Templates.Count <= 1)
        {
            Notify.Warning("A doctor must keep at least one template - create another before deleting this one.");
            return;
        }

        if (MessageBox.Show($"Delete the template \"{template.Name}\"? This can't be undone.",
                "Delete template", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _file.Templates.Remove(template);
        if (!_file.Templates.Any(t => t.IsDefault))
            _file.Templates[0].IsDefault = true; // promote a new default

        _store.Save(_file);
        Notify.Success($"Deleted \"{template.Name}\".");
        Reload();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ── helpers ─────────────────────────────────────────────────────────────

    private string UniqueName(string desired)
    {
        var name = desired;
        var n = 2;
        while (_file.Templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{desired} {n++}";
        return name;
    }

    private static NoteTemplate DeepClone(NoteTemplate template) =>
        JsonSerializer.Deserialize<NoteTemplate>(JsonSerializer.Serialize(template))!;
}
