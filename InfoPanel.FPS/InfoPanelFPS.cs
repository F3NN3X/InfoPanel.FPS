using InfoPanel.FPS.Constants;
using InfoPanel.FPS.Interfaces;
using InfoPanel.FPS.Models;
using InfoPanel.FPS.Services;
using InfoPanel.Plugins;

/*
 * Plugin: InfoPanel.FPS
 * Version: 1.1.6
 * Author: F3NN3X
 * Description: An optimized InfoPanel plugin using RTSS shared memory to monitor game performance. Reads FPS directly from RivaTuner Statistics Server for pixel-perfect accuracy and anti-cheat compatibility. Tracks FPS, frame time, 1% low FPS over time, window title, display resolution, refresh rate, and GPU name in the UI. Features thread-safe sensor updates and universal game support without hardcoded logic.
 * Changelog (Recent):
 *   - v1.1.6 (October 15, 2025): Thread safety fixes and RTSS improvements.
 *     - Fixed collection modification crash with thread-safe sensor updates using lock synchronization.
 *     - Direct FPS reading from RTSS Frames field (offset 276) for pixel-perfect accuracy.
 *     - Removed all hardcoded game logic for universal compatibility.
 *     - Enhanced title caching with PID-based filtering and alt-tab support.
 *     - Service-based architecture with specialized monitoring services.
 *     - Clean codebase with zero build warnings.
 *   - v1.1.0 (September 19, 2025): Architectural refactoring and reliability improvements.
 *     - Major refactoring using C# best practices with service-based architecture.
 *     - Split monolithic code into dedicated services: PerformanceMonitoringService, WindowDetectionService, SystemInformationService, SensorManagementService.
 *     - Introduced dependency injection pattern with service interfaces for better testability and maintainability.
 *     - Added comprehensive data models and constants for better code organization.
 *     - Fixed self-detection issue: plugin now excludes InfoPanel's own process and system overlays.
 *     - Implemented primary display filtering: only monitors fullscreen apps on the main display.
 *     - Enhanced cleanup detection: dual-detection system ensures reliable app closure detection.
 *     - Improved state management: simplified monitoring flags prevent rapid switching issues.
 *     - Added robust error handling and comprehensive logging throughout the application.
 *   - v1.0.17 (July 12, 2025): Improved resolution display format.
 *     - Changed resolution format to include spaces (e.g., "3840 x 2160" instead of "3840x2160") for better readability.
 *     - Updated all instances of resolution display for consistency.
 *   - v1.0.16 (June 3, 2025): Added GPU Name sensor.
 *     - New PluginText sensor displays the name of the system's graphics card in the UI.
 *     - Added System.Management reference for WMI queries to detect GPU information.
 *     - Ensured all dependencies are in root folder without subdirectories.
 * Note: Full history in CHANGELOG.md. A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
 */

namespace InfoPanel.FPS
{
    /// <summary>
    /// Main plugin class that coordinates between services to monitor fullscreen application performance.
    /// Uses dependency injection pattern with dedicated services for better separation of concerns.
    /// </summary>
    public class FpsPlugin : BasePlugin, IDisposable
    {
        private readonly IPerformanceMonitoringService _performanceService;
        private readonly IWindowDetectionService _windowDetectionService;
        private readonly ISystemInformationService _systemInfoService;
        private readonly ISensorManagementService _sensorService;

        private readonly MonitoringState _currentState = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed;

        /// <summary>
        /// Initializes the plugin with its metadata and creates service instances.
        /// </summary>
        public FpsPlugin()
            : base(
                "fps-plugin",
                "InfoPanel.FPS",
                "Simple FPS plugin showing FPS, frame time, 1% low FPS, window title, resolution, and refresh rate using RTSS"
            )
        {
            // Critical: Add early logging for debugging
            System.Diagnostics.Debug.WriteLine("=== InfoPanel.FPS Constructor Called ===");
            Console.WriteLine("=== InfoPanel.FPS Constructor Called ===");

            // Initialize services - in a real DI scenario, these would be injected
            _performanceService = new PerformanceMonitoringService();
            _windowDetectionService = new WindowDetectionService();
            _systemInfoService = new SystemInformationService();
            _sensorService = new SensorManagementService();

            // Subscribe to service events
            _performanceService.MetricsUpdated += OnPerformanceMetricsUpdated;
            _windowDetectionService.WindowChanged += OnWindowChanged;

            Console.WriteLine("FpsPlugin initialized with all services");
        }

        /// <summary>
        /// No configuration file is used.
        /// </summary>
        public override string? ConfigFilePath => null;

        /// <summary>
        /// Updates occur every 1 second for stable UI refreshes.
        /// </summary>
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(MonitoringConstants.UiUpdateIntervalSeconds);

        /// <summary>
        /// Initializes the plugin and starts monitoring services.
        /// </summary>
        public override void Initialize()
        {
            try
            {
                Console.WriteLine("=== FpsPlugin Initialize() called ===");

                _cancellationTokenSource = new CancellationTokenSource();

                // Initialize system information
                _currentState.System = _systemInfoService.GetSystemInformation();

                // Start window detection service
                _windowDetectionService.StartMonitoring();

                // Start continuous monitoring loop (like the original implementation)
                _ = Task.Run(async () => await StartContinuousMonitoringAsync(_cancellationTokenSource.Token).ConfigureAwait(false));

                // Perform initial window check
                _ = Task.Run(async () => await PerformInitialWindowCheckAsync().ConfigureAwait(false));

                Console.WriteLine("FpsPlugin initialization completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during plugin initialization: {ex}");
            }
        }

        /// <summary>
        /// Registers sensors with InfoPanel's UI container.
        /// </summary>
        public override void Load(List<IPluginContainer> containers)
        {
            try
            {
                Console.WriteLine("=== FpsPlugin Load() called ===");

                _sensorService.CreateAndRegisterSensors(containers);
                
                // Update sensors with initial system information
                _sensorService.UpdateSystemSensors(_currentState.System);
                
                Console.WriteLine("Sensors loaded and registered with InfoPanel");

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading sensors: {ex}");
            }
        }

        /// <summary>
        /// Not implemented; UpdateAsync is used instead.
        /// </summary>
        public override void Update() => throw new NotImplementedException();

        /// <summary>
        /// Periodic update method that updates sensors with current state.
        /// Also performs cleanup detection like the original version.
        /// </summary>
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Traditional window detection and monitoring logic
                var currentWindow = _windowDetectionService.GetCurrentFullscreenWindow();
                uint pid = currentWindow?.ProcessId ?? 0;
                
                // Update current window information if we have a valid window
                if (pid != 0 && currentWindow != null)
                {
                    _currentState.Window = currentWindow;
                }
                
                // If we found a fullscreen app but aren't monitoring it, start monitoring
                if (pid != 0 && !_performanceService.IsMonitoring)
                {
                    Console.WriteLine($"UpdateAsync detected new fullscreen app (PID: {pid}); starting DXGI monitoring");
                    _currentState.IsMonitoring = true;
                    await StartMonitoringAsync(pid).ConfigureAwait(false);
                }
                // If PID changed from what we're monitoring, switch to new process
                else if (pid != 0 && _performanceService.IsMonitoring && _currentState.Window.ProcessId != pid)
                {
                    Console.WriteLine($"UpdateAsync detected PID change: monitoring {_currentState.Window.ProcessId} but found {pid}; switching monitoring");
                    await StopMonitoringAsync().ConfigureAwait(false);
                    _currentState.IsMonitoring = true;
                    await StartMonitoringAsync(pid).ConfigureAwait(false);
                }
                // If no fullscreen app detected but we're still monitoring, check if RTSS monitored process still exists
                else if (pid == 0 && _performanceService.IsMonitoring)
                {
                    // Check if the RTSS monitored process still exists (backgrounded/alt-tabbed)
                    // NOTE: Must check the PID that RTSS is monitoring, not the current window PID!
                    uint monitoredPid = _currentState.Performance.MonitoredProcessId;
                    bool monitoredProcessExists = false;
                    
                    if (monitoredPid > 0)
                    {
                        try
                        {
                            using var process = System.Diagnostics.Process.GetProcessById((int)monitoredPid);
                            monitoredProcessExists = !process.HasExited;
                        }
                        catch (ArgumentException)
                        {
                            monitoredProcessExists = false;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"UpdateAsync: Error checking if RTSS monitored process {monitoredPid} exists: {ex}");
                            monitoredProcessExists = false;
                        }
                    }

                    if (monitoredProcessExists)
                    {
                        Console.WriteLine($"UpdateAsync: RTSS monitored process {monitoredPid} still exists (backgrounded/alt-tabbed), continuing monitoring");
                        // Process still exists - keep monitoring even though not fullscreen
                        // Don't update _currentState.Window since we don't have new window data
                    }
                    else
                    {
                        Console.WriteLine($"UpdateAsync: RTSS monitored process {monitoredPid} no longer exists, stopping monitoring");
                        await StopMonitoringAsync().ConfigureAwait(false);
                    }
                }
                // Check if monitored process still exists (additional safety check)
                else if (_performanceService.IsMonitoring && _currentState.Window.ProcessId != 0)
                {
                    bool processExists = false;
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById((int)_currentState.Window.ProcessId);
                        processExists = !process.HasExited;
                    }
                    catch (ArgumentException)
                    {
                        processExists = false;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UpdateAsync: Error checking process {_currentState.Window.ProcessId}: {ex}");
                        processExists = false;
                    }

                    if (!processExists)
                    {
                        Console.WriteLine($"UpdateAsync: Monitored process {_currentState.Window.ProcessId} no longer exists; stopping monitoring");
                        await StopMonitoringAsync().ConfigureAwait(false);
                    }
                }

                // Update sensors with current state
                _sensorService.UpdateSensors(_currentState);

                _currentState.LastUpdate = DateTime.Now;

                // Log current state for debugging
                Console.WriteLine($"UpdateAsync - Monitoring: {_performanceService.IsMonitoring}, " +
                                $"Window PID: {_currentState.Window.ProcessId}, " +
                                $"Detected PID: {pid}, " +
                                $"FPS: {_currentState.Performance.Fps:F1}, " +
                                $"Title: {_currentState.Window.WindowTitle}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in UpdateAsync: {ex}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Determines if a process is a system/non-gaming process that should be ignored.
        /// </summary>
        private bool IsSystemProcess(string processName)
        {
            var systemProcesses = new[]
            {
                "explorer", "dwm", "winlogon", "csrss", "services", "svchost", "lsass", "smss", "wininit",
                "taskmgr", "taskhostw", "rundll32", "dllhost", "conhost", "fontdrvhost", "WUDFHost",
                "spoolsv", "RuntimeBroker", "SearchIndexer", "audiodg", "SecurityHealthSystray",
                "Adobe Desktop Service", "TextInputHost", "WaveLink", "Photoshop", "Files", "InfoPanel",
                "Code", "notepad", "calc", "cmd", "powershell", "pwsh", "WindowsTerminal", "devenv"
            };
            
            return systemProcesses.Any(sp => processName.Contains(sp, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Closes the plugin by disposing resources.
        /// </summary>
        public override void Close() => Dispose();

        /// <summary>
        /// Performs initial window check asynchronously without blocking Initialize.
        /// </summary>
        private async Task PerformInitialWindowCheckAsync()
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false); // Small delay for stabilization
                
                var currentWindow = _windowDetectionService.GetCurrentFullscreenWindow();
                if (currentWindow?.IsValid == true)
                {
                    _currentState.Window = currentWindow;
                    await StartMonitoringAsync(currentWindow.ProcessId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in initial window check: {ex}");
            }
        }

        /// <summary>
        /// Continuously monitors fullscreen apps and manages monitoring lifecycle.
        /// This replicates the original StartFPSMonitoringAsync logic.
        /// </summary>
        private async Task StartContinuousMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var currentWindow = _windowDetectionService.GetCurrentFullscreenWindow();
                    var systemInfo = _systemInfoService.GetSystemInformation();
                    uint pid = currentWindow?.ProcessId ?? 0;

                    Console.WriteLine($"Continuous monitoring check - PID: {pid}, " +
                                    $"Title: {currentWindow?.WindowTitle ?? "None"}, " +
                                    $"IsMonitoring: {_performanceService.IsMonitoring}, " +
                                    $"CurrentWindowValid: {currentWindow?.IsValid}, " +
                                    $"IsFullscreen: {currentWindow?.IsFullscreen}");

                    if (pid != 0 && !_performanceService.IsMonitoring)
                    {
                        Console.WriteLine($"STARTING monitoring for new fullscreen app PID {pid}");
                        // Start monitoring new fullscreen app
                        _currentState.Window = currentWindow!;
                        _currentState.System = systemInfo;
                        _currentState.IsMonitoring = true;
                        
                        // Reset performance metrics like the old version
                        _currentState.Performance = new PerformanceMetrics();
                        await StartMonitoringAsync(pid).ConfigureAwait(false);
                    }
                    else if (pid == 0 && _performanceService.IsMonitoring)
                    {
                        // No fullscreen window detected, but check if RTSS monitored process still exists
                        // (e.g., user alt-tabbed but RTSS still monitors the background process)
                        // NOTE: Must check the PID that RTSS is monitoring, not the current window PID!
                        uint monitoredPid = _currentState.Performance.MonitoredProcessId;
                        bool monitoredProcessExists = false;
                        
                        if (monitoredPid > 0)
                        {
                            try
                            {
                                using var process = System.Diagnostics.Process.GetProcessById((int)monitoredPid);
                                monitoredProcessExists = !process.HasExited;
                            }
                            catch (ArgumentException)
                            {
                                // Process not found
                                monitoredProcessExists = false;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error checking if RTSS monitored process {monitoredPid} exists: {ex}");
                                monitoredProcessExists = false;
                            }
                        }

                        if (monitoredProcessExists)
                        {
                            Console.WriteLine($"RTSS monitored process {monitoredPid} still exists (backgrounded/alt-tabbed), continuing monitoring");
                            // Process still exists - keep monitoring even though it's not fullscreen
                            // RTSS will continue to report FPS data
                            _currentState.System = systemInfo;
                        }
                        else
                        {
                            Console.WriteLine($"RTSS monitored process {monitoredPid} no longer exists, stopping monitoring");
                            // Process actually closed - stop monitoring
                            await StopMonitoringAsync().ConfigureAwait(false);
                            _currentState.System = systemInfo; // Update system info even when not monitoring
                        }
                    }
                    else if (pid != 0 && _performanceService.IsMonitoring)
                    {
                        Console.WriteLine($"App still fullscreen, checking if same process (Current: {_currentState.Window.ProcessId})");
                        
                        // Check if the currently monitored process still exists
                        bool currentProcessExists = false;
                        try
                        {
                            using var process = System.Diagnostics.Process.GetProcessById((int)_currentState.Window.ProcessId);
                            currentProcessExists = !process.HasExited;
                        }
                        catch (ArgumentException)
                        {
                            // Process not found
                            currentProcessExists = false;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error checking if process {_currentState.Window.ProcessId} exists: {ex}");
                            currentProcessExists = false;
                        }

                        if (!currentProcessExists)
                        {
                            Console.WriteLine($"Currently monitored process {_currentState.Window.ProcessId} no longer exists, stopping monitoring");
                            await StopMonitoringAsync().ConfigureAwait(false);
                            _currentState.System = systemInfo;
                        }
                        else if (_currentState.Window.ProcessId == currentWindow!.ProcessId && 
                            _currentState.Window.WindowTitle != currentWindow.WindowTitle)
                        {
                            Console.WriteLine($"Same process, updating window title from '{_currentState.Window.WindowTitle}' to '{currentWindow.WindowTitle}'");
                            _currentState.Window.WindowTitle = currentWindow.WindowTitle;
                        }
                        else if (_currentState.Window.ProcessId != currentWindow.ProcessId)
                        {
                            Console.WriteLine($"DIFFERENT PROCESS DETECTED: Current={_currentState.Window.ProcessId}, New={currentWindow.ProcessId}. Switching monitoring.");
                            // Different process - need to switch monitoring
                            await StopMonitoringAsync().ConfigureAwait(false);
                            _currentState.Window = currentWindow;
                            _currentState.IsMonitoring = true;
                            await StartMonitoringAsync(currentWindow.ProcessId).ConfigureAwait(false);
                        }
                        _currentState.System = systemInfo;
                    }
                    else if (pid == 0 && !_performanceService.IsMonitoring)
                    {
                        // No fullscreen app detected, update system info but keep sensors clear
                        _currentState.System = systemInfo;
                        _currentState.Window = new WindowInformation { WindowTitle = "-" }; // Match old version
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException) 
            {
                // Expected on cancellation
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Continuous monitoring loop error: {ex}");
            }
        }

        /// <summary>
        /// Starts performance monitoring for the specified process.
        /// </summary>
        private async Task StartMonitoringAsync(uint processId)
        {
            try
            {
                // Performance service has its own guard to prevent duplicate starts
                // Don't stop/reset here as it clears the frame time buffer needed for 1% low calculation
                Console.WriteLine($"StartMonitoringAsync: Starting monitoring for process ID: {processId}");

                // Start performance monitoring (service will skip if already monitoring same PID)
                var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
                
                Console.WriteLine($"StartMonitoringAsync: Calling _performanceService.StartMonitoringAsync for PID: {processId}");
                await _performanceService.StartMonitoringAsync(processId, cancellationToken).ConfigureAwait(false);

                Console.WriteLine($"StartMonitoringAsync: Performance service IsMonitoring: {_performanceService.IsMonitoring}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"StartMonitoringAsync: Error starting monitoring for PID {processId}: {ex}");
                _currentState.IsMonitoring = false;
            }
        }

        /// <summary>
        /// Stops performance monitoring and resets state.
        /// </summary>
        private async Task StopMonitoringAsync()
        {
            try
            {
                Console.WriteLine("Stopping performance monitoring");

                _performanceService.StopMonitoring();
                _currentState.IsMonitoring = false;
                
                // Reset state - clear window info and performance
                _currentState.Window = new WindowInformation { WindowTitle = "-" };
                _currentState.Performance = new PerformanceMetrics(); // Reset to 0s

                // Update sensors immediately to show reset state
                _sensorService.UpdateSensors(_currentState);

                Console.WriteLine("Performance monitoring stopped and state reset");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping monitoring: {ex}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Handles performance metrics updates from the monitoring service.
        /// </summary>
        private void OnPerformanceMetricsUpdated(PerformanceMetrics metrics)
        {
            try
            {
                _currentState.Performance = metrics;
                _sensorService.UpdatePerformanceSensors(metrics);
                
                Console.WriteLine($"Performance metrics updated - FPS: {metrics.Fps:F1}, " +
                                $"Frame Time: {metrics.FrameTime:F2}ms, " +
                                $"1% Low: {metrics.OnePercentLowFps:F1}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling performance metrics update: {ex}");
            }
        }

        /// <summary>
        /// Handles window change events from the detection service.
        /// </summary>
        private void OnWindowChanged(WindowInformation windowInfo)
        {
            try
            {
                Console.WriteLine($"Window changed - PID: {windowInfo.ProcessId}, " +
                                $"Title: {windowInfo.WindowTitle}, " +
                                $"Fullscreen: {windowInfo.IsFullscreen}");

                // Update sensor immediately
                _sensorService.UpdateWindowSensor(windowInfo);

                // The actual monitoring logic will be handled in UpdateAsync
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling window change: {ex}");
            }
        }

        /// <summary>
        /// Disposes resources, both managed and unmanaged.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // Unsubscribe from events
                        _performanceService.MetricsUpdated -= OnPerformanceMetricsUpdated;
                        _windowDetectionService.WindowChanged -= OnWindowChanged;

                        // Stop and dispose services
                        _performanceService?.Dispose();
                        _windowDetectionService?.Dispose();

                        // Reset sensors
                        _sensorService?.ResetSensors();

                        // Cancel any ongoing operations
                        _cancellationTokenSource?.Cancel();
                        _cancellationTokenSource?.Dispose();

                        Console.WriteLine("FpsPlugin disposed successfully");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during plugin disposal: {ex}");
                    }
                }

                _disposed = true;
            }
        }



        /// <summary>
        /// Public entry point for IDisposable.Dispose.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer ensures cleanup if Dispose isn't called.
        /// </summary>
        ~FpsPlugin()
        {
            Dispose(false);
        }
    }
}