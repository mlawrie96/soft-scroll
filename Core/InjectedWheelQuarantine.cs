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
///    Environment.TickCount64 *within a single boot session*).
///
/// IMPORTANT: TickCount64 resets to 0 on every reboot, but this file is NOT
/// boot-scoped -- it can persist across reboots with a deadline value from a
/// much longer previous uptime, which would silently re-arm a stuck
/// multi-day quarantine if trusted without a plausibility check. Both
/// RefreshFromFile and ShouldDrop reject any deadline more than
/// DurationMs+PlausibilityMarginMs in the future, and Start() proactively
/// deletes any leftover file on launch. See lessons.md, "boot-reset bug".
///
/// Hot-path rule: never log synchronously from ShouldDrop (WH_MOUSE_LL). Drop
/// counts are flushed from the watcher thread.
/// </summary>
public static class InjectedWheelQuarantine
{
    public const string EventName = @"Local\SoftScroll_QuarantineInjectedWheel";

    /// <summary>How long to swallow injected wheel after a gesture signal.</summary>
    public static long DurationMs { get; set; } = 900;

    /// <summary>
    /// Safety margin above DurationMs for the plausibility check below — real
    /// arms are always "now + DurationMs"; this just tolerates normal
    /// scheduling/IPC slop without being loose enough to accept garbage.
    /// </summary>
    private const long PlausibilityMarginMs = 2000;

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

        // Self-heal on every launch: a leftover deadline file is *usually* a
        // stale cross-boot leftover (TickCount64 resets on every reboot; this
        // file doesn't) -- but Windows gives no ordering guarantee between
        // Startup entries, so AHK could legitimately start before this
        // process and signal a real quarantine in that narrow window. Don't
        // discard that: apply the same plausibility check used everywhere
        // else, and only clear the file if it's actually stale/implausible.
        try
        {
            if (File.Exists(_filePath))
            {
                var text = File.ReadAllText(_filePath).Trim();
                var now = Environment.TickCount64;
                var plausible = long.TryParse(text, out var until)
                    && until > now
                    && until - now <= DurationMs + PlausibilityMarginMs;
                if (!plausible)
                {
                    File.Delete(_filePath);
                    Serilog.Log.Information("[InjectedWheelQuarantine] Cleared stale/implausible deadline file on startup");
                }
                else
                {
                    Serilog.Log.Information(
                        "[InjectedWheelQuarantine] Found plausible deadline file on startup (boot-race arm), honoring it: until={Until} tickNow={Now}",
                        until, now);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[InjectedWheelQuarantine] Failed to check/clear deadline file on startup");
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

        var now = Environment.TickCount64;
        var until = Interlocked.Read(ref _untilTick);

        // Defense-in-depth: never trust a stored deadline further in the
        // future than DurationMs could plausibly produce, regardless of how
        // it got set. Environment.TickCount64 resets to 0 on every reboot,
        // but the file-based deadline is NOT boot-scoped -- a value written
        // during a previous, much-longer uptime session (e.g. tick 270000000
        // at ~75h uptime) can outlive a reboot on disk and, without this
        // check, silently re-arm a multi-day "stuck" quarantine the moment
        // this process starts back up and reads it. See lessons.md.
        if (until - now > DurationMs + PlausibilityMarginMs)
            return false;

        if (now >= until)
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

            // Same clock as AHK A_TickCount / GetTickCount (lower 32 bits of TickCount64)
            // *within a single boot session* -- but the file itself persists across
            // reboots, and TickCount64 resets to 0 on every boot. Reject anything
            // implausibly far out instead of ratcheting up to it; a stale
            // cross-boot leftover should be ignored, not honored for days.
            if (until - now > DurationMs + PlausibilityMarginMs) return;

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
            var now = Environment.TickCount64;
            if (until <= now) return; // stale (in the past)
            if (until - now > DurationMs + PlausibilityMarginMs) return; // implausible (e.g. cross-boot leftover)
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
