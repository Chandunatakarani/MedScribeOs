using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MedScribeOS.Models;
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

    private readonly ITemplateStore _templateStore = new JsonTemplateStore();
    private readonly ISessionService _session = SessionService.Instance;

    /// <summary>Live conversation, bound to the chat ListBox. Turns are appended as each is transcribed; the item is replaced (not mutated) when a speaker is flipped.</summary>
    public ObservableCollection<ConversationTurn> ChatTurns { get; } = new();

    /// <summary>File Analyzer conversation, bound to its own chat ListBox - same bubbles, same ⇄ flip correction as the live chat.</summary>
    public ObservableCollection<ConversationTurn> FileTurns { get; } = new();

    // Filled by Analyze; both are shaped by whichever template the doctor picked.
    private NoteTemplate? _activeTemplate;
    private TemplateExtractionResult? _extraction;
    // section key -> (field key -> the editable TextBox showing that field)
    private readonly Dictionary<string, Dictionary<string, TextBox>> _sectionBoxes = new();

    // File Analyzer progress: a bar (determinate for chunked audio, indeterminate
    // otherwise) plus a running mm:ss so a long transcription never looks frozen.
    // _opEtaSeconds, when known (chunk timings, or PerfStats rates learned from
    // earlier runs), adds a live "~m:ss left" countdown next to the elapsed time.
    private readonly DispatcherTimer _opTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _opStart;
    private string _opLabel = "";
    private double? _opEtaSeconds;

    public MainWindow()
    {
        InitializeComponent();
        GlassChrome.Apply(this);
        _opTimer.Tick += (_, _) => RefreshOpStatus();

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
            _dictation.ErrorOccurred += err => Dispatcher.Invoke(() =>
            {
                TxtLastPhrase.Text = $"[error] {err}";
                Notify.Error($"Dictation: {err}");
            });

            _livePreview = new LivePreviewRecorder();
            _livePreview.LevelChanged += level => Dispatcher.Invoke(() => MicLevelBar.Value = level);
            _livePreview.ErrorOccurred += err => Dispatcher.Invoke(() =>
            {
                TxtConvoStatus.Text = $"[mic error: {err}]";
                Notify.Error($"Microphone: {err}");
            });

            _liveTranscriber = new LiveConversationTranscriber(_openAi);
            _liveTranscriber.TurnAdded += turn => Dispatcher.Invoke(() =>
            {
                ChatTurns.Add(turn);
                ChatList.ScrollIntoView(turn);
                UpdateChatEmptyState();
            });
            _liveTranscriber.ErrorOccurred += err => Dispatcher.Invoke(() =>
            {
                TxtConvoStatus.Text = $"[live transcription error: {err}]";
                Notify.Error($"Live transcription: {err}");
            });
            _liveTranscriber.Notice += msg => Dispatcher.Invoke(() => Notify.Warning(msg));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The AI provider isn't configured, so Voice Analyzer and Voice Dictation are disabled.\n\n{ex.Message}",
                "MedScribeAI - setup needed", MessageBoxButton.OK, MessageBoxImage.Warning);
            Notify.Error("AI provider isn't configured - Voice Analyzer and Voice Dictation are disabled. See config.json.");
        }

        UpdateEnrollmentUi();

        var user = AuthService.CurrentUser;
        if (user == null)
        {
            TxtCurrentUser.Text = "";
        }
        else
        {
            var display = string.IsNullOrWhiteSpace(user.Name) ? user.Mail : user.Name;
            TxtCurrentUser.Text = display;
            Notify.Success($"Signed in as {display}.");
        }

        LoadTemplatesIntoPicker();
    }

    // ── Note templates ─────────────────────────────────────────────────────

    /// <summary>
    /// (Re)fills both template pickers (Voice Analyzer + File Analyzer) from the
    /// signed-in doctor's JSON file, pre-selecting their default. Called on load
    /// and whenever the Templates manager closes.
    /// </summary>
    private void LoadTemplatesIntoPicker()
    {
        if (!_session.IsAuthenticated) return;

        var file = _templateStore.Load(_session.DoctorId);
        var templates = file.Templates
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FillPicker(TemplatePicker, templates);
        FillPicker(FileTemplatePicker, templates);
    }

    private static void FillPicker(ComboBox picker, List<NoteTemplate> templates)
    {
        var previousId = (picker.SelectedItem as NoteTemplate)?.TemplateId;
        picker.ItemsSource = templates;
        picker.SelectedItem =
            templates.FirstOrDefault(t => t.TemplateId == previousId)
            ?? templates.FirstOrDefault(t => t.IsDefault)
            ?? templates.FirstOrDefault();
    }

    private void TemplatePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatePicker.SelectedItem is NoteTemplate t)
        {
            var fieldCount = t.Sections.Sum(s => s.Fields.Count);
            TxtTemplateHint.Text = $"{t.Sections.Count} section(s), {fieldCount} field(s) — shapes the Analyze output.";
        }
        else
        {
            TxtTemplateHint.Text = "";
        }
    }

    private void BtnTemplates_Click(object sender, RoutedEventArgs e)
    {
        new TemplateListWindow { Owner = this }.ShowDialog();
        LoadTemplatesIntoPicker();
    }

    private void BtnSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Sign out of MedScribeAI?",
                "Sign out", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        (System.Windows.Application.Current as App)?.SignOut();
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
        using var busy = BusyButton.Begin(BtnEnrollVoice, "Recording 8s…");
        BtnStartConvo.IsEnabled = false;
        TxtEnrollStatus.Text = "🔴 Recording reference — read a sentence or two out loud for 8 seconds…";

        try
        {
            await DoctorVoiceEnrollment.RecordAsync(TimeSpan.FromSeconds(8));
            UpdateEnrollmentUi();
            Notify.Success("Doctor voice enrolled - conversation turns will be labeled by voice match.");
        }
        catch (Exception ex)
        {
            TxtEnrollStatus.Text = $"Enrollment failed: {DescribeError(ex)}";
            Notify.Error($"Voice enrollment failed: {DescribeError(ex)}");
        }
        finally
        {
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

        // The shared review panel belongs to whichever tab last analyzed - hide
        // it on any tab switch so it can't dangle under an unrelated screen.
        if (PanelHpiRos != null) PanelHpiRos.Visibility = Visibility.Collapsed;
    }

    // ── File Analyzer: load a file, then run the same extraction ─────────────
    private async void BtnChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a transcript, recording, or document",
            Filter = "All supported (*.txt;*.wav;*.mp3;*.m4a;*.pdf;*.docx)|*.txt;*.wav;*.mp3;*.m4a;*.pdf;*.docx|" +
                     "Text transcript (*.txt)|*.txt|" +
                     "Audio recording (*.wav;*.mp3;*.m4a)|*.wav;*.mp3;*.m4a|" +
                     "Document (*.pdf;*.docx)|*.pdf;*.docx",
        };
        if (dlg.ShowDialog() != true) return;

        var path = dlg.FileName;
        using var busy = BusyButton.Begin(BtnChooseFile, "Loading…");
        BtnFileAnalyze.IsEnabled = false;
        try
        {
            string text;
            List<ConversationTurn>? turns = null;

            if (DocumentText.IsAudio(path))
            {
                if (_openAi == null) { Notify.Error("Transcription isn't configured (see config.json)."); return; }
                var segments = await TranscribeAudioWithProgressAsync(path);
                turns = TranscriptTurns.FromSegments(segments);
                text = turns.Count > 0
                    ? TranscriptTurns.ToLabeledText(turns)
                    : string.Join(" ", segments.Select(s => s.Text.Trim()));
            }
            else
            {
                if (DocumentText.IsDocument(path))
                {
                    BeginOp("Reading file…");
                    text = await Task.Run(() => DocumentText.FromFile(path));
                }
                else
                {
                    Notify.Warning("Unsupported file type. Use .txt, .wav/.mp3/.m4a, or .pdf/.docx.");
                    return;
                }
                // "Doctor: ..." / "Pt: ..." style transcripts become bubbles too
                turns = TranscriptTurns.FromLabeledText(text);
            }

            text = text.Trim();
            TxtFileTranscript.Text = text;
            TxtFileName.Text = Path.GetFileName(path);

            if (turns is { Count: > 0 })
            {
                SetFileTurns(turns);
                ShowFileTurnsView();
                EndOp($"{turns.Count} turns detected. Flip any wrong speaker with ⇄, then pick a template and Analyze.");
                Notify.Success($"Loaded {Path.GetFileName(path)} — {turns.Count} turns.");
            }
            else if (text.Length == 0)
            {
                FileTurns.Clear(); // stale turns from a previous file must not linger
                ShowFileTextView();
                EndOp("That file produced no text. If it's a scanned PDF, it has no selectable text to read.");
                Notify.Warning("No readable text found in that file.");
            }
            else
            {
                FileTurns.Clear();
                ShowFileTextView();
                EndOp($"Loaded {text.Length:N0} characters. Review the transcript, pick a template, then Analyze.");
                Notify.Success($"Loaded {Path.GetFileName(path)}.");
            }
        }
        catch (Exception ex)
        {
            EndOp($"Couldn't load that file: {DescribeError(ex)}");
            Notify.Error($"Couldn't load the file: {DescribeError(ex)}");
        }
        finally
        {
            BtnFileAnalyze.IsEnabled = true;
        }
    }

    // ── File Analyzer: turns view <-> raw text view ─────────────────────────

    private void SetFileTurns(List<ConversationTurn> turns)
    {
        FileTurns.Clear();
        foreach (var t in turns) FileTurns.Add(t);
    }

    /// <summary>True while the bubbles (not the raw TextBox) are what the user sees - Analyze then uses the corrected turns.</summary>
    private bool FileTurnsViewActive => FileChatList.Visibility == Visibility.Visible;

    private void ShowFileTurnsView()
    {
        TxtFileTranscript.Visibility = Visibility.Collapsed;
        FileChatList.Visibility = Visibility.Visible;
        BtnFileViewToggle.Visibility = Visibility.Visible;
        BtnFileViewToggle.Content = "✏️ Edit as text";
        TxtFileViewLabel.Text = "CONVERSATION — flip any wrong speaker with ⇄";
    }

    private void ShowFileTextView()
    {
        TxtFileTranscript.Visibility = Visibility.Visible;
        FileChatList.Visibility = Visibility.Collapsed;
        // the toggle only makes sense when there are turns to go back to
        BtnFileViewToggle.Visibility = FileTurns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnFileViewToggle.Content = "💬 View as turns";
        TxtFileViewLabel.Text = "TRANSCRIPT — editable before analysis";
    }

    private void BtnFileViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FileTurnsViewActive)
        {
            // turns -> text: flatten the (possibly flipped) turns for free editing
            TxtFileTranscript.Text = TranscriptTurns.ToLabeledText(FileTurns);
            ShowFileTextView();
        }
        else
        {
            // text -> turns: re-parse the edited labels
            var turns = TranscriptTurns.FromLabeledText(TxtFileTranscript.Text);
            if (turns is { Count: > 0 })
            {
                SetFileTurns(turns);
                ShowFileTurnsView();
            }
            else
            {
                Notify.Warning("Couldn't find \"Doctor:\" / \"Patient:\" labels in the text - staying in text view.");
            }
        }
    }

    /// <summary>
    /// Short clips: one call with an indeterminate bar + running mm:ss. Longer
    /// recordings: decode + split into ~1 min chunks and transcribe them one by
    /// one, so the bar shows real "chunk N of M" progress.
    ///
    /// Returns the timestamped segments (chunk offsets already applied, so
    /// times are absolute across the whole recording) - TranscriptTurns splits
    /// them into Doctor/Patient turns for free. The ETA starts from the rate
    /// PerfStats learned on this machine and, for chunked runs, corrects
    /// itself from the actual per-chunk times as they land.
    /// </summary>
    private async Task<List<RawDiarizedSegment>> TranscribeAudioWithProgressAsync(string path)
    {
        var openAi = _openAi ?? throw new InvalidOperationException("Transcription isn't configured.");
        var chunkSeconds = openAi.AudioChunkSeconds;
        var started = DateTime.Now;

        TimeSpan duration;
        try { duration = await Task.Run(() => AudioFile.Duration(path)); }
        catch { duration = TimeSpan.Zero; }

        // rate learned from previous runs -> estimated total seconds (null on first ever run)
        var eta = PerfStats.TranscribeSecPerAudioSec is { } rate && duration > TimeSpan.Zero
            ? rate * duration.TotalSeconds
            : (double?)null;

        if (duration <= TimeSpan.FromSeconds(Math.Max(90, chunkSeconds * 2)))
        {
            BeginOp(eta is null
                ? "Transcribing audio… (first run also loads the model — can take a few minutes)"
                : "Transcribing audio…", etaSeconds: eta);
            Notify.Info("Transcribing the recording…");
            var result = await openAi.TranscribeSegmentsAsync(path);
            if (duration > TimeSpan.Zero) PerfStats.ObserveTranscribe(duration, DateTime.Now - started);
            return result;
        }

        BeginOp("Preparing audio…", etaSeconds: eta);
        var chunks = await Task.Run(() => AudioFile.SplitToWavChunks(path, chunkSeconds));
        var maxParallel = Math.Clamp(openAi.AudioMaxParallel, 1, chunks.Count);
        Notify.Info($"Transcribing {chunks.Count} segments" + (maxParallel > 1 ? $", {maxParallel} at a time…" : "…"));

        // Each chunk -> its segments, shifted by the chunk's offset so times are
        // absolute across the whole recording. Slotted back by index to keep
        // recording order; up to maxParallel chunks transcribe concurrently.
        var perChunk = Enumerable.Range(0, chunks.Count).Select(_ => new List<RawDiarizedSegment>()).ToArray();
        var completed = 0;
        using var gate = new SemaphoreSlim(maxParallel);

        async Task TranscribeChunk(int i)
        {
            await gate.WaitAsync();
            try
            {
                var offset = (double)i * chunkSeconds; // chunks are cut at exact multiples of chunkSeconds
                var segs = await openAi.TranscribeSegmentsAsync(chunks[i]);
                perChunk[i] = segs
                    .Select(seg => seg with { StartSeconds = seg.StartSeconds + offset, EndSeconds = seg.EndSeconds + offset })
                    .ToList();
            }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref completed);
                // elapsed already reflects the parallel speed-up, so scaling it
                // to the full count keeps the ETA sane for any maxParallel
                var est = (DateTime.Now - started).TotalSeconds * chunks.Count / done;
                Dispatcher.Invoke(() => UpdateOp($"Transcribed {done} of {chunks.Count} segments…", (double)done / chunks.Count, est));
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, chunks.Count).Select(TranscribeChunk));
            UpdateOp("Finishing…", 1.0);
            if (duration > TimeSpan.Zero) PerfStats.ObserveTranscribe(duration, DateTime.Now - started);
            return perChunk.SelectMany(x => x).ToList();
        }
        finally
        {
            foreach (var c in chunks)
            {
                try { File.Delete(c); } catch { /* best effort */ }
            }
        }
    }

    private async void BtnFileAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_openAi == null)
        {
            Notify.Error("Can't analyze - the AI provider isn't configured (see config.json).");
            return;
        }

        // The turns view (with the user's ⇄ corrections) is the better source
        // when it's active - the extraction prompt then knows who said what,
        // exactly like the Voice Analyzer path. Otherwise the raw text is used.
        var useTurns = FileTurnsViewActive && FileTurns.Count > 0;
        var text = useTurns ? TranscriptTurns.ToLabeledText(FileTurns) : TxtFileTranscript.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            Notify.Warning("Load a file or paste a transcript first.");
            return;
        }
        if (FileTemplatePicker.SelectedItem is not NoteTemplate template)
        {
            Notify.Warning("Pick a note template first.");
            return;
        }
        _activeTemplate = template;

        using var busy = BusyButton.Begin(BtnFileAnalyze, "Analyzing…");
        BtnChooseFile.IsEnabled = false;
        var eta = PerfStats.ChatCharsPerSec is { } rate ? text.Length / rate : (double?)null;
        BeginOp($"Analyzing with the \"{template.Name}\" template…", etaSeconds: eta);
        Notify.Info($"Analyzing with the \"{template.Name}\" template…");
        var started = DateTime.Now;
        try
        {
            _extraction = useTurns
                ? await _openAi.ExtractStructuredAsync(FileTurns.ToList(), template)
                : await _openAi.ExtractStructuredFromTextAsync(text, template);
            PerfStats.ObserveChat(text.Length, DateTime.Now - started);
            RenderExtraction();
            PanelHpiRos.Visibility = Visibility.Visible;
            EndOp($"Done. Review every field in \"{template.Name}\" before injecting.");
            Notify.Success($"\"{template.Name}\" draft ready - review every field before injecting.");
        }
        catch (Exception ex)
        {
            EndOp($"Analysis failed: {DescribeError(ex)}");
            MessageBox.Show($"Analysis failed: {DescribeError(ex)}", "MedScribeAI", MessageBoxButton.OK, MessageBoxImage.Error);
            Notify.Error($"Analysis failed: {DescribeError(ex)}");
        }
        finally
        {
            BtnChooseFile.IsEnabled = true;
        }
    }

    // ── File Analyzer progress bar + running mm:ss + ETA countdown ──────────
    private void BeginOp(string label, double? fraction = null, double? etaSeconds = null)
    {
        _opLabel = label;
        _opEtaSeconds = etaSeconds;
        if (!_opTimer.IsEnabled) { _opStart = DateTime.Now; _opTimer.Start(); }

        FileProgress.Visibility = Visibility.Visible;
        if (fraction is { } f)
        {
            FileProgress.IsIndeterminate = false;
            FileProgress.Value = Math.Clamp(f, 0, 1) * 100;
        }
        else
        {
            FileProgress.IsIndeterminate = true;
        }
        RefreshOpStatus();
    }

    private void UpdateOp(string label, double fraction, double? etaSeconds = null) => BeginOp(label, fraction, etaSeconds);

    private void EndOp(string finalStatus)
    {
        _opTimer.Stop();
        _opEtaSeconds = null;
        FileProgress.Visibility = Visibility.Collapsed;
        FileProgress.IsIndeterminate = false;
        FileProgress.Value = 0;
        TxtFileStatus.Text = finalStatus;
    }

    private void RefreshOpStatus()
    {
        var elapsed = DateTime.Now - _opStart;
        var line = $"{_opLabel}   {Mmss(elapsed)}";
        if (_opEtaSeconds is { } eta)
        {
            var remaining = eta - elapsed.TotalSeconds;
            line += remaining > 1
                ? $"  ·  ~{Mmss(TimeSpan.FromSeconds(remaining))} left"
                : "  ·  almost done…";
        }
        TxtFileStatus.Text = line;
    }

    private static string Mmss(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    // ── Voice Analyzer: Start / End Conversation ─────────────────────────────
    private void BtnStartConvo_Click(object sender, RoutedEventArgs e)
    {
        // A template has to be chosen up front - it shapes the extraction, and
        // it's locked in for the whole conversation.
        if (TemplatePicker.SelectedItem is not NoteTemplate template)
        {
            Notify.Warning("Select a note template before starting the conversation.");
            TemplatePicker.Focus();
            return;
        }

        // Voice enrollment is only needed for real (voice-anchored) diarization.
        var needsEnrollment = _openAi?.DiarizationEnabled ?? true;
        if (needsEnrollment && !DoctorVoiceEnrollment.IsEnrolled)
        {
            MessageBox.Show(
                "Enroll the doctor's voice first (one-time, ~8 seconds) so Voice Analyzer can tell Doctor and Patient apart by voice instead of guessing.",
                "MedScribeAI - enrollment needed", MessageBoxButton.OK, MessageBoxImage.Warning);
            Notify.Warning("Enroll the doctor's voice before starting a conversation.");
            return;
        }

        _activeTemplate = template;

        _recorder.Start();
        _livePreview?.Start();
        ChatTurns.Clear();
        _liveTranscriber?.Start();
        BtnStartConvo.IsEnabled = false;
        BtnEndConvo.IsEnabled = true;
        // Lock the template for the duration of the recording.
        TemplatePicker.IsEnabled = false;
        BtnManageTemplates.IsEnabled = false;
        Notify.Info($"Recording started with the \"{template.Name}\" template - speak naturally.");
        TxtConvoStatus.Text = "🔴 Recording — speak naturally";
        TxtConvoStatus.Foreground = (Brush)FindResource("TextPrimaryBrush");
        MicLevelBar.Visibility = Visibility.Visible;
        PanelAnalyzeAction.Visibility = Visibility.Collapsed;
        PanelHpiRos.Visibility = Visibility.Collapsed;
        UpdateChatEmptyState();
    }

    private async void BtnEndConvo_Click(object sender, RoutedEventArgs e)
    {
        using var busy = BusyButton.Begin(BtnEndConvo, "Finishing…");
        _livePreview?.Stop();
        MicLevelBar.Visibility = Visibility.Collapsed;
        MicLevelBar.Value = 0;
        _recorder.Stop(); // full-session WAV is kept only as a backup now - live turns are the real transcript

        if (_liveTranscriber == null)
        {
            TxtConvoStatus.Text = "Nothing recorded, or the AI provider isn't configured.";
            Notify.Error("Couldn't transcribe - the AI provider isn't configured. Check config.json and restart.");
            busy.Dispose();
            BtnEndConvo.IsEnabled = false;
            BtnStartConvo.IsEnabled = true;
            TemplatePicker.IsEnabled = true;
            BtnManageTemplates.IsEnabled = true;
            return;
        }

        // A few turns may still be mid-transcription (queued right as End
        // Conversation was pressed) - wait for those to land before treating
        // ChatTurns as final, since they ARE the record now.
        TxtConvoStatus.Text = "Finishing up the last few turns…";
        TxtConvoStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");

        try
        {
            await _liveTranscriber.StopAndFlushAsync();
            UpdateChatEmptyState();

            TxtConvoStatus.Text = ChatTurns.Count > 0
                ? $"✓ {ChatTurns.Count} turns captured — review below, then Analyze."
                : "No speech was detected during the recording.";
            TxtConvoStatus.Foreground = (Brush)FindResource("TextPrimaryBrush");
            PanelAnalyzeAction.Visibility = ChatTurns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (ChatTurns.Count > 0)
                Notify.Success($"{ChatTurns.Count} turns captured - review them, then Analyze.");
            else
                Notify.Warning("Recording stopped, but no speech was detected.");
        }
        catch (Exception ex)
        {
            TxtConvoStatus.Text = $"Error finishing transcription: {DescribeError(ex)}";
            TxtConvoStatus.Foreground = (Brush)FindResource("RedBrush");
            Notify.Error($"Couldn't finish transcription: {DescribeError(ex)}");
        }
        finally
        {
            // restore the button's normal face first, then park it disabled -
            // End Conversation stays off until the next Start.
            busy.Dispose();
            BtnEndConvo.IsEnabled = false;
            BtnStartConvo.IsEnabled = true;
            TemplatePicker.IsEnabled = true;
            BtnManageTemplates.IsEnabled = true;
        }
    }

    /// <summary>Shows the placeholder text only while the chat is empty.</summary>
    private void UpdateChatEmptyState()
    {
        TxtChatEmpty.Visibility = ChatTurns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtChatEmpty.Text = _recorder.IsRecording
            ? "🎙️ Recording — turns appear here live as you and the patient speak."
            : "Press Start Conversation to begin. Turns appear here live as a two-sided chat — nothing is charted automatically.";
    }

    /// <summary>
    /// Per-bubble correction: flip a turn between Doctor and Patient. Replaces
    /// the (immutable) item in its collection so the bound list re-renders that
    /// bubble on the other side. Shared by the live chat and the File Analyzer
    /// chat (both use the same bubble template); the corrected labels feed
    /// straight into Analyze.
    /// </summary>
    private void FlipSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConversationTurn turn) return;

        var flipped = turn.Speaker == SpeakerRole.Doctor ? SpeakerRole.Patient : SpeakerRole.Doctor;

        var i = ChatTurns.IndexOf(turn);
        if (i >= 0) { ChatTurns[i] = turn with { Speaker = flipped }; return; }

        i = FileTurns.IndexOf(turn);
        if (i >= 0) FileTurns[i] = turn with { Speaker = flipped };
    }

    // ── Voice Analyzer: Analyze → template sections ─────────────────────────
    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_openAi == null)
        {
            Notify.Error("Can't analyze - OpenAI isn't configured. Set an API key and restart.");
            return;
        }
        if (ChatTurns.Count == 0)
        {
            Notify.Warning("Nothing to analyze yet - record a conversation first.");
            return;
        }

        // Use the template locked in at Start Conversation; fall back to the
        // picker only if analysis is somehow reached without a recording.
        var template = _activeTemplate ?? TemplatePicker.SelectedItem as NoteTemplate;
        if (template == null)
        {
            Notify.Warning("Pick a note template before analyzing.");
            return;
        }
        _activeTemplate = template;

        using var busy = BusyButton.Begin(BtnAnalyze, "Analyzing…");
        Notify.Info($"Analyzing conversation with the \"{template.Name}\" template…");
        var sourceChars = ChatTurns.Sum(t => t.Text.Length);
        var started = DateTime.Now;
        try
        {
            _extraction = await _openAi.ExtractStructuredAsync(ChatTurns.ToList(), template);
            PerfStats.ObserveChat(sourceChars, DateTime.Now - started);
            RenderExtraction();
            PanelHpiRos.Visibility = Visibility.Visible;
            Notify.Success($"\"{template.Name}\" draft ready - review every field before injecting.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Analysis failed: {DescribeError(ex)}", "MedScribeAI", MessageBoxButton.OK, MessageBoxImage.Error);
            Notify.Error($"Analysis failed: {DescribeError(ex)}");
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

    /// <summary>
    /// Builds the Doctor Review panel from the active template: one labelled
    /// block per section, an editable box per field pre-filled with the model's
    /// value, and a per-section action button. HPI and ROS keep their eCW-aware
    /// injectors; any other (custom) section gets a "copy to clipboard" action
    /// since eCW has no known automation target for it.
    /// </summary>
    private void RenderExtraction()
    {
        TemplateResultPanel.Children.Clear();
        _sectionBoxes.Clear();
        if (_activeTemplate == null || _extraction == null) return;

        TxtReviewHeader.Text = $"⚕️ Doctor Review — \"{_activeTemplate.Name}\" — all fields editable before injecting";

        foreach (var section in _activeTemplate.Sections)
        {
            TemplateResultPanel.Children.Add(new TextBlock
            {
                Text = section.Label.ToUpperInvariant(),
                Style = (Style)FindResource("LabelStyle"),
                Margin = new Thickness(0, 14, 0, 6),
            });

            var boxes = new Dictionary<string, TextBox>();
            _extraction.Sections.TryGetValue(section.SectionKey, out var values);

            foreach (var field in section.Fields)
            {
                var value = values != null && values.TryGetValue(field.FieldKey, out var v) ? v : "Not discussed";
                TemplateResultPanel.Children.Add(BuildFieldRow(field.Label, value, field.FieldKey, boxes));
            }
            _sectionBoxes[section.SectionKey] = boxes;

            var injectButton = new Button
            {
                Content = InjectVerbFor(section),
                Style = (Style)FindResource(IsEcwSection(section) ? "PrimaryButtonStyle" : "GhostButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 4),
            };
            var captured = section;
            injectButton.Click += (_, _) => InjectSection(captured);
            TemplateResultPanel.Children.Add(injectButton);
        }
    }

    private static bool IsEcwSection(TemplateSection section) =>
        section.SectionKey.Equals("HPI", StringComparison.OrdinalIgnoreCase) ||
        section.SectionKey.Equals("ROS", StringComparison.OrdinalIgnoreCase);

    private static string InjectVerbFor(TemplateSection section) => section.SectionKey.ToUpperInvariant() switch
    {
        "HPI" => "⚡ Inject HPI (into focused problem's box)",
        "ROS" => "⚡ Inject ROS",
        _ => $"⧉ Copy {section.Label} to clipboard",
    };

    private void InjectSection(TemplateSection section)
    {
        var text = BuildSectionText(section);
        if (string.IsNullOrWhiteSpace(text))
        {
            Notify.Warning($"{section.Label} has nothing to inject - every field is empty or \"Not discussed\".");
            return;
        }

        var result = section.SectionKey.ToUpperInvariant() switch
        {
            "ROS" => EcwInjector.TryInjectIntoRos(text),
            "HPI" => EcwInjector.TryInjectIntoFocusedHpi(text),
            _ => CopyToClipboard(text),
        };
        ReportInjection(section.Label, result);
    }

    private static EcwInjector.InjectResult CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return new EcwInjector.InjectResult(true, "Copied to the clipboard - paste it into the right eCW field with Ctrl+V.");
        }
        catch (Exception ex)
        {
            return new EcwInjector.InjectResult(false, $"Couldn't copy to the clipboard: {ex.Message}");
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

    private void ReportInjection(string section, EcwInjector.InjectResult result)
    {
        TxtInjectStatus.Text = result.Message;
        TxtInjectStatus.Foreground = (Brush)FindResource(result.Success ? "TextPrimaryBrush" : "RedBrush");

        if (result.Success)
            Notify.Success($"{section}: {result.Message}");
        else
            Notify.Error($"{section} not injected: {result.Message}");
    }

    /// <summary>"Label: value" lines for a section, skipping empty / "Not discussed" fields, read live from the editable boxes.</summary>
    private string BuildSectionText(TemplateSection section)
    {
        if (!_sectionBoxes.TryGetValue(section.SectionKey, out var boxes)) return "";

        var lines = new List<string>();
        foreach (var field in section.Fields)
        {
            if (boxes.TryGetValue(field.FieldKey, out var box)
                && !string.IsNullOrWhiteSpace(box.Text)
                && !box.Text.Trim().Equals("Not discussed", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{field.Label}: {box.Text.Trim()}");
            }
        }
        return string.Join("\n", lines);
    }

    // ── Voice Dictation tab ──────────────────────────────────────────────────
    private void MicCircle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dictation == null)
        {
            Notify.Error("Voice Dictation is unavailable - OpenAI isn't configured.");
            return;
        }

        if (_dictation.IsRunning)
        {
            _dictation.Stop();
            SetMicVisual(false);
            Notify.Info("Dictation OFF.");
        }
        else
        {
            _dictation.Start();
            SetMicVisual(true);
            Notify.Info("Dictation ON - click into an eCW field and speak.");
        }
    }

    private void SetMicVisual(bool on)
    {
        MicCircle.Fill = (Brush)FindResource(on ? "AccentBrush" : "BorderBrush2");
        TxtMicStatus.Text = on ? "ON — click any eCW field and speak" : "OFF — click to enable dictation";
        TxtMicStatus.Foreground = (Brush)FindResource(on ? "AccentBrush" : "TextSecondaryBrush");
    }
}