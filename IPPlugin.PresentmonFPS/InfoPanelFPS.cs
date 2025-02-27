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
    /// <summary>
    /// Monitors fullscreen app performance, exposing FPS, frame time, 1% low FPS, and variance via InfoPanel sensors.
    /// </summary>
    public class FpsPlugin : BasePlugin, IDisposable
    {
        // Sensors for displaying metrics in InfoPanel UI
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _onePercentLowFpsSensor = new("1% low fps", "1% Low Frames Per Second", 0, "FPS");
        private readonly PluginSensor _currentFrameTimeSensor = new("current frame time", "Current Frame Time", 0, "ms");
        private readonly PluginSensor _frameTimeVarianceSensor = new("frame time variance", "Frame Time Variance", 0, "ms²");

        // Circular buffer for frame times, used for percentile and variance calculations
        private readonly float[] _frameTimes = new float[1000];
        private int _frameTimeIndex; // Current index in the frame time buffer
        private int _frameTimeCount; // Number of frame times stored

        // Manages cancellation for async operations
        private CancellationTokenSource? _cts;
        private volatile uint _currentPid; // Tracks the current fullscreen app's PID; 0 if none
        private volatile bool _isMonitoring; // Indicates if FpsInspector is actively monitoring
        private DateTime _lastEventTime = DateTime.MinValue; // Last time a window event was processed
        private DateTime _lastUpdate = DateTime.MinValue; // Last time UI sensors were updated

        // Variables for Welford’s online variance algorithm
        private double _frameTimeMean; // Running mean of frame times
        private double _frameTimeM2;   // Running sum of squared differences for variance
        private int _updateCount;      // Number of frame time updates processed

        // Windows event hook handle for foreground changes
        private IntPtr _eventHook;
        private readonly User32.WinEventProc _winEventProcDelegate; // Delegate for event hook callback
        private readonly float[] _histogram = new float[100]; // Pre-allocated histogram for 1% low calculation

        // Constants defining operational limits
        private const int MaxFrameTimes = 1000; // Maximum frame time samples in buffer
        private const int RetryAttempts = 3;    // Number of retries for FpsInspector startup
        private const int RetryDelayMs = 1000;  // Delay between retry attempts (ms)
        private const int MinFrameTimesForLowFps = 10; // Minimum frame times for valid 1% low calculation
        private const int LowFpsRecalcInterval = 30;   // Recalculate 1% low every 30 frames
        private const int EventDebounceMs = 500;       // Debounce window events by 500ms

        /// <summary>
        /// Initializes the plugin with its metadata.
        /// </summary>
        public FpsPlugin()
            : base("fps-plugin", "InfoPanel.FPS", "Retrieves FPS, frame time, and low FPS metrics using PresentMonFPS - v1.0.11")
        {
            _winEventProcDelegate = new User32.WinEventProc(WinEventProc);
        }

        /// <summary>
        /// No configuration file is used.
        /// </summary>
        public override string? ConfigFilePath => null;

        /// <summary>
        /// Updates occur every 1 second for stable UI refreshes.
        /// </summary>
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        /// <summary>
        /// Sets up event hooks and starts monitoring.
        /// </summary>
        public override void Initialize()
        {
            _cts = new CancellationTokenSource();
            SetupEventHook();
            _ = StartFPSMonitoringAsync(_cts.Token); // Start continuous monitoring
            _ = StartInitialMonitoringAsync(_cts.Token); // Non-blocking initial PID check
        }

        /// <summary>
        /// Performs initial PID check asynchronously without blocking Initialize.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
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

        /// <summary>
        /// Closes the plugin by disposing resources.
        /// </summary>
        public override void Close() => Dispose();

        /// <summary>
        /// Finalizer ensures unmanaged resources are cleaned up if Dispose isn’t called.
        /// </summary>
        ~FpsPlugin()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes resources, both managed and unmanaged, based on the disposing context.
        /// </summary>
        /// <param name="disposing">True if called from Dispose(), false from finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            ResetSensorsAndQueue(); // Reset sensors before cancelling tasks
            _cts?.Cancel();         // Cancel any ongoing tasks
            if (_eventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_eventHook); // Release the Windows event hook
                _eventHook = IntPtr.Zero;
            }
            _cts?.Dispose();        // Dispose the cancellation token source
            _currentPid = 0;        // Clear the current PID
            _isMonitoring = false;  // Mark monitoring as stopped
            if (disposing)
            {
                Console.WriteLine("Plugin disposed; all sensors reset to 0, PID cleared");
            }
        }

        /// <summary>
        /// Public entry point for IDisposable.Dispose, ensuring proper cleanup.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this); // Prevent finalizer from running
        }

        /// <summary>
        /// Registers sensors with InfoPanel’s UI container.
        /// </summary>
        /// <param name="containers">List of plugin containers to populate.</param>
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_onePercentLowFpsSensor);
            container.Entries.Add(_currentFrameTimeSensor);
            container.Entries.Add(_frameTimeVarianceSensor);
            containers.Add(container);
        }

        /// <summary>
        /// Not implemented; UpdateAsync is used instead.
        /// </summary>
        public override void Update() => throw new NotImplementedException();

        /// <summary>
        /// Updates sensor values and checks for app closure every 1 second.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            uint pid = GetActiveFullscreenProcessId();
            if (pid == 0 && _currentPid != 0)
            {
                ResetSensorsAndQueue(); // Reset sensors when no fullscreen app is detected
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
                _ = StartMonitoringWithRetryAsync(pid, _cts.Token); // Start monitoring in background
            }
            Console.WriteLine("UpdateAsync - FPS: {0}, Frame Time: {1}, 1% Low: {2}, Variance: {3}, Frame Times Count: {4}",
                _fpsSensor.Value, _currentFrameTimeSensor.Value, _onePercentLowFpsSensor.Value, _frameTimeVarianceSensor.Value, _frameTimeCount);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Sets up the Windows event hook to monitor foreground window changes.
        /// </summary>
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

        /// <summary>
        /// Handles foreground window change events with debouncing.
        /// </summary>
        private void WinEventProc(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastEventTime).TotalMilliseconds < EventDebounceMs) return; // Debounce events
            _lastEventTime = now;
            _ = HandleWindowChangeAsync(); // Process change asynchronously
        }

        /// <summary>
        /// Continuously monitors fullscreen apps and manages FpsInspector lifecycle.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the monitoring loop.</param>
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
            catch (TaskCanceledException) { } // Expected on cancellation
            catch (Exception ex)
            {
                Console.WriteLine("Monitoring loop error: {0}", ex.ToString());
            }
        }

        /// <summary>
        /// Handles window change events by checking fullscreen status and updating monitoring.
        /// </summary>
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

        /// <summary>
        /// Starts FpsInspector with retry logic for robustness.
        /// </summary>
        /// <param name="pid">Process ID to monitor.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
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

        /// <summary>
        /// Updates frame time buffer and recalculates metrics, throttling UI updates to 1 second.
        /// </summary>
        /// <param name="frameTime">Latest frame time in milliseconds.</param>
        /// <param name="fps">Latest FPS value.</param>
        private void UpdateFrameTimesAndMetrics(float frameTime, float fps)
        {
            if (_cts is null || _cts.IsCancellationRequested) return; // Skip if cancelled

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

        /// <summary>
        /// Calculates 1% low FPS using a histogram-based approximation over the frame time buffer.
        /// </summary>
        private void UpdateLowFpsMetrics()
        {
            Array.Clear(_histogram, 0, _histogram.Length);
            if (_frameTimeCount == 0) return;

            float minFrameTime = float.MaxValue;
            float maxFrameTime = float.MinValue;

            // First pass: Find min and max frame times
            for (int i = 0; i < _frameTimeCount; i++)
            {
                float frameTime = _frameTimes[i];
                minFrameTime = Math.Min(minFrameTime, frameTime);
                maxFrameTime = Math.Max(maxFrameTime, frameTime);
            }

            float range = maxFrameTime - minFrameTime;
            if (range <= 0) return; // No variation, skip calculation

            float bucketSize = range / _histogram.Length;

            // Second pass: Build histogram
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

            // Find 1% low frame time from histogram
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

        /// <summary>
        /// Resets all sensors and internal state to initial values.
        /// </summary>
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

        /// <summary>
        /// Gets the process ID of the current fullscreen app, or 0 if none detected.
        /// </summary>
        /// <returns>Process ID if fullscreen app is detected, otherwise 0.</returns>
        private uint GetActiveFullscreenProcessId()
        {
            HWND hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return 0u; // No foreground window

            RECT windowRect;
            if (!GetWindowRect(hWnd, out windowRect)) return 0u; // Failed to get window rectangle

            HMONITOR hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return 0u; // No monitor found

            var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo)) return 0u; // Failed to get monitor info

            var monitorRect = monitorInfo.rcMonitor;
            bool isExactMatch = windowRect.Equals(monitorRect);

            if (!isExactMatch)
            {
                RECT extendedFrameBounds;
                if (DwmGetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out extendedFrameBounds).Succeeded)
                    isExactMatch = extendedFrameBounds.Equals(monitorRect); // Check extended bounds for borderless fullscreen
            }

            if (isExactMatch)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                return IsValidApplicationPid(pid) ? pid : 0u; // Validate and return PID
            }
            return 0u;
        }

        /// <summary>
        /// Validates if a PID belongs to a legitimate application with a main window.
        /// </summary>
        /// <param name="pid">Process ID to validate.</param>
        /// <returns>True if valid, false otherwise.</returns>
        private static bool IsValidApplicationPid(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return pid > 4 && process.MainWindowHandle != IntPtr.Zero; // Exclude system PIDs and ensure window exists
            }
            catch (ArgumentException)
            {
                return false; // Process not found
            }
            catch (Exception ex)
            {
                Console.WriteLine("PID validation error: {0}", ex.ToString());
                return false;
            }
        }
    }
}