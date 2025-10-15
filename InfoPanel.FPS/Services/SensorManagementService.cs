using InfoPanel.FPS.Constants;
using InfoPanel.FPS.Interfaces;
using InfoPanel.FPS.Models;
using InfoPanel.Plugins;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Service responsible for managing InfoPanel sensors and their updates.
    /// Handles creation, registration, and updating of all performance and system sensors.
    /// </summary>
    public class SensorManagementService : ISensorManagementService
    {
        private readonly PluginSensor _fpsSensor;
        private readonly PluginSensor _onePercentLowFpsSensor;
        private readonly PluginSensor _currentFrameTimeSensor;
        private readonly PluginText _windowTitleSensor;
        private readonly PluginText _resolutionSensor;
        private readonly PluginSensor _refreshRateSensor;
        private readonly PluginText _gpuNameSensor;
        
        /// <summary>
        /// Cached window title to prevent flickering when window validation temporarily fails.
        /// Window.IsValid can become false during normal operation (e.g., when fullscreen state briefly changes,
        /// during alt-tab, or window events) even though monitoring is still active and FPS is being captured.
        /// By caching the last known good window title, we prevent the sensor from showing "Nothing to capture"
        /// during these temporary validation failures.
        /// </summary>
        private string _lastValidWindowTitle = string.Empty;

        /// <summary>
        /// Initializes a new instance of the SensorManagementService.
        /// </summary>
        public SensorManagementService()
        {
            // Initialize performance sensors
            _fpsSensor = new PluginSensor(
                SensorConstants.FpsSensorId,
                SensorConstants.FpsSensorDisplayName,
                0,
                SensorConstants.FpsUnit
            );

            _onePercentLowFpsSensor = new PluginSensor(
                SensorConstants.OnePercentLowFpsSensorId,
                SensorConstants.OnePercentLowFpsSensorDisplayName,
                0,
                SensorConstants.FpsUnit
            );

            _currentFrameTimeSensor = new PluginSensor(
                SensorConstants.CurrentFrameTimeSensorId,
                SensorConstants.CurrentFrameTimeSensorDisplayName,
                0,
                SensorConstants.FrameTimeUnit
            );

            // Initialize text sensors
            _windowTitleSensor = new PluginText(
                SensorConstants.WindowTitleSensorId,
                SensorConstants.WindowTitleSensorDisplayName,
                SensorConstants.DefaultWindowTitle
            );

            _resolutionSensor = new PluginText(
                SensorConstants.ResolutionSensorId,
                SensorConstants.ResolutionSensorDisplayName,
                SensorConstants.DefaultResolution
            );

            _refreshRateSensor = new PluginSensor(
                SensorConstants.RefreshRateSensorId,
                SensorConstants.RefreshRateSensorDisplayName,
                0,
                SensorConstants.RefreshRateUnit
            );

            _gpuNameSensor = new PluginText(
                SensorConstants.GpuNameSensorId,
                SensorConstants.GpuNameSensorDisplayName,
                SensorConstants.DefaultGpuName
            );

            Console.WriteLine("Sensor management service initialized with all sensors");
        }

        /// <summary>
        /// Creates and registers all sensors with the provided container.
        /// </summary>
        /// <param name="containers">List of plugin containers to add sensors to.</param>
        public void CreateAndRegisterSensors(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            
            // Add all sensors to the container
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_onePercentLowFpsSensor);
            container.Entries.Add(_currentFrameTimeSensor);
            container.Entries.Add(_windowTitleSensor);
            container.Entries.Add(_resolutionSensor);
            container.Entries.Add(_refreshRateSensor);
            container.Entries.Add(_gpuNameSensor);

            containers.Add(container);
            
            Console.WriteLine($"Registered {container.Entries.Count} sensors in FPS container");
        }

        /// <summary>
        /// Updates all sensors with the current monitoring state.
        /// </summary>
        /// <param name="state">Current monitoring state containing all metrics.</param>
        public void UpdateSensors(MonitoringState state)
        {
            try
            {
                // Update performance sensors
                if (state.Performance.IsValid && state.IsMonitoring)
                {
                    _fpsSensor.Value = state.Performance.Fps;
                    _currentFrameTimeSensor.Value = state.Performance.FrameTime;
                    _onePercentLowFpsSensor.Value = state.Performance.OnePercentLowFps;
                }
                else
                {
                    // Reset performance sensors when not monitoring
                    _fpsSensor.Value = 0;
                    _currentFrameTimeSensor.Value = 0;
                    _onePercentLowFpsSensor.Value = 0;
                }

                // Update window information with caching to prevent flickering
                // ONLY show window title for the process that RTSS is actively monitoring
                if (state.IsMonitoring)
                {
                    // Get the PID that RTSS is actually monitoring (providing FPS data)
                    uint monitoredPid = state.Performance.MonitoredProcessId;
                    
                    // DETAILED DEBUG: Show all values for diagnosis
                    Console.WriteLine($"[TITLE CACHE DEBUG] MonitoredPID: {monitoredPid}, WindowPID: {state.Window.ProcessId}, WindowTitle: '{state.Window.WindowTitle}', CachedTitle: '{_lastValidWindowTitle}'");
                    
                    // Update cached title ONLY if window PID matches RTSS monitored PID
                    if (monitoredPid > 0 && 
                        state.Window.ProcessId == monitoredPid && 
                        !string.IsNullOrWhiteSpace(state.Window.WindowTitle))
                    {
                        if (_lastValidWindowTitle != state.Window.WindowTitle)
                        {
                            Console.WriteLine($"Window title cached for RTSS PID {monitoredPid}: '{state.Window.WindowTitle}' (was '{_lastValidWindowTitle}')");
                            _lastValidWindowTitle = state.Window.WindowTitle;
                        }
                    }
                    else
                    {
                        // Log why caching failed
                        if (monitoredPid == 0)
                            Console.WriteLine("[TITLE CACHE] Not caching: MonitoredPID is 0 (RTSS not hooked yet)");
                        else if (state.Window.ProcessId != monitoredPid)
                            Console.WriteLine($"[TITLE CACHE] Not caching: Window PID {state.Window.ProcessId} != Monitored PID {monitoredPid}");
                        else if (string.IsNullOrWhiteSpace(state.Window.WindowTitle))
                            Console.WriteLine($"[TITLE CACHE] Not caching: Window title is empty/whitespace");
                    }
                    
                    // Use cached title if we have one, otherwise show NoCapture
                    _windowTitleSensor.Value = !string.IsNullOrEmpty(_lastValidWindowTitle) 
                        ? _lastValidWindowTitle 
                        : SensorConstants.NoCapture;
                    
                    // Debug log for mismatched PIDs
                    if (monitoredPid > 0 && state.Window.ProcessId > 0 && state.Window.ProcessId != monitoredPid)
                    {
                        Console.WriteLine($"PID mismatch: Window PID {state.Window.ProcessId} ({state.Window.WindowTitle}) != RTSS monitored PID {monitoredPid}, ignoring window title");
                    }
                }
                else
                {
                    // When not monitoring, reset cache and show default
                    _lastValidWindowTitle = string.Empty;
                    _windowTitleSensor.Value = SensorConstants.DefaultWindowTitle;
                }

                // Update system information (always available)
                _resolutionSensor.Value = state.System.Resolution;
                _refreshRateSensor.Value = state.System.RefreshRate;
                _gpuNameSensor.Value = state.System.GpuName;

                Console.WriteLine($"Sensors updated - FPS: {_fpsSensor.Value}, " +
                                $"Frame Time: {_currentFrameTimeSensor.Value}ms, " +
                                $"1% Low: {_onePercentLowFpsSensor.Value}, " +
                                $"Title: {_windowTitleSensor.Value}, " +
                                $"Resolution: {_resolutionSensor.Value}, " +
                                $"Refresh: {_refreshRateSensor.Value}Hz");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating sensors: {ex}");
            }
        }

        /// <summary>
        /// Resets all sensors to their default values.
        /// </summary>
        public void ResetSensors()
        {
            try
            {
                // Reset performance sensors
                _fpsSensor.Value = 0;
                _onePercentLowFpsSensor.Value = 0;
                _currentFrameTimeSensor.Value = 0;

                // Reset information sensors to defaults
                _windowTitleSensor.Value = SensorConstants.DefaultWindowTitle;
                _resolutionSensor.Value = SensorConstants.DefaultResolution;
                _refreshRateSensor.Value = 0;
                _gpuNameSensor.Value = SensorConstants.DefaultGpuName;
                
                // Clear cached window title
                _lastValidWindowTitle = string.Empty;

                Console.WriteLine("All sensors reset to default values");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resetting sensors: {ex}");
            }
        }

        /// <summary>
        /// Updates only the performance sensors with new metrics.
        /// </summary>
        /// <param name="metrics">Performance metrics to apply.</param>
        public void UpdatePerformanceSensors(PerformanceMetrics metrics)
        {
            try
            {
                if (metrics.IsValid)
                {
                    _fpsSensor.Value = metrics.Fps;
                    _currentFrameTimeSensor.Value = metrics.FrameTime;
                    _onePercentLowFpsSensor.Value = metrics.OnePercentLowFps;
                    
                    Console.WriteLine($"Performance sensors updated - FPS: {metrics.Fps:F1}, " +
                                    $"Frame Time: {metrics.FrameTime:F2}ms, " +
                                    $"1% Low: {metrics.OnePercentLowFps:F1}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating performance sensors: {ex}");
            }
        }

        /// <summary>
        /// Updates only the window information sensor.
        /// </summary>
        /// <param name="windowInfo">Window information to apply.</param>
        public void UpdateWindowSensor(WindowInformation windowInfo)
        {
            try
            {
                if (windowInfo.IsValid)
                {
                    var newTitle = !string.IsNullOrWhiteSpace(windowInfo.WindowTitle) 
                        ? windowInfo.WindowTitle 
                        : "Untitled";
                    
                    // Preserve existing good titles - don't overwrite with generic defaults
                    if (newTitle != "Untitled" || _windowTitleSensor.Value == SensorConstants.NoCapture || _windowTitleSensor.Value == SensorConstants.DefaultWindowTitle)
                    {
                        _windowTitleSensor.Value = newTitle;
                        Console.WriteLine($"Window sensor updated - Title: {_windowTitleSensor.Value}");
                    }
                    else
                    {
                        Console.WriteLine($"Window sensor preserved existing title - keeping: {_windowTitleSensor.Value}");
                    }
                }
                else
                {
                    // Reset to NoCapture when window becomes invalid
                    _windowTitleSensor.Value = SensorConstants.NoCapture;
                    Console.WriteLine($"Window sensor reset - Title: {_windowTitleSensor.Value}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating window sensor: {ex}");
            }
        }

        /// <summary>
        /// Updates only the system information sensors.
        /// </summary>
        /// <param name="systemInfo">System information to apply.</param>
        public void UpdateSystemSensors(SystemInformation systemInfo)
        {
            try
            {
                _resolutionSensor.Value = systemInfo.Resolution;
                _refreshRateSensor.Value = systemInfo.RefreshRate;
                _gpuNameSensor.Value = systemInfo.GpuName;
                
                Console.WriteLine($"System sensors updated - Resolution: {systemInfo.Resolution}, " +
                                $"Refresh: {systemInfo.RefreshRate}Hz, GPU: {systemInfo.GpuName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating system sensors: {ex}");
            }
        }

        /// <summary>
        /// Gets the current values of all sensors for debugging purposes.
        /// </summary>
        /// <returns>A dictionary containing sensor IDs and their current values.</returns>
        public Dictionary<string, object> GetSensorValues()
        {
            return new Dictionary<string, object>
            {
                [SensorConstants.FpsSensorId] = _fpsSensor.Value,
                [SensorConstants.OnePercentLowFpsSensorId] = _onePercentLowFpsSensor.Value,
                [SensorConstants.CurrentFrameTimeSensorId] = _currentFrameTimeSensor.Value,
                [SensorConstants.WindowTitleSensorId] = _windowTitleSensor.Value,
                [SensorConstants.ResolutionSensorId] = _resolutionSensor.Value,
                [SensorConstants.RefreshRateSensorId] = _refreshRateSensor.Value,
                [SensorConstants.GpuNameSensorId] = _gpuNameSensor.Value
            };
        }
    }
}