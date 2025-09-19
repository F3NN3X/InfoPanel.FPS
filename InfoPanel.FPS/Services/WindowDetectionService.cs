using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InfoPanel.FPS.Constants;
using InfoPanel.FPS.Interfaces;
using InfoPanel.FPS.Models;
using Vanara.PInvoke;
using static Vanara.PInvoke.DwmApi;
using static Vanara.PInvoke.User32;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Service responsible for detecting fullscreen windows and monitoring window changes.
    /// Uses Windows API hooks to detect when applications enter or exit fullscreen mode.
    /// </summary>
    public class WindowDetectionService : IWindowDetectionService
    {
        private IntPtr _eventHook;
        private readonly User32.WinEventProc _winEventProcDelegate;
        private DateTime _lastEventTime = DateTime.MinValue;
        private volatile bool _isMonitoring;

        /// <summary>
        /// Event fired when a fullscreen window is detected or lost.
        /// </summary>
        public event Action<WindowInformation>? WindowChanged;

        /// <summary>
        /// Initializes a new instance of the WindowDetectionService.
        /// </summary>
        public WindowDetectionService()
        {
            _winEventProcDelegate = new User32.WinEventProc(WinEventProc);
        }

        /// <summary>
        /// Starts monitoring for fullscreen window changes.
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring)
            {
                Console.WriteLine("Window detection already started");
                return;
            }

            SetupEventHook();
            _isMonitoring = true;
            Console.WriteLine("Window detection service started");
        }

        /// <summary>
        /// Stops monitoring for window changes.
        /// </summary>
        public void StopMonitoring()
        {
            if (_eventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_eventHook);
                _eventHook = IntPtr.Zero;
            }
            _isMonitoring = false;
            Console.WriteLine("Window detection service stopped");
        }

        /// <summary>
        /// Gets information about the current fullscreen window.
        /// This method replicates the original GetActiveFullscreenProcessIdAndTitle logic.
        /// </summary>
        /// <returns>Window information or null if no fullscreen window is detected.</returns>
        public WindowInformation? GetCurrentFullscreenWindow()
        {
            HWND hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                Console.WriteLine("GetCurrentFullscreenWindow: No foreground window");
                return null;
            }

            if (!GetWindowRect(hWnd, out RECT windowRect))
            {
                Console.WriteLine($"GetCurrentFullscreenWindow: Failed to get window rectangle for hWnd {hWnd}");
                return null;
            }

            // Get the monitor hosting the window for fullscreen detection
            HMONITOR hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero)
            {
                Console.WriteLine($"GetCurrentFullscreenWindow: No monitor found for hWnd {hWnd}");
                return null;
            }

            var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                Console.WriteLine($"GetCurrentFullscreenWindow: Failed to get monitor info for monitor {hMonitor}");
                return null;
            }

            // Only monitor applications on the primary display
            bool isPrimaryMonitor = monitorInfo.dwFlags.HasFlag(User32.MonitorInfoFlags.MONITORINFOF_PRIMARY);
            if (!isPrimaryMonitor)
            {
                Console.WriteLine($"GetCurrentFullscreenWindow: Window {hWnd} is not on primary monitor, skipping");
                return null;
            }

            var monitorRect = monitorInfo.rcMonitor;
            
            // Calculate areas for fullscreen detection
            long windowArea = (long)(windowRect.right - windowRect.left) * (windowRect.bottom - windowRect.top);
            long monitorArea = (long)(monitorRect.right - monitorRect.left) * (monitorRect.bottom - monitorRect.top);
            bool isFullscreen = windowArea >= monitorArea * MonitoringConstants.FullscreenAreaThreshold;

            if (!isFullscreen) // Check extended bounds for borderless fullscreen
            {
                if (DwmGetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extendedFrameBounds).Succeeded)
                {
                    long extendedArea = (long)(extendedFrameBounds.right - extendedFrameBounds.left) * 
                                      (extendedFrameBounds.bottom - extendedFrameBounds.top);
                    isFullscreen = extendedArea >= monitorArea * MonitoringConstants.FullscreenAreaThreshold;
                    
                    Console.WriteLine($"Extended bounds check - hWnd: {hWnd}, Area: {extendedArea}, Monitor: {monitorArea}, Fullscreen: {isFullscreen}");
                }
                else
                {
                    Console.WriteLine($"Failed to get extended frame bounds for hWnd {hWnd}");
                }
            }
            else
            {
                Console.WriteLine($"Window bounds check - hWnd: {hWnd}, Area: {windowArea}, Monitor: {monitorArea}, Fullscreen: {isFullscreen}");
            }

            if (!isFullscreen)
            {
                Console.WriteLine($"Window hWnd {hWnd} is not fullscreen");
                return null;
            }

            // Get process ID and validate
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (!IsValidApplicationPid(pid))
            {
                Console.WriteLine($"Invalid PID {pid} for hWnd {hWnd}");
                return null;
            }

            // Get window title
            string windowTitle = GetWindowTitle(hWnd);

            var windowInfo = new WindowInformation
            {
                ProcessId = pid,
                WindowHandle = (IntPtr)hWnd,
                WindowTitle = windowTitle,
                IsFullscreen = true
            };

            Console.WriteLine($"Fullscreen window detected - hWnd: {hWnd}, PID: {pid}, Title: {windowTitle}");
            return windowInfo;
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
                    0,
                    0,
                    User32.WINEVENT.WINEVENT_OUTOFCONTEXT
                )
                .DangerousGetHandle();

            if (_eventHook == IntPtr.Zero)
            {
                Console.WriteLine("Failed to set up window event hook");
            }
            else
            {
                Console.WriteLine("Window event hook established");
            }
        }

        /// <summary>
        /// Handles foreground window change events with debouncing.
        /// </summary>
        private void WinEventProc(
            HWINEVENTHOOK hWinEventHook,
            uint eventType,
            HWND hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastEventTime).TotalMilliseconds < MonitoringConstants.EventDebounceMs)
                return;

            _lastEventTime = now;
            _ = Task.Run(() => HandleWindowChangeAsync(hwnd));
        }

        /// <summary>
        /// Handles window change events asynchronously.
        /// </summary>
        private async Task HandleWindowChangeAsync(HWND hwnd)
        {
            try
            {
                await Task.Delay(50).ConfigureAwait(false); // Small delay for window stabilization
                
                var windowInfo = AnalyzeWindow(hwnd);
                
                Console.WriteLine($"Window change detected - PID: {windowInfo.ProcessId}, " +
                                $"Title: {windowInfo.WindowTitle}, " +
                                $"Fullscreen: {windowInfo.IsFullscreen}");

                WindowChanged?.Invoke(windowInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling window change: {ex}");
            }
        }

        /// <summary>
        /// Analyzes a window to determine if it's fullscreen and gathers information.
        /// </summary>
        private WindowInformation AnalyzeWindow(HWND hWnd)
        {
            var windowInfo = new WindowInformation
            {
                WindowHandle = (IntPtr)hWnd
            };

            if (hWnd == IntPtr.Zero)
                return windowInfo;

            try
            {
                // Get process ID
                GetWindowThreadProcessId(hWnd, out uint pid);
                windowInfo.ProcessId = pid;

                // Get window title
                windowInfo.WindowTitle = GetWindowTitle(hWnd);

                // Check if window is fullscreen
                windowInfo.IsFullscreen = IsWindowFullscreen(hWnd);

                // Validate process
                if (!IsValidApplicationPid(pid))
                {
                    windowInfo.ProcessId = 0;
                    windowInfo.IsFullscreen = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing window {hWnd}: {ex}");
            }

            return windowInfo;
        }

        /// <summary>
        /// Gets the title of the specified window.
        /// </summary>
        private static string GetWindowTitle(HWND hWnd)
        {
            try
            {
                int length = GetWindowTextLength(hWnd);
                if (length <= 0)
                    return "Untitled";

                StringBuilder title = new StringBuilder(length + 1);
                GetWindowText(hWnd, title, title.Capacity);
                return title.ToString();
            }
            catch
            {
                return "Untitled";
            }
        }

        /// <summary>
        /// Determines if a window is currently fullscreen.
        /// </summary>
        private static bool IsWindowFullscreen(HWND hWnd)
        {
            try
            {
                if (!GetWindowRect(hWnd, out RECT windowRect))
                    return false;

                // Get the monitor hosting the window
                HMONITOR hMonitor = MonitorFromWindow(hWnd, MonitorFlags.MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero)
                    return false;

                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(hMonitor, ref monitorInfo))
                    return false;

                // Only consider fullscreen on primary monitor
                bool isPrimaryMonitor = monitorInfo.dwFlags.HasFlag(User32.MonitorInfoFlags.MONITORINFOF_PRIMARY);
                if (!isPrimaryMonitor)
                {
                    Console.WriteLine($"IsWindowFullscreen: Window {hWnd} is not on primary monitor");
                    return false;
                }

                var monitorRect = monitorInfo.rcMonitor;
                
                // Calculate areas for fullscreen detection
                long windowArea = (long)(windowRect.right - windowRect.left) * (windowRect.bottom - windowRect.top);
                long monitorArea = (long)(monitorRect.right - monitorRect.left) * (monitorRect.bottom - monitorRect.top);
                bool isFullscreen = windowArea >= monitorArea * MonitoringConstants.FullscreenAreaThreshold;

                if (!isFullscreen)
                {
                    // Check extended bounds for borderless fullscreen
                    if (DwmGetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extendedFrameBounds).Succeeded)
                    {
                        long extendedArea = (long)(extendedFrameBounds.right - extendedFrameBounds.left) * 
                                          (extendedFrameBounds.bottom - extendedFrameBounds.top);
                        isFullscreen = extendedArea >= monitorArea * MonitoringConstants.FullscreenAreaThreshold;
                        
                        Console.WriteLine($"Extended bounds check - Area: {extendedArea}, Monitor: {monitorArea}, Fullscreen: {isFullscreen}");
                    }
                }
                else
                {
                    Console.WriteLine($"Window bounds check - Area: {windowArea}, Monitor: {monitorArea}, Fullscreen: {isFullscreen}");
                }

                return isFullscreen;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking fullscreen status for window {hWnd}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Validates if a PID belongs to a legitimate application with a main window.
        /// </summary>
        private static bool IsValidApplicationPid(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                
                // Exclude InfoPanel's own process
                uint currentProcessId = (uint)Environment.ProcessId;
                if (pid == currentProcessId)
                {
                    Console.WriteLine($"PID validation - Excluding own process: {pid}");
                    return false;
                }
                
                // Basic validation
                if (pid <= 4 || process.MainWindowHandle == IntPtr.Zero)
                {
                    Console.WriteLine($"PID validation - PID: {pid}, MainWindow: {process.MainWindowHandle}, Invalid: basic validation failed");
                    return false;
                }
                
                // Exclude common system processes and overlays
                string processName = process.ProcessName.ToLowerInvariant();
                string[] excludedProcesses = 
                {
                    "dwm", "explorer", "winlogon", "csrss", "lsass", "services", "svchost",
                    "infopanel", "displaywindow", "windowsshell", "systemsettings", "shell"
                };
                
                foreach (string excluded in excludedProcesses)
                {
                    if (processName.Contains(excluded))
                    {
                        Console.WriteLine($"PID validation - Excluding process: {pid} ({processName})");
                        return false;
                    }
                }
                
                Console.WriteLine($"PID validation - PID: {pid}, Process: {processName}, Valid: true");
                return true;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"PID {pid} not found");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error validating PID {pid}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Disposes the service and releases resources.
        /// </summary>
        public void Dispose()
        {
            StopMonitoring();
        }
    }
}