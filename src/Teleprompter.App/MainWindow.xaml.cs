using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Teleprompter.App.Services;
using Teleprompter.App.ViewModels;
using Teleprompter.Core.Text;

namespace Teleprompter.App;

/// <summary>
/// The single, teleprompter-first window: the scrolling script fills the view,
/// a slim toolbar drives it, and the editor slides in over the top on demand.
/// </summary>
public partial class MainWindow : Window
{
    // Where the current word rides, as a fraction of viewport height. Must
    // match the guide-line row split in MainWindow.xaml (0.33* / Auto / 0.67*).
    private const double ReadingFraction = 0.33;

    private ScrollController? _scroll;

    // The whole script is one Run; words are located by character offset.
    // Rects are measured lazily and cached until the text re-wraps.
    private Run? _scriptRun;
    private ScriptToken[] _tokens = Array.Empty<ScriptToken>();
    private Rect[] _tokenRects = Array.Empty<Rect>();
    private int _currentToken = -1;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _isFullscreen;
    private Rect _boundsBeforeCamera;
    private bool _topmostBeforeCamera;
    private bool _inCameraMode;
    private DispatcherTimer? _countdownTimer;
    private int _countdownValue;

    public MainWindow()
    {
        InitializeComponent();

        ViewModel = new MainViewModel();
        DataContext = ViewModel;

        RestoreWindowPlacement(ViewModel.InitialSettings);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        ThemeService.ThemeApplied += OnThemeApplied;
    }

    // The word marker uses a DynamicResource, so only the native title bar
    // needs a nudge on a palette swap.
    private void OnThemeApplied(bool isDark) => ApplyTitleBarTheme(isDark);

    /// <summary>Restore the last session's window placement if it is still on-screen.</summary>
    private void RestoreWindowPlacement(AppSettings settings)
    {
        if (!settings.HasWindowPlacement)
        {
            return;
        }

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(settings.WindowLeft, settings.WindowTop, settings.WindowWidth, settings.WindowHeight);

        if (!virtualScreen.IntersectsWith(saved))
        {
            return; // monitor layout changed; keep the default centered position
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        Topmost = settings.Topmost;
    }

    public MainViewModel ViewModel { get; }

    // ----- Dark native title bar (Windows 10 20H1+ / Windows 11) -----

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ----- Global start/stop hotkey (Ctrl+Alt+Space, works while OBS etc. has focus) -----

    private const int HotkeyMessage = 0x0312;
    private const int HotkeyId = 0xA17E;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkSpace = 0x20;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private HwndSource? _hwndSource;

    private void ApplyTitleBarTheme(bool dark)
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyTitleBarTheme(ThemeService.IsDarkActive);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // Before the first frame, so the window never flashes into a recording.
        CaptureProtection.SetEnabled(ViewModel.HideFromCapture);
        CaptureProtection.Apply(hwnd);

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(OnWindowMessage);
        // Best-effort: if another app owns the combo, local shortcuts still work.
        _ = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt, VkSpace);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HotkeyMessage && wParam.ToInt32() == HotkeyId)
        {
            ViewModel.ToggleCommand.Execute(null);
            handled = true;
        }
        else if (Program.ActivateMessageId != 0 && msg == (int)Program.ActivateMessageId)
        {
            // A second launch asked us to come to the front.
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scroll = new ScrollController(Scroller)
        {
            SmoothTime = ViewModel.ScrollSmoothness,
            FlowMode = ViewModel.FlowModeEnabled
        };
        _scroll.Attach();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.ScriptRebuilt += OnScriptRebuilt;

        TopmostToggle.IsChecked = Topmost;
        ApplyMirror();
        BuildDocument();

        // Show the remembered per-script position right away (no property
        // change fires for the value seeded at construction).
        if (ViewModel.CurrentTokenIndex >= 0)
        {
            HighlightAndScroll(ViewModel.CurrentTokenIndex);
        }

        // Optional demo/test hook: launch with --autostart-sim to begin the
        // simulated reader immediately (no mic needed).
        if (Environment.GetCommandLineArgs().Contains("--autostart-sim"))
        {
            ViewModel.ForceSimulation = true;
            ViewModel.ToggleCommand.Execute(null);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Persist the pre-camera/pre-fullscreen placement, never the transient one.
        Rect bounds = _inCameraMode
            ? _boundsBeforeCamera
            : WindowState == WindowState.Normal && !_isFullscreen
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;

        bool maximized = _isFullscreen
            ? _stateBeforeFullscreen == WindowState.Maximized
            : WindowState == WindowState.Maximized;

        bool topmost = _inCameraMode ? _topmostBeforeCamera : Topmost;

        ViewModel.SaveSettings(bounds, maximized, topmost);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_hwndSource is not null)
        {
            _ = UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _hwndSource.RemoveHook(OnWindowMessage);
            _hwndSource = null;
        }

        CaptureProtection.SetEnabled(false);
        ThemeService.ThemeApplied -= OnThemeApplied;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.ScriptRebuilt -= OnScriptRebuilt;
        _countdownTimer?.Stop();
        _scroll?.Detach();
        ViewModel.Dispose();
    }

    private void OnScriptRebuilt(object? sender, EventArgs e) => Dispatcher.Invoke(BuildDocument);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentTokenIndex):
                HighlightAndScroll(ViewModel.CurrentTokenIndex);
                break;
            case nameof(MainViewModel.ScrollSmoothness):
                if (_scroll is not null)
                {
                    _scroll.SmoothTime = ViewModel.ScrollSmoothness;
                }

                break;
            case nameof(MainViewModel.MirrorHorizontal):
                ApplyMirror();
                break;
            case nameof(MainViewModel.FlowModeEnabled):
                if (_scroll is not null)
                {
                    _scroll.FlowMode = ViewModel.FlowModeEnabled;
                }

                break;
            case nameof(MainViewModel.HideFromCapture):
                CaptureProtection.SetEnabled(ViewModel.HideFromCapture);
                break;
            case nameof(MainViewModel.TrackingStateText):
                _scroll?.SetReaderPaused(ViewModel.TrackingStateText == "Paused");
                break;
            case nameof(MainViewModel.IsRunning):
                if (ViewModel.IsRunning)
                {
                    HideEditor();
                    if (ViewModel.CountdownEnabled && !ViewModel.ForceSimulation)
                    {
                        StartCountdown();
                    }
                }
                else
                {
                    CancelCountdown();
                }

                break;
        }
    }

    // ----- 3-2-1 countdown -----

    private void StartCountdown()
    {
        CancelCountdown();
        _countdownValue = 3;
        CountdownText.Text = "3";
        CountdownOverlay.Visibility = Visibility.Visible;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _countdownValue--;
        if (_countdownValue <= 0)
        {
            CancelCountdown();
            return;
        }

        CountdownText.Text = _countdownValue.ToString();
    }

    private void OnCountdownClicked(object sender, RoutedEventArgs e) => CancelCountdown();

    private void CancelCountdown()
    {
        if (_countdownTimer is not null)
        {
            _countdownTimer.Tick -= OnCountdownTick;
            _countdownTimer.Stop();
            _countdownTimer = null;
        }

        CountdownOverlay.Visibility = Visibility.Collapsed;
    }

    // ----- Camera mode -----

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// Work area of the monitor this window is on, in WPF device-independent
    /// units. Camera mode must target the current monitor — the webcam is
    /// wherever the user put the window, not necessarily on the primary screen.
    /// </summary>
    private Rect GetCurrentMonitorWorkArea()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
                && PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                Matrix fromDevice = target.TransformFromDevice;
                Point topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
                Point bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
                return new Rect(topLeft, bottomRight);
            }
        }
        catch (Exception)
        {
            // Fall through to the primary work area.
        }

        return SystemParameters.WorkArea;
    }

    private void OnToggleCameraMode(object sender, RoutedEventArgs e)
    {
        if (!_inCameraMode)
        {
            if (_isFullscreen)
            {
                ToggleFullscreen();
            }

            _boundsBeforeCamera = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            _topmostBeforeCamera = Topmost;

            Rect work = GetCurrentMonitorWorkArea();
            double width = 520;
            WindowState = WindowState.Normal;
            Width = width;
            Height = Math.Max(MinHeight, work.Height * 0.72);
            Left = work.Left + (work.Width - width) / 2;
            Top = work.Top;
            Topmost = true;
            TopmostToggle.IsChecked = true;
            _inCameraMode = true;
        }
        else
        {
            Left = _boundsBeforeCamera.Left;
            Top = _boundsBeforeCamera.Top;
            Width = _boundsBeforeCamera.Width;
            Height = _boundsBeforeCamera.Height;
            Topmost = _topmostBeforeCamera;
            TopmostToggle.IsChecked = _topmostBeforeCamera;
            _inCameraMode = false;
        }

        if (CameraToggle.IsChecked != _inCameraMode)
        {
            CameraToggle.IsChecked = _inCameraMode;
        }
    }

    // ----- Keyboard shortcuts -----

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Don't hijack keys while typing in the script editor.
        bool editing = Keyboard.FocusedElement is TextBox;

        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when EditorPanel.Visibility == Visibility.Visible:
                OnDoneEditing(sender, e);
                e.Handled = true;
                break;
            case Key.Space when !editing:
                ViewModel.ToggleCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageDown when !editing
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                ViewModel.JumpParagraph(1);
                e.Handled = true;
                break;
            case Key.PageUp when !editing
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                ViewModel.JumpParagraph(-1);
                e.Handled = true;
                break;
            case Key.PageDown when !editing:
                ViewModel.NudgeWords(8);
                e.Handled = true;
                break;
            case Key.PageUp when !editing:
                ViewModel.NudgeWords(-8);
                e.Handled = true;
                break;
            case Key.Home when !editing:
                ViewModel.ResetPositionCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ----- Click a word to re-anchor -----

    private void OnPromptClicked(object sender, MouseButtonEventArgs e)
    {
        int tokenIndex = HitTestToken(e.GetPosition(Prompt));
        if (tokenIndex >= 0)
        {
            ViewModel.SeekToToken(tokenIndex);
        }
    }

    /// <summary>
    /// Finds the clicked word: the text position under the point becomes a
    /// character offset, and a binary search over the token starts finds the
    /// word that contains it.
    /// </summary>
    private int HitTestToken(Point point)
    {
        if (_scriptRun is null || _tokens.Length == 0)
        {
            return -1;
        }

        TextPointer? position = Prompt.GetPositionFromPoint(point, snapToText: false);
        if (position is null)
        {
            return -1; // clicked beside the text
        }

        int offset = _scriptRun.ContentStart.GetOffsetToPosition(position);
        int lo = 0, hi = _tokens.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_tokens[mid].Start <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 && offset <= _tokens[found].End ? found : -1;
    }

    /// <summary>
    /// The word's box in Prompt coordinates, measured once and cached until
    /// the text re-wraps. Empty when it cannot be measured yet.
    /// </summary>
    private Rect GetTokenRect(int index)
    {
        if (_scriptRun is null || index < 0 || index >= _tokens.Length)
        {
            return Rect.Empty;
        }

        Rect cached = _tokenRects[index];
        if (!cached.IsEmpty)
        {
            return cached;
        }

        // Only after a rebuild or re-wrap; during reading the layout is valid.
        if (!Prompt.IsArrangeValid)
        {
            Prompt.UpdateLayout();
        }

        ScriptToken token = _tokens[index];
        TextPointer? start = _scriptRun.ContentStart.GetPositionAtOffset(token.Start);
        TextPointer? end = _scriptRun.ContentStart.GetPositionAtOffset(token.End);
        if (start is null || end is null)
        {
            return Rect.Empty;
        }

        Rect first = start.GetCharacterRect(LogicalDirection.Forward);
        Rect last = end.GetCharacterRect(LogicalDirection.Backward);
        if (first.IsEmpty || last.IsEmpty)
        {
            return Rect.Empty;
        }

        // A word never wraps, but guard against the end landing on the next line.
        Rect rect = last.Top > first.Top + 1
            ? new Rect(first.Left, first.Top, Math.Max(1, first.Height * 0.5), first.Height)
            : new Rect(first.TopLeft, new Point(Math.Max(first.Left + 1, last.Right), first.Bottom));
        _tokenRects[index] = rect;
        return rect;
    }

    private void InvalidateTokenRects()
    {
        if (_tokenRects.Length > 0)
        {
            Array.Fill(_tokenRects, Rect.Empty);
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // reset first so Maximized covers the taskbar
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
            _isFullscreen = false;
        }

        if (FullToggle.IsChecked != _isFullscreen)
        {
            FullToggle.IsChecked = _isFullscreen;
        }
    }

    // ----- Document build + tracking display -----

    /// <summary>
    /// Rebuilds the flowing text as a single Run. Words are found by their
    /// character offsets, so the script is laid out once and never touched
    /// while reading.
    /// </summary>
    private void BuildDocument()
    {
        ScriptModel model = ViewModel.Script ?? ScriptModel.Build(ViewModel.ScriptText ?? string.Empty);

        Prompt.Inlines.Clear();
        _scriptRun = new Run(model.Text);
        Prompt.Inlines.Add(_scriptRun);

        _tokens = model.Tokens.ToArray();
        _tokenRects = new Rect[_tokens.Length];
        InvalidateTokenRects();
        _currentToken = -1;
        WordMarker.Visibility = Visibility.Collapsed;

        _scroll?.JumpTo(0);
    }

    private void HighlightAndScroll(int tokenIndex)
    {
        if (tokenIndex < 0 || tokenIndex >= _tokens.Length)
        {
            // Position was reset (Top button, Home, script switch): the view
            // must actually go back to the top, not just drop the highlight.
            _currentToken = -1;
            WordMarker.Visibility = Visibility.Collapsed;
            _scroll?.SetTarget(0.0);
            return;
        }

        _currentToken = tokenIndex;
        Rect rect = GetTokenRect(tokenIndex);
        if (rect.IsEmpty)
        {
            return;
        }

        // Moving the box behind the text costs nothing; the text stays laid out.
        Canvas.SetLeft(WordMarker, rect.Left - 3);
        Canvas.SetTop(WordMarker, rect.Top);
        WordMarker.Width = rect.Width + 6;
        WordMarker.Height = rect.Height;
        WordMarker.Visibility = Visibility.Visible;

        // Scroll toward where the reader is now: recognition reports words
        // late, so aim a few words past the last recognized one.
        int leadIndex = Math.Min(_tokens.Length - 1, tokenIndex + ViewModel.ScrollLeadWords);
        Rect leadRect = leadIndex == tokenIndex ? rect : GetTokenRect(leadIndex);
        if (leadRect.IsEmpty)
        {
            leadRect = rect;
        }

        // The rect is content-relative (fixed within the text, not affected
        // by scrolling), so the target offset is simply its position minus
        // the reading line. Adding the live scroll offset here would create
        // a runaway feedback loop.
        double viewport = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        double readingLine = viewport * ReadingFraction;
        _scroll?.SetTarget(leadRect.Top - readingLine);
    }

    // Font size, column width and window size all re-wrap the text.
    private void OnPromptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateTokenRects();
        if (_currentToken >= 0)
        {
            HighlightAndScroll(_currentToken);
        }
    }

    // One wheel notch (delta 120) moves ~120px — about one prompter line.
    private void OnPromptMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _scroll?.UserScroll(-e.Delta);
        e.Handled = true;
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Use the new size directly: a ScrollViewer's ViewportHeight is not yet
        // updated when SizeChanged fires, so reading it here yields a stale value
        // and too little head-room, leaving the first lines stuck above the line.
        double viewport = e.NewSize.Height;
        if (viewport <= 0)
        {
            return;
        }

        double top = viewport * ReadingFraction;
        double bottom = viewport * (1.0 - ReadingFraction);

        // Line length is governed by the centered column (ColumnWidth); the
        // side padding is just breathing room inside it.
        // The padding change resizes Prompt, which re-measures the word
        // positions and re-centers the current word (OnPromptSizeChanged).
        Prompt.Padding = new Thickness(24, top, 24, bottom);
    }

    private void ApplyMirror()
    {
        Scroller.RenderTransformOrigin = new Point(0.5, 0.5);
        Scroller.RenderTransform = ViewModel.MirrorHorizontal
            ? new ScaleTransform(-1, 1)
            : Transform.Identity;

        if (MirrorToggle.IsChecked != ViewModel.MirrorHorizontal)
        {
            MirrorToggle.IsChecked = ViewModel.MirrorHorizontal;
        }
    }

    // ----- Editor overlay -----

    private void OnEditScript(object sender, RoutedEventArgs e)
    {
        EditorPanel.Visibility = Visibility.Visible;
    }

    private void OnDoneEditing(object sender, RoutedEventArgs e)
    {
        HideEditor();
        // Recompile through the view model so its ScriptModel stays in sync —
        // rendering from a stale model showed old text after the first Start.
        ViewModel.RebuildScript();
    }

    private void HideEditor() => EditorPanel.Visibility = Visibility.Collapsed;

    private void OnFontSmaller(object sender, RoutedEventArgs e)
        => ViewModel.FontSize = Math.Max(24, ViewModel.FontSize - 4);

    private void OnFontLarger(object sender, RoutedEventArgs e)
        => ViewModel.FontSize = Math.Min(120, ViewModel.FontSize + 4);

    // The mirror/topmost handlers serve two callers: the toolbar toggle itself
    // (state already flipped by the click) and the compact ☰ menu buttons
    // (plain buttons, so flip the state here and sync the toggle).
    private void OnToggleMirror(object sender, RoutedEventArgs e)
    {
        bool mirrored = ReferenceEquals(sender, MirrorToggle)
            ? MirrorToggle.IsChecked == true
            : !ViewModel.MirrorHorizontal;
        ViewModel.MirrorHorizontal = mirrored;
        MirrorToggle.IsChecked = mirrored;
        ApplyMirror();
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        bool onTop = ReferenceEquals(sender, TopmostToggle)
            ? TopmostToggle.IsChecked == true
            : !Topmost;
        Topmost = onTop;
        TopmostToggle.IsChecked = onTop;
    }

    /// <summary>Any button chosen in the ☰ menu closes it (combo clicks stay open).</summary>
    private void OnCompactMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (e.Source is Button)
        {
            MenuToggle.IsChecked = false;
        }
    }

    private const double CompactToolbarWidth = 760;

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        => ViewModel.IsCompactToolbar = e.NewSize.Width < CompactToolbarWidth;

    private void OnNavigateLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser/mail handler is not our problem to solve.
        }

        e.Handled = true;
    }

    // ----- Drag a script file (.docx / .txt) onto the window -----

    private static bool TryGetDroppedFile(DragEventArgs e, out string path)
    {
        path = string.Empty;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            path = files[0];
            return true;
        }

        return false;
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (TryGetDroppedFile(e, out _))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedFile(e, out string path))
        {
            ViewModel.LoadScriptFromPath(path);
            e.Handled = true;
        }
    }

    /// <summary>The picker opens over the window, so the settings popup must get out of the way.</summary>
    private void OnOpenLanguagePicker(object sender, RoutedEventArgs e) => SettingsToggle.IsChecked = false;

    private void OnSettingsPopupOpened(object sender, EventArgs e)
    {
        // Never taller than the window it opens over — scroll instead of
        // clipping options off-screen (the panel outgrew small windows).
        SettingsScroll.MaxHeight = Math.Clamp(ActualHeight - 150, 280, 640);
    }
}
