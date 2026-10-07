using System;
using System.Runtime.InteropServices;

namespace Teleprompter.App.Services;

/// <summary>
/// Keeps every window of this app out of screen recordings and screen shares
/// (OBS, Zoom, Teams, Snipping Tool…) while it stays visible on the monitor.
/// Popups, dropdowns, tooltips and message boxes are separate top-level
/// windows, so a WinEvent hook protects each one the moment it is shown.
/// </summary>
public static class CaptureProtection
{
    private const uint WdaNone = 0x0;
    private const uint WdaMonitor = 0x1;            // pre-2004 fallback: shows as a black box
    private const uint WdaExcludeFromCapture = 0x11; // Windows 10 2004+: fully invisible

    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const uint GaRoot = 2;

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Held in a field: a collected delegate would crash the next callback.
    private static readonly WinEventProc HookProc = OnWindowShown;
    private static IntPtr _hook;
    private static bool _hidden;

    /// <summary>True when windows can be made fully invisible (Windows 10 2004+).</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>Protects (or releases) all open windows and every window shown later.</summary>
    public static void SetEnabled(bool hidden)
    {
        _hidden = hidden;

        if (hidden && _hook == IntPtr.Zero)
        {
            // Out-of-context callbacks arrive on this (UI) thread's message loop.
            _hook = SetWinEventHook(
                EventObjectShow, EventObjectShow, IntPtr.Zero, HookProc,
                (uint)Environment.ProcessId, 0, WinEventOutOfContext);
        }
        else if (!hidden && _hook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _ = EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            Apply(hwnd, hidden);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Applies the current state to one window (call before it is first drawn).</summary>
    public static void Apply(IntPtr hwnd) => Apply(hwnd, _hidden);

    private static void Apply(IntPtr hwnd, bool hidden)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!hidden)
        {
            _ = SetWindowDisplayAffinity(hwnd, WdaNone);
        }
        else if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            _ = SetWindowDisplayAffinity(hwnd, WdaMonitor);
        }
    }

    private static void OnWindowShown(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_hidden && idObject == ObjIdWindow && idChild == 0 && GetAncestor(hwnd, GaRoot) == hwnd)
        {
            Apply(hwnd, true);
        }
    }
}
