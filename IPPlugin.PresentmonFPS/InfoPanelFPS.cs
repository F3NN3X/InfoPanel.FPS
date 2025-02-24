using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using PresentMonFps;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.DwmApi;
using System.Runtime.InteropServices;

/*
 * Plugin: InfoPanel.FPS
 * Version: 1.0.9
 * Author: F3NN3X
 * Description: An optimized InfoPanel plugin using PresentMonFps to monitor fullscreen app performance. Tracks FPS, frame time, 1% low FPS (99th percentile), 0.1% low FPS (99.9th percentile), and frame time variance over 1000 frames. Updates every 1 second with efficient event-driven detection, ensuring immediate startup, reset on closure, and proper metric clearing.
 * Changelog:
 *   - v1.0.9 (Feb 24, 2025): Fixed 1% low reset on closure.
 *     - Reset Logic: Ensured immediate ResetSensorsAndQueue before cancellation.
 *     - Histogram: Cleared in ResetSensorsAndQueue to prevent stale percentiles.
 *     - Update Prevention: Blocked post-cancel updates in UpdateFrameTimesAndMetrics.
 *   - v1.0.8 (Feb 24, 2025): Fixed initial startup and reset delays.
 *     - Startup: Moved event hook to Initialize, added immediate PID check.
 *     - Reset: Forced immediate sensor reset on cancellation.
 *   - v1.0.7 (Feb 24, 2025): Added _isMonitoring flag, pre-allocated histogram, field-initialized event hook.
 *   - v1.0.6 (Feb 24, 2025): Fixed monitoring restart on focus regain.
 *   - v1.0.5 (Feb 24, 2025): Optimized performance and structure.
 *   - v1.0.4 (Feb 24, 2025): Added event hooks, new metrics, and improved detection.
 *   - v1.0.3 (Feb 24, 2025): Stabilized resets, 1% low FPS, and update smoothness.
 *   - v1.0.2 (Feb 22, 2025): Improved frame time update frequency.
 *   - v1.0.1 (Feb 22, 2025): Enhanced stability and consistency.
 *   - v1.0.0 (Feb 20, 2025): Initial release.
 * Note: A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
 */

namespace InfoPanel.Extras
{
    public class FpsPlugin : BasePlugin, IDisposable
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _onePercentLowFpsSensor = new("1% low fps", "1% Low Frames Per Second", 0, "FPS");
        private readonly PluginSensor _zeroPointOnePercentLowFpsSensor = new("0.1% low fps", "0.1% Low Frames Per Second", 0, "FPS");
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

        private double _mean;
        private double _m2;
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
            : base("fps-plugin", "InfoPanel.FPS", "Retrieves FPS, frame time, and low FPS metrics using PresentMonFPS - v1.0.9")
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

            // Immediate PID check to start monitoring on launch
            Task.Run(async () =>
            {
                uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                if (pid != 0 && !_isMonitoring)
                {
                    _currentPid = pid;
                    ResetSensorsAndQueue();
                    await StartMonitoringWithRetryAsync(pid, _cts.Token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        public override void Close() => Dispose();

        public void Dispose()
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
            Console.WriteLine("Plugin disposed; all sensors reset to 0, PID cleared");
            GC.SuppressFinalize(this);
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_onePercentLowFpsSensor);
            container.Entries.Add(_zeroPointOnePercentLowFpsSensor);
            container.Entries.Add(_currentFrameTimeSensor);
            container.Entries.Add(_frameTimeVarianceSensor);
            containers.Add(container);
        }

        public override void Update() => throw new NotImplementedException();

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
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
                Console.WriteLine($"UpdateAsync starting/restarting monitoring for PID: {pid}");
                _ = StartMonitoringWithRetryAsync(pid, _cts.Token);
            }
            Console.WriteLine($"UpdateAsync - FPS: {_fpsSensor.Value}, Frame Time: {_currentFrameTimeSensor.Value}, 1% Low: {_onePercentLowFpsSensor.Value}, 0.1% Low: {_zeroPointOnePercentLowFpsSensor.Value}, Variance: {_frameTimeVarianceSensor.Value}, Frame Times Count: {_frameTimeCount}");
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
                    uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                    Console.WriteLine($"Detected fullscreen process ID: {pid}");
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
                Console.WriteLine($"Monitoring loop error: {ex.Message}");
            }
        }

        private async Task HandleWindowChangeAsync()
        {
            uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
            Console.WriteLine($"Event detected - New PID: {pid}, Current PID: {_currentPid}, IsMonitoring: {_isMonitoring}");
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
                    Console.WriteLine($"Starting FpsInspector for PID: {pid} (Attempt {attempt}/{RetryAttempts})");
                    _isMonitoring = true;
                    await FpsInspector.StartForeverAsync(
                        fpsRequest,
                        result =>
                        {
                            if (result is null || (_cts?.IsCancellationRequested ?? true)) return;

                            float fps = (float)result.Fps;
                            float frameTime = 1000.0f / fps;

                            UpdateFrameTimesAndMetrics(frameTime, fps);
                        },
                        cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"FpsInspector started for PID: {pid}");
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"FpsInspector failed (attempt {attempt}/{RetryAttempts}): {ex.Message}");
                    _isMonitoring = false;
                    if (attempt < RetryAttempts)
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
                    else
                        ResetSensorsAndQueue();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error (attempt {attempt}/{RetryAttempts}): {ex}");
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
            if (_cts?.IsCancellationRequested ?? true) return; // Prevent updates after cancellation

            _frameTimes[_frameTimeIndex] = frameTime;
            _frameTimeIndex = (_frameTimeIndex + 1) % MaxFrameTimes;
            _frameTimeCount = Math.Min(_frameTimeCount + 1, MaxFrameTimes);

            _updateCount++;
            double delta = frameTime - _mean;
            _mean += delta / _updateCount;
            double delta2 = frameTime - _mean;
            _m2 += delta * delta2;

            if (_updateCount >= 2)
                _frameTimeVarianceSensor.Value = (float)(_m2 / (_updateCount - 1));

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
            float minFrameTime = _frameTimes.Take(_frameTimeCount).Min();
            float maxFrameTime = _frameTimes.Take(_frameTimeCount).Max();
            float range = maxFrameTime - minFrameTime;
            if (range <= 0) return;

            float bucketSize = range / _histogram.Length;
            foreach (float ft in _frameTimes.Take(_frameTimeCount))
            {
                int index = (int)((ft - minFrameTime) / bucketSize);
                if (index >= _histogram.Length) index = _histogram.Length - 1;
                _histogram[index]++;
            }

            float total = _frameTimeCount;
            float onePercentCount = total * 0.01f;
            float zeroPointOnePercentCount = total * 0.001f;
            float onePercentFrameTime = 0, zeroPointOnePercentFrameTime = 0;
            float cumulative = 0;

            for (int i = _histogram.Length - 1; i >= 0; i--)
            {
                cumulative += _histogram[i];
                if (cumulative >= onePercentCount && onePercentFrameTime == 0)
                    onePercentFrameTime = minFrameTime + (i + 0.5f) * bucketSize;
                if (cumulative >= zeroPointOnePercentCount && zeroPointOnePercentFrameTime == 0)
                    zeroPointOnePercentFrameTime = minFrameTime + (i + 0.5f) * bucketSize;
                if (onePercentFrameTime > 0 && zeroPointOnePercentFrameTime > 0) break;
            }

            _onePercentLowFpsSensor.Value = onePercentFrameTime > 0 ? 1000.0f / onePercentFrameTime : 0;
            _zeroPointOnePercentLowFpsSensor.Value = zeroPointOnePercentFrameTime > 0 ? 1000.0f / zeroPointOnePercentFrameTime : 0;
        }

        private void ResetSensorsAndQueue()
        {
            _fpsSensor.Value = 0;
            _currentFrameTimeSensor.Value = 0;
            _onePercentLowFpsSensor.Value = 0;
            _zeroPointOnePercentLowFpsSensor.Value = 0;
            _frameTimeVarianceSensor.Value = 0;
            Array.Clear(_frameTimes, 0, _frameTimes.Length);
            Array.Clear(_histogram, 0, _histogram.Length); // Clear histogram to reset percentiles
            _frameTimeIndex = 0;
            _frameTimeCount = 0;
            _mean = 0;
            _m2 = 0;
            _updateCount = 0;
        }

        private async Task<uint> GetActiveFullscreenProcessIdAsync()
        {
            return await Task.Run(() =>
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
            }).ConfigureAwait(false);
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
                Console.WriteLine($"PID validation error: {ex.Message}");
                return false;
            }
        }
    }
}