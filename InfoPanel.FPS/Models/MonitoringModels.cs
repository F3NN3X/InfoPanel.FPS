namespace InfoPanel.FPS.Models
{
    /// <summary>
    /// Represents performance metrics for a monitored application.
    /// </summary>
    public class PerformanceMetrics
    {
        /// <summary>
        /// Current frames per second.
        /// </summary>
        public float Fps { get; set; }

        /// <summary>
        /// Current frame time in milliseconds.
        /// </summary>
        public float FrameTime { get; set; }

        /// <summary>
        /// 1% low FPS (99th percentile) calculated from frame time buffer.
        /// </summary>
        public float OnePercentLowFps { get; set; }

        /// <summary>
        /// Total number of frame time samples collected.
        /// </summary>
        public int FrameTimeCount { get; set; }
        
        /// <summary>
        /// Process ID that RTSS is actively monitoring.
        /// Used to ensure window title matches the process providing FPS data.
        /// </summary>
        public uint MonitoredProcessId { get; set; }

        /// <summary>
        /// Indicates whether the metrics are valid and should be displayed.
        /// </summary>
        public bool IsValid => Fps > 0 && FrameTime > 0;
    }

    /// <summary>
    /// Represents information about the currently monitored window.
    /// </summary>
    public class WindowInformation
    {
        /// <summary>
        /// Process ID of the monitored application.
        /// </summary>
        public uint ProcessId { get; set; }

        /// <summary>
        /// Title of the monitored window.
        /// </summary>
        public string WindowTitle { get; set; } = string.Empty;

        /// <summary>
        /// Handle to the monitored window.
        /// </summary>
        public IntPtr WindowHandle { get; set; }

        /// <summary>
        /// Indicates whether the window is currently fullscreen.
        /// </summary>
        public bool IsFullscreen { get; set; }

        /// <summary>
        /// Indicates whether this represents a valid monitored window.
        /// </summary>
        public bool IsValid => ProcessId > 0 && WindowHandle != IntPtr.Zero && IsFullscreen;
    }

    /// <summary>
    /// Represents display and system information.
    /// </summary>
    public class SystemInformation
    {
        /// <summary>
        /// Display resolution in "width x height" format.
        /// </summary>
        public string Resolution { get; set; } = "0 x 0";

        /// <summary>
        /// Display refresh rate in Hz.
        /// </summary>
        public uint RefreshRate { get; set; }

        /// <summary>
        /// Name of the system's graphics card.
        /// </summary>
        public string GpuName { get; set; } = "Unknown GPU";
    }

    /// <summary>
    /// Represents the current monitoring state.
    /// </summary>
    public class MonitoringState
    {
        /// <summary>
        /// Current performance metrics.
        /// </summary>
        public PerformanceMetrics Performance { get; set; } = new();

        /// <summary>
        /// Current window information.
        /// </summary>
        public WindowInformation Window { get; set; } = new();

        /// <summary>
        /// Current system information.
        /// </summary>
        public SystemInformation System { get; set; } = new();

        /// <summary>
        /// Indicates whether monitoring is currently active.
        /// </summary>
        public bool IsMonitoring { get; set; }

        /// <summary>
        /// Timestamp of the last update.
        /// </summary>
        public DateTime LastUpdate { get; set; } = DateTime.MinValue;
    }
}