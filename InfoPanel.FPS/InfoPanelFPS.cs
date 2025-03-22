using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using PresentMonFps;
using Vanara.PInvoke;
using static Vanara.PInvoke.DwmApi;
using static Vanara.PInvoke.User32;

/*
 * Plugin: InfoPanel.FPS
 * Version: 1.0.13
 * Author: F3NN3X
 * Description: An optimized simple InfoPanel plugin using PresentMonFps to monitor fullscreen app performance. Tracks FPS, frame time, 1% low FPS (99th percentile) over 1000 frames, and displays the current app's window title in the UI. Updates every 1 second with efficient event-driven detection, ensuring immediate startup, reset on closure, and proper metric clearing.
 * Changelog (Recent):
 *   - v1.0.13 (Mar 22, 2025): Added window title sensor.
 *     - New PluginText sensor displays the title of the current fullscreen app in the UI for user-friendly identification.
 *   - v1.0.12 (Mar 10, 2025): Simplified metrics.
 *     - Removed frame time variance sensor and related calculations for a leaner plugin.
 *   - v1.0.11 (Feb 27, 2025): Performance and robustness enhancements.
 *     - Reduced string allocations with format strings in logs.
 *     - Simplified Initialize by moving initial PID check to StartInitialMonitoringAsync.
 *     - Optimized GetActiveFullscreenProcessId to synchronous method.
 *     - Optimized UpdateLowFpsMetrics with single-pass min/max/histogram.
 *     - Enhanced exception logging with full stack traces.
 *     - Improved null safety for _cts checks.
 *     - Added finalizer for unmanaged resource cleanup.
 * Note: Full history in CHANGELOG.md. A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
 */

namespace InfoPanel.FPS
{
    // Monitors fullscreen app performance, exposing FPS, frame time, 1% low FPS, and window title via InfoPanel sensors
    public class FpsPlugin : BasePlugin, IDisposable
    {
        // Sensors for displaying metrics in InfoPanel UI
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _onePercentLowFpsSensor = new(
            "1% low fps",
            "1% Low FPS",
            0,
            "FPS"
        );
        private readonly PluginSensor _currentFrameTimeSensor = new(
            "current frame time",
            "Current Frame Time",
            0,
            "ms"
        );
        private readonly PluginText _windowTitle = new(
            "windowtitle",
            "Currently Capturing",
            "Nothing to capture"
        ); // Window title sensor

        // Circular buffer for frame times, used for percentile calculations
        private readonly float[] _frameTimes = new float[1000];
        private int _frameTimeIndex; // Current index in the frame time buffer
        private int _frameTimeCount; // Number of frame times stored

        // Manages cancellation for async operations
        private CancellationTokenSource? _cts;
        private volatile uint _currentPid; // Tracks the current fullscreen app's PID; 0 if none
        private volatile bool _isMonitoring; // Indicates if FpsInspector is actively monitoring
        private DateTime _lastEventTime = DateTime.MinValue; // Last time a window event was processed
        private DateTime _lastUpdate = DateTime.MinValue; // Last time UI sensors were updated

        // Variables for tracking frame time updates
        private int _updateCount; // Number of frame time updates processed

        // Windows event hook handle and delegate for foreground changes
        private IntPtr _eventHook; // Handle to the event hook
        private readonly User32.WinEventProc _winEventProcDelegate; // Delegate for event hook callback
        private readonly float[] _histogram = new float[100]; // Pre-allocated histogram for 1% low calculation

        // Constants defining operational limits
        private const int MaxFrameTimes = 1000; // Maximum frame time samples in buffer
        private const int RetryAttempts = 3; // Number of retries for FpsInspector startup
        private const int RetryDelayMs = 1000; // Delay between retry attempts (ms)
        private const int MinFrameTimesForLowFps = 10; // Minimum frame times for valid 1% low calculation
        private const int LowFpsRecalcInterval = 30; // Recalculate 1% low every 30 frames
        private const int EventDebounceMs = 500; // Debounce window events by 500ms

        // Initializes the plugin with its metadata
        public FpsPlugin()
            : base(
                "fps-plugin",
                "InfoPanel.FPS",
                "Simple FPS plugin showing FPS, frame time, 1% low FPS, and window title using PresentMonFPS - v1.0.13"
            )
        {
            _winEventProcDelegate = new User32.WinEventProc(WinEventProc);
        }

        // No configuration file is used
        public override string? ConfigFilePath => null;

        // Updates occur every 1 second for stable UI refreshes
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Sets up event hooks and starts monitoring
        public override void Initialize()
        {
            _cts = new CancellationTokenSource();
            SetupEventHook();
            _ = StartFPSMonitoringAsync(_cts.Token); // Start continuous monitoring in background
            _ = StartInitialMonitoringAsync(_cts.Token); // Non-blocking initial PID check
        }

        // Performs initial PID check asynchronously without blocking Initialize
        private async Task StartInitialMonitoringAsync(CancellationToken cancellationToken)
        {
            // Currently synchronous up to StartMonitoringWithRetryAsync; kept async for future expansion
            (uint pid, string windowTitle) = GetActiveFullscreenProcessIdAndTitle();
            if (pid != 0 && !_isMonitoring)
            {
                _currentPid = pid;
                _windowTitle.Value = windowTitle; // Set initial title
                ResetSensorsAndQueue();
                await StartMonitoringWithRetryAsync(pid, cancellationToken).ConfigureAwait(false);
            }
        }

        // Closes the plugin by disposing resources
        public override void Close() => Dispose();

        // Finalizer ensures unmanaged resources are cleaned up if Dispose isn’t called
        ~FpsPlugin()
        {
            Dispose(false);
        }

        // Disposes resources, both managed and unmanaged, based on the disposing context
        protected virtual void Dispose(bool disposing)
        {
            ResetSensorsAndQueue(); // Reset sensors before cancelling tasks
            _cts?.Cancel(); // Cancel any ongoing tasks
            if (_eventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_eventHook); // Release the Windows event hook
                _eventHook = IntPtr.Zero;
            }
            _cts?.Dispose(); // Dispose the cancellation token source
            _currentPid = 0; // Clear the current PID
            _isMonitoring = false; // Mark monitoring as stopped
            if (disposing)
            {
                Console.WriteLine("Plugin disposed; all sensors reset to 0, PID cleared");
            }
        }

        // Public entry point for IDisposable.Dispose, ensuring proper cleanup
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this); // Prevent finalizer from running
        }

        // Registers sensors with InfoPanel’s UI container
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_onePercentLowFpsSensor);
            container.Entries.Add(_currentFrameTimeSensor);
            container.Entries.Add(_windowTitle); // Add window title sensor to UI
            containers.Add(container);
        }

        // Not implemented; UpdateAsync is used instead
        public override void Update() => throw new NotImplementedException();

        // Updates sensor values and checks for app closure every 1 second
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            (uint pid, string windowTitle) = GetActiveFullscreenProcessIdAndTitle();
            if (pid == 0 && _currentPid != 0) // App closed or lost fullscreen
            {
                ResetSensorsAndQueue(); // Reset sensors when no fullscreen app is detected
                _cts?.Cancel();
                _currentPid = 0;
                _isMonitoring = false;
                _windowTitle.Value = "-"; // Reset title to default
                Console.WriteLine("UpdateAsync detected no fullscreen app; all sensors reset to 0");
            }
            else if (pid != 0 && !_isMonitoring) // New fullscreen app detected
            {
                ResetSensorsAndQueue();
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _currentPid = pid;
                _windowTitle.Value = windowTitle; // Set initial title
                Console.WriteLine(
                    "UpdateAsync starting/restarting monitoring for PID: {0}, Title: {1}",
                    pid,
                    windowTitle
                );
                _ = StartMonitoringWithRetryAsync(pid, _cts.Token); // Start monitoring in background
            }
            else if (pid != 0 && _currentPid == pid) // Update title if still monitoring same app
            {
                _windowTitle.Value = windowTitle;
            }
            // Log current sensor values and title for debugging
            Console.WriteLine(
                "UpdateAsync - FPS: {0}, Frame Time: {1}, 1% Low: {2}, Title: {3}, Frame Times Count: {4}",
                _fpsSensor.Value,
                _currentFrameTimeSensor.Value,
                _onePercentLowFpsSensor.Value,
                _windowTitle.Value,
                _frameTimeCount
            );
            await Task.CompletedTask;
        }

        // Sets up the Windows event hook to monitor foreground window changes
        private void SetupEventHook()
        {
            _eventHook = SetWinEventHook(
                    User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                    User32.EventConstants.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _winEventProcDelegate,
                    0,
                    0,
                    User32.WINEVENT.WINEVENT_OUTOFCONTEXT
                )
                .DangerousGetHandle();
            if (_eventHook == IntPtr.Zero)
                Console.WriteLine("Failed to set up event hook");
        }

        // Handles foreground window change events with debouncing
        private void WinEventProc(
            HWINEVENTHOOK hWinEventHook,
            uint eventType,
            HWND hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime
        )
        {
            DateTime now = DateTime.Now;
            if ((now - _lastEventTime).TotalMilliseconds < EventDebounceMs)
                return; // Debounce events to prevent rapid firing
            _lastEventTime = now;
            _ = HandleWindowChangeAsync(); // Process change asynchronously
        }

        // Continuously monitors fullscreen apps and manages FpsInspector lifecycle
        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    (uint pid, string windowTitle) = GetActiveFullscreenProcessIdAndTitle();
                    Console.WriteLine(
                        "Detected fullscreen process ID: {0}, Title: {1}",
                        pid,
                        windowTitle
                    );
                    if (pid != 0 && !_isMonitoring) // Start monitoring new fullscreen app
                    {
                        ResetSensorsAndQueue();
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        _currentPid = pid;
                        _windowTitle.Value = windowTitle; // Set initial title
                        await StartMonitoringWithRetryAsync(pid, _cts.Token).ConfigureAwait(false);
                    }
                    else if (pid == 0 && _currentPid != 0) // Stop monitoring when app closes
                    {
                        ResetSensorsAndQueue();
                        _cts?.Cancel();
                        _currentPid = 0;
                        _isMonitoring = false;
                        _windowTitle.Value = "-"; // Reset title to default
                        Console.WriteLine(
                            "No fullscreen app detected; all sensor values reset to 0"
                        );
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                        .ConfigureAwait(false); // Check every second
                }
            }
            catch (TaskCanceledException) { } // Expected on cancellation
            catch (Exception ex)
            {
                Console.WriteLine("Monitoring loop error: {0}", ex.ToString());
            }
        }

        // Handles window change events by checking fullscreen status and updating monitoring
        private async Task HandleWindowChangeAsync()
        {
            (uint pid, string windowTitle) = GetActiveFullscreenProcessIdAndTitle();
            Console.WriteLine(
                "Event detected - New PID: {0}, Title: {1}, Current PID: {2}, IsMonitoring: {3}",
                pid,
                windowTitle,
                _currentPid,
                _isMonitoring
            );
            if (pid != 0 && !_isMonitoring) // Start monitoring new fullscreen app
            {
                ResetSensorsAndQueue();
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _currentPid = pid;
                _windowTitle.Value = windowTitle; // Set initial title
                await StartMonitoringWithRetryAsync(pid, _cts.Token).ConfigureAwait(false);
            }
            else if (pid == 0 && _currentPid != 0) // Stop monitoring when app closes
            {
                ResetSensorsAndQueue();
                _cts?.Cancel();
                _currentPid = 0;
                _isMonitoring = false;
                _windowTitle.Value = "-"; // Reset title to default
            }
        }

        // Starts FpsInspector with retry logic for robustness
        private async Task StartMonitoringWithRetryAsync(
            uint pid,
            CancellationToken cancellationToken
        )
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++)
            {
                try
                {
                    var fpsRequest = new FpsRequest { TargetPid = pid };
                    Console.WriteLine(
                        "Starting FpsInspector for PID: {0} (Attempt {1}/{2})",
                        pid,
                        attempt,
                        RetryAttempts
                    );
                    _isMonitoring = true;
                    await FpsInspector
                        .StartForeverAsync(
                            fpsRequest,
                            result =>
                            {
                                if (
                                    result is null
                                    || (_cts is null || _cts.IsCancellationRequested)
                                )
                                    return; // Skip if result is null or cancelled

                                float fps = (float)result.Fps;
                                float frameTime = 1000.0f / fps;
                                UpdateFrameTimesAndMetrics(frameTime, fps);
                            },
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    Console.WriteLine("FpsInspector started for PID: {0}", pid);
                    break; // Success, exit retry loop
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine(
                        "FpsInspector failed (attempt {0}/{1}): {2}",
                        attempt,
                        RetryAttempts,
                        ex.ToString()
                    );
                    _isMonitoring = false;
                    if (attempt < RetryAttempts)
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false); // Wait before retry
                    else
                        ResetSensorsAndQueue(); // Final attempt failed, reset
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        "Unexpected error (attempt {0}/{1}): {2}",
                        attempt,
                        RetryAttempts,
                        ex.ToString()
                    );
                    _isMonitoring = false;
                    if (attempt < RetryAttempts)
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false); // Wait before retry
                    else
                        ResetSensorsAndQueue(); // Final attempt failed, reset
                }
            }
        }

        // Updates frame time buffer and recalculates metrics, throttling UI updates to 1 second
        private void UpdateFrameTimesAndMetrics(float frameTime, float fps)
        {
            if (_cts is null || _cts.IsCancellationRequested)
                return; // Skip if cancelled

            // Store frame time in circular buffer
            _frameTimes[_frameTimeIndex] = frameTime;
            _frameTimeIndex = (_frameTimeIndex + 1) % MaxFrameTimes;
            _frameTimeCount = Math.Min(_frameTimeCount + 1, MaxFrameTimes);

            // Increment update count for 1% low recalculation
            _updateCount++;

            // Recalculate 1% low FPS periodically
            if (
                _updateCount % LowFpsRecalcInterval == 0
                && _frameTimeCount >= MinFrameTimesForLowFps
            )
                UpdateLowFpsMetrics();

            // Update UI sensors every second
            DateTime now = DateTime.Now;
            if ((now - _lastUpdate).TotalSeconds >= 1)
            {
                _fpsSensor.Value = fps;
                _currentFrameTimeSensor.Value = frameTime;
                _lastUpdate = now;
            }
        }

        // Calculates 1% low FPS using a histogram-based approximation over the frame time buffer
        private void UpdateLowFpsMetrics()
        {
            Array.Clear(_histogram, 0, _histogram.Length);
            if (_frameTimeCount == 0)
                return; // No data to process

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
            if (range <= 0)
                return; // No variation, skip calculation

            float bucketSize = range / _histogram.Length;

            // Second pass: Build histogram
            for (int i = 0; i < _frameTimeCount; i++)
            {
                float ft = _frameTimes[i];
                int index = (int)((ft - minFrameTime) / bucketSize);
                if (index >= _histogram.Length)
                    index = _histogram.Length - 1; // Clamp to last bucket
                _histogram[index]++;
            }

            // Calculate 1% low frame time from histogram
            float total = _frameTimeCount;
            float onePercentCount = total * 0.01f;
            float onePercentFrameTime = 0;
            float cumulative = 0;

            for (int i = _histogram.Length - 1; i >= 0; i--)
            {
                cumulative += _histogram[i];
                if (cumulative >= onePercentCount && onePercentFrameTime == 0)
                {
                    onePercentFrameTime = minFrameTime + (i + 0.5f) * bucketSize; // Midpoint of bucket
                    break;
                }
            }

            _onePercentLowFpsSensor.Value =
                onePercentFrameTime > 0 ? 1000.0f / onePercentFrameTime : 0;
        }

        // Resets all sensors and internal state to initial values
        private void ResetSensorsAndQueue()
        {
            _fpsSensor.Value = 0;
            _currentFrameTimeSensor.Value = 0;
            _onePercentLowFpsSensor.Value = 0;
            _windowTitle.Value = "Nothing to capture"; // Reset title to default
            Array.Clear(_frameTimes, 0, _frameTimes.Length);
            Array.Clear(_histogram, 0, _histogram.Length);
            _frameTimeIndex = 0;
            _frameTimeCount = 0;
            _updateCount = 0;
        }

        // Gets the process ID and window title of the current fullscreen app, or (0, "") if none detected
        private (uint pid, string windowTitle) GetActiveFullscreenProcessIdAndTitle()
        {
            HWND hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
                return (0u, ""); // No foreground window

            RECT windowRect;
            if (!GetWindowRect(hWnd, out windowRect))
                return (0u, ""); // Failed to get window rectangle

            HMONITOR hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero)
                return (0u, ""); // No monitor found

            var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
                return (0u, ""); // Failed to get monitor info

            var monitorRect = monitorInfo.rcMonitor;
            bool isExactMatch = windowRect.Equals(monitorRect);

            if (!isExactMatch) // Check extended bounds for borderless fullscreen
            {
                RECT extendedFrameBounds;
                if (
                    DwmGetWindowAttribute(
                        hWnd,
                        DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                        out extendedFrameBounds
                    ).Succeeded
                )
                    isExactMatch = extendedFrameBounds.Equals(monitorRect);
            }

            if (isExactMatch)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (IsValidApplicationPid(pid))
                {
                    // Get window title
                    int length = GetWindowTextLength(hWnd);
                    if (length > 0)
                    {
                        StringBuilder title = new StringBuilder(length + 1);
                        GetWindowText(hWnd, title, title.Capacity);
                        return (pid, title.ToString());
                    }
                    return (pid, "Untitled"); // Fallback if no title
                }
                return (0u, ""); // Invalid PID
            }
            return (0u, ""); // Not fullscreen
        }

        // Validates if a PID belongs to a legitimate application with a main window
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
