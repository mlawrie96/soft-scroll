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
///    a GetTickCount / TickCount64 deadline (AHK A_TickCount — same clock as
///    Environment.TickCount64 while uptime &lt; ~24.8 days).
///
/// Hot-path rule: never log synchronously from ShouldDrop (WH_MOUSE_LL). Drop
/// counts are flushed from the watcher thread.
/// </summary>
public static class InjectedWheelQuarantine
{
    public const string EventName = @"Local\SoftScroll_QuarantineInjectedWheel";

    /// <summary>How long to swallow injected wheel after a gesture signal.</summary>
    public static long DurationMs { get; set; } = 900;

    private static long _untilTick;
    private static EventWaitHandle? _evt;
    private static Thread? _watcher;
    private static volatile bool _running;
    private static string? _filePath;
    private static long _lastFileReadMs;
    private static long _dropCount;
    private static long _lastFlushedDrops;
    private static long _lastLoggedFileDeadline;

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
        FlushDropCount();
    }

    public static bool ShouldDrop(bool isInjected)
    {
        if (!isInjected) return false;
        RefreshFromFile();
        if (Environment.TickCount64 >= Interlocked.Read(ref _untilTick))
            return false;
        Interlocked.Increment(ref _dropCount);
        return true;
    }

    private static void Arm(long durationMs, string source)
    {
        var until = Environment.TickCount64 + durationMs;
        Interlocked.Exchange(ref _untilTick, until);
        Serilog.Log.Information(
            "[InjectedWheelQuarantine] armed via {Source} until={Until} durationMs={Ms} tickNow={Now}",
            source, until, durationMs, Environment.TickCount64);
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
            if (!long.TryParse(text, out var until)) return;

            // Same clock as AHK A_TickCount / GetTickCount (lower 32 bits of TickCount64).
            if (until > Interlocked.Read(ref _untilTick))
                Interlocked.Exchange(ref _untilTick, until);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Watcher-only: log new file deadlines without touching the hook path.</summary>
    private static void LogNewFileArmIfAny()
    {
        try
        {
            if (_filePath == null || !File.Exists(_filePath)) return;
            var text = File.ReadAllText(_filePath).Trim();
            if (!long.TryParse(text, out var until)) return;
            if (until == _lastLoggedFileDeadline) return;
            if (until <= Environment.TickCount64) return; // stale
            _lastLoggedFileDeadline = until;
            Serilog.Log.Information(
                "[InjectedWheelQuarantine] armed via file until={Until} remainingMs={Rem} tickNow={Now}",
                until, until - Environment.TickCount64, Environment.TickCount64);
        }
        catch
        {
            /* ignore */
        }
    }

    private static void FlushDropCount()
    {
        var total = Interlocked.Read(ref _dropCount);
        var prev = Interlocked.Read(ref _lastFlushedDrops);
        if (total == prev) return;
        Interlocked.Exchange(ref _lastFlushedDrops, total);
        var delta = total - prev;
        Serilog.Log.Information(
            "[InjectedWheelQuarantine] dropped {Delta} injected wheel event(s) (sessionTotal={Total}) until={Until} tickNow={Now}",
            delta, total, Interlocked.Read(ref _untilTick), Environment.TickCount64);
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
                LogNewFileArmIfAny();
                FlushDropCount();
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
