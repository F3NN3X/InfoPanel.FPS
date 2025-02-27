using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using PresentMonFps;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.DwmApi;

/*
 * Plugin: InfoPanel.FPS
 * Version: 1.0.11
 * Author: F3NN3X
 * Description: An optimized InfoPanel plugin using PresentMonFps to monitor fullscreen app performance. Tracks FPS, frame time, 1% low FPS (99th percentile), and frame time variance over 1000 frames. Updates every 1 second with efficient event-driven detection, ensuring immediate startup, reset on closure, and proper metric clearing.
 * Changelog (Recent):
 *   - v1.0.11 (Feb 27, 2025): Performance and robustness enhancements.
 *     - Reduced string allocations with format strings in logs.
 *     - Simplified Initialize by moving initial PID check to StartInitialMonitoringAsync.
 *     - Optimized GetActiveFullscreenProcessId to synchronous method.
 *     - Optimized UpdateLowFpsMetrics with single-pass min/max/histogram.
 *     - Enhanced exception logging with full stack traces.
 *     - Improved null safety for _cts checks.
 *     - Added finalizer for unmanaged resource cleanup.
 *   - v1.0.10 (Feb 27, 2025): Removed 0.1% low FPS calculation.
 *   - v1.0.9 (Feb 24, 2025): Fixed 1% low reset on closure.
 * Note: Full history in CHANGELOG.md. A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
 */

namespace InfoPanel.FPS
{
    public class FpsPlugin : BasePlugin, IDisposable
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _onePercentLowFpsSensor = new("1% low fps", "1% Low Frames Per Second", 0, "FPS");
        private readonly PluginSensor _currentFrameTimeSensor = new("current frame time", "Current Frame Time", 0, "ms");
        private readonly PluginSensor _frameTimeVarianceSensor = new("frame time variance", "Frame Time Variance", 0, "ms²");

        private readonly float[] _frameTimes = new float[1000];
        private int _frameTimeIndex;
        private int _frameTimeCount;

        private CancellationTokenSource? _cts;
        private volatile uint _currentPid;
        private volatile bool _isMonitoring;
        private DateTime _lastEventTime = DateTime.MinValue;
        private DateTime _lastUpdate = DateTime.MinValue;

        private double _frameTimeMean;
        private double _frameTimeM2;
        private int _updateCount;

        private IntPtr _eventHook;
        private readonly User32.WinEventProc _winEventProcDelegate;
        private readonly float[] _histogram = new float[100];

        private const int MaxFrameTimes = 1000;
        private const int RetryAttempts = 3;
        private const int RetryDelayMs = 1000;
        private const int MinFrameTimesForLowFps = 10;
        private const int LowFpsRecalcInterval = 30;
        private const int EventDebounceMs = 500;

        public FpsPlugin()
            : base("fps-plugin", "InfoPanel.FPS", "Retrieves FPS, frame time, and low FPS metrics using PresentMonFPS - v1.0.11")
        {
            _winEventProcDelegate = new User32.WinEventProc(WinEventProc);
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            _cts = new CancellationTokenSource();
            SetupEventHook();
            _ = StartFPSMonitoringAsync(_cts.Token);
            _ = StartInitialMonitoringAsync(_cts.Token); // Non-blocking initial PID check
        }

        private async Task StartInitialMonitoringAsync(CancellationToken cancellationToken)
        {
            // Currently synchronous up to StartMonitoringWithRetryAsync; kept async for future expansion
            uint pid = GetActiveFullscreenProcessId();
            if (pid != 0 && !_isMonitoring)
            {
                _currentPid = pid;
                ResetSensorsAndQueue();
                await StartMonitoringWithRetryAsync(pid, cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Close() => Dispose();

        ~FpsPlugin()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            ResetSensorsAndQueue(); // Immediate reset before cancellation
            _cts?.Cancel();
            if (_eventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_eventHook);
                _eventHook = IntPtr.Zero;
            }
            _cts?.Dispose();
            _currentPid = 0;
            _isMonitoring = false;
            if (disposing)
            {
                Console.WriteLine("Plugin disposed; all sensors reset to 0, PID cleared");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_onePercentLowFpsSensor);
            container.Entries.Add(_currentFrameTimeSensor);
            container.Entries.Add(_frameTimeVarianceSensor);
            containers.Add(container);
        }

        public override void Update() => throw new NotImplementedException();

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            uint pid = GetActiveFullscreenProcessId();
            if (pid == 0 && _currentPid != 0)
            {
                ResetSensorsAndQueue(); // Reset first
                _cts?.Cancel();
                _currentPid = 0;
                _isMonitoring = false;
                Console.WriteLine("UpdateAsync detected no fullscreen app; all sensors reset to 0");
            }
            else if (pid != 0 && !_isMonitoring)
            {
                ResetSensorsAndQueue();
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _currentPid = pid;
                Console.WriteLine("UpdateAsync starting/restarting monitoring for PID: {0}", pid);
                _ = StartMonitoringWithRetryAsync(pid, _cts.Token);
            }
            Console.WriteLine("UpdateAsync - FPS: {0}, Frame Time: {1}, 1% Low: {2}, Variance: {3}, Frame Times Count: {4}",
                _fpsSensor.Value, _currentFrameTimeSensor.Value, _onePercentLowFpsSensor.Value, _frameTimeVarianceSensor.Value, _frameTimeCount);
            await Task.CompletedTask;
        }

        private void SetupEventHook()
        {
            _eventHook = SetWinEventHook(
                User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProcDelegate,
                0, 0,
                User32.WINEVENT.WINEVENT_OUTOFCONTEXT).DangerousGetHandle();
            if (_eventHook == IntPtr.Zero)
                Console.WriteLine("Failed to set up event hook");
        }

        private void WinEventProc(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastEventTime).TotalMilliseconds < EventDebounceMs) return;
            _lastEventTime = now;
            _ = HandleWindowChangeAsync();
        }

        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint pid = GetActiveFullscreenProcessId();
                    Console.WriteLine("Detected fullscreen process ID: {0}", pid);
                    if (pid != 0 && !_isMonitoring)
                    {
                        ResetSensorsAndQueue();
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        _currentPid = pid;
                        await StartMonitoringWithRetryAsync(pid, _cts.Token).ConfigureAwait(false);
                    }
                    else if (pid == 0 && _currentPid != 0)
                    {
                        ResetSensorsAndQueue();
                        _cts?.Cancel();
                        _currentPid = 0;
                        _isMonitoring = false;
                        Console.WriteLine("No fullscreen app detected; all sensor values reset to 0");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Monitoring loop error: {0}", ex.ToString());
            }
        }

        private async Task HandleWindowChangeAsync()
        {
            uint pid = GetActiveFullscreenProcessId();
            Console.WriteLine("Event detected - New PID: {0}, Current PID: {1}, IsMonitoring: {2}", pid, _currentPid, _isMonitoring);
            if (pid != 0 && !_isMonitoring)
            {
                ResetSensorsAndQueue();
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _currentPid = pid;
                await StartMonitoringWithRetryAsync(pid, _cts.Token).ConfigureAwait(false);
            }
            else if (pid == 0 && _currentPid != 0)
            {
                ResetSensorsAndQueue();
                _cts?.Cancel();
                _currentPid = 0;
                _isMonitoring = false;
            }
        }

        private async Task StartMonitoringWithRetryAsync(uint pid, CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++)
            {
                try
                {
                    var fpsRequest = new FpsRequest { TargetPid = pid };
                    Console.WriteLine("Starting FpsInspector for PID: {0} (Attempt {1}/{2})", pid, attempt, RetryAttempts);
                    _isMonitoring = true;
                    await FpsInspector.StartForeverAsync(
                        fpsRequest,
                        result =>
                        {
                            if (result is null || (_cts is null || _cts.IsCancellationRequested)) return;

                            float fps = (float)result.Fps;
                            float frameTime = 1000.0f / fps;

                            UpdateFrameTimesAndMetrics(frameTime, fps);
                        },
                        cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("FpsInspector started for PID: {0}", pid);
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine("FpsInspector failed (attempt {0}/{1}): {2}", attempt, RetryAttempts, ex.ToString());
                    _isMonitoring = false;
                    if (attempt < RetryAttempts)
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
                    else
                        ResetSensorsAndQueue();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unexpected error (attempt {0}/{1}): {2}", attempt, RetryAttempts, ex.ToString());
                    _isMonitoring = false;
                    if (attempt < RetryAttempts)
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
                    else
                        ResetSensorsAndQueue();
                }
            }
        }

        private void UpdateFrameTimesAndMetrics(float frameTime, float fps)
        {
            if (_cts is null || _cts.IsCancellationRequested) return;

            _frameTimes[_frameTimeIndex] = frameTime;
            _frameTimeIndex = (_frameTimeIndex + 1) % MaxFrameTimes;
            _frameTimeCount = Math.Min(_frameTimeCount + 1, MaxFrameTimes);

            _updateCount++;
            double delta = frameTime - _frameTimeMean;
            _frameTimeMean += delta / _updateCount;
            double delta2 = frameTime - _frameTimeMean;
            _frameTimeM2 += delta * delta2;

            if (_updateCount >= 2)
                _frameTimeVarianceSensor.Value = (float)(_frameTimeM2 / (_updateCount - 1));

            if (_updateCount % LowFpsRecalcInterval == 0 && _frameTimeCount >= MinFrameTimesForLowFps)
                UpdateLowFpsMetrics();

            DateTime now = DateTime.Now;
            if ((now - _lastUpdate).TotalSeconds >= 1)
            {
                _fpsSensor.Value = fps;
                _currentFrameTimeSensor.Value = frameTime;
                _lastUpdate = now;
            }
        }

        private void UpdateLowFpsMetrics()
        {
            Array.Clear(_histogram, 0, _histogram.Length);
            if (_frameTimeCount == 0) return;

            float minFrameTime = float.MaxValue;
            float maxFrameTime = float.MinValue;

            for (int i = 0; i < _frameTimeCount; i++)
            {
                float frameTime = _frameTimes[i];
                minFrameTime = Math.Min(minFrameTime, frameTime);
                maxFrameTime = Math.Max(maxFrameTime, frameTime);
            }

            float range = maxFrameTime - minFrameTime;
            if (range <= 0) return;

            float bucketSize = range / _histogram.Length;
            for (int i = 0; i < _frameTimeCount; i++)
            {
                float ft = _frameTimes[i];
                int index = (int)((ft - minFrameTime) / bucketSize);
                if (index >= _histogram.Length) index = _histogram.Length - 1;
                _histogram[index]++;
            }

            float total = _frameTimeCount;
            float onePercentCount = total * 0.01f;
            float onePercentFrameTime = 0;
            float cumulative = 0;

            for (int i = _histogram.Length - 1; i >= 0; i--)
            {
                cumulative += _histogram[i];
                if (cumulative >= onePercentCount && onePercentFrameTime == 0)
                {
                    onePercentFrameTime = minFrameTime + (i + 0.5f) * bucketSize;
                    break;
                }
            }

            _onePercentLowFpsSensor.Value = onePercentFrameTime > 0 ? 1000.0f / onePercentFrameTime : 0;
        }

        private void ResetSensorsAndQueue()
        {
            _fpsSensor.Value = 0;
            _currentFrameTimeSensor.Value = 0;
            _onePercentLowFpsSensor.Value = 0;
            _frameTimeVarianceSensor.Value = 0;
            Array.Clear(_frameTimes, 0, _frameTimes.Length);
            Array.Clear(_histogram, 0, _histogram.Length);
            _frameTimeIndex = 0;
            _frameTimeCount = 0;
            _frameTimeMean = 0;
            _frameTimeM2 = 0;
            _updateCount = 0;
        }

        private uint GetActiveFullscreenProcessId()
        {
            HWND hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return 0u;

            RECT windowRect;
            if (!GetWindowRect(hWnd, out windowRect)) return 0u;

            HMONITOR hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return 0u;

            var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo)) return 0u;

            var monitorRect = monitorInfo.rcMonitor;
            bool isExactMatch = windowRect.Equals(monitorRect);

            if (!isExactMatch)
            {
                RECT extendedFrameBounds;
                if (DwmGetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out extendedFrameBounds).Succeeded)
                    isExactMatch = extendedFrameBounds.Equals(monitorRect);
            }

            if (isExactMatch)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                return IsValidApplicationPid(pid) ? pid : 0u;
            }
            return 0u;
        }

        private static bool IsValidApplicationPid(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return pid > 4 && process.MainWindowHandle != IntPtr.Zero;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PID validation error: {0}", ex.ToString());
                return false;
            }
        }
    }
}