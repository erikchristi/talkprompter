using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Teleprompter.App.Services;
using Teleprompter.Audio;
using Teleprompter.Core.Languages;
using Teleprompter.Core.Matching;
using Teleprompter.Core.Speech;
using Teleprompter.Core.Text;
using Teleprompter.Speech;

namespace Teleprompter.App.ViewModels;

/// <summary>
/// Coordinates the whole app: owns the script library, the matcher, the speech
/// engine and the microphone, and turns recognition results into a live
/// reading position on the UI thread.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private const string DefaultScript =
        "Welcome to TalkPrompter.\n\n" +
        "As you read this text aloud, the script scrolls itself to keep pace with your voice. " +
        "If you stop speaking or wander off script, the scrolling pauses and waits for you. " +
        "The moment you start reading again, it picks up right where you left off.\n\n" +
        "Everything runs locally on your Windows machine. Your microphone audio never leaves the device. " +
        "Press Start, open the prompter, and begin reading whenever you are ready.";

    private const double AssumedWordsPerMinute = 150.0;
    private const int MaxRecentScripts = 10;
    private const double MicMeterFullWidth = 56.0;

    private readonly Dispatcher _dispatcher;
    private readonly UpdateService _updates;

    private ScriptMatcher? _matcher;
    private ISpeechEngine? _engine;
    private IAudioCapture? _capture;
    private ScriptFileWatcher? _fileWatcher;
    private bool _pendingFileReload;

    // Where the next Start begins (-1 = top). Fed by clicks, nudges, and the
    // per-script remembered position.
    private int _resumeTokenIndex = -1;
    private bool _switchingScript;

    // Coalescing latest-hypothesis dispatch (see OnHypothesis).
    private readonly object _pendingLock = new();
    private SpeechHypothesis? _pendingHypothesis;
    private bool _dispatchQueued;

    // Mic level metering (audio thread → throttled UI updates).
    private double _levelHold;
    private DateTime _lastLevelPushUtc;

    // Live pace measurement for the time-remaining estimate.
    private DateTime _firstAdvanceUtc;
    private int _wordsReadThisRun;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        InitialSettings = JsonSettingsStore.Load();
        _fontSize = Math.Clamp(InitialSettings.FontSize, 24, 120);
        // Values above the range come from the old 0.12-0.9 slider, whose slow
        // end made the view trail the voice by a second; reset those to default.
        _scrollSmoothness = InitialSettings.ScrollSmoothness is >= 0.06 and <= 0.35
            ? InitialSettings.ScrollSmoothness
            : DefaultScrollSmoothness;
        _sensitivity = Math.Clamp(InitialSettings.Sensitivity, 0.0, 1.0);
        _columnWidth = Math.Clamp(InitialSettings.ColumnWidth, 360, 1600);
        _mirrorHorizontal = InitialSettings.MirrorHorizontal;
        _countdownEnabled = InitialSettings.CountdownEnabled;
        _showHeardText = InitialSettings.ShowHeardText;
        _flowModeEnabled = InitialSettings.FlowModeEnabled;
        _hideFromCapture = InitialSettings.HideFromCapture;
        if (EngineChoices.Contains(InitialSettings.EngineChoice))
        {
            _selectedEngineChoice = InitialSettings.EngineChoice;
        }

        foreach (ScriptEntry entry in InitialSettings.RecentScripts
                     .OrderByDescending(e => e.LastOpenedUtc)
                     .Take(MaxRecentScripts))
        {
            Scripts.Add(ScriptSlot.From(entry));
        }

        if (Scripts.Count == 0)
        {
            string text = string.IsNullOrWhiteSpace(InitialSettings.ScriptText)
                ? DefaultScript
                : InitialSettings.ScriptText;
            Scripts.Add(new ScriptSlot { Name = ScriptSlot.DeriveName(text), Text = text });
        }

        // Seed from the most recent script without triggering the switch logic.
        _selectedScript = Scripts[0];
        _scriptText = _selectedScript.Text;
        _resumeTokenIndex = _selectedScript.LastTokenIndex;
        _currentTokenIndex = _selectedScript.LastTokenIndex;

        foreach (AudioInputDevice device in AudioDevices.List())
        {
            Devices.Add(device);
        }

        _selectedDevice = Devices.FirstOrDefault(d => d.Name == InitialSettings.MicrophoneName)
            ?? Devices.FirstOrDefault();

        // Updates are off in this build: settings.json is shared with the
        // original installed app and still names its feed, whose releases
        // would replace this version.
        _updates = new UpdateService(null);

        _selectedThemeChoice = ThemeService.Parse(InitialSettings.Theme) switch
        {
            ThemeMode.Light => "Light",
            ThemeMode.Auto => "Auto (match Windows)",
            _ => "Dark"
        };

        _autoUpdateCheckEnabled = InitialSettings.AutoUpdateCheck;
        _followFileChanges = InitialSettings.FollowFileChanges;

        // Languages: fields, not properties — no script rebuild or save while constructing.
        _modelPromptDismissed = InitialSettings.ModelPromptDismissed;
        _models = ModelLocator.FindModels();
        _activeLanguage = VoiceLanguageCatalog.ResolveActive(InitialSettings.VoiceLanguage, _models.LanguageCodes);
        _isFirstRunPrompt = !_models.CanRecognize(_activeLanguage.Code) && !_modelPromptDismissed;
        // Upgrading with several packs installed by hand: ask which one is the reading language.
        bool unsureWhichPack = InitialSettings.VoiceLanguage is null && _models.LanguageCodes.Count > 1;
        _showModelPrompt = _isFirstRunPrompt || unsureWhichPack;
        RefreshLanguageState();

        AttachFileWatcher();
    }

    // ----- Theme -----

    public IReadOnlyList<string> ThemeChoices { get; } = new[]
    {
        "Auto (match Windows)",
        "Dark",
        "Light"
    };

    [ObservableProperty] private string _selectedThemeChoice = "Dark";

    private ThemeMode ThemeModeFromChoice => SelectedThemeChoice switch
    {
        "Light" => ThemeMode.Light,
        "Auto (match Windows)" => ThemeMode.Auto,
        _ => ThemeMode.Dark
    };

    partial void OnSelectedThemeChoiceChanged(string value)
        => ThemeService.Apply(ThemeModeFromChoice);

    /// <summary>Settings as loaded at startup; the window reads placement from here.</summary>
    public AppSettings InitialSettings { get; }

    public IReadOnlyList<string> EngineChoices { get; } = new[]
    {
        "Auto (recommended)",
        "Script-locked Vosk",
        "sherpa-onnx"
    };

    [ObservableProperty] private string _selectedEngineChoice = "Auto (recommended)";

    private EnginePreference Preference => SelectedEngineChoice switch
    {
        "Script-locked Vosk" => EnginePreference.ScriptLockedVosk,
        "sherpa-onnx" => EnginePreference.SherpaOnnx,
        _ => EnginePreference.Auto
    };

    public ObservableCollection<AudioInputDevice> Devices { get; } = new();

    public ObservableCollection<ScriptSlot> Scripts { get; } = new();

    [ObservableProperty] private ScriptSlot? _selectedScript;

    /// <summary>The compiled script the prompter renders.</summary>
    public ScriptModel? Script { get; private set; }

    /// <summary>Raised when <see cref="Script"/> is rebuilt so views can re-render.</summary>
    public event EventHandler? ScriptRebuilt;

    [ObservableProperty] private string _scriptText = DefaultScript;
    [ObservableProperty] private double _fontSize = 52;
    private const double DefaultScrollSmoothness = 0.15;

    [ObservableProperty] private double _scrollSmoothness = DefaultScrollSmoothness;
    [ObservableProperty] private double _sensitivity = 0.6;
    [ObservableProperty] private double _columnWidth = 680;
    [ObservableProperty] private bool _countdownEnabled = true;
    [ObservableProperty] private bool _showHeardText;

    /// <summary>Narrow window: secondary toolbar controls collapse into the ☰ menu.</summary>
    [ObservableProperty] private bool _isCompactToolbar;
    [ObservableProperty] private bool _flowModeEnabled;
    [ObservableProperty] private bool _hideFromCapture;

    /// <summary>Full invisibility needs Windows 10 2004+; older builds show a black box.</summary>
    public bool CaptureProtectionSupported => CaptureProtection.IsSupported;
    [ObservableProperty] private bool _mirrorHorizontal;
    [ObservableProperty] private bool _forceSimulation;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _engineDescription = string.Empty;
    [ObservableProperty] private string _trackingStateText = "Idle";
    [ObservableProperty] private int _currentTokenIndex = -1;
    [ObservableProperty] private AudioInputDevice? _selectedDevice;
    [ObservableProperty] private string _lastHeardText = string.Empty;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private string _updateStatus = string.Empty;
    [ObservableProperty] private bool _updateReady;

    public double MicLevelWidth => MicLevel * MicMeterFullWidth;

    /// <summary>The heard strip's text: empty (hidden) unless enabled and live.</summary>
    public string HeardStripText => ShowHeardText ? LastHeardText : string.Empty;

    partial void OnLastHeardTextChanged(string value) => OnPropertyChanged(nameof(HeardStripText));

    partial void OnShowHeardTextChanged(bool value) => OnPropertyChanged(nameof(HeardStripText));

    partial void OnMicLevelChanged(double value) => OnPropertyChanged(nameof(MicLevelWidth));

    public string ToggleLabel => IsRunning ? "Stop" : "Start";

    /// <summary>Rough read time of the whole script, shown in the editor.</summary>
    public string EstimatedDuration
    {
        get
        {
            int words = (ScriptText ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words == 0)
            {
                return "empty script";
            }

            TimeSpan span = TimeSpan.FromMinutes(words / AssumedWordsPerMinute);
            return span.TotalMinutes >= 1
                ? $"{words} words · ≈ {(int)span.TotalMinutes} min {span.Seconds:D2} s at {AssumedWordsPerMinute:F0} wpm"
                : $"{words} words · ≈ {span.Seconds} s at {AssumedWordsPerMinute:F0} wpm";
        }
    }

    partial void OnScriptTextChanged(string value) => OnPropertyChanged(nameof(EstimatedDuration));

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(ToggleLabel));

    partial void OnSensitivityChanged(double value)
    {
        if (_matcher is not null)
        {
            _matcher.Options = BuildMatcherOptions();
        }
    }

    partial void OnSelectedScriptChanged(ScriptSlot? oldValue, ScriptSlot? newValue)
    {
        if (_switchingScript || newValue is null)
        {
            return;
        }

        _switchingScript = true;
        try
        {
            if (IsRunning)
            {
                Stop();
            }

            if (oldValue is not null)
            {
                CommitSlot(oldValue);
            }

            newValue.LastOpenedUtc = DateTime.UtcNow;
            ScriptText = newValue.Text;
            RebuildScript();
            AttachFileWatcher();

            _resumeTokenIndex = newValue.LastTokenIndex;
            CurrentTokenIndex = -1;
            if (_resumeTokenIndex >= 0 && Script is not null && _resumeTokenIndex < Script.TokenCount)
            {
                CurrentTokenIndex = _resumeTokenIndex;
                StatusText = $"“{newValue.Name}” — resumes at word {_resumeTokenIndex + 1}";
            }
            else
            {
                StatusText = $"“{newValue.Name}”";
            }
        }
        finally
        {
            _switchingScript = false;
        }
    }

    // Higher sensitivity lowers the similarity bar so the prompter follows more
    // eagerly through recognition errors (accented or fast speech).
    private MatcherOptions BuildMatcherOptions()
    {
        double threshold = 0.60 - 0.34 * Math.Clamp(Sensitivity, 0.0, 1.0);
        return new MatcherOptions { AcceptThreshold = threshold };
    }

    /// <summary>Recompile <see cref="Script"/> from the current text and re-render.</summary>
    public void RebuildScript()
    {
        Script = ScriptModel.Build(ScriptText ?? string.Empty, ActiveLanguage.TextRules);
        ScriptRebuilt?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    [RelayCommand]
    private void Start()
    {
        if (IsRunning)
        {
            return;
        }

        string text = ScriptText ?? string.Empty;
        RebuildScript();
        if (Script!.TokenCount == 0)
        {
            StatusText = "Script is empty — open Edit script and add your text first.";
            return;
        }

        _matcher = new ScriptMatcher(Script!, BuildMatcherOptions());
        _firstAdvanceUtc = default;
        _wordsReadThisRun = 0;

        bool resumed = false;
        if (_resumeTokenIndex >= 0 && _resumeTokenIndex < Script!.TokenCount)
        {
            _matcher.SeekToToken(_resumeTokenIndex);
            CurrentTokenIndex = _matcher.CurrentTokenIndex;
            resumed = true;
        }
        else
        {
            CurrentTokenIndex = -1;
        }

        // A pack may have been deleted since the last scan; loading a missing
        // model would fail deep inside the native recognizer.
        RefreshModels();

        var vocabulary = Script!.MatchWords.Distinct().ToList();
        SpeechEngineFactory.Selection selection;
        try
        {
            selection = SpeechEngineFactory.Create(
                ForceSimulation ? null : _models.SherpaDirFor(ActiveLanguage.Code),
                ForceSimulation ? null : _models.VoskDirFor(ActiveLanguage.Code),
                text,
                vocabulary,
                Preference);
        }
        catch (Exception ex)
        {
            Stop();
            StatusText = $"Could not load the {ActiveLanguage.EnglishName} voice pack: {ex.Message}";
            return;
        }

        _engine = selection.Engine;
        _engine.HypothesisReceived += OnHypothesis;
        EngineDescription = selection.Description;

        try
        {
            _engine.Start();

            if (!selection.IsSimulated)
            {
                var capture = new MicrophoneCapture();
                capture.DataAvailable += OnAudioData;
                capture.CaptureStopped += OnCaptureStopped;
                _capture = capture;
                capture.Start(SelectedDevice);
            }
        }
        catch (Exception ex)
        {
            // Stop() resets StatusText, so the error message must be set after.
            Stop();
            StatusText = $"Could not start: {ex.Message}";
            return;
        }

        IsRunning = true;
        StatusText = selection.IsSimulated
            ? ForceSimulation
                ? "Simulating a reader…"
                : $"No {ActiveLanguage.EnglishName} voice pack yet, so this is a simulation. Get it in Settings › Language."
            : resumed
                ? $"Listening from word {CurrentTokenIndex + 1}… · {selection.Description}"
                : $"Listening… · {selection.Description}";

        SaveSettingsSnapshot();
    }

    [RelayCommand]
    private void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnAudioData;
            if (_capture is MicrophoneCapture mic)
            {
                mic.CaptureStopped -= OnCaptureStopped;
            }

            _capture.Dispose();
            _capture = null;
        }

        if (_engine is not null)
        {
            _engine.HypothesisReceived -= OnHypothesis;
            try
            {
                _engine.Stop();
            }
            catch (Exception)
            {
                // Best-effort flush on shutdown.
            }

            _engine.Dispose();
            _engine = null;
        }

        // Continue from here on the next Start, and remember it per script.
        if (CurrentTokenIndex >= 0)
        {
            _resumeTokenIndex = CurrentTokenIndex;
        }

        // A file change that arrived mid-recording applies now.
        if (_pendingFileReload)
        {
            _ = ApplyFileReloadAsync();
        }

        if (SelectedScript is not null)
        {
            CommitSlot(SelectedScript);
        }

        MicLevel = 0;
        _levelHold = 0;
        LastHeardText = string.Empty;
        IsRunning = false;
        StatusText = BuildTakeSummary();
        TrackingStateText = "Idle";
    }

    /// <summary>After a real take, report its length and measured pace.</summary>
    private string BuildTakeSummary()
    {
        if (_wordsReadThisRun < 10 || _firstAdvanceUtc == default)
        {
            return "Stopped";
        }

        TimeSpan elapsed = DateTime.UtcNow - _firstAdvanceUtc;
        double wpm = _wordsReadThisRun / Math.Max(1.0 / 60.0, elapsed.TotalMinutes);
        return $"Take: {(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2} · {_wordsReadThisRun} words · {wpm:F0} wpm";
    }

    /// <summary>Jump to the previous/next paragraph start (section navigation).</summary>
    public void JumpParagraph(int direction)
    {
        if (Script is null)
        {
            RebuildScript();
        }

        IReadOnlyList<int> starts = Script!.ParagraphStartTokens;
        if (starts.Count == 0)
        {
            return;
        }

        int position = CurrentTokenIndex >= 0 ? CurrentTokenIndex : Math.Max(0, _resumeTokenIndex);
        int currentParagraph = 0;
        for (int i = 0; i < starts.Count; i++)
        {
            if (starts[i] <= position)
            {
                currentParagraph = i;
            }
            else
            {
                break;
            }
        }

        int target = Math.Clamp(currentParagraph + direction, 0, starts.Count - 1);

        // Seek marks the given token as already read, so aim one before the
        // paragraph start; -1 means "from the very top".
        SeekToToken(starts[target] - 1);
        StatusText = $"Paragraph {target + 1} of {starts.Count}";
    }

    [RelayCommand]
    private void ResetPosition()
    {
        _matcher?.Reset();
        CurrentTokenIndex = -1;
        _resumeTokenIndex = -1;
        if (SelectedScript is not null)
        {
            SelectedScript.LastTokenIndex = -1;
        }

        TrackingStateText = "Idle";
        _firstAdvanceUtc = default;
        _wordsReadThisRun = 0;
    }

    [RelayCommand]
    private void NewScript()
    {
        if (SelectedScript is not null)
        {
            CommitSlot(SelectedScript);
        }

        var slot = new ScriptSlot { Name = "Untitled", Text = string.Empty };
        Scripts.Insert(0, slot);
        TrimScripts();
        SelectedScript = slot;
    }

    [RelayCommand]
    private void LoadFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open script",
            Filter = "Scripts (*.txt;*.docx)|*.txt;*.docx|Word documents (*.docx)|*.docx|Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadScriptFromPath(dialog.FileName);
        }
    }

    private static string ReadScriptFile(string fileName)
        => Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase)
            ? DocxReader.ExtractText(fileName)
            : File.ReadAllText(fileName);

    // ----- Live file sync (Word saves refresh the script) -----

    [ObservableProperty] private bool _followFileChanges = true;

    partial void OnFollowFileChangesChanged(bool value) => AttachFileWatcher();

    private void AttachFileWatcher()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        _pendingFileReload = false;

        string? path = SelectedScript?.FilePath;
        if (!FollowFileChanges || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            _fileWatcher = new ScriptFileWatcher(
                path,
                () => _dispatcher.BeginInvoke(OnScriptFileChanged));
        }
        catch (Exception)
        {
            _fileWatcher = null; // unwatchable location (removed drive etc.)
        }
    }

    private void OnScriptFileChanged()
    {
        if (!FollowFileChanges || SelectedScript?.FilePath is null)
        {
            return;
        }

        if (IsRunning)
        {
            // Never yank the script out from under a live take.
            _pendingFileReload = true;
            StatusText = "Script file changed — reloads when you stop.";
            return;
        }

        _ = ApplyFileReloadAsync();
    }

    private async Task ApplyFileReloadAsync()
    {
        _pendingFileReload = false;
        string? path = SelectedScript?.FilePath;
        if (path is null)
        {
            return;
        }

        // Word saves via temp-and-rename, so the file can be briefly locked or
        // missing; retry off the UI thread until it settles.
        string? text = await Task.Run(() =>
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return ReadScriptFile(path);
                    }
                }
                catch (IOException)
                {
                }
                catch (InvalidDataException)
                {
                }

                Thread.Sleep(250);
            }

            return null;
        });

        if (text is null)
        {
            StatusText = $"{Path.GetFileName(path)} changed but could not be read.";
            return;
        }

        if (SelectedScript is null || text == ScriptText)
        {
            return;
        }

        SelectedScript.Text = text;
        ScriptText = text;
        RebuildScript();

        // The text shifted, so clamp the remembered position into the new script.
        int lastToken = Script!.TokenCount - 1;
        _resumeTokenIndex = Math.Min(_resumeTokenIndex, lastToken);
        if (CurrentTokenIndex > lastToken)
        {
            CurrentTokenIndex = lastToken;
        }

        StatusText = $"Reloaded {Path.GetFileName(path)}";
    }

    /// <summary>
    /// Load a script file (.docx or plain text) into the library — used by the
    /// open dialog and by dropping a file onto the window.
    /// </summary>
    public void LoadScriptFromPath(string fileName)
    {
        try
        {
            string text = ReadScriptFile(fileName);

            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = $"{Path.GetFileName(fileName)} contains no readable text.";
                return;
            }

            ScriptSlot? existing = Scripts.FirstOrDefault(s =>
                string.Equals(s.FilePath, fileName, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Text = text;
                SelectedScript = existing;   // switch logic loads it
                if (ReferenceEquals(SelectedScript, existing))
                {
                    ScriptText = text;       // same slot re-loaded: refresh text
                    RebuildScript();
                }
            }
            else
            {
                if (SelectedScript is not null)
                {
                    CommitSlot(SelectedScript);
                }

                var slot = new ScriptSlot
                {
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    FilePath = fileName,
                    Text = text
                };
                Scripts.Insert(0, slot);
                TrimScripts();
                SelectedScript = slot;
            }

            StatusText = $"Loaded {Path.GetFileName(fileName)}";
        }
        catch (InvalidDataException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open file: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-anchor the reader (clicked word, nudge, or resume). Works while
    /// running (matcher seeks live) and while idle (position preview that the
    /// next Start begins from).
    /// </summary>
    public void SeekToToken(int tokenIndex)
    {
        _resumeTokenIndex = tokenIndex;

        if (_matcher is not null)
        {
            _matcher.SeekToToken(tokenIndex);
            CurrentTokenIndex = _matcher.CurrentTokenIndex;
        }
        else
        {
            CurrentTokenIndex = tokenIndex;
        }

        if (SelectedScript is not null)
        {
            SelectedScript.LastTokenIndex = tokenIndex;
        }
    }

    /// <summary>Move the position by whole words (presenter-remote page keys).</summary>
    public void NudgeWords(int delta)
    {
        if (Script is null)
        {
            RebuildScript();
        }

        if (Script is null || Script.TokenCount == 0)
        {
            return;
        }

        int basis = CurrentTokenIndex >= 0 ? CurrentTokenIndex : _resumeTokenIndex;
        int target = Math.Clamp(basis + delta, 0, Script.TokenCount - 1);
        SeekToToken(target);
    }

    /// <summary>Persist current preferences plus the window placement supplied by the view.</summary>
    public void SaveSettings(Rect windowBounds, bool maximized, bool topmost)
    {
        JsonSettingsStore.Save(BuildSettings() with
        {
            WindowLeft = windowBounds.Left,
            WindowTop = windowBounds.Top,
            WindowWidth = windowBounds.Width,
            WindowHeight = windowBounds.Height,
            WindowMaximized = maximized,
            Topmost = topmost
        });
    }

    private void SaveSettingsSnapshot()
        => JsonSettingsStore.Save(BuildSettings() with
        {
            WindowLeft = InitialSettings.WindowLeft,
            WindowTop = InitialSettings.WindowTop,
            WindowWidth = InitialSettings.WindowWidth,
            WindowHeight = InitialSettings.WindowHeight,
            WindowMaximized = InitialSettings.WindowMaximized,
            Topmost = InitialSettings.Topmost
        });

    private AppSettings BuildSettings()
    {
        if (SelectedScript is not null)
        {
            CommitSlot(SelectedScript);
        }

        return new AppSettings
        {
            ScriptText = ScriptText,
            FontSize = FontSize,
            ScrollSmoothness = ScrollSmoothness,
            Sensitivity = Sensitivity,
            ColumnWidth = ColumnWidth,
            MirrorHorizontal = MirrorHorizontal,
            CountdownEnabled = CountdownEnabled,
            ShowHeardText = ShowHeardText,
            FlowModeEnabled = FlowModeEnabled,
            HideFromCapture = HideFromCapture,
            EngineChoice = SelectedEngineChoice,
            MicrophoneName = SelectedDevice?.Name,
            RecentScripts = Scripts.Select(s => s.ToEntry()).ToArray(),
            Theme = ThemeService.Serialize(ThemeModeFromChoice),
            AutoUpdateCheck = AutoUpdateCheckEnabled,
            FollowFileChanges = FollowFileChanges,
            ModelPromptDismissed = _modelPromptDismissed,
            VoiceLanguage = ActiveLanguage.Code,
            // Config-only values with no UI must survive every save, or the
            // first settings write would silently disable auto-update.
            UpdateFeedUrl = InitialSettings.UpdateFeedUrl,
            UpdateFeedToken = InitialSettings.UpdateFeedToken
        };
    }

    /// <summary>Snapshot the live editing state into a library slot.</summary>
    private void CommitSlot(ScriptSlot slot)
    {
        if (ReferenceEquals(slot, SelectedScript))
        {
            slot.Text = ScriptText ?? string.Empty;
            slot.LastTokenIndex = CurrentTokenIndex >= 0 ? CurrentTokenIndex : _resumeTokenIndex;
        }

        if (slot.FilePath is null)
        {
            slot.Name = ScriptSlot.DeriveName(slot.Text);
        }
    }

    private void TrimScripts()
    {
        while (Scripts.Count > MaxRecentScripts)
        {
            ScriptSlot oldest = Scripts
                .Where(s => !ReferenceEquals(s, SelectedScript))
                .OrderBy(s => s.LastOpenedUtc)
                .First();
            Scripts.Remove(oldest);
        }
    }

    // Runs on the audio thread — feed the engine directly, off the UI thread.
    private void OnAudioData(byte[] buffer, int count)
    {
        _engine?.AcceptWaveform(buffer, count);
        UpdateMicLevel(buffer, count);
    }

    // The microphone died mid-run (unplugged, sleep, driver): stop cleanly and
    // tell the user instead of silently going deaf.
    private void OnCaptureStopped(Exception? exception)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!IsRunning)
            {
                return;
            }

            Stop();
            StatusText = exception is null
                ? "Microphone disconnected — check the device and press Start."
                : $"Microphone error: {exception.Message}";
        });
    }

    // ----- Update flow: ask first, then download with visible progress -----

    [ObservableProperty] private bool _autoUpdateCheckEnabled = true;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateVersionText = string.Empty;
    [ObservableProperty] private string _updateNotes = string.Empty;
    [ObservableProperty] private bool _updateDownloading;
    [ObservableProperty] private double _updateDownloadProgress;
    [ObservableProperty] private bool _updateDownloaded;

    private bool _deferredUpdateOffer;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!_updates.IsConfigured)
        {
            UpdateStatus = "Update feed not configured (set UpdateFeedUrl in settings.json).";
            return;
        }

        if (!_updates.IsSupported)
        {
            UpdateStatus = "Updates apply to the installed app only (run Setup.exe).";
            return;
        }

        if (_updates.IsDownloaded)
        {
            UpdateAvailable = true;
            return;
        }

        try
        {
            UpdateStatus = "Checking for updates…";
            if (!await _updates.CheckAsync())
            {
                UpdateStatus = "You're up to date.";
                return;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update check failed: {ex.Message}";
            return;
        }

        UpdateVersionText = $"Version {_updates.AvailableVersion}";
        UpdateNotes = string.IsNullOrWhiteSpace(_updates.AvailableNotes)
            ? "See the GitHub releases page for details."
            : CleanReleaseNotes(_updates.AvailableNotes);
        UpdateStatus = $"Update {_updates.AvailableVersion} available.";

        // Never stack on top of the first-run voice pack prompt.
        if (ShowModelPrompt)
        {
            _deferredUpdateOffer = true;
        }
        else
        {
            UpdateAvailable = true;
        }
    }

    partial void OnShowModelPromptChanged(bool value)
    {
        if (!value && _deferredUpdateOffer)
        {
            _deferredUpdateOffer = false;
            UpdateAvailable = true;
        }
    }

    /// <summary>Markdown headings and bullets read as noise in a plain TextBlock.</summary>
    private static string CleanReleaseNotes(string markdown)
    {
        var lines = markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line =>
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    return trimmed.TrimStart('#').Trim();
                }

                return trimmed.StartsWith("- ") ? "•  " + trimmed[2..] : line;
            });

        return string.Join('\n', lines).Trim();
    }

    [RelayCommand]
    private async Task AcceptUpdateAsync()
    {
        if (UpdateDownloading || _updates.IsDownloaded)
        {
            return;
        }

        UpdateDownloading = true;
        UpdateDownloadProgress = 0;
        try
        {
            var progress = new Progress<int>(p => UpdateDownloadProgress = p);
            await _updates.DownloadAsync(progress, CancellationToken.None);
            UpdateDownloaded = true;
            UpdateReady = true;
            UpdateStatus = $"Update {_updates.AvailableVersion} downloaded — applies when you close the app.";
        }
        catch (Exception ex)
        {
            UpdateAvailable = false;
            UpdateStatus = $"Update download failed: {ex.Message}";
        }
        finally
        {
            UpdateDownloading = false;
        }
    }

    [RelayCommand]
    private void PostponeUpdate()
    {
        UpdateAvailable = false;
        UpdateStatus = $"Update {_updates.AvailableVersion} available — install any time from Settings.";
    }

    [RelayCommand]
    private void CloseUpdateOverlay() => UpdateAvailable = false;

    [RelayCommand]
    private void RestartUpdate() => _updates.RestartNow();

    /// <summary>Startup check — only meaningful in the installed app, and only
    /// when the user has not turned automatic checking off.</summary>
    public async Task AutoCheckUpdatesAsync()
    {
        if (!AutoUpdateCheckEnabled || !_updates.IsConfigured || !_updates.IsSupported)
        {
            return;
        }

        await CheckUpdatesAsync();
    }

    /// <summary>
    /// Peak meter with fast attack / slow decay, throttled to ~20 UI updates/s.
    /// Proof at a glance that audio is flowing from the selected microphone.
    /// </summary>
    private void UpdateMicLevel(byte[] buffer, int count)
    {
        int peak = 0;
        for (int i = 0; i + 1 < count; i += 8) // every 4th sample is plenty for a meter
        {
            int sample = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8)));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        double instant = peak / 32768.0;
        _levelHold = Math.Max(instant, _levelHold * 0.86);

        DateTime now = DateTime.UtcNow;
        if ((now - _lastLevelPushUtc).TotalMilliseconds < 50)
        {
            return;
        }

        _lastLevelPushUtc = now;
        double level = _levelHold;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => MicLevel = level);
    }

    /// <summary>
    /// Raised on the audio/decode thread. Partials are cumulative, so only the
    /// newest one matters: keep the latest and queue at most one UI dispatch at
    /// Input priority. Under load this skips stale hypotheses instead of letting
    /// the dispatcher queue grow — the highlight stays glued to the voice.
    /// </summary>
    private void OnHypothesis(object? sender, SpeechHypothesis hypothesis)
    {
        lock (_pendingLock)
        {
            _pendingHypothesis = hypothesis;
            if (_dispatchQueued)
            {
                return;
            }

            _dispatchQueued = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Input, ProcessPendingHypothesis);
    }

    private void ProcessPendingHypothesis()
    {
        SpeechHypothesis? hypothesis;
        lock (_pendingLock)
        {
            hypothesis = _pendingHypothesis;
            _pendingHypothesis = null;
            _dispatchQueued = false;
        }

        if (hypothesis is null || _matcher is null)
        {
            return;
        }

        LastHeardText = FormatHeard(hypothesis.Text);

        MatchUpdate update = _matcher.Process(hypothesis.Text);
        if (update.TokenIndex > CurrentTokenIndex && update.State == TrackingState.Tracking)
        {
            if (_firstAdvanceUtc == default)
            {
                _firstAdvanceUtc = DateTime.UtcNow;
            }

            _wordsReadThisRun += update.TokenIndex - Math.Max(0, CurrentTokenIndex);
        }

        CurrentTokenIndex = update.TokenIndex;
        TrackingStateText = update.State.ToString();
        StatusText = update.State switch
        {
            TrackingState.Tracking => $"Tracking{RemainingSuffix()}",
            TrackingState.Paused => "Paused — waiting for you",
            TrackingState.Lost => "Lost the line — click a word to re-anchor",
            _ => StatusText
        };
    }

    private static string FormatHeard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return "heard:  " + string.Join(' ', words.TakeLast(8)).ToLowerInvariant();
    }

    /// <summary>
    /// " · ≈ m:ss left" once enough has been read to measure the live pace;
    /// falls back to the assumed rate early in the run.
    /// </summary>
    private string RemainingSuffix()
    {
        if (Script is null || CurrentTokenIndex < 0)
        {
            return string.Empty;
        }

        int total = Script.TokenCount;
        int remaining = total - (CurrentTokenIndex + 1);
        if (remaining <= 0)
        {
            return " · done";
        }

        double wordsPerSecond = AssumedWordsPerMinute / 60.0;
        double elapsed = _firstAdvanceUtc == default
            ? 0.0
            : (DateTime.UtcNow - _firstAdvanceUtc).TotalSeconds;
        if (elapsed > 10.0 && _wordsReadThisRun > 15)
        {
            wordsPerSecond = _wordsReadThisRun / elapsed;
        }

        var left = TimeSpan.FromSeconds(remaining / Math.Max(0.5, wordsPerSecond));
        return $" · ≈ {(int)left.TotalMinutes}:{left.Seconds:D2} left";
    }

    // Recognition reports a word ~0.3-0.6 s after it was spoken, so the view
    // scrolls toward where the reader is now, not where they were.
    private const double RecognitionLagSeconds = 0.4;
    private const int MaxLeadWords = 4;

    /// <summary>
    /// Words the scroll target runs ahead of the last recognized word: the
    /// measured reading pace times the recognition lag. Zero until the pace
    /// of this take is known.
    /// </summary>
    public int ScrollLeadWords
    {
        get
        {
            if (!IsRunning || _firstAdvanceUtc == default || _wordsReadThisRun < 6)
            {
                return 0;
            }

            double elapsed = (DateTime.UtcNow - _firstAdvanceUtc).TotalSeconds;
            if (elapsed < 2.0)
            {
                return 0;
            }

            double wordsPerSecond = _wordsReadThisRun / elapsed;
            return Math.Clamp((int)Math.Round(wordsPerSecond * RecognitionLagSeconds), 0, MaxLeadWords);
        }
    }

    public string AppVersion
    {
        get
        {
            Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "dev" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(JsonSettingsStore.SettingsPath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open folder: {ex.Message}";
        }
    }

    public void Dispose()
    {
        Stop();
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }
}
