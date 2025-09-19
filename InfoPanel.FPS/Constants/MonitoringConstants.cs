namespace InfoPanel.FPS.Constants
{
    /// <summary>
    /// Contains configuration constants used throughout the FPS monitoring plugin.
    /// </summary>
    public static class MonitoringConstants
    {
        /// <summary>
        /// Maximum number of frame time samples to store in the circular buffer.
        /// </summary>
        public const int MaxFrameTimes = 1000;

        /// <summary>
        /// Number of retry attempts for FpsInspector startup.
        /// </summary>
        public const int RetryAttempts = 3;

        /// <summary>
        /// Delay between retry attempts in milliseconds.
        /// </summary>
        public const int RetryDelayMs = 1000;

        /// <summary>
        /// Minimum number of frame times required for valid 1% low FPS calculation.
        /// </summary>
        public const int MinFrameTimesForLowFps = 10;

        /// <summary>
        /// Recalculate 1% low FPS every N frames.
        /// </summary>
        public const int LowFpsRecalcInterval = 30;

        /// <summary>
        /// Debounce window events by this many milliseconds.
        /// </summary>
        public const int EventDebounceMs = 500;

        /// <summary>
        /// Require this percentage of monitor area coverage for fullscreen detection.
        /// </summary>
        public const float FullscreenAreaThreshold = 0.95f;

        /// <summary>
        /// Update interval for UI sensors in seconds.
        /// </summary>
        public const int UiUpdateIntervalSeconds = 1;

        /// <summary>
        /// Size of the histogram used for 1% low FPS calculation.
        /// </summary>
        public const int HistogramSize = 100;
    }

    /// <summary>
    /// Contains sensor configuration constants.
    /// </summary>
    public static class SensorConstants
    {
        public const string FpsSensorId = "fps";
        public const string OnePercentLowFpsSensorId = "1% low fps";
        public const string CurrentFrameTimeSensorId = "current frame time";
        public const string WindowTitleSensorId = "windowtitle";
        public const string ResolutionSensorId = "resolution";
        public const string RefreshRateSensorId = "refreshrate";
        public const string GpuNameSensorId = "gpu-name";

        public const string FpsSensorDisplayName = "Frames Per Second";
        public const string OnePercentLowFpsSensorDisplayName = "1% Low FPS";
        public const string CurrentFrameTimeSensorDisplayName = "Current Frame Time";
        public const string WindowTitleSensorDisplayName = "Currently Capturing";
        public const string ResolutionSensorDisplayName = "Display Resolution";
        public const string RefreshRateSensorDisplayName = "Display Refresh Rate";
        public const string GpuNameSensorDisplayName = "GPU Name";

        public const string FpsUnit = "FPS";
        public const string FrameTimeUnit = "ms";
        public const string RefreshRateUnit = "Hz";

        public const string DefaultWindowTitle = "Nothing to capture";
        public const string DefaultResolution = "0 x 0";
        public const string DefaultGpuName = "Unknown GPU";
        public const string NoCapture = "-";
    }
}