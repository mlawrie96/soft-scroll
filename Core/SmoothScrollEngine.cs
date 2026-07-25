using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoftScroll.Native;
using SoftScroll.Settings;

namespace SoftScroll.Core;

public sealed class SmoothScrollEngine : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _signal = new(false);

    private Axis _v = new();
    private Axis _h = new();

    private AppSettings _s = AppSettings.CreateDefault();

    // Use constants from ScrollConstants
    private static readonly int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;
    private static readonly int EMIT_UNIT = ScrollConstants.EMIT_UNIT;
    private static readonly double BASE_STEP_PX = ScrollConstants.BASE_STEP_PX;
    private static readonly int PULSE_CLAMP_MIN = ScrollConstants.PULSE_CLAMP_MIN;
    private static readonly int PULSE_CLAMP_MAX = ScrollConstants.PULSE_CLAMP_MAX;

    // Display refresh rate — detected lazily on first Start() to avoid blocking startup
    private static int? DisplayRefreshRate;
    private static readonly object _refreshLock = new();

    private enum ScrollAxis
    {
        None,
        Vertical,
        Horizontal
    }

    private ScrollAxis _lastAxis = ScrollAxis.None;

    // Adaptive frame rate: match display Hz for smoothness, drop to 60fps when idle
    private double _targetFrameMs = 1000.0 / 120; // default 120fps for new instances
    private long _lastWorkTime;

    private const double SPIN_WAIT_COUNT = 10;
    private const int IDLE_TIMEOUT_MS = 2000; // drop to 60fps after 2s idle

    public SmoothScrollEngine(AppSettings settings)
    {
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings s)
    {
        lock (_lock)
        {
            _s = s;
        }
    }

    public void Start()
    {
        // Detect display refresh rate on first start (lazy to avoid blocking app startup)
        if (!DisplayRefreshRate.HasValue)
        {
            lock (_refreshLock)
            {
                if (!DisplayRefreshRate.HasValue)
                    DisplayRefreshRate = NativeMethods.GetDisplayRefreshRate();
            }
            // Target frame rate: match display refresh if >= 60Hz, floor at 120fps
            _targetFrameMs = DisplayRefreshRate.Value >= 120 ? 1000.0 / DisplayRefreshRate.Value : 1000.0 / 120;
        }

        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "SmoothScrollEngine" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            // Reset axis state inside lock to avoid race with worker thread
            _v = new();
            _h = new();
        }
        _signal.Set();
        _thread?.Join(1000);
    }

    public void OnWheel(int delta, bool isInjected = false)
    {
        lock (_lock)
        {
            if (_lastAxis == ScrollAxis.Horizontal)
            {
                _h = new();
            }

            var dir = _s.ReverseWheelDirection ? -1 : 1;
            // Third-party injected input (software KVMs like Synergy, RDP, etc.)
            // can carry a different effective sign convention than real hardware
            // for the same physical scroll gesture (e.g. a Synergy client
            // receiving scroll forwarded from a macOS server). Never assume it
            // shares the hardware path's convention — apply this independently.
            if (isInjected && _s.ReverseInjectedWheelDirection) dir *= -1;
            var now = Environment.TickCount64;
            _v.RegisterNotch(now, delta * dir, _s, isInjected);
            _lastAxis = ScrollAxis.Vertical;
        }
        _signal.Set();
    }

    public void OnWheelWithSettings(int delta, AppSettings customSettings)
    {
        lock (_lock)
        {
            var dir = customSettings.ReverseWheelDirection ? -1 : 1;
            var now = Environment.TickCount64;
            _v.RegisterNotch(now, delta * dir, customSettings);
        }
        _signal.Set();
    }

    public void OnHWheel(int delta)
    {
        lock (_lock)
        {
            if (_lastAxis == ScrollAxis.Vertical)
            {
                _v = new();
            }

            // No ReverseWheelDirection for horizontal: scrolling right (positive delta)
            // must always mean "scroll right" per Windows convention.
            var now = Environment.TickCount64;
            _h.RegisterNotch(now, delta, _s);
            _lastAxis = ScrollAxis.Horizontal;
        }
        _signal.Set();
    }

    private void Worker()
    {
        var sw = Stopwatch.StartNew();
        double lastMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            try
            {
                // Check if there's anything to emit
                bool workAvailable;
                double remainingTotal;
                lock (_lock)
                {
                    workAvailable = Math.Abs(_v.RemainingPx) >= 0.1
                        || Math.Abs(_h.RemainingPx) >= 0.1;
                    remainingTotal = Math.Abs(_v.RemainingPx) + Math.Abs(_h.RemainingPx);
                }

                if (!workAvailable)
                {
                    // Block until a wheel event signals us or timeout elapses.
                    // Timeout guarantees eventual shutdown even if no signal arrives.
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    // Reset time base after idle to prevent frame-1 jitter on new notch
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    _lastWorkTime = Environment.TickCount64;
                    continue;
                }

                var nowMs = sw.Elapsed.TotalMilliseconds;
                var dt = Math.Max(1.0, nowMs - lastMs);
                lastMs = nowMs;
                _lastWorkTime = Environment.TickCount64;

                // Adaptive frame rate computation
                var frameMs = ComputeAdaptiveFrameMs(remainingTotal);

                int outV = 0, outH = 0;
                lock (_lock)
                {
                    outV = _v.Step(dt, _s);
                    if (_s.HorizontalSmoothness) outH = _h.Step(dt, _s); else outH = 0;
                }

                // Buffered SendInput: emit both axes in a single call
                if (outV != 0 || outH != 0) SendWheel(outV, outH);

                var sleep = frameMs - (sw.Elapsed.TotalMilliseconds - nowMs);
                if (sleep > 0.5) Thread.Sleep((int)Math.Round(sleep));
                else Thread.SpinWait((int)SPIN_WAIT_COUNT);
            }
            catch (Exception ex)
            {
                // Prevent worker thread from dying silently
                System.Diagnostics.Debug.WriteLine($"SmoothScrollEngine worker: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Adaptive frame rate: scales from target (display Hz / 120) down to 60fps when idle.
    /// When remaining scroll is small (&lt; 50px) and no recent notch, drop to 60fps to save CPU.
    /// When remaining is large or recent rapid notches, ramp up to target Hz.
    /// </summary>
    private double ComputeAdaptiveFrameMs(double remainingPx)
    {
        var idleTime = Environment.TickCount64 - _lastWorkTime;

        // Idle ≥ 2s → drop to 60fps
        if (idleTime >= IDLE_TIMEOUT_MS)
            return 1000.0 / 60;

        // Active scrolling: use target (display-matched) frame rate
        return _targetFrameMs;
    }

    private static void SendWheel(int mouseData, int hMouseData)
    {
        // Vertical scroll uses SendInput + MOUSEEVENTF_WHEEL.
        // Horizontal scroll uses PostMessageW + WM_MOUSEWHEEL + MK_SHIFT, but with
        // an inverted delta sign. WM_MOUSEHWHEEL (positive = right) and
        // WM_MOUSEWHEEL+MK_SHIFT (positive = up → interpreted as left by target)
        // use opposite sign conventions, so we flip the sign to preserve the user's
        // physical scroll direction. See GitHub issue #13.
        // Both axes are independent — each uses its own data.
        if (hMouseData != 0)
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                var hwnd = NativeMethods.WindowFromPoint(pt);
                if (hwnd != IntPtr.Zero)
                {
                    hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                    if (hwnd != IntPtr.Zero)
                    {
                        // wParam: MK_SHIFT | (wheelData << 16) — encode delta in high word.
                        // Invert hMouseData because Shift+vertical convention is opposite of HWHEEL.
                        IntPtr wParam = (IntPtr)((uint)(-hMouseData) << 16 | NativeMethods.MK_SHIFT);
                        IntPtr lParam = (IntPtr)((pt.y << 16) | (pt.x & 0xFFFF));
                        NativeMethods.PostMessageW(hwnd, NativeMethods.WM_MOUSEWHEEL, wParam, lParam);
                    }
                }
            }
        }

        if (mouseData != 0)
        {
            var size = Marshal.SizeOf<NativeMethods.INPUT>();
            var inp = new NativeMethods.INPUT
            {
                type = 0,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = mouseData, dwExtraInfo = NativeMethods.OWN_INPUT_SIGNATURE }
                }
            };
            NativeMethods.SendInput(1, [inp], size);
        }
    }

    public void ResetHorizontalAxis()
    {
        lock (_lock)
        {
            _h = new();
        }
    }

    public void Dispose() => Stop();

    public static double ComputeEasingFraction(double dtMs, double duration, EasingMode mode, double tailToHeadRatio, bool easingEnabled)
    {
        if (!easingEnabled || mode == EasingMode.Linear)
        {
            return Math.Min(1.0, dtMs / duration);
        }

        var t = dtMs / duration;

        return mode switch
        {
            EasingMode.CubicOut => 1.0 - Math.Pow(1.0 - Math.Min(t, 1.0), 3),
            EasingMode.QuinticOut => 1.0 - Math.Pow(1.0 - Math.Min(t, 1.0), 5),
            _ => 1.0 - Math.Exp(-(2.0 + tailToHeadRatio) * t) // ExponentialOut (default)
        };
    }

    private struct Axis
    {
        public double RemainingPx;
        public long LastNotchTime;
        public int AccelFactor;
        public double UnitAccum;

        // Momentum fields
        public double Velocity;       // px/ms
        public bool InMomentum;
        private double _momentumAccum;

        // EWMA of inter-arrival gap for injected notches only, used to infer
        // whether an injected stream behaves like a real mouse wheel (bursty,
        // larger gaps) or a continuous device -- a trackpad -- forwarded
        // through the KVM (sustained 60-120Hz stream, small gaps). See
        // ComputeInjectedScale.
        private double _injectedAvgGapMs;
        private const double InjectedGapSmoothing = 0.25;

        public void RegisterNotch(long nowMs, int delta, AppSettings s, bool isInjected = false)
        {
            // Cancel momentum on new user input
            if (InMomentum)
            {
                InMomentum = false;
                Velocity = 0;
                _momentumAccum = 0;
            }

            var timeSinceLast = nowMs - LastNotchTime;
            var injectedScale = 1.0;

            if (isInjected)
            {
                // Third-party injected input (software KVMs like Synergy/Barrier/
                // Input Leap, RDP, automation tools) has no timing guarantees:
                // their wire protocols carry no original event timestamp, and
                // network jitter can deliver several genuinely-separate physical
                // notches to this hook in a tight burst. Windows-local arrival
                // time is not a trustworthy proxy for physical scroll speed for
                // this source, so injected notches never ramp acceleration —
                // avoiding an artificial multi-x overscroll on bursty delivery.
                AccelFactor = 1;

                // Synergy has no device-type bit in its wire protocol at all
                // (verified against its source: macOS capture never reads
                // kCGScrollWheelEventIsContinuous, so nothing downstream could
                // use it even if we wanted to). Continuous devices (trackpads)
                // still behave distinctly from mouse wheels in one way we CAN
                // observe here: macOS/Synergy convert every scroll callback
                // (60-120Hz, no batching) into its own wheel message, so a
                // trackpad swipe sustains a much higher, steadier notch rate
                // than a mouse wheel ever produces. Track a smoothed
                // inter-arrival gap and derive the scale from THAT — sustained
                // fast arrival (trackpad-like) gets dampened toward
                // InjectedWheelScale; slower/burstier arrival (mouse-like) is
                // left close to 1.0. This lets a Synergy-forwarded mouse and a
                // Synergy-forwarded trackpad each find their own natural scale
                // instead of both being forced through one flat constant.
                if (timeSinceLast > 0 && timeSinceLast < 2000)
                {
                    _injectedAvgGapMs = _injectedAvgGapMs <= 0
                        ? timeSinceLast
                        : _injectedAvgGapMs * (1 - InjectedGapSmoothing) + timeSinceLast * InjectedGapSmoothing;
                }

                injectedScale = ComputeInjectedScale(_injectedAvgGapMs, s);
            }
            else if (timeSinceLast <= s.AccelerationDeltaMs)
            {
                AccelFactor = Math.Min(s.AccelerationMax, Math.Max(1, AccelFactor + 1));
            }
            else
            {
                AccelFactor = 1;
            }

            LastNotchTime = nowMs;

            var notches = delta / (double)WHEEL_DELTA;
            var rawPixels = notches * s.StepSizePx * AccelFactor * injectedScale;
            // Ceiling at the max a single genuine hardware notch could ever
            // produce (one notch at max acceleration). A no-op for hardware
            // (which can't exceed this today), a safety net for injected input
            // against bursty delivery, KVM-side pre-scaling, or duplicate replay.
            var maxPixels = s.StepSizePx * s.AccelerationMax;
            var pixels = Math.Clamp(rawPixels, -maxPixels, maxPixels);
            RemainingPx += pixels;

            // Injected-source timing can't be trusted for velocity either, for
            // the same reason as the acceleration ramp above — only real
            // hardware input feeds momentum.
            if (!isInjected && s.MomentumEnabled && timeSinceLast > 0 && timeSinceLast < 500)
            {
                Velocity = pixels / timeSinceLast;
            }
        }

        /// <summary>
        /// Maps a smoothed inter-notch gap (ms) to a scale factor: gaps at or
        /// above InjectedMouseLikeGapMs (bursty, mouse-like cadence) return 1.0
        /// (untouched); gaps at or below InjectedTrackpadLikeGapMs (sustained
        /// high-frequency, trackpad-like) return InjectedWheelScale (the
        /// dampened floor); linear interpolation between the two.
        /// </summary>
        private static double ComputeInjectedScale(double avgGapMs, AppSettings s)
        {
            if (avgGapMs <= 0) return 1.0; // no data yet (first notch) — don't touch it
            if (avgGapMs >= s.InjectedMouseLikeGapMs) return 1.0;
            if (avgGapMs <= s.InjectedTrackpadLikeGapMs) return s.InjectedWheelScale;

            var span = s.InjectedMouseLikeGapMs - s.InjectedTrackpadLikeGapMs;
            var t = (avgGapMs - s.InjectedTrackpadLikeGapMs) / (double)span;
            return s.InjectedWheelScale + t * (1.0 - s.InjectedWheelScale);
        }

        public int Step(double dtMs, AppSettings s)
        {
            // Momentum phase: if normal scroll finished and velocity is significant
            if (s.MomentumEnabled && !InMomentum && Math.Abs(RemainingPx) < 0.1 && Math.Abs(Velocity) > 0.05)
            {
                var elapsed = Environment.TickCount64 - LastNotchTime;
                if (elapsed > 80) // Wait a short moment after last notch
                {
                    InMomentum = true;
                }
            }

            if (InMomentum)
            {
                // Friction: higher value = stops faster. Scale 0-100 to 0.001-0.02
                var friction = 0.001 + (s.MomentumFriction / 100.0) * 0.019;
                Velocity *= Math.Pow(1.0 - friction, dtMs);

                if (Math.Abs(Velocity) < 0.02)
                {
                    InMomentum = false;
                    Velocity = 0;
                    _momentumAccum = 0;
                    return 0;
                }

                var momentumPx = Velocity * dtMs;
                var wheelUnits = (momentumPx / BASE_STEP_PX) * WHEEL_DELTA;
                _momentumAccum += wheelUnits / EMIT_UNIT;

                int mPulses = 0;
                if (Math.Abs(_momentumAccum) >= 1.0)
                {
                    mPulses = (int)_momentumAccum;
                    _momentumAccum -= mPulses;
                }
                if (mPulses == 0) return 0;
                mPulses = Math.Clamp(mPulses, PULSE_CLAMP_MIN, PULSE_CLAMP_MAX);
                return mPulses * EMIT_UNIT;
            }

            // Normal smooth scroll
            if (Math.Abs(RemainingPx) < 0.1)
            {
                RemainingPx = 0;
                UnitAccum = 0;
                return 0;
            }

            var duration = Math.Max(1.0, s.AnimationTimeMs);
            var frac = ComputeEasingFraction(dtMs, duration, s.EasingMode, s.TailToHeadRatio, s.AnimationEasing);

            var emitPx = RemainingPx * frac;
            RemainingPx -= emitPx;

            var wUnits = (emitPx / BASE_STEP_PX) * WHEEL_DELTA;

            var units = wUnits / EMIT_UNIT;
            UnitAccum += units;

            int pulses = 0;
            if (Math.Abs(UnitAccum) >= 1.0)
            {
                pulses = (int)UnitAccum;
                UnitAccum -= pulses;
            }

            if (pulses == 0) return 0;
            pulses = Math.Clamp(pulses, PULSE_CLAMP_MIN, PULSE_CLAMP_MAX);
            return pulses * EMIT_UNIT;
        }
    }
}
