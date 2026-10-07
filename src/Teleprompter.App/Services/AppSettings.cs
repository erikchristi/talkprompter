using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Teleprompter.App.Services;

/// <summary>
/// Everything the app remembers between sessions. Loaded once at startup and
/// written atomically on exit (and after each Start, so a crash loses nothing
/// important).
/// </summary>
public sealed record AppSettings
{
    public string? ScriptText { get; init; }
    public double FontSize { get; init; } = 52;
    public double ScrollSmoothness { get; init; } = 0.15;
    public double Sensitivity { get; init; } = 0.6;
    public double ColumnWidth { get; init; } = 680;
    public bool MirrorHorizontal { get; init; }
    public bool Topmost { get; init; }
    public bool CountdownEnabled { get; init; } = true;
    public string EngineChoice { get; init; } = "Auto (recommended)";
    public string? MicrophoneName { get; init; }
    public bool ShowHeardText { get; init; }
    public bool FlowModeEnabled { get; init; }

    /// <summary>Keep the app's windows out of screen recordings and screen shares.</summary>
    public bool HideFromCapture { get; init; } = true;

    /// <summary>Velopack update feed: a folder path, https URL, or GitHub repo URL. Empty disables updates.</summary>
    public string UpdateFeedUrl { get; init; } = "https://github.com/Sven-Bo/talkprompter";

    /// <summary>Access token for a private GitHub releases feed (leave empty for public repos).</summary>
    public string UpdateFeedToken { get; init; } = string.Empty;

    /// <summary>"Auto", "Dark", or "Light".</summary>
    public string Theme { get; init; } = "Dark";

    /// <summary>Check for updates on start. The manual check always works.</summary>
    public bool AutoUpdateCheck { get; init; } = true;

    /// <summary>Reload a file-backed script automatically when the file changes.</summary>
    public bool FollowFileChanges { get; init; } = true;

    /// <summary>True once the first-run voice pack prompt was answered or skipped.</summary>
    public bool ModelPromptDismissed { get; init; }

    /// <summary>
    /// Language code of the voice pack in use (e.g. "pl"). Null until chosen;
    /// the app then uses whichever pack is installed.
    /// </summary>
    public string? VoiceLanguage { get; init; }

    public IReadOnlyList<ScriptEntry> RecentScripts { get; init; } = Array.Empty<ScriptEntry>();

    public double WindowLeft { get; init; } = double.NaN;
    public double WindowTop { get; init; } = double.NaN;
    public double WindowWidth { get; init; } = 1180;
    public double WindowHeight { get; init; } = 780;
    public bool WindowMaximized { get; init; }

    [JsonIgnore]
    public bool HasWindowPlacement =>
        !double.IsNaN(WindowLeft) && !double.IsNaN(WindowTop)
        && WindowWidth >= 200 && WindowHeight >= 150;
}
