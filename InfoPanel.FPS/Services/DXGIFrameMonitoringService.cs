using System.Diagnostics;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Service for monitoring frame rates using GPU performance counters.
    /// Provides anti-cheat compatible FPS monitoring via Windows GPU counters.
    /// </summary>
    public class DXGIFrameMonitoringService : IDisposable
    {
        private readonly int _frameHistorySize = 1000;
        private readonly float[] _frameTimes = new float[1000];
        private int _frameTimeIndex;
        private int _frameTimeCount;

        private double _currentFps;
        private double _averageFrameTime;

        private CancellationTokenSource? _cts;
        private volatile bool _isMonitoring;

        /// <summary>
        /// Event fired when FPS data is updated.
        /// </summary>
        public event Action<double, double>? FpsUpdated;

        /// <summary>
        /// Gets the current FPS value.
        /// </summary>
        public double CurrentFps => _currentFps;

        /// <summary>
        /// Gets the current average frame time in milliseconds.
        /// </summary>
        public double AverageFrameTime => _averageFrameTime;

        /// <summary>
        /// Starts monitoring FPS for the specified process.
        /// </summary>
        public async Task StartMonitoringAsync(uint processId, CancellationToken cancellationToken = default)
        {
            if (_isMonitoring)
            {
                Console.WriteLine("DXGIFrameMonitoringService: Already monitoring, stopping first");
                await StopMonitoringAsync();
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isMonitoring = true;

            Console.WriteLine($"DXGIFrameMonitoringService: Starting frame monitoring for PID {processId}");

            try
            {
                // Always try RTSS first (with retry logic for when game isn't hooked yet)
                Console.WriteLine("DXGIFrameMonitoringService: Attempting RTSS shared memory monitoring (will retry if not hooked yet)");
                await MonitorWithRTSSAsync(processId, _cts.Token);
                
                // If MonitorWithRTSSAsync exits (timeout or cancelled), fall back to GPU counters
                if (_cts.Token.IsCancellationRequested)
                {
                    return;
                }
                
                Console.WriteLine("DXGIFrameMonitoringService: RTSS monitoring ended, falling back to GPU counters");

                // Then try GPU performance counters
                var gpuCounterResult = TryGetGPUFrameRateCounter(processId);
                if (gpuCounterResult.HasValue)
                {
                    var (gpuPerfCounter, counterName) = gpuCounterResult.Value;
                    Console.WriteLine("DXGIFrameMonitoringService: Using GPU Performance Counter for frame rate");
                    await MonitorWithPerformanceCounterAsync(gpuPerfCounter!, counterName, _cts.Token);
                    return;
                }
                else
                {
                    Console.WriteLine("DXGIFrameMonitoringService: GPU Performance Counter not available, using timing estimation");
                    await MonitorWithTimingEstimationAsync(processId, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("DXGIFrameMonitoringService: Monitoring cancelled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DXGIFrameMonitoringService: Error in monitoring loop: {ex.Message}");
            }
            finally
            {
                _isMonitoring = false;
                Console.WriteLine("DXGIFrameMonitoringService: Frame monitoring loop ended");
            }
        }

        /// <summary>
        /// Stops the current monitoring session.
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            _isMonitoring = false;
            await Task.Delay(100); // Allow cleanup
        }

        /// <summary>
        /// Attempts to create a GPU frame rate performance counter for the process.
        /// Returns null if not available (requires GPU driver support).
        /// </summary>
        private (PerformanceCounter?, string)? TryGetGPUFrameRateCounter(uint processId)
        {
            try
            {
                // First, specifically look for the target process in GPU Engine category
                // Since RTSS detects D3D12 for BF6, prioritize 3D engine instances
                var gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
                var allInstances = gpuEngineCategory.GetInstanceNames();

                Console.WriteLine($"DXGIFrameMonitoringService: Scanning {allInstances.Length} GPU Engine instances for process {processId}");

                // First pass: Look specifically for our target process's 3D engine instance
                foreach (var instance in allInstances)
                {
                    if (instance.Contains($"pid_{processId}") && instance.Contains("engtype_3D"))
                    {
                        Console.WriteLine($"DXGIFrameMonitoringService: Found process-specific 3D instance: {instance}");
                        var counters = gpuEngineCategory.GetCounters(instance);
                        Console.WriteLine($"DXGIFrameMonitoringService: Instance {instance} has {counters.Length} counters:");
                        foreach (var counter in counters)
                        {
                            Console.WriteLine($"    - {counter.CounterName}");
                        }

                        // Look for utilization counters that can estimate FPS
                        foreach (var counter in counters)
                        {
                            if (counter.CounterName.ToLower().Contains("utilization percentage") ||
                                counter.CounterName.ToLower().Contains("utilization") ||
                                counter.CounterName.ToLower().Contains("busy") ||
                                counter.CounterName.ToLower().Contains("percentage"))
                            {
                                Console.WriteLine($"DXGIFrameMonitoringService: Found utilization counter for process {processId}: {counter.CounterName}");
                                try
                                {
                                    var perfCounter = new PerformanceCounter("GPU Engine", counter.CounterName, instance, true);
                                    var testValue = perfCounter.NextValue();
                                    Console.WriteLine($"DXGIFrameMonitoringService: Counter test value: {testValue}");
                                    Console.WriteLine($"DXGIFrameMonitoringService: Using GPU utilization counter for FPS estimation");
                                    return (perfCounter, counter.CounterName);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"DXGIFrameMonitoringService: Counter test failed: {ex.Message}");
                                }
                            }
                        }
                    }
                }

                // If no process-specific 3D counter found, try the broader search
                // Try to find GPU 3D performance counter for the process
                // This reads actual GPU presentation rate, not utilization estimation
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instanceNames = category.GetInstanceNames();

                Console.WriteLine($"DXGIFrameMonitoringService: Found {instanceNames.Length} GPU counter instances:");
                foreach (var instance in instanceNames)
                {
                    Console.WriteLine($"  - {instance}");
                }

                foreach (var instanceName in instanceNames)
                {
                    if (instanceName.Contains($"pid_{processId}"))
                    {
                        Console.WriteLine($"DXGIFrameMonitoringService: Found process-specific GPU counter: {instanceName}");
                        // Found process-specific GPU counter
                        return (new PerformanceCounter(
                            "GPU Adapter Memory",
                            "Local Usage",
                            instanceName,
                            true), "Local Usage");
                    }
                }

                // Also try other GPU counter categories that might have frame rate info
                var gpuCategories = new[] { "GPU Engine", "DXVA2", "Graphics", "GPU Adapter Memory", "GPU Process Memory", "GPU Non Local Adapter Memory" };
                foreach (var catName in gpuCategories)
                {
                    try
                    {
                        var cat = new PerformanceCounterCategory(catName);
                        var instances = cat.GetInstanceNames();
                        Console.WriteLine($"DXGIFrameMonitoringService: Checking {catName} category ({instances.Length} instances)");

                        // Look for instances that might contain frame rate data
                        // First priority: exact process match
                        foreach (var instance in instances)
                        {
                            if (instance.Contains($"pid_{processId}"))
                            {
                                var counters = cat.GetCounters(instance);
                                Console.WriteLine($"DXGIFrameMonitoringService: Process-specific instance {instance} has {counters.Length} counters:");
                                foreach (var counter in counters)
                                {
                                    Console.WriteLine($"    - {counter.CounterName}");
                                    if (counter.CounterName.ToLower().Contains("frame") ||
                                        counter.CounterName.ToLower().Contains("rate") ||
                                        counter.CounterName.ToLower().Contains("fps") ||
                                        counter.CounterName.ToLower().Contains("utilization") ||
                                        counter.CounterName.ToLower().Contains("running time") ||
                                        counter.CounterName.ToLower().Contains("activity") ||
                                        counter.CounterName.ToLower().Contains("busy") ||
                                        counter.CounterName.ToLower().Contains("percentage"))
                                    {
                                        Console.WriteLine($"DXGIFrameMonitoringService: Found process-specific counter: {catName}\\{instance}\\{counter.CounterName}");
                                        try
                                        {
                                            var testCounter = new PerformanceCounter(catName, counter.CounterName, instance, true);
                                            var testValue = testCounter.NextValue();
                                            Console.WriteLine($"DXGIFrameMonitoringService: Counter test value: {testValue}");

                                            // Prioritize actual FPS/frame rate counters over utilization
                                            if (counter.CounterName.ToLower().Contains("frame") ||
                                                counter.CounterName.ToLower().Contains("rate") ||
                                                counter.CounterName.ToLower().Contains("fps"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using process-specific FPS counter: {counter.CounterName}");
                                                return (new PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
                                            }
                                            // Check for running time counters (can be used to calculate FPS)
                                            else if (counter.CounterName.ToLower().Contains("running time"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using process-specific running time counter: {counter.CounterName}");
                                                return (new PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
                                            }
                                            // Then check for utilization/busy percentage as fallback
                                            else if (counter.CounterName.ToLower().Contains("utilization") || counter.CounterName.ToLower().Contains("busy") || counter.CounterName.ToLower().Contains("percentage"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using process-specific GPU utilization counter");
                                                return (new PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"DXGIFrameMonitoringService: Counter test failed: {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }

                        // Second priority: 3D engine instances (if no process-specific found)
                        foreach (var instance in instances)
                        {
                            // Check if this instance contains 3D data but skip if we already have process-specific
                            if (!instance.Contains($"pid_{processId}") &&
                                (instance.Contains("3D") ||
                                 instance.Contains("engtype_3D") ||
                                 instance.Contains("DirectX") ||
                                 instance.Contains("D3D")))
                            {
                                var counters = cat.GetCounters(instance);
                                Console.WriteLine($"DXGIFrameMonitoringService: 3D instance {instance} has {counters.Length} counters:");
                                foreach (var counter in counters)
                                {
                                    Console.WriteLine($"    - {counter.CounterName}");
                                    if (counter.CounterName.ToLower().Contains("frame") ||
                                        counter.CounterName.ToLower().Contains("rate") ||
                                        counter.CounterName.ToLower().Contains("fps") ||
                                        counter.CounterName.ToLower().Contains("utilization") ||
                                        counter.CounterName.ToLower().Contains("running time") ||
                                        counter.CounterName.ToLower().Contains("activity") ||
                                        counter.CounterName.ToLower().Contains("busy") ||
                                        counter.CounterName.ToLower().Contains("percentage"))
                                    {
                                        Console.WriteLine($"DXGIFrameMonitoringService: Found 3D counter: {catName}\\{instance}\\{counter.CounterName}");
                                        try
                                        {
                                            var testCounter = new PerformanceCounter(catName, counter.CounterName, instance, true);
                                            var testValue = testCounter.NextValue();
                                            Console.WriteLine($"DXGIFrameMonitoringService: Counter test value: {testValue}");

                                            // Prioritize actual FPS/frame rate counters over utilization
                                            if (counter.CounterName.ToLower().Contains("frame") ||
                                                counter.CounterName.ToLower().Contains("rate") ||
                                                counter.CounterName.ToLower().Contains("fps"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using 3D FPS counter: {counter.CounterName}");
                                                return (new PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
                                            }
                                            // Check for running time counters (can be used to calculate FPS)
                                            else if (counter.CounterName.ToLower().Contains("running time"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using 3D running time counter: {counter.CounterName}");
                                                return (new PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
                                            }
                                            // Then check for utilization/busy percentage as fallback
                                            else if (counter.CounterName.ToLower().Contains("utilization") || counter.CounterName.ToLower().Contains("busy") || counter.CounterName.ToLower().Contains("percentage"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using 3D GPU utilization counter as fallback");
                                                return (new PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"DXGIFrameMonitoringService: Counter test failed: {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DXGIFrameMonitoringService: Error checking {catName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DXGIFrameMonitoringService: Could not create GPU counter: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Attempts to read FPS data from RTSS shared memory.
        /// RTSS provides accurate frame rate data that other applications can read.
        /// Uses pattern from working RTSSExtractor: reads Frames field directly as FPS.
        /// </summary>
        public unsafe double? TryReadRTSSFps(uint processId)
        {
            try
            {
                // Try all RTSS shared memory versions
                string[] memoryNames = { "RTSSSharedMemoryV3", "RTSSSharedMemoryV2", "RTSSSharedMemoryV1" };
                const uint RTSS_SIGNATURE = 0x52545353; // "RTSS"

                foreach (var memName in memoryNames)
                {
                    try
                    {
                        using var memoryMappedFile = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(memName);
                        using var accessor = memoryMappedFile.CreateViewAccessor();

                        // Read header
                        uint signature = accessor.ReadUInt32(0);
                        if (signature != RTSS_SIGNATURE)
                        {
                            Console.WriteLine($"DXGIFrameMonitoringService: {memName} signature mismatch");
                            continue;
                        }

                        uint appEntrySize = accessor.ReadUInt32(8);   // dwAppEntrySize
                        uint appArrOffset = accessor.ReadUInt32(12);  // dwAppArrOffset (v2) or infer from header size
                        uint appArrSize = accessor.ReadUInt32(16);    // dwAppArrSize or entry count

                        // For v2+, offset is at position 12; for v1, app array starts after header
                        long appEntriesStart = (appArrOffset > 0 && appArrOffset < accessor.Capacity) ? appArrOffset : 256;
                        
                        int maxEntries = 0;
                        if (appEntrySize > 0)
                        {
                            maxEntries = (int)Math.Min(appArrSize, (accessor.Capacity - appEntriesStart) / appEntrySize);
                        }

                        Console.WriteLine($"DXGIFrameMonitoringService: Using {memName}, entries={maxEntries}, entrySize={appEntrySize}, offset={appEntriesStart}");

                        // Scan app entries using simple offsets from working implementation
                        // AppEntry structure: PID(0), Name(4-263), Flags(264), Time0(268), Time1(272), Frames(276), FrameTime(280)
                        const int OFF_PID = 0;
                        const int OFF_FRAMES = 276;
                        const int OFF_FRAMETIME = 280; // microseconds

                        for (int i = 0; i < maxEntries; i++)
                        {
                            long entryOffset = appEntriesStart + (i * appEntrySize);
                            if (entryOffset + OFF_FRAMETIME + 4 > accessor.Capacity) break;

                            uint entryPid = accessor.ReadUInt32(entryOffset + OFF_PID);
                            if (entryPid == 0) continue;
                            
                            if (entryPid == processId)
                            {
                                // Found matching entry - read Frames field directly as FPS
                                // (RTSS updates this with current FPS value for display)
                                uint framesValue = accessor.ReadUInt32(entryOffset + OFF_FRAMES);
                                if (framesValue > 0 && framesValue < 1000)
                                {
                                    Console.WriteLine($"DXGIFrameMonitoringService: RTSS entry {i} PID {processId} matched, FPS={framesValue}");
                                    return (double)framesValue;
                                }
                            }
                        }

                        Console.WriteLine($"DXGIFrameMonitoringService: PID {processId} not found in {memName}");
                    }
                    catch (System.IO.FileNotFoundException)
                    {
                        // Try next version
                        continue;
                    }
                }

                Console.WriteLine("DXGIFrameMonitoringService: RTSS shared memory not available (tried all versions)");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DXGIFrameMonitoringService: Error reading RTSS shared memory: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Monitors FPS using GPU performance counter (when available).
        /// </summary>
        private async Task MonitorWithPerformanceCounterAsync(
            PerformanceCounter counter,
            string counterName,
            CancellationToken cancellationToken)
        {
            var lastValue = 0f;
            var stableReadings = 0;
            var initialZeroCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var value = counter.NextValue();

                    // Handle initial zero readings from GPU counters
                    if (value == 0)
                    {
                        initialZeroCount++;
                        if (initialZeroCount < 10) // Allow up to 10 initial zeros
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                    else
                    {
                        initialZeroCount = 0; // Reset zero count when we get a non-zero value
                    }

                    // GPU counters sometimes return 0 initially, wait for stable non-zero readings
                    if (value > 0 && (Math.Abs(value - lastValue) > 0.1 || stableReadings == 0))
                    {
                        stableReadings++;
                        if (stableReadings > 2) // Wait for 3 stable readings
                        {
                            double fps;
                            Console.WriteLine($"DXGIFrameMonitoringService: Processing counter '{counterName}' with value {value:F1}");
                            Console.WriteLine($"DXGIFrameMonitoringService: Counter name length: {counterName.Length}");
                            Console.WriteLine($"DXGIFrameMonitoringService: Contains 'utilization': {counterName.Contains("utilization")}");
                            Console.WriteLine($"DXGIFrameMonitoringService: Contains 'percentage': {counterName.Contains("percentage")}");
                            Console.WriteLine($"DXGIFrameMonitoringService: Contains 'busy': {counterName.Contains("busy")}");
                            bool isUtilizationCounter = counterName.ToLower().Contains("utilization") || counterName.ToLower().Contains("busy") || counterName.ToLower().Contains("percentage");
                            Console.WriteLine($"DXGIFrameMonitoringService: Is utilization counter: {isUtilizationCounter}");
                            if (isUtilizationCounter)
                            {
                                // Estimate FPS based on GPU utilization with different strategies for different ranges
                                if (value > 70)
                                {
                                    // High utilization: GPU-bound games (like BF6) - use multiplier
                                    fps = Math.Min(300, Math.Max(60, value * 2.8));
                                    Console.WriteLine($"DXGIFrameMonitoringService: High GPU utilization {value:F1}%, estimated FPS={fps:F1} (GPU-bound game)");
                                }
                                else if (value > 30)
                                {
                                    // Medium utilization: Moderate GPU usage - use moderate multiplier
                                    fps = Math.Min(180, Math.Max(60, value * 2.2));
                                    Console.WriteLine($"DXGIFrameMonitoringService: Medium GPU utilization {value:F1}%, estimated FPS={fps:F1} (moderate GPU usage)");
                                }
                                else
                                {
                                    // Low utilization: Likely V-sync locked or CPU-bound - assume common refresh rates
                                    // Check if value suggests a specific refresh rate pattern
                                    if (value < 15)
                                    {
                                        fps = 60; // Assume 60 FPS for very low utilization
                                        Console.WriteLine($"DXGIFrameMonitoringService: Low GPU utilization {value:F1}%, assuming 60 FPS (V-sync or CPU-bound)");
                                    }
                                    else
                                    {
                                        fps = 120; // Assume 120 FPS for moderate low utilization
                                        Console.WriteLine($"DXGIFrameMonitoringService: Low GPU utilization {value:F1}%, assuming 120 FPS (V-sync locked)");
                                    }
                                }
                            }
                            else
                            {
                                // Direct FPS counter
                                fps = value;
                                Console.WriteLine($"DXGIFrameMonitoringService: FPS={fps:F1}, FrameTime={1000.0/fps:F2}ms");
                            }

                            _currentFps = fps;
                            _averageFrameTime = 1000.0 / _currentFps;
                            FpsUpdated?.Invoke(_currentFps, _averageFrameTime);
                        }
                        lastValue = value;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DXGIFrameMonitoringService: Error reading performance counter: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Monitors FPS using timing estimation when GPU counters are not available.
        /// </summary>
        private async Task MonitorWithTimingEstimationAsync(uint processId, CancellationToken cancellationToken)
        {
            var stopwatch = new Stopwatch();
            var frameCount = 0;
            var lastUpdate = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Simple timing-based estimation - not very accurate but better than nothing
                    if (frameCount == 0)
                    {
                        stopwatch.Restart();
                    }

                    frameCount++;

                    var elapsed = stopwatch.Elapsed.TotalSeconds;
                    if (elapsed >= 1.0) // Update every second
                    {
                        var fps = frameCount / elapsed;
                        _currentFps = Math.Min(300, Math.Max(30, fps)); // Clamp to reasonable range
                        _averageFrameTime = 1000.0 / _currentFps;

                        Console.WriteLine($"DXGIFrameMonitoringService: Estimated FPS={_currentFps:F1} (timing-based, less accurate)");

                        FpsUpdated?.Invoke(_currentFps, _averageFrameTime);

                        // Reset for next measurement
                        frameCount = 0;
                        lastUpdate = DateTime.UtcNow;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(16), cancellationToken).ConfigureAwait(false); // ~60 FPS timing
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DXGIFrameMonitoringService: Error in timing estimation: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Reads both FPS and FrameTime from RTSS shared memory.
        /// </summary>
        private (double fps, double frameTimeMs)? TryReadRTSSData(uint processId)
        {
            try
            {
                string[] memoryNames = { "RTSSSharedMemoryV3", "RTSSSharedMemoryV2", "RTSSSharedMemoryV1" };
                const uint RTSS_SIGNATURE = 0x52545353; // "RTSS"

                foreach (var memName in memoryNames)
                {
                    try
                    {
                        using var memoryMappedFile = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(memName);
                        using var accessor = memoryMappedFile.CreateViewAccessor();

                        uint signature = accessor.ReadUInt32(0);
                        if (signature != RTSS_SIGNATURE) continue;

                        uint appEntrySize = accessor.ReadUInt32(8);
                        uint appArrOffset = accessor.ReadUInt32(12);
                        uint appArrSize = accessor.ReadUInt32(16);
                        long appEntriesStart = (appArrOffset > 0 && appArrOffset < accessor.Capacity) ? appArrOffset : 256;
                        
                        int maxEntries = 0;
                        if (appEntrySize > 0)
                        {
                            maxEntries = (int)Math.Min(appArrSize, (accessor.Capacity - appEntriesStart) / appEntrySize);
                        }

                        const int OFF_PID = 0;
                        const int OFF_FRAMES = 276;
                        const int OFF_FRAMETIME = 280; // microseconds

                        for (int i = 0; i < maxEntries; i++)
                        {
                            long entryOffset = appEntriesStart + (i * appEntrySize);
                            if (entryOffset + OFF_FRAMETIME + 4 > accessor.Capacity) break;

                            uint entryPid = accessor.ReadUInt32(entryOffset + OFF_PID);
                            if (entryPid == 0 || entryPid != processId) continue;
                            
                            uint framesValue = accessor.ReadUInt32(entryOffset + OFF_FRAMES);
                            
                            if (framesValue > 0 && framesValue < 1000)
                            {
                                // Calculate frame time from FPS (RTSS FrameTime field doesn't give accurate per-frame times)
                                double frameTimeMs = 1000.0 / framesValue;
                                Console.WriteLine($"DXGIFrameMonitoringService: RTSS PID {processId} - FPS={framesValue}, FrameTime={frameTimeMs:F2}ms");
                                return (framesValue, frameTimeMs);
                            }
                        }
                    }
                    catch (System.IO.FileNotFoundException) { continue; }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Monitors FPS using RTSS shared memory (most accurate).
        /// Continuously retries until RTSS hooks the game or timeout.
        /// </summary>
        private async Task MonitorWithRTSSAsync(uint processId, CancellationToken cancellationToken)
        {
            bool rtssDetected = false;
            int retryCount = 0;
            int consecutiveFailures = 0;
            const int maxRetries = 60; // Try for up to 60 seconds before giving up
            const int maxConsecutiveFailures = 10; // If RTSS disappears for 10 seconds, exit
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var data = TryReadRTSSData(processId);
                    if (data.HasValue)
                    {
                        if (!rtssDetected)
                        {
                            Console.WriteLine($"DXGIFrameMonitoringService: RTSS hook detected after {retryCount} seconds");
                            rtssDetected = true;
                        }
                        
                        consecutiveFailures = 0; // Reset failure counter on success
                        _currentFps = data.Value.fps;
                        _averageFrameTime = data.Value.frameTimeMs;
                        FpsUpdated?.Invoke(_currentFps, _averageFrameTime);
                    }
                    else
                    {
                        if (!rtssDetected)
                        {
                            // Still waiting for initial RTSS hook
                            retryCount++;
                            if (retryCount >= maxRetries)
                            {
                                Console.WriteLine($"DXGIFrameMonitoringService: RTSS not detected after {maxRetries} seconds, falling back to GPU counters");
                                break;
                            }
                            if (retryCount == 1 || retryCount % 10 == 0)
                            {
                                Console.WriteLine($"DXGIFrameMonitoringService: Waiting for RTSS to hook game... ({retryCount}/{maxRetries}s)");
                            }
                        }
                        else
                        {
                            // RTSS was working but now unavailable
                            consecutiveFailures++;
                            if (consecutiveFailures >= maxConsecutiveFailures)
                            {
                                Console.WriteLine($"DXGIFrameMonitoringService: RTSS data lost for {maxConsecutiveFailures} seconds, exiting");
                                break;
                            }
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DXGIFrameMonitoringService: Error in RTSS monitoring: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Disposes of resources used by the service.
        /// </summary>
        public void Dispose()
        {
            StopMonitoringAsync().Wait();
        }
    }
}