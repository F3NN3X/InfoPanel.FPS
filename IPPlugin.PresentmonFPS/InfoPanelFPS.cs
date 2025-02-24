using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using PresentMonFps;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

/*
 * Plugin: InfoPanel.FPS
 * Version: 1.0.3
 * Author: F3NN3X
 * Description: An InfoPanel plugin that leverages PresentMonFps to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds, and 1% low FPS (99th percentile over 1000 frames). Updates every 1 second via FpsInspector for stability, with per-frame 1% low calculations and immediate resets on closure.
 * Changelog:
 *   - v1.0.3 (Feb 24, 2025): Stabilized resets, 1% low FPS, and update smoothness after multiple iterations.
 *     - Reset: Restored reliable reset on closure with lightweight PID check in UpdateAsync (v1.0.20), reverted to v1.0.2’s active polling approach (v1.0.16), ensured FpsInspector stops promptly on pid == 0 (v1.0.20).
 *     - 1% Low: Fixed calculation and sticking issues by ensuring FpsInspector callback fires (v1.0.19), moved 1% low update outside throttle for per-frame accuracy (v1.0.18), retained v1.0.7’s every-frame update and v1.0.6’s minimum frame times.
 *     - Stability: Unified updates through FpsInspector with 1-second throttling (v1.0.19), removed conflicting GetFpsAsync polling (v1.0.19), synced monitoring loop to 1s (v1.0.18), built on v1.0.5’s stability enhancements.
 *     - Detection: Kept v1.0.12’s borderless fullscreen fix (dimension matching).
 *   - v1.0.2 (Feb 22, 2025): Improved update frequency (last fully working version).
 *     - Performance: Reduced UpdateInterval to 200ms.
 *   - v1.0.1 (Feb 22, 2025): Enhanced stability.
 *     - Consistency: Aligned plugin name.
 *   - v1.0.0 (Feb 20, 2025): Initial release.
 * Note: A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
 */

namespace InfoPanel.Extras
{
    // Defines the FPS monitoring plugin, inheriting from BasePlugin to integrate with InfoPanel’s framework for displaying performance metrics
    public class FpsPlugin : BasePlugin
    {
        // Sensor instances that register performance metrics with InfoPanel’s UI
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS"); // Tracks and displays the current FPS value of the monitored fullscreen app
        private readonly PluginSensor _onePercentLowFpsSensor = new(
            "1% low fps",
            "1% Low Frames Per Second",
            0,
            "FPS"
        ); // Tracks and displays the 1% low FPS, calculated as the 99th percentile over 1000 frames
        private readonly PluginSensor _currentFrameTimeSensor = new(
            "current frame time",
            "Current Frame Time",
            0,
            "ms"
        ); // Tracks and displays the current frame time in milliseconds, derived from FPS

        // Thread-safe queue to store up to 1000 frame time samples for computing the 1% low FPS metric; uses ConcurrentQueue for safe multi-threaded access
        private readonly ConcurrentQueue<float> _frameTimes = new ConcurrentQueue<float>();

        // Cancellation token sources to manage the lifecycle of background tasks
        private CancellationTokenSource? _cancellationTokenSource; // Controls the main monitoring loop, allowing graceful shutdown when the plugin closes
        private CancellationTokenSource? _monitoringCts; // Controls the FpsInspector monitoring task, enabling restarts or cancellation as needed

        // Stores the process ID (PID) of the fullscreen application currently being monitored; 0 if no app is active
        private uint _currentPid;

        // Tracks the last update time for throttling FPS and frame time updates to 1 second, and for detecting stalls in FpsInspector
        private DateTime _lastUpdate = DateTime.MinValue;

        // Synchronization object to prevent concurrent updates to sensor values and the frame times queue across threads
        private readonly object _updateLock = new object();

        // Configuration constants defining operational limits and retry behavior
        private const int MaxFrameTimes = 1000; // Maximum number of frame time samples to keep in the queue for 1% low FPS calculation
        private const int RetryAttempts = 3; // Number of times to retry starting FpsInspector if it fails (e.g., due to file conflicts)
        private const int RetryDelayMs = 1000; // Delay in milliseconds between retry attempts to allow system recovery
        private const int MinFrameTimesForLowFps = 10; // Minimum number of frame times required for a valid 1% low FPS calculation

        // Constructor that sets up the plugin with a unique identifier, name, and description for integration into InfoPanel
        public FpsPlugin()
            : base(
                "fps-plugin",
                "InfoPanel.FPS",
                "Retrieves FPS, frame time, and 1% low FPS using PresentMonFPS - v1.0.3"
            ) { }

        // Specifies that this plugin does not rely on an external configuration file; returns null to indicate no config is needed
        public override string? ConfigFilePath => null;

        // Sets the update interval to 1 second for stable, throttled updates aligned with InfoPanel’s refresh rate and FpsInspector’s throttling
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Initializes the plugin by starting a background task to monitor fullscreen applications and their FPS; runs continuously until closed
        public override void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource(); // Creates a new token source to manage the monitoring loop
            _ = StartFPSMonitoringAsync(_cancellationTokenSource.Token); // Launches the monitoring task in the background without blocking
        }

        // Cleans up resources when the plugin is unloaded or closed, ensuring all tasks stop and sensors reset
        public override void Close()
        {
            _cancellationTokenSource?.Cancel(); // Signals the main monitoring loop to stop
            _monitoringCts?.Cancel(); // Signals the FpsInspector task to stop
            _cancellationTokenSource?.Dispose(); // Releases the main token source
            _monitoringCts?.Dispose(); // Releases the FpsInspector token source
            lock (_updateLock) // Ensures no concurrent updates during reset
            {
                _fpsSensor.Value = 0; // Resets FPS sensor to 0
                _currentFrameTimeSensor.Value = 0; // Resets frame time sensor to 0
                _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor to 0
                while (_frameTimes.TryDequeue(out _)) { } // Clears the frame time queue
            }
            _currentPid = 0; // Clears the current PID to indicate no active monitoring
            Console.WriteLine("Plugin closed; all sensors reset to 0, PID cleared"); // Logs the shutdown for debugging
        }

        // Registers the plugin’s sensors with InfoPanel’s UI by grouping them into a labeled container for display
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS"); // Creates a container with the label "FPS" to organize sensors in the UI
            container.Entries.Add(_fpsSensor); // Adds the current FPS sensor
            container.Entries.Add(_onePercentLowFpsSensor); // Adds the 1% low FPS sensor
            container.Entries.Add(_currentFrameTimeSensor); // Adds the current frame time sensor
            containers.Add(container); // Registers the container with InfoPanel
        }

        // Synchronous update method required by BasePlugin; not used as InfoPanel relies on UpdateAsync for asynchronous updates
        public override void Update() => throw new NotImplementedException();

        // Asynchronously checks for fullscreen app closure and logs current sensor values every 1 second; ensures resets happen quickly
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            // Lightweight PID check to catch closure faster than the monitoring loop
            uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
            if (pid == 0 && _currentPid != 0) // Fullscreen app closed
            {
                if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                {
                    _monitoringCts.Cancel(); // Stops the FpsInspector task immediately
                    _monitoringCts.Dispose(); // Releases the token source
                    Console.WriteLine("Stopped monitoring task from UpdateAsync due to PID 0"); // Logs the stop action
                }
                _currentPid = 0; // Updates the tracked PID to reflect no active app
                lock (_updateLock) // Ensures no concurrent updates during reset
                {
                    _fpsSensor.Value = 0; // Resets FPS sensor
                    _currentFrameTimeSensor.Value = 0; // Resets frame time sensor
                    _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor
                    while (_frameTimes.TryDequeue(out _)) { } // Clears the frame time queue
                }
                Console.WriteLine(
                    "UpdateAsync detected no fullscreen app; all sensor values reset to 0"
                ); // Logs the reset
            }
            // Logs current sensor values for debugging and UI display
            Console.WriteLine(
                $"UpdateAsync - FPS: {_fpsSensor.Value}, Frame Time: {_currentFrameTimeSensor.Value}, 1% Low: {_onePercentLowFpsSensor.Value}, Frame Times Count: {_frameTimes.Count}"
            );
            await Task.CompletedTask; // Completes the async task without blocking
        }

        // Background task that continuously monitors fullscreen applications and manages FpsInspector for FPS tracking
        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested) // Loops until the plugin is closed
                {
                    uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false); // Checks for an active fullscreen app
                    Console.WriteLine($"Detected fullscreen process ID: {pid}"); // Logs the detected PID

                    if (pid != 0 && pid != _currentPid) // New fullscreen app detected
                    {
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel(); // Cancels any previous FpsInspector task
                            _monitoringCts.Dispose(); // Releases the old token source
                            Console.WriteLine("Stopped previous monitoring task for old PID"); // Logs the stop
                        }
                        _currentPid = pid; // Updates the tracked PID
                        lock (_updateLock) // Ensures no concurrent updates during reset
                        {
                            while (_frameTimes.TryDequeue(out _)) { } // Clears the frame time queue for the new app
                            _fpsSensor.Value = 0; // Resets FPS sensor
                            _currentFrameTimeSensor.Value = 0; // Resets frame time sensor
                            _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor
                        }
                        Console.WriteLine("Cleared frame time queue and reset sensors for new PID"); // Logs the reset
                        await StartMonitoringWithRetryAsync(pid, cancellationToken)
                            .ConfigureAwait(false); // Starts monitoring the new app
                    }
                    else if (pid == 0 && _currentPid != 0) // Fullscreen app closed (backup check)
                    {
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel(); // Stops the FpsInspector task
                            _monitoringCts.Dispose(); // Releases the token source
                            Console.WriteLine("Stopped monitoring task as fullscreen app closed"); // Logs the stop
                        }
                        _currentPid = 0; // Updates the tracked PID
                        lock (_updateLock) // Ensures no concurrent updates during reset
                        {
                            _fpsSensor.Value = 0; // Resets FPS sensor
                            _currentFrameTimeSensor.Value = 0; // Resets frame time sensor
                            _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor
                            while (_frameTimes.TryDequeue(out _)) { } // Clears the frame time queue
                        }
                        Console.WriteLine(
                            "No fullscreen app detected; all sensor values reset to 0"
                        ); // Logs the reset
                    }

                    // Checks if FpsInspector has stalled (no updates for over 15 seconds) and restarts it
                    if (
                        _currentPid != 0
                        && _lastUpdate != DateTime.MinValue
                        && (DateTime.Now - _lastUpdate).TotalSeconds > 15
                    )
                    {
                        Console.WriteLine("FpsInspector stalled (>15s no update); restarting..."); // Logs the stall detection
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel(); // Stops the stalled task
                            _monitoringCts.Dispose(); // Releases the token source
                        }
                        await StartMonitoringWithRetryAsync(_currentPid, cancellationToken)
                            .ConfigureAwait(false); // Restarts monitoring
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                        .ConfigureAwait(false); // Waits 1 second before the next PID check
                }
            }
            catch (TaskCanceledException) { } // Normal exception when the plugin is closed; silently ignored
            catch (Exception ex)
            {
                Console.WriteLine($"Monitoring loop error: {ex.Message}"); // Logs unexpected errors for troubleshooting
            }
            finally
            {
                _monitoringCts?.Cancel(); // Ensures FpsInspector stops on shutdown
                _monitoringCts?.Dispose(); // Releases the FpsInspector token source
                lock (_updateLock) // Ensures no concurrent updates during final reset
                {
                    _fpsSensor.Value = 0; // Resets FPS sensor
                    _currentFrameTimeSensor.Value = 0; // Resets frame time sensor
                    _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor
                    while (_frameTimes.TryDequeue(out _)) { } // Clears the frame time queue
                }
                _currentPid = 0; // Clears the PID
                Console.WriteLine("Monitoring loop ended; all sensors reset to 0, PID cleared"); // Logs the final cleanup
            }
        }

        // Attempts to start FpsInspector monitoring for a specified PID, with retry logic for transient failures
        private async Task StartMonitoringWithRetryAsync(
            uint pid,
            CancellationToken cancellationToken
        )
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++) // Loops up to the maximum retry attempts
            {
                try
                {
                    _monitoringCts = new CancellationTokenSource(); // Creates a new token source for this attempt
                    var fpsRequest = new FpsRequest { TargetPid = pid }; // Configures the request with the target PID
                    Console.WriteLine(
                        $"Starting FpsInspector for PID: {pid} (Attempt {attempt}/{RetryAttempts})"
                    ); // Logs the attempt

                    // Launches FpsInspector in a background task to continuously update sensor values
                    await Task.Run(
                            () =>
                                FpsInspector.StartForeverAsync(
                                    fpsRequest,
                                    (result) =>
                                    {
                                        if (result == null) // Checks for null results from FpsInspector
                                        {
                                            Console.WriteLine(
                                                "FpsInspector returned null; skipping update"
                                            ); // Logs the skip
                                            return;
                                        }

                                        DateTime now = DateTime.Now; // Captures the current time for throttling
                                        lock (_updateLock) // Synchronizes updates to prevent concurrent modifications
                                        {
                                            float fps = (float)result.Fps; // Extracts FPS from the result
                                            float frameTime = 1000.0f / fps; // Calculates frame time in milliseconds

                                            // Manages the frame time queue for 1% low FPS calculation
                                            if (_frameTimes.Count >= MaxFrameTimes)
                                            {
                                                if (
                                                    _frameTimes.TryDequeue(
                                                        out float removedFrameTime
                                                    )
                                                )
                                                    Console.WriteLine(
                                                        $"Removed oldest frame time: {removedFrameTime} ms"
                                                    ); // Logs queue management
                                            }
                                            _frameTimes.Enqueue(frameTime); // Adds the new frame time to the queue
                                            Console.WriteLine(
                                                $"Added frame time: {frameTime} ms, Current queue size: {_frameTimes.Count}"
                                            ); // Logs the addition

                                            // Calculates 1% low FPS with every frame (v1.0.7 fix)
                                            int count = _frameTimes.Count; // Gets the current queue size
                                            float calculatedOnePercentLowFps = 0; // Initializes the 1% low FPS value
                                            if (count >= MinFrameTimesForLowFps) // Ensures enough samples (v1.0.6 accuracy)
                                            {
                                                var orderedFrameTimes = _frameTimes
                                                    .OrderBy(ft => ft)
                                                    .ToList(); // Sorts frame times ascending
                                                int index = (int)(0.99 * (count - 1)); // Finds the 99th percentile index
                                                float ninetyNinthPercentileFrameTime =
                                                    orderedFrameTimes[index]; // Gets the frame time at that index
                                                calculatedOnePercentLowFps =
                                                    ninetyNinthPercentileFrameTime > 0
                                                        ? 1000.0f / ninetyNinthPercentileFrameTime
                                                        : 0; // Converts to FPS
                                                Console.WriteLine(
                                                    $"1% Low FPS calculation - Count: {count}, Index: {index}, 99th Percentile Frame Time: {ninetyNinthPercentileFrameTime} ms, Calculated 1% Low FPS: {calculatedOnePercentLowFps}"
                                                ); // Logs the calculation
                                            }
                                            _onePercentLowFpsSensor.Value =
                                                calculatedOnePercentLowFps; // Updates the 1% low FPS sensor

                                            // Throttles FPS and frame time updates to every 1 second (v1.0.5 stability)
                                            if ((now - _lastUpdate).TotalSeconds >= 1)
                                            {
                                                _fpsSensor.Value = fps; // Updates the FPS sensor
                                                _currentFrameTimeSensor.Value = frameTime; // Updates the frame time sensor
                                                _lastUpdate = now; // Updates the timestamp for throttling
                                                Console.WriteLine(
                                                    $"FpsInspector update - FPS: {fps}, Frame Time: {frameTime} ms, 1% Low FPS: {_onePercentLowFpsSensor.Value}"
                                                ); // Logs the update
                                            }
                                        }
                                    },
                                    _monitoringCts.Token // Passes the token for cancellation
                                ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    Console.WriteLine($"FpsInspector started for PID: {pid}"); // Confirms successful start
                    break; // Exits the retry loop on success
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"FpsInspector failed (attempt {attempt}/{RetryAttempts}): {ex.Message}"
                    ); // Logs the failure
                    if (attempt < RetryAttempts) // If more attempts remain
                    {
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false); // Waits before retrying
                    }
                    else // All retries exhausted
                    {
                        Console.WriteLine("All retries exhausted; resetting..."); // Logs the exhaustion
                        lock (_updateLock) // Ensures no concurrent updates
                        {
                            _fpsSensor.Value = 0; // Resets FPS sensor
                            _currentFrameTimeSensor.Value = 0; // Resets frame time sensor
                            _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor
                            while (_frameTimes.TryDequeue(out _)) { } // Clears the frame time queue
                        }
                        _currentPid = 0; // Clears the PID
                    }
                }
            }
        }

        // Detects the process ID of the currently active fullscreen application by comparing window and monitor dimensions
        private async Task<uint> GetActiveFullscreenProcessIdAsync()
        {
            return await Task.Run(() =>
                {
                    var hWnd = GetForegroundWindow(); // Gets the handle of the currently focused window
                    if (hWnd == IntPtr.Zero) // No window in focus
                    {
                        Console.WriteLine("No foreground window detected; returning PID 0"); // Logs the absence
                        return 0u; // Returns 0 to indicate no fullscreen app
                    }

                    if (!GetWindowRect(hWnd, out RECT windowRect)) // Attempts to get the window’s dimensions
                    {
                        Console.WriteLine("Failed to retrieve window dimensions; returning PID 0"); // Logs the failure
                        return 0u; // Returns 0 on error
                    }

                    var hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST); // Finds the nearest monitor
                    if (hMonitor == IntPtr.Zero) // No monitor detected
                    {
                        Console.WriteLine("No monitor detected; returning PID 0"); // Logs the failure
                        return 0u; // Returns 0 on error
                    }

                    var monitorInfo = new MONITORINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
                    }; // Initializes structure for monitor details
                    if (!GetMonitorInfo(hMonitor, ref monitorInfo)) // Fetches monitor dimensions
                    {
                        Console.WriteLine("Failed to retrieve monitor details; returning PID 0"); // Logs the failure
                        return 0u; // Returns 0 on error
                    }

                    var monitorRect = monitorInfo.rcMonitor; // Extracts the monitor’s bounding rectangle
                    Console.WriteLine(
                        $"Window dimensions: Left={windowRect.left}, Top={windowRect.top}, Right={windowRect.right}, Bottom={windowRect.bottom}"
                    ); // Logs window dimensions
                    Console.WriteLine(
                        $"Monitor dimensions: Left={monitorRect.left}, Top={monitorRect.top}, Right={monitorRect.right}, Bottom={monitorRect.bottom}"
                    ); // Logs monitor dimensions

                    // Checks if the window matches the monitor’s dimensions exactly, indicating fullscreen mode
                    if (
                        windowRect.left == monitorRect.left
                        && windowRect.top == monitorRect.top
                        && windowRect.right == monitorRect.right
                        && windowRect.bottom == monitorRect.bottom
                    )
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid); // Retrieves the PID of the fullscreen window
                        if (pid <= 4) // Excludes system processes (e.g., Idle PID 0, System PID 4)
                        {
                            Console.WriteLine(
                                $"Detected PID {pid} is a system process; returning PID 0"
                            ); // Logs the exclusion
                            return 0u; // Returns 0 for system processes
                        }
                        Console.WriteLine($"Fullscreen application detected with PID: {pid}"); // Logs the detection
                        return pid; // Returns the PID of the fullscreen app
                    }

                    Console.WriteLine(
                        "Foreground window is not in fullscreen mode; returning PID 0"
                    ); // Logs non-fullscreen status
                    return 0u; // Returns 0 if not fullscreen
                })
                .ConfigureAwait(false);
        }
    }
}
