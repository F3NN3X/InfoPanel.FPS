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
 * Version: 1.0.0
 * Author: F3NN3X
 * Description: An InfoPanel plugin that leverages PresentMonFps to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds, and 1% low FPS (99th percentile over 1000 frames). Updates every 1 second to align with InfoPanel’s default refresh rate. Includes retry logic and stall detection for robust operation.
 * Changelog:
 *   - v1.0.0 (Feb 20, 2025): Initial stable release.
 *     - Core Features: Detects fullscreen applications, monitors FPS in real-time, calculates current frame time, and computes 1% low FPS over a 1000-frame sliding window.
 *     - Stability Enhancements: Implements 3 retry attempts with a 1-second delay for FpsInspector errors (e.g., HRESULT 0x800700B7 - "Cannot create a file when that file already exists"), and includes 15-second stall detection with automatic monitoring restarts.
 *     - Simplifications: Removed earlier attempts at FPS smoothing, update throttling, and rounding to mitigate UI jitter, identified as an InfoPanel limitation rather than a plugin issue.
 * Note: A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
 */

namespace InfoPanel.Extras
{
    // Defines the FPS monitoring plugin, inheriting from BasePlugin to hook into InfoPanel’s framework for displaying performance metrics
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

        // Configuration constants defining operational limits and retry behavior
        private const int MaxFrameTimes = 1000; // Maximum number of frame time samples to keep in the queue for 1% low FPS calculation
        private const int RetryAttempts = 3; // Number of times to retry starting FpsInspector if it fails due to errors like file conflicts
        private const int RetryDelayMs = 1000; // Delay in milliseconds between retry attempts to give the system time to recover

        // Records the timestamp of the last successful FpsInspector update; used to detect stalls if no updates occur for too long
        private DateTime _lastUpdate = DateTime.MinValue;

        // Constructor that sets up the plugin with a unique identifier, name, and description for integration into InfoPanel
        public FpsPlugin()
            : base("fps-plugin", "FPS - PresentMonFPS", "Retrieves FPS details using PresentMonFPS. - v1.0.0")
        { }

        // Specifies that this plugin does not rely on an external configuration file; returns null to indicate no config is needed
        public override string? ConfigFilePath => null;

        // Sets the update interval to 1 second, aligning with InfoPanel’s default refresh rate to ensure smooth UI updates
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Initializes the plugin by starting a background task to monitor FPS; runs continuously until the plugin is closed
        public override void Initialize()
        {
            _cancellationTokenSource = new CancellationTokenSource(); // Creates a new token source to manage the monitoring loop
            _ = StartFPSMonitoringAsync(_cancellationTokenSource.Token); // Launches the monitoring task in the background without blocking
        }

        // Cleans up resources when the plugin is unloaded or closed, ensuring all tasks are stopped and memory is freed
        public override void Close()
        {
            _cancellationTokenSource?.Cancel(); // Signals the main monitoring loop to stop
            _monitoringCts?.Cancel(); // Signals the FpsInspector task to stop
            _cancellationTokenSource?.Dispose(); // Releases the main token source
            _monitoringCts?.Dispose(); // Releases the FpsInspector token source
        }

        // Registers the plugin’s sensors with InfoPanel’s UI by grouping them into a labeled container
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS"); // Creates a container with the label "FPS" to organize the sensors in the UI
            container.Entries.Add(_fpsSensor); // Adds the current FPS sensor to the container
            container.Entries.Add(_onePercentLowFpsSensor); // Adds the 1% low FPS sensor
            container.Entries.Add(_currentFrameTimeSensor); // Adds the current frame time sensor
            containers.Add(container); // Registers the container with InfoPanel for display
        }

        // Synchronous update method required by BasePlugin; not used as InfoPanel relies on UpdateAsync for asynchronous updates
        public override void Update() => throw new NotImplementedException();

        // Asynchronously updates sensor values every 1 second by polling FPS data from FpsInspector; runs on InfoPanel’s update schedule
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await GetFpsAsync().ConfigureAwait(false); // Fetches the latest FPS data without blocking the UI thread
            Console.WriteLine(
                $"UpdateAsync - FPS: {_fpsSensor.Value}, Frame Time: {_currentFrameTimeSensor.Value}, 1% Low: {_onePercentLowFpsSensor.Value}, Frame Times Count: {_frameTimes.Count}"
            ); // Logs current sensor values and frame time queue size for debugging
        }

        // Background task that continuously monitors fullscreen applications and manages the FpsInspector instance for FPS tracking
        private async Task StartFPSMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested) // Loops until the plugin is closed
                {
                    // Detects the process ID of the currently active fullscreen application
                    uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false);
                    Console.WriteLine($"Detected fullscreen process ID: {pid}"); // Logs the detected PID for debugging

                    if (pid != 0 && pid != _currentPid) // A new fullscreen app has been detected
                    {
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel(); // Cancels the previous FpsInspector task if it’s still running
                            _monitoringCts.Dispose(); // Cleans up the old token source
                            Console.WriteLine("Stopped previous monitoring task for the old PID");
                        }
                        _currentPid = pid; // Updates the tracked PID to the new fullscreen app
                        await StartMonitoringWithRetryAsync(pid, cancellationToken)
                            .ConfigureAwait(false); // Starts monitoring the new process with retry logic
                    }
                    else if (pid == 0) // No fullscreen app is currently active
                    {
                        _fpsSensor.Value = 0; // Resets FPS sensor to 0
                        _currentFrameTimeSensor.Value = 0; // Resets frame time sensor to 0
                        _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor to 0
                        Console.WriteLine("No fullscreen app detected; all sensor values reset to 0");
                    }

                    // Checks if FpsInspector has stalled (no updates for over 15 seconds) and restarts it if necessary
                    if (
                        _currentPid != 0
                        && _lastUpdate != DateTime.MinValue
                        && (DateTime.Now - _lastUpdate).TotalSeconds > 15
                    )
                    {
                        Console.WriteLine("FpsInspector stalled (no updates for >15s); initiating restart...");
                        if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
                        {
                            _monitoringCts.Cancel(); // Stops the stalled task
                            _monitoringCts.Dispose(); // Releases the stalled task’s token source
                            Console.WriteLine("Terminated stalled FpsInspector task");
                        }
                        await StartMonitoringWithRetryAsync(_currentPid, cancellationToken)
                            .ConfigureAwait(false); // Restarts monitoring for the current PID
                    }

                    await Task.Delay(UpdateInterval, cancellationToken).ConfigureAwait(false); // Waits 1 second before checking again, respecting the cancellation token
                }
            }
            catch (TaskCanceledException) { } // Normal exception when the plugin is closed; silently ignored
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error in monitoring loop: {ex.Message}"); // Logs any other errors for troubleshooting
            }
            finally
            {
                _monitoringCts?.Cancel(); // Ensures the FpsInspector task is stopped on shutdown
                _monitoringCts?.Dispose(); // Cleans up the FpsInspector token source
                _fpsSensor.Value = 0; // Resets FPS sensor to 0 on shutdown
                _currentFrameTimeSensor.Value = 0; // Resets frame time sensor to 0
                _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor to 0
            }
        }

        // Attempts to start FpsInspector monitoring for a specified process ID, with retry logic to handle transient failures like file access errors
        private async Task StartMonitoringWithRetryAsync(
            uint pid,
            CancellationToken cancellationToken
        )
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++) // Loops up to the maximum number of retry attempts
            {
                try
                {
                    _monitoringCts = new CancellationTokenSource(); // Creates a new token source for this monitoring attempt
                    var fpsRequest = new FpsRequest { TargetPid = pid }; // Configures the request with the target process ID
                    Console.WriteLine(
                        $"Starting FpsInspector for PID: {pid} (Attempt {attempt}/{RetryAttempts})"
                    ); // Logs the attempt for debugging

                    // Launches FpsInspector in a background task to continuously update sensor values
                    await Task.Run(
                            () =>
                                FpsInspector.StartForeverAsync(
                                    fpsRequest,
                                    (result) =>
                                    {
                                        float fps = (float)result.Fps; // Extracts the FPS value from the result
                                        float frameTime = 1000.0f / fps; // Calculates frame time in milliseconds (1000/FPS)
                                        _fpsSensor.Value = fps; // Updates the FPS sensor with the latest value
                                        _currentFrameTimeSensor.Value = frameTime; // Updates the frame time sensor
                                        _lastUpdate = DateTime.Now; // Updates the timestamp to mark this as a successful update
                                        Console.WriteLine(
                                            $"FpsInspector update - FPS: {fps}, Frame Time: {frameTime} ms"
                                        ); // Logs the new values

                                        // Manages the frame time queue for 1% low FPS calculation
                                        if (_frameTimes.Count >= MaxFrameTimes)
                                            _frameTimes.TryDequeue(out _); // Removes the oldest frame time if the queue is at capacity
                                        _frameTimes.Enqueue(frameTime); // Adds the new frame time to the queue
                                        Console.WriteLine(
                                            $"Added frame time: {frameTime} ms, Current queue size: {_frameTimes.Count}"
                                        ); // Logs the queue update

                                        // Calculates the 1% low FPS based on the 99th percentile of frame times
                                        int count = _frameTimes.Count; // Gets the current number of frame times
                                        int index = (int)(0.99 * (count - 1)); // Computes the index for the 99th percentile
                                        if (index >= 0 && index < count) // Ensures the index is valid
                                        {
                                            float ninetyNinthPercentileFrameTime = _frameTimes
                                                .OrderBy(ft => ft) // Sorts frame times in ascending order
                                                .Skip(index) // Skips to the 99th percentile position
                                                .FirstOrDefault(); // Takes the frame time at that position
                                            _onePercentLowFpsSensor.Value =
                                                ninetyNinthPercentileFrameTime > 0
                                                    ? 1000.0f / ninetyNinthPercentileFrameTime
                                                    : 0; // Converts the percentile frame time back to FPS
                                            Console.WriteLine(
                                                $"Calculated 1% Low FPS: {_onePercentLowFpsSensor.Value}"
                                            ); // Logs the result
                                        }
                                        else
                                        {
                                            _onePercentLowFpsSensor.Value = 0; // Sets to 0 if there aren’t enough samples
                                            Console.WriteLine("Not enough frame times to calculate 1% low FPS");
                                        }
                                    },
                                    _monitoringCts.Token // Passes the token to allow cancellation
                                ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    Console.WriteLine(
                        $"FpsInspector successfully started for PID: {pid} on attempt {attempt}"
                    ); // Confirms successful start
                    break; // Exits the retry loop since monitoring started successfully
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"FpsInspector failed on attempt {attempt}/{RetryAttempts}: {ex.Message}"
                    ); // Logs the failure reason
                    if (attempt < RetryAttempts) // If more attempts remain
                    {
                        Console.WriteLine($"Waiting {RetryDelayMs}ms before retrying...");
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false); // Pauses before the next attempt
                    }
                    else // Max retries reached; force a restart
                    {
                        Console.WriteLine($"Max retries reached for PID: {pid}; forcing a restart...");
                        _monitoringCts?.Cancel(); // Cancels the failed task
                        _monitoringCts?.Dispose(); // Disposes of the failed task’s token source
                        _monitoringCts = new CancellationTokenSource(); // Prepares a new token source for the restart

                        // Forces a restart of FpsInspector monitoring after exhausting retries
                        await Task.Run(
                                () =>
                                    FpsInspector.StartForeverAsync(
                                        new FpsRequest { TargetPid = pid },
                                        (result) =>
                                        {
                                            float fps = (float)result.Fps;
                                            float frameTime = 1000.0f / fps;
                                            _fpsSensor.Value = fps;
                                            _currentFrameTimeSensor.Value = frameTime;
                                            _lastUpdate = DateTime.Now;
                                            Console.WriteLine(
                                                $"Forced restart - FPS: {fps}, Frame Time: {frameTime} ms"
                                            );

                                            if (_frameTimes.Count >= MaxFrameTimes)
                                                _frameTimes.TryDequeue(out _);
                                            _frameTimes.Enqueue(frameTime);
                                            Console.WriteLine(
                                                $"Frame time added: {frameTime} ms, Queue size: {_frameTimes.Count}"
                                            );

                                            int count = _frameTimes.Count;
                                            int index = (int)(0.99 * (count - 1));
                                            if (index >= 0 && index < count)
                                            {
                                                float ninetyNinthPercentileFrameTime = _frameTimes
                                                    .OrderBy(ft => ft)
                                                    .Skip(index)
                                                    .FirstOrDefault();
                                                _onePercentLowFpsSensor.Value =
                                                    ninetyNinthPercentileFrameTime > 0
                                                        ? 1000.0f / ninetyNinthPercentileFrameTime
                                                        : 0;
                                                Console.WriteLine(
                                                    $"1% Low FPS: {_onePercentLowFpsSensor.Value}"
                                                );
                                            }
                                            else
                                            {
                                                _onePercentLowFpsSensor.Value = 0;
                                                Console.WriteLine("Not enough frame times for 1% low FPS");
                                            }
                                        },
                                        _monitoringCts.Token
                                    ),
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        Console.WriteLine($"FpsInspector forcibly restarted for PID: {pid} after max retries");
                    }
                }
            }
        }

        // Polls FPS data once per UpdateAsync cycle (every 1 second) to keep the FPS sensor current; other metrics updated via callback
        private async Task GetFpsAsync()
        {
            try
            {
                uint pid = await GetActiveFullscreenProcessIdAsync().ConfigureAwait(false); // Checks for an active fullscreen app
                if (pid == 0) // No fullscreen app found
                {
                    _fpsSensor.Value = 0; // Resets FPS sensor
                    _currentFrameTimeSensor.Value = 0; // Resets frame time sensor
                    _onePercentLowFpsSensor.Value = 0; // Resets 1% low FPS sensor
                    return; // Exits early since there’s nothing to monitor
                }

                var fpsRequest = new FpsRequest { TargetPid = pid }; // Prepares a request for a single FPS measurement
                var fpsResult = await FpsInspector.StartOnceAsync(fpsRequest).ConfigureAwait(false); // Performs a one-time FPS poll
                _fpsSensor.Value = (float)fpsResult.Fps; // Updates the FPS sensor with the latest value (frame time and 1% low updated separately)
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error polling FPS in GetFpsAsync: {ex.Message}"); // Logs any issues for debugging
            }
        }

        // Detects the process ID of the currently active fullscreen application by comparing window and monitor dimensions
        private async Task<uint> GetActiveFullscreenProcessIdAsync()
        {
            return await Task.Run(() =>
            {
                var hWnd = GetForegroundWindow(); // Gets the handle of the window currently in focus
                if (hWnd == IntPtr.Zero) // No window is in focus
                {
                    Console.WriteLine("No foreground window detected; cannot determine fullscreen status");
                    return 0u; // Returns 0 to indicate no fullscreen app
                }

                if (!GetWindowRect(hWnd, out RECT windowRect)) // Attempts to get the window’s dimensions
                {
                    Console.WriteLine("Failed to retrieve window dimensions; cannot check fullscreen status");
                    return 0u; // Returns 0 on failure
                }

                var hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST); // Finds the monitor closest to the window
                if (hMonitor == IntPtr.Zero) // No monitor found
                {
                    Console.WriteLine("No monitor detected for the foreground window");
                    return 0u; // Returns 0 if monitor detection fails
                }

                var monitorInfo = new MONITORINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
                }; // Initializes a structure to hold monitor details
                if (!GetMonitorInfo(hMonitor, ref monitorInfo)) // Fetches the monitor’s dimensions
                {
                    Console.WriteLine("Failed to retrieve monitor details; cannot verify fullscreen status");
                    return 0u; // Returns 0 on failure
                }

                var monitorRect = monitorInfo.rcMonitor; // Extracts the monitor’s bounding rectangle
                Console.WriteLine(
                    $"Window dimensions: Left={windowRect.left}, Top={windowRect.top}, Right={windowRect.right}, Bottom={windowRect.bottom}"
                ); // Logs window dimensions for debugging
                Console.WriteLine(
                    $"Monitor dimensions: Left={monitorRect.left}, Top={monitorRect.top}, Right={monitorRect.right}, Bottom={monitorRect.bottom}"
                ); // Logs monitor dimensions

                // Checks if the window’s dimensions exactly match the monitor’s, indicating fullscreen mode
                if (
                    windowRect.left == monitorRect.left
                    && windowRect.top == monitorRect.top
                    && windowRect.right == monitorRect.right
                    && windowRect.bottom == monitorRect.bottom
                )
                {
                    GetWindowThreadProcessId(hWnd, out uint pid); // Retrieves the process ID of the fullscreen window
                    Console.WriteLine($"Fullscreen application detected with PID: {pid}");
                    return pid; // Returns the PID of the fullscreen app
                }

                Console.WriteLine("Foreground window is not in fullscreen mode");
                return 0u; // Returns 0 if the window isn’t fullscreen
            })
                .ConfigureAwait(false);
        }
    }
}