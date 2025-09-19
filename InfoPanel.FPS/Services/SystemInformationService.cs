using System.Management;
using System.Runtime.InteropServices;
using InfoPanel.FPS.Interfaces;
using InfoPanel.FPS.Models;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Service responsible for gathering system information including display settings and GPU details.
    /// </summary>
    public class SystemInformationService : ISystemInformationService
    {
        private SystemInformation? _cachedSystemInfo;
        private DateTime _lastCacheUpdate = DateTime.MinValue;
        private readonly TimeSpan _cacheValidDuration = TimeSpan.FromMinutes(5); // Cache for 5 minutes

        /// <summary>
        /// Gets the current system information including display settings and GPU name.
        /// </summary>
        /// <returns>Current system information.</returns>
        public SystemInformation GetSystemInformation()
        {
            // Return cached info if still valid
            if (_cachedSystemInfo != null && 
                DateTime.Now - _lastCacheUpdate < _cacheValidDuration)
            {
                return _cachedSystemInfo;
            }

            // Refresh system information
            var (resolution, refreshRate) = GetPrimaryMonitorSettings();
            var gpuName = GetGpuName();

            _cachedSystemInfo = new SystemInformation
            {
                Resolution = resolution,
                RefreshRate = refreshRate,
                GpuName = gpuName
            };

            _lastCacheUpdate = DateTime.Now;
            Console.WriteLine($"System info refreshed - Resolution: {resolution}, Refresh: {refreshRate}Hz, GPU: {gpuName}");

            return _cachedSystemInfo;
        }

        /// <summary>
        /// Gets the primary monitor's resolution and refresh rate.
        /// </summary>
        /// <returns>A tuple containing resolution string and refresh rate.</returns>
        public (string resolution, uint refreshRate) GetPrimaryMonitorSettings()
        {
            try
            {
                DEVMODE devMode = new DEVMODE();
                devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    string resolution = $"{devMode.dmPelsWidth} x {devMode.dmPelsHeight}";
                    uint refreshRate = (uint)devMode.dmDisplayFrequency;
                    
                    Console.WriteLine($"Primary monitor settings - Resolution: {resolution}, Refresh Rate: {refreshRate}Hz");
                    return (resolution, refreshRate);
                }
                else
                {
                    Console.WriteLine("Failed to get primary monitor settings via EnumDisplaySettings");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting primary monitor settings: {ex}");
            }

            Console.WriteLine("Using fallback monitor settings");
            return ("0 x 0", 0u);
        }

        /// <summary>
        /// Gets the name of the system's graphics card.
        /// </summary>
        /// <returns>GPU name or default value if not found.</returns>
        public string GetGpuName()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                using var results = searcher.Get();
                
                foreach (var obj in results)
                {
                    using (obj)
                    {
                        var name = obj["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            Console.WriteLine($"GPU detected: {name}");
                            return name;
                        }
                    }
                }
                
                Console.WriteLine("No GPU found in WMI query results");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving GPU name via WMI: {ex}");
            }

            return "Unknown GPU";
        }

        /// <summary>
        /// Invalidates the cached system information, forcing a refresh on next access.
        /// </summary>
        public void InvalidateCache()
        {
            _cachedSystemInfo = null;
            _lastCacheUpdate = DateTime.MinValue;
            Console.WriteLine("System information cache invalidated");
        }

        /// <summary>
        /// Gets detailed monitor information for a specific monitor.
        /// </summary>
        /// <param name="monitorHandle">Handle to the monitor.</param>
        /// <returns>Monitor information or null if failed.</returns>
        public (string resolution, uint refreshRate)? GetMonitorSettings(IntPtr monitorHandle)
        {
            try
            {
                var monitorInfo = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
                {
                    Console.WriteLine($"Failed to get monitor info for handle {monitorHandle}");
                    return null;
                }

                // Get device name and query its settings
                string deviceName = monitorInfo.szDevice;
                DEVMODE devMode = new DEVMODE();
                devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

                if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    string resolution = $"{devMode.dmPelsWidth} x {devMode.dmPelsHeight}";
                    uint refreshRate = (uint)devMode.dmDisplayFrequency;
                    
                    Console.WriteLine($"Monitor {deviceName} settings - Resolution: {resolution}, Refresh: {refreshRate}Hz");
                    return (resolution, refreshRate);
                }

                Console.WriteLine($"Failed to get display settings for device {deviceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting monitor settings for handle {monitorHandle}: {ex}");
            }

            return null;
        }

        /// <summary>
        /// Gets information about all available monitors in the system.
        /// </summary>
        /// <returns>List of monitor information.</returns>
        public List<(IntPtr handle, string resolution, uint refreshRate)> GetAllMonitorSettings()
        {
            var monitors = new List<(IntPtr handle, string resolution, uint refreshRate)>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, null, (hMonitor, hdcMonitor, lprcMonitor, dwData) =>
                {
                    var settings = GetMonitorSettings(hMonitor);
                    if (settings.HasValue)
                    {
                        monitors.Add((hMonitor, settings.Value.resolution, settings.Value.refreshRate));
                    }
                    return true;
                }, IntPtr.Zero);

                Console.WriteLine($"Detected {monitors.Count} monitors");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enumerating monitors: {ex}");
            }

            return monitors;
        }
    }
}