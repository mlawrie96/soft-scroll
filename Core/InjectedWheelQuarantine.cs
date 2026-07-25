using System;
using System.IO;
using System.Threading;

namespace SoftScroll.Core;

/// <summary>
/// Temporarily drops third-party injected wheel events (Synergy/Barrier/etc.)
/// without touching real hardware scroll. Signaled by Mac gesture AHK after
/// taskview/next/prev — Synergy residual trackpad motion arrives as a wheel burst.
///
/// Signals (either works):
/// 1. Named AutoReset event Local\SoftScroll_QuarantineInjectedWheel
/// 2. File %AppData%\SoftScroll\gesture_wheel_quarantine_until.txt containing
///    a TickCount64 deadline (AHK writes this — more reliable than OpenEvent).
/// </summary>
public static class InjectedWheelQuarantine
{
    public const string EventName = @"Local\SoftScroll_QuarantineInjectedWheel";

    public static long DurationMs { get; set; } = 450;

    private static long _untilTick;
    private static EventWaitHandle? _evt;
    private static Thread? _watcher;
    private static volatile bool _running;
    private static string? _filePath;
    private static long _lastFileReadMs;

    public static void Start()
    {
        if (_running) return;
        _running = true;
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoftScroll", "gesture_wheel_quarantine_until.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            _evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[InjectedWheelQuarantine] Failed to create event {Name}", EventName);
        }

        _watcher = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "SoftScroll.InjectedWheelQuarantine",
        };
        _watcher.Start();
        Serilog.Log.Information("[InjectedWheelQuarantine] Watching event+file duration={Ms}ms file={File}",
            DurationMs, _filePath);
    }

    public static void Stop()
    {
        _running = false;
        try { _evt?.Set(); } catch { /* ignore */ }
        try { _evt?.Dispose(); } catch { /* ignore */ }
        _evt = null;
    }

    public static bool ShouldDrop(bool isInjected)
    {
        if (!isInjected) return false;
        RefreshFromFile();
        return Environment.TickCount64 < Interlocked.Read(ref _untilTick);
    }

    private static void Arm(long durationMs, string source)
    {
        var until = Environment.TickCount64 + durationMs;
        Interlocked.Exchange(ref _untilTick, until);
        Serilog.Log.Information("[InjectedWheelQuarantine] armed via {Source} until={Until}", source, until);
    }

    private static void RefreshFromFile()
    {
        var now = Environment.TickCount64;
        if (now - _lastFileReadMs < 20) return; // throttle
        _lastFileReadMs = now;
        try
        {
            if (_filePath == null || !File.Exists(_filePath)) return;
            var text = File.ReadAllText(_filePath).Trim();
            if (long.TryParse(text, out var until) && until > Interlocked.Read(ref _untilTick))
                Interlocked.Exchange(ref _untilTick, until);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void WatchLoop()
    {
        while (_running)
        {
            try
            {
                // Wait for event OR poll file every 50ms
                bool signaled = _evt != null && _evt.WaitOne(50);
                if (!_running) break;
                if (signaled)
                    Arm(DurationMs, "event");
                RefreshFromFile();
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[InjectedWheelQuarantine] watcher error");
                Thread.Sleep(100);
            }
        }
    }
}
