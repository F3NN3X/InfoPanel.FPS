using InfoPanel.FPS.Constants;
using InfoPanel.FPS.Interfaces;
using InfoPanel.FPS.Models;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Service responsible for monitoring application performance via DXGI frame statistics.
    /// Uses GPU performance counters for anti-cheat compatible FPS monitoring.
    /// </summary>
    public class PerformanceMonitoringService : IPerformanceMonitoringService
    {
        private readonly DXGIFrameMonitoringService _dxgiService;
        private readonly float[] _frameTimes = new float[MonitoringConstants.MaxFrameTimes];
        private readonly float[] _histogram = new float[MonitoringConstants.HistogramSize];
        
        private int _frameTimeIndex;
        private int _frameTimeCount;
        private int _updateCount;
        
        private CancellationTokenSource? _cancellationTokenSource;
        private volatile bool _isMonitoring;
        private volatile uint _currentProcessId;

        /// <summary>
        /// Initializes a new instance of the PerformanceMonitoringService with DXGI.
        /// </summary>
        public PerformanceMonitoringService()
        {
            _dxgiService = new DXGIFrameMonitoringService();
            _dxgiService.FpsUpdated += OnFpsUpdated;
            
            Console.WriteLine("PerformanceMonitoringService: Initialized with DXGI frame monitoring");
            Console.WriteLine("DXGI provides anti-cheat compatible FPS monitoring using GPU performance counters");
        }

        /// <summary>
        /// Event fired when new performance metrics are available.
        /// </summary>
        public event Action<PerformanceMetrics>? MetricsUpdated;

        /// <summary>
        /// Handles FPS updates from DXGI service.
        /// </summary>
        private void OnFpsUpdated(double fps, double frameTimeMs)
        {
            if (!_isMonitoring || fps <= 0)
                return;

            // Filter out startup/initialization artifacts (FPS < 10 is unrealistic for active gameplay)
            if (fps < 10)
            {
                Console.WriteLine($"PerformanceMonitoringService.OnFpsUpdated: Ignoring outlier FPS={fps:F1} (likely initialization artifact)");
                return;
            }

            // Add frame time to circular buffer
            _frameTimes[_frameTimeIndex] = (float)frameTimeMs;
            _frameTimeIndex = (_frameTimeIndex + 1) % MonitoringConstants.MaxFrameTimes;
            if (_frameTimeCount < MonitoringConstants.MaxFrameTimes)
                _frameTimeCount++;

            Console.WriteLine($"PerformanceMonitoringService.OnFpsUpdated: FPS={fps:F1}, FrameTime={frameTimeMs:F2}ms, BufferCount={_frameTimeCount}");

            // Update histogram for percentile calculations
            UpdateHistogram((float)frameTimeMs);

            // Calculate metrics
            var metrics = CalculateMetrics((float)fps, (float)frameTimeMs);
            Console.WriteLine($"PerformanceMonitoringService.OnFpsUpdated: Calculated 1% Low={metrics.OnePercentLowFps:F1}");
            MetricsUpdated?.Invoke(metrics);

            _updateCount++;
        }

        /// <summary>
        /// Updates the frame time histogram for percentile calculations.
        /// </summary>
        private void UpdateHistogram(float frameTime)
        {
            int binIndex = Math.Min((int)(frameTime / 2.0f), MonitoringConstants.HistogramSize - 1);
            _histogram[binIndex]++;
        }

        /// <summary>
        /// Calculates performance metrics from current FPS and frame time.
        /// </summary>
        private PerformanceMetrics CalculateMetrics(float fps, float frameTime)
        {
            return new PerformanceMetrics
            {
                Fps = fps,
                FrameTime = frameTime,
                OnePercentLowFps = CalculateOnePercentLowFps(),
                FrameTimeCount = _frameTimeCount,
                MonitoredProcessId = _dxgiService.MonitoredProcessId
            };
        }

        /// <summary>
        /// Indicates whether performance monitoring is currently active.
        /// </summary>
        public bool IsMonitoring => _isMonitoring;

        /// <summary>
        /// Starts monitoring performance for the specified process.
        /// </summary>
        public async Task StartMonitoringAsync(uint processId, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"PerformanceMonitoringService.StartMonitoringAsync: Called for PID {processId}");
            
            if (_isMonitoring && _currentProcessId == processId)
            {
                Console.WriteLine($"PerformanceMonitoringService: Already monitoring process {processId}");
                return;
            }

            Console.WriteLine($"PerformanceMonitoringService: Stopping previous monitoring and resetting metrics");
            StopMonitoring();
            ResetMetrics();

            _currentProcessId = processId;
            _cancellationTokenSource = new CancellationTokenSource();
            
            var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _cancellationTokenSource.Token).Token;

            Console.WriteLine($"PerformanceMonitoringService: Starting RTSS monitoring for PID {processId}");
            await StartDXGIMonitoringAsync(processId, combinedToken).ConfigureAwait(false);
            
            Console.WriteLine($"PerformanceMonitoringService: StartMonitoringAsync completed for PID {processId}");
        }

        /// <summary>
        /// Stops performance monitoring and resets metrics.
        /// </summary>
        public void StopMonitoring()
        {
            _cancellationTokenSource?.Cancel();
            _isMonitoring = false;
            _currentProcessId = 0;
            Console.WriteLine("Performance monitoring stopped");
        }

        /// <summary>
        /// Gets current performance metrics (average of buffered frame times).
        /// </summary>
        public PerformanceMetrics GetCurrentMetrics()
        {
            var currentFrameTime = _frameTimeCount > 0 ? _frameTimes[(_frameTimeIndex - 1 + MonitoringConstants.MaxFrameTimes) % MonitoringConstants.MaxFrameTimes] : 0;
            var currentFps = currentFrameTime > 0 ? 1000.0f / currentFrameTime : 0;

            return new PerformanceMetrics
            {
                Fps = currentFps,
                FrameTime = currentFrameTime,
                OnePercentLowFps = CalculateOnePercentLowFps(),
                FrameTimeCount = _frameTimeCount,
                MonitoredProcessId = _dxgiService.MonitoredProcessId
            };
        }

        /// <summary>
        /// Gets the process ID that RTSS is currently monitoring.
        /// </summary>
        /// <returns>The monitored process ID, or 0 if no process is being monitored.</returns>
        public uint GetRTSSMonitoredProcessId()
        {
            return _dxgiService.GetRTSSMonitoredProcessId();
        }

        /// <summary>
        /// Starts DXGI monitoring.
        /// </summary>
        private async Task StartDXGIMonitoringAsync(uint processId, CancellationToken cancellationToken)
        {
            Console.WriteLine("PerformanceMonitoringService: Starting DXGI frame monitoring (anti-cheat compatible)");
            
            _isMonitoring = true;
            
            // Start DXGI monitoring
            await _dxgiService.StartMonitoringAsync(processId, cancellationToken).ConfigureAwait(false);
            
            Console.WriteLine("PerformanceMonitoringService: DXGI monitoring started");
        }
        
        /// <summary>
        /// Calculates 1% low FPS from the frame time histogram.
        /// </summary>
        private float CalculateOnePercentLowFps()
        {
            if (_frameTimeCount < 10)
            {
                Console.WriteLine($"PerformanceMonitoringService.CalculateOnePercentLowFps: Not enough samples ({_frameTimeCount} < 10)");
                return 0;
            }

            // Use rolling window of last 100 frames (or all frames if fewer) to match RTSS behavior
            // This prevents old startup spikes from affecting current 1% low readings
            int windowSize = Math.Min(100, _frameTimeCount);
            float[] recentFrameTimes = new float[windowSize];
            
            // Copy the most recent frame times from circular buffer
            for (int i = 0; i < windowSize; i++)
            {
                int bufferIndex = (_frameTimeIndex - windowSize + i + MonitoringConstants.MaxFrameTimes) % MonitoringConstants.MaxFrameTimes;
                recentFrameTimes[i] = _frameTimes[bufferIndex];
            }
            
            // Sort to find 99th percentile (1% low)
            Array.Sort(recentFrameTimes);

            // 1% low FPS = 99th percentile of frame times (slowest 1% of frames in window)
            int percentile99Index = (int)(windowSize * 0.99f);
            if (percentile99Index >= windowSize)
                percentile99Index = windowSize - 1;

            float frameTime99th = recentFrameTimes[percentile99Index];
            float onePctLow = frameTime99th > 0 ? 1000.0f / frameTime99th : 0;
            
            Console.WriteLine($"PerformanceMonitoringService.CalculateOnePercentLowFps: 99th percentile at index {percentile99Index} of {windowSize} recent frames, frameTime={frameTime99th:F2}ms, 1%Low={onePctLow:F1}");
            
            return onePctLow;
        }

        /// <summary>
        /// Resets all metrics and buffered frame times.
        /// </summary>
        private void ResetMetrics()
        {
            _frameTimeIndex = 0;
            _frameTimeCount = 0;
            _updateCount = 0;
            Array.Clear(_frameTimes, 0, _frameTimes.Length);
            Array.Clear(_histogram, 0, _histogram.Length);
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            StopMonitoring();
            _dxgiService?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}
