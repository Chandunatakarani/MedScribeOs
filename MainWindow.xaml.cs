using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MedScribeOS.Services;
// Same WPF/WinForms overlap as DictationEngine.cs - pin TextBox to the WPF one.
using TextBox = System.Windows.Controls.TextBox;

namespace MedScribeOS;

public partial class MainWindow : Window
{
    private readonly AudioRecorder _recorder = new();
    private readonly OpenAiClient? _openAi;
    private readonly DictationEngine? _dictation;
    private readonly LivePreviewRecorder? _livePreview;
    private readonly LiveConversationTranscriber? _liveTranscriber;

    private List<ConversationTurn> _turns = new();
    private HpiRosResult? _hpiRos;
    private readonly Dictionary<string, TextBox> _hpiBoxes = new();
    private readonly Dictionary<string, TextBox> _rosBoxes = new();

    private static readonly Dictionary<string, string> HpiLabels = new()
    {
        ["chief_complaint"] = "Chief Complaint",
        ["onset"] = "Onset",
        ["location"] = "Location",
        ["duration"] = "Duration",
        ["character"] = "Character",
        ["severity"] = "Severity",
        ["aggravating_factors"] = "Aggravating Factors",
        ["relieving_factors"] = "Relieving Factors",
        ["associated_symptoms"] = "Associated Symptoms",
        ["prior_episodes"] = "Prior Episodes",
        ["medications_tried"] = "Medications Tried",
    };

    private static readonly Dictionary<string, string> RosLabels = new()
    {
        ["constitutional"] = "Constitutional",
        ["heent"] = "HEENT",
        ["cardiovascular"] = "Cardiovascular",
        ["respiratory"] = "Respiratory",
        ["gastrointestinal"] = "GI",
        ["genitourinary"] = "GU",
        ["musculoskeletal"] = "Musculoskeletal",
        ["neurological"] = "Neurological",
        ["skin"] = "Skin",
        ["psychiatric"] = "Psychiatric",
    };

    public MainWindow()
    {
        InitializeComponent();

        // Selecting the initial tab has to happen AFTER InitializeComponent
        // finishes, not via IsChecked="True" in the XAML - setting IsChecked
        // in XAML fires the Checked event WHILE the file is still being
        // parsed, before later-declared elements like PanelVoiceDictation
        // and PanelFileAnalyzer have been assigned to their fields yet,
        // which is exactly what crashed ShowPanel() with a null reference.
        TabVoiceAnalyzer.IsChecked = true;

        try
        {
            _openAi = new OpenAiClient();
            _dictation = new DictationEngine(_openAi);
            _dictation.PhraseInjected += phrase => Dispatcher.Invoke(() => TxtLastPhrase.Text = phrase);
            _dictation.ErrorOccurred += err => Dispatcher.Invoke(() => TxtLastPhrase.Text = $"[error] {err}");

            _livePreview = new LivePreviewRecorder();
            _livePreview.LevelChanged += level => Dispatcher.Invoke(() => MicLevelBar.Value = level);
            _livePreview.ErrorOccurred += err => Dispatcher.Invoke(() => TxtConvoStatus.Text = $"[mic error: {err}]");

            _liveTranscriber = new LiveConversationTranscriber(_openAi);
            _liveTranscriber.TurnAdded += turn => Dispatcher.Invoke(() =>
            {
                _turns.Add(turn);
                AppendLiveTurn(turn);
            });
            _liveTranscriber.ErrorOccurred += err => Dispatcher.Invoke(() =>
                TxtConvoStatus.Text = $"[live transcription error: {err}]");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"OPENAI_API_KEY is not set, so Voice Analyzer and Voice Dictation won't work until it is.\n\n{ex.Message}",
                "MedScribeAI - setup needed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateEnrollmentUi();
    }

    // ── Voice Analyzer: doctor voice enrollment ──────────────────────────────
    private void UpdateEnrollmentUi()
    {
        BtnEnrollVoice.Content = DoctorVoiceEnrollment.IsEnrolled ? "🎙️ Re-enroll Doctor Voice" : "🎙️ Enroll Doctor Voice";
        TxtEnrollStatus.Text = DoctorVoiceEnrollment.IsEnrolled
            ? "✓ Enrolled - Start Conversation will label turns by matching this voice."
            : "Required once so Doctor/Patient turns can be told apart by voice, not guesswork.";
    }

    private async void BtnEnrollVoice_Click(object sender, RoutedEventArgs e)
    {
        BtnEnrollVoice.IsEnabled = false;
        BtnStartConvo.IsEnabled = false;
        TxtEnrollStatus.Text = "🔴 Recording reference — read a sentence or two out loud for 8 seconds…";

        try
        {
            await DoctorVoiceEnrollment.RecordAsync(TimeSpan.FromSeconds(8));
            UpdateEnrollmentUi();
        }
        catch (Exception ex)
        {
            TxtEnrollStatus.Text = $"Enrollment failed: {DescribeError(ex)}";
        }
        finally
        {
            BtnEnrollVoice.IsEnabled = true;
            BtnStartConvo.IsEnabled = true;
        }
    }

    // ── Tab switching ──────────────────────────────────────────────────────
    private void TabVoiceAnalyzer_Checked(object sender, RoutedEventArgs e) => ShowPanel(PanelVoiceAnalyzer);
    private void TabVoiceDictation_Checked(object sender, RoutedEventArgs e) => ShowPanel(PanelVoiceDictation);
    private void TabFileAnalyzer_Checked(object sender, RoutedEventArgs e) => ShowPanel(PanelFileAnalyzer);

    private void ShowPanel(UIElement panel)
    {
        // Defensive guard: if this ever fires before all three panel fields
        // are assigned (the same timing issue IsChecked="True" caused),
        // do nothing instead of crashing.
        if (PanelVoiceAnalyzer == null || PanelVoiceDictation == null || PanelFileAnalyzer == null) return;

        PanelVoiceAnalyzer.Visibility = Visibility.Collapsed;
        PanelVoiceDictation.Visibility = Visibility.Collapsed;
        PanelFileAnalyzer.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
    }

    // ── Voice Analyzer: Start / End Conversation ─────────────────────────────
    private void BtnStartConvo_Click(object sender, RoutedEventArgs e)
    {
        if (!DoctorVoiceEnrollment.IsEnrolled)
        {
            MessageBox.Show(
                "Enroll the doctor's voice first (one-time, ~8 seconds) so Voice Analyzer can tell Doctor and Patient apart by voice instead of guessing.",
                "MedScribeAI - enrollment needed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _recorder.Start();
        _livePreview?.Start();
        _turns = new List<ConversationTurn>();
        _liveTranscriber?.Start();
        BtnStartConvo.IsEnabled = false;
        BtnEndConvo.IsEnabled = true;
        TxtConvoStatus.Text = "🔴 Recording — speak naturally";
        TxtConvoStatus.Foreground = (Brush)FindResource("TextPrimaryBrush");
        MicLevelBar.Visibility = Visibility.Visible;
        PanelAnalyzeAction.Visibility = Visibility.Collapsed;
        PanelHpiRos.Visibility = Visibility.Collapsed;
        RenderTranscript();
    }

    private async void BtnEndConvo_Click(object sender, RoutedEventArgs e)
    {
        BtnEndConvo.IsEnabled = false;
        _livePreview?.Stop();
        MicLevelBar.Visibility = Visibility.Collapsed;
        MicLevelBar.Value = 0;
        _recorder.Stop(); // full-session WAV is kept only as a backup now - live turns are the real transcript

        if (_liveTranscriber == null)
        {
            TxtConvoStatus.Text = "Nothing recorded, or OPENAI_API_KEY is missing.";
            BtnStartConvo.IsEnabled = true;
            return;
        }

        // A few turns may still be mid-transcription (queued right as End
        // Conversation was pressed) - wait for those to land before treating
        // _turns as final, since they ARE the record now, not a placeholder.
        TxtConvoStatus.Text = "Finishing up the last few turns…";
        TxtConvoStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");

        try
        {
            await _liveTranscriber.StopAndFlushAsync();
            if (_turns.Count == 0) RenderTranscript();

            TxtConvoStatus.Text = _turns.Count > 0
                ? $"✓ {_turns.Count} turns captured — review below, then Analyze."
                : "No speech was detected during the recording.";
            TxtConvoStatus.Foreground = (Brush)FindResource("TextPrimaryBrush");
            PanelAnalyzeAction.Visibility = _turns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TxtConvoStatus.Text = $"Error finishing transcription: {DescribeError(ex)}";
            TxtConvoStatus.Foreground = (Brush)FindResource("RedBrush");
        }
        finally
        {
            BtnStartConvo.IsEnabled = true;
        }
    }

    /// <summary>Full rebuild - used for the initial/empty state and whenever a turn's speaker gets toggled.</summary>
    private void RenderTranscript()
    {
        TranscriptPanel.Children.Clear();

        if (_turns.Count == 0)
        {
            TranscriptPanel.Children.Add(new TextBlock
            {
                Text = _recorder.IsRecording
                    ? "🎙️ Recording — turns appear here live as you and the patient speak."
                    : "Press Start Conversation to begin. The full conversation will appear here for your review before any AI drafting happens - nothing is charted automatically.",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic
            });
            return;
        }

        for (int i = 0; i < _turns.Count; i++)
        {
            TranscriptPanel.Children.Add(BuildTurnRow(_turns[i], i));
        }
    }

    /// <summary>
    /// Adds just the newest turn's row without touching earlier rows - a
    /// full RenderTranscript() rebuild would tear down and recreate every
    /// TextBox on screen, which would blow away an in-progress edit if the
    /// provider is correcting an earlier turn's text right as a new one
    /// streams in.
    /// </summary>
    private void AppendLiveTurn(ConversationTurn turn)
    {
        if (_turns.Count == 1)
        {
            RenderTranscript(); // first turn - panel currently only has the placeholder
            return;
        }
        TranscriptPanel.Children.Add(BuildTurnRow(turn, _turns.Count - 1));
    }

    private UIElement BuildTurnRow(ConversationTurn turn, int idx)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

        var speakerBtn = new Button
        {
            Content = turn.Speaker == "Doctor" ? "DR" : "PT",
            Width = 40,
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource(turn.Speaker == "Doctor" ? "TextPrimaryBrush" : "BorderBrush2"),
            Foreground = turn.Speaker == "Doctor" ? Brushes.Black : Brushes.White,
            FontWeight = FontWeights.Bold
        };
        speakerBtn.Click += (_, __) =>
        {
            var newSpeaker = _turns[idx].Speaker == "Doctor" ? "Patient" : "Doctor";
            _turns[idx] = _turns[idx] with { Speaker = newSpeaker };
            RenderTranscript();
        };
        DockPanel.SetDock(speakerBtn, Dock.Left);

        var textBox = new TextBox
        {
            Text = turn.Text,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 36
        };
        textBox.LostFocus += (_, __) =>
        {
            _turns[idx] = _turns[idx] with { Text = textBox.Text };
        };

        row.Children.Add(speakerBtn);
        row.Children.Add(textBox);
        return row;
    }

    // ── Voice Analyzer: Analyze → HPI / ROS ──────────────────────────────────
    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_openAi == null || _turns.Count == 0) return;

        BtnAnalyze.IsEnabled = false;
        try
        {
            _hpiRos = await _openAi.ExtractHpiRosAsync(_turns);
            RenderHpiRos();
            PanelHpiRos.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Analysis failed: {DescribeError(ex)}", "MedScribeAI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnAnalyze.IsEnabled = true;
        }
    }

    /// <summary>
    /// .NET's top-level exception message for SSL/network failures ("The SSL
    /// connection could not be established, see inner exception") deliberately
    /// hides the real reason - it's in InnerException. Chain both together so
    /// error messages are actually diagnosable instead of generic.
    /// </summary>
    private static string DescribeError(Exception ex)
    {
        return ex.InnerException != null
            ? $"{ex.Message} → {ex.InnerException.Message}"
            : ex.Message;
    }

    private void RenderHpiRos()
    {
        if (_hpiRos == null) return;

        HpiFieldsPanel.Children.Clear();
        _hpiBoxes.Clear();
        foreach (var (key, label) in HpiLabels)
        {
            _hpiRos.Hpi.TryGetValue(key, out var value);
            HpiFieldsPanel.Children.Add(BuildFieldRow(label, value ?? "Not discussed", key, _hpiBoxes));
        }

        RosFieldsPanel.Children.Clear();
        _rosBoxes.Clear();
        foreach (var (key, label) in RosLabels)
        {
            _hpiRos.Ros.TryGetValue(key, out var value);
            RosFieldsPanel.Children.Add(BuildFieldRow(label, value ?? "Not discussed", key, _rosBoxes));
        }
    }

    private UIElement BuildFieldRow(string label, string value, string key, Dictionary<string, TextBox> store)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("TextSecondaryBrush"), FontSize = 11 });
        var box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 30, Margin = new Thickness(0, 2, 0, 0) };
        store[key] = box;
        panel.Children.Add(box);
        return panel;
    }

    // ── Voice Analyzer: Inject buttons ───────────────────────────────────────
    private void BtnInjectRos_Click(object sender, RoutedEventArgs e)
    {
        var result = EcwInjector.TryInjectIntoRos(BuildFieldText(RosLabels, _rosBoxes));
        TxtInjectStatus.Text = result.Message;
        TxtInjectStatus.Foreground = (Brush)FindResource(result.Success ? "TextPrimaryBrush" : "RedBrush");
    }

    private void BtnInjectHpi_Click(object sender, RoutedEventArgs e)
    {
        var result = EcwInjector.TryInjectIntoFocusedHpi(BuildFieldText(HpiLabels, _hpiBoxes));
        TxtInjectStatus.Text = result.Message;
        TxtInjectStatus.Foreground = (Brush)FindResource(result.Success ? "TextPrimaryBrush" : "RedBrush");
    }

    private static string BuildFieldText(Dictionary<string, string> labels, Dictionary<string, TextBox> boxes)
    {
        var lines = new List<string>();
        foreach (var (key, label) in labels)
        {
            if (boxes.TryGetValue(key, out var box) && !string.IsNullOrWhiteSpace(box.Text) && box.Text != "Not discussed")
            {
                lines.Add($"{label}: {box.Text}");
            }
        }
        return string.Join("\n", lines);
    }

    // ── Voice Dictation tab ──────────────────────────────────────────────────
    private void MicCircle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dictation == null) return;

        if (_dictation.IsRunning)
        {
            _dictation.Stop();
            SetMicVisual(false);
        }
        else
        {
            _dictation.Start();
            SetMicVisual(true);
        }
    }

    private void SetMicVisual(bool on)
    {
        MicCircle.Fill = (Brush)FindResource(on ? "TextPrimaryBrush" : "BorderBrush2");
        TxtMicStatus.Text = on ? "ON — click any eCW field and speak" : "OFF — click to enable dictation";
        TxtMicStatus.Foreground = (Brush)FindResource(on ? "TextPrimaryBrush" : "TextSecondaryBrush");
    }
}