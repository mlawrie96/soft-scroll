using System;
using System.Threading;

namespace SoftScroll.Core;

/// <summary>
/// Temporarily drops <em>third-party injected</em> wheel events (Synergy/Barrier/etc.)
/// without touching real hardware scroll. Used when Mac BTT fires a 4-finger gesture:
/// Synergy still forwards residual trackpad motion as a wheel burst; AHK cannot safely
/// install Wheel* hotkeys (MaxHotkeysPerInterval), so SoftScroll swallows the burst here.
///
/// Signal: named AutoReset event <c>Local\SoftScroll_QuarantineInjectedWheel</c>
/// (AHK OpenEvent + SetEvent after taskview/next/prev).
/// </summary>
public static class InjectedWheelQuarantine
{
    public const string EventName = @"Local\SoftScroll_QuarantineInjectedWheel";

    /// <summary>How long to drop injected wheel after each signal.</summary>
    public static long DurationMs { get; set; } = 450;

    private static long _untilTick;
    private static EventWaitHandle? _evt;
    private static Thread? _watcher;
    private static volatile bool _running;

    public static void Start()
    {
        if (_running) return;
        _running = true;
        try
        {
            _evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[InjectedWheelQuarantine] Failed to create event {Name}", EventName);
            _running = false;
            return;
        }

        _watcher = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "SoftScroll.InjectedWheelQuarantine",
        };
        _watcher.Start();
        Serilog.Log.Information("[InjectedWheelQuarantine] Watching {Name} duration={Ms}ms", EventName, DurationMs);
    }

    public static void Stop()
    {
        _running = false;
        try { _evt?.Set(); } catch { /* ignore */ }
        try { _evt?.Dispose(); } catch { /* ignore */ }
        _evt = null;
    }

    /// <summary>True → caller should swallow this wheel event (Handled=true, no engine).</summary>
    public static bool ShouldDrop(bool isInjected)
    {
        if (!isInjected) return false;
        return Environment.TickCount64 < Interlocked.Read(ref _untilTick);
    }

    private static void WatchLoop()
    {
        while (_running)
        {
            try
            {
                if (_evt == null) break;
                _evt.WaitOne();
                if (!_running) break;
                var until = Environment.TickCount64 + DurationMs;
                Interlocked.Exchange(ref _untilTick, until);
                Serilog.Log.Debug("[InjectedWheelQuarantine] armed until TickCount64={Until}", until);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[InjectedWheelQuarantine] watcher error");
                Thread.Sleep(100);
            }
        }
    }
}
