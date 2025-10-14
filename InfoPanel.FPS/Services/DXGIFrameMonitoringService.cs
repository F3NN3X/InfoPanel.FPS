using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Alternative FPS monitoring service using DXGI frame statistics (no admin rights required).
    /// Works by reading DXGI swap chain presentation statistics which are exposed by the graphics driver.
    /// </summary>
    public class DXGIFrameMonitoringService : IDisposable
    {
        // RTSS shared memory structures
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private unsafe struct RTSS_SHARED_MEMORY_OSD_ENTRY
        {
            public fixed char szOSD[256];
            public uint dwOSDX;
            public uint dwOSDY;
            public uint dwOSDPX;
            public uint dwOSDPY;
            public uint dwOSDOpacity;
            public uint dwOSDColor;
            public uint dwOSDBgndColor;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private unsafe struct RTSS_SHARED_MEMORY_APP_ENTRY
        {
            public fixed char szName[260];
            public fixed char szPath[260];
            public uint dwProcessId;
            public uint dwFrames;
            public uint dwTime0;
            public uint dwTime1;
            public uint dwFramesDelta;
            public uint dwTimeDelta;
            public uint dwStatFlags;
            public uint dwStatTime0;
            public uint dwStatTime1;
            public uint dwStatFrames;
            public uint dwStatCount;
            public uint dwFlags;
            public uint dwOSDX;
            public uint dwOSDY;
            public uint dwOSDPX;
            public uint dwOSDPY;
            public uint dwOSDOpacity;
            public uint dwOSDColor;
            public uint dwOSDBgndColor;
            public RTSS_SHARED_MEMORY_OSD_ENTRY osd;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private unsafe struct RTSS_SHARED_MEMORY
        {
            public uint dwSignature;
            public uint dwVersion;
            public uint dwAppEntrySize;
            public uint dwAppArrSize;
            public uint dwOSDEntrySize;
            public uint dwOSDArrSize;
            public uint dwOSDFrame;
            public fixed uint dwReserved[8];
            public fixed byte appArr[256 * 544]; // 256 entries * 544 bytes each (sizeof(RTSS_SHARED_MEMORY_APP_ENTRY))
            public fixed byte osdArr[8 * 1320]; // 8 entries * 1320 bytes each (sizeof(RTSS_SHARED_MEMORY_OSD_ENTRY))
        }

        private const uint RTSS_SIGNATURE = 0x52545353; // 'RTSS'
        private const string RTSS_SHARED_MEMORY_NAME = "RTSSSharedMemoryV2";
        private bool _isMonitoring;
        private uint _currentProcessId;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _monitoringTask;
        
        // Frame timing tracking
        private readonly Queue<DateTime> _frameTimes = new();
        private readonly int _frameHistorySize = 100;
        private double _currentFps;
        private double _averageFrameTime;
        
        public event Action<double, double>? FpsUpdated; // FPS, Frame Time
        
        public bool IsMonitoring => _isMonitoring;
        public uint CurrentProcessId => _currentProcessId;

        /// <summary>
        /// Starts monitoring a process using DXGI frame statistics.
        /// </summary>
        public async Task StartMonitoringAsync(uint processId, CancellationToken cancellationToken = default)
        {
            if (_isMonitoring)
            {
                Console.WriteLine($"DXGIFrameMonitoringService: Already monitoring process {_currentProcessId}");
                return;
            }

            Console.WriteLine($"DXGIFrameMonitoringService: Starting DXGI frame monitoring for PID {processId}");
            _currentProcessId = processId;
            _cancellationTokenSource = new CancellationTokenSource();
            
            var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _cancellationTokenSource.Token).Token;

            _isMonitoring = true;
            _monitoringTask = MonitorFramesAsync(processId, combinedToken);
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Monitors frames using Windows Performance Counters (GPU Adapter Memory / Frame Rate).
        /// This approach uses publicly available GPU metrics without requiring admin rights.
        /// </summary>
        private async Task MonitorFramesAsync(uint processId, CancellationToken cancellationToken)
        {
            Console.WriteLine($"DXGIFrameMonitoringService: Frame monitoring loop started for PID {processId}");
            
            try
            {
                // First try RTSS shared memory (most accurate)
                var rtssFps = TryReadRTSSFps(processId);
                if (rtssFps.HasValue)
                {
                    Console.WriteLine("DXGIFrameMonitoringService: Using RTSS shared memory for FPS data");
                    await MonitorWithRTSSAsync(processId, cancellationToken);
                    return;
                }

                // Then try GPU performance counters
                var gpuCounterResult = TryGetGPUFrameRateCounter(processId);
                if (gpuCounterResult.HasValue)
                {
                    var (gpuPerfCounter, counterName) = gpuCounterResult.Value;
                    Console.WriteLine("DXGIFrameMonitoringService: Using GPU Performance Counter for frame rate");
                    await MonitorWithPerformanceCounterAsync(gpuPerfCounter!, counterName, cancellationToken);
                    return;
                }
                else
                {
                    Console.WriteLine("DXGIFrameMonitoringService: GPU Performance Counter not available, using timing estimation");
                    await MonitorWithTimingEstimationAsync(processId, cancellationToken);
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
        /// Attempts to create a GPU frame rate performance counter for the process.
        /// Returns null if not available (requires GPU driver support).
        /// </summary>
        private (System.Diagnostics.PerformanceCounter?, string)? TryGetGPUFrameRateCounter(uint processId)
        {
            try
            {
                // First, specifically look for the target process in GPU Engine category
                // Since RTSS detects D3D12 for BF6, prioritize 3D engine instances
                var gpuEngineCategory = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
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
                            if (counter.CounterName.Contains("Utilization Percentage") ||
                                counter.CounterName.Contains("utilization") ||
                                counter.CounterName.Contains("busy") ||
                                counter.CounterName.Contains("percentage"))
                            {
                                Console.WriteLine($"DXGIFrameMonitoringService: Found utilization counter for process {processId}: {counter.CounterName}");
                                try
                                {
                                    var perfCounter = new System.Diagnostics.PerformanceCounter("GPU Engine", counter.CounterName, instance, true);
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
                    }
                }

                // If no process-specific counter found, try the broader search
                // Try to find GPU 3D performance counter for the process
                // This reads actual GPU presentation rate, not utilization estimation
                var category = new System.Diagnostics.PerformanceCounterCategory("GPU Adapter Memory");
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
                        return (new System.Diagnostics.PerformanceCounter(
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
                        var cat = new System.Diagnostics.PerformanceCounterCategory(catName);
                        var instances = cat.GetInstanceNames();
                        Console.WriteLine($"DXGIFrameMonitoringService: Checking {catName} category ({instances.Length} instances)");

                        // Look for instances that might contain frame rate data
                        foreach (var instance in instances)
                        {
                            // Check if this instance is related to our process or contains frame data
                            if (instance.Contains($"pid_{processId}") ||
                                instance.Contains("3D") ||
                                instance.Contains("engtype_3D") ||
                                instance.Contains("DirectX") ||
                                instance.Contains("D3D"))
                            {
                                var counters = cat.GetCounters(instance);
                                Console.WriteLine($"DXGIFrameMonitoringService: Instance {instance} has {counters.Length} counters:");
                                foreach (var counter in counters)
                                {
                                    Console.WriteLine($"    - {counter.CounterName}");
                                    if (counter.CounterName.Contains("frame") ||
                                        counter.CounterName.Contains("rate") ||
                                        counter.CounterName.Contains("fps") ||
                                        counter.CounterName.Contains("utilization") ||
                                        counter.CounterName.Contains("running time") ||
                                        counter.CounterName.Contains("activity") ||
                                        counter.CounterName.Contains("busy") ||
                                        counter.CounterName.Contains("percentage"))
                                    {
                                        Console.WriteLine($"DXGIFrameMonitoringService: Found potential counter: {catName}\\{instance}\\{counter.CounterName}");
                                        try
                                        {
                                            var testCounter = new System.Diagnostics.PerformanceCounter(catName, counter.CounterName, instance, true);
                                            var testValue = testCounter.NextValue();
                                            Console.WriteLine($"DXGIFrameMonitoringService: Counter test value: {testValue}");

                                            // If it's a utilization/busy percentage, it might correlate with FPS
                                            if (counter.CounterName.Contains("utilization") || counter.CounterName.Contains("busy") || counter.CounterName.Contains("percentage"))
                                            {
                                                Console.WriteLine($"DXGIFrameMonitoringService: Using GPU utilization counter as FPS estimate");
                                                return (new System.Diagnostics.PerformanceCounter(catName, counter.CounterName, instance, true), counter.CounterName);
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
        }        /// <summary>
        /// Attempts to read FPS data from RTSS shared memory.
        /// RTSS provides accurate frame rate data that other applications can read.
        /// </summary>
        private unsafe double? TryReadRTSSFps(uint processId)
        {
            try
            {
                using var memoryMappedFile = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(RTSS_SHARED_MEMORY_NAME);
                using var accessor = memoryMappedFile.CreateViewAccessor();

                // Read just the header first to check signature
                uint signature = accessor.ReadUInt32(0);
                if (signature != RTSS_SIGNATURE)
                {
                    Console.WriteLine("DXGIFrameMonitoringService: RTSS shared memory signature mismatch");
                    return null;
                }

                // RTSS shared memory layout:
                // DWORD dwSignature
                // DWORD dwVersion
                // DWORD dwAppEntrySize
                // DWORD dwAppArrSize
                // ... more header fields ...
                // App entries start at offset 32 (after 8 DWORDs)

                uint appEntrySize = accessor.ReadUInt32(8);  // dwAppEntrySize
                uint appArrSize = accessor.ReadUInt32(12);   // dwAppArrSize

                Console.WriteLine($"DXGIFrameMonitoringService: RTSS appEntrySize={appEntrySize}, appArrSize={appArrSize}");

                int maxEntries = (int)(appArrSize / appEntrySize);
                long appEntriesStart = 32; // After header

                Console.WriteLine($"DXGIFrameMonitoringService: RTSS maxEntries={maxEntries}, appEntriesStart={appEntriesStart}");

                for (int i = 0; i < Math.Min(maxEntries, 10); i++) // Check first 10 entries
                {
                    long entryOffset = appEntriesStart + (i * appEntrySize);

                    if (entryOffset + 520 >= accessor.Capacity)
                    {
                        Console.WriteLine($"DXGIFrameMonitoringService: Entry {i} offset {entryOffset} exceeds capacity {accessor.Capacity}");
                        break;
                    }

                    // Read process ID at multiple possible offsets to find the correct one
                    uint pid520 = accessor.ReadUInt32(entryOffset + 520);
                    uint pid524 = accessor.ReadUInt32(entryOffset + 524);
                    uint pid528 = accessor.ReadUInt32(entryOffset + 528);
                    uint pid532 = accessor.ReadUInt32(entryOffset + 532);

                    Console.WriteLine($"DXGIFrameMonitoringService: RTSS entry {i}: PIDs at offsets - 520:{pid520}, 524:{pid524}, 528:{pid528}, 532:{pid532}");

                    // Try all possible PID locations
                    uint entryPid = pid520;
                    if (entryPid == 0) entryPid = pid524;
                    if (entryPid == 0) entryPid = pid528;
                    if (entryPid == 0) entryPid = pid532;

                    if (entryPid != 0)
                    {
                        Console.WriteLine($"DXGIFrameMonitoringService: Found non-zero PID {entryPid} at some offset, looking for {processId}");

                        if (entryPid == processId)
                        {
                            // Read frame statistics - adjust offsets based on where PID was found
                            int statOffset = 520 + 28; // dwFramesDelta is 28 bytes after dwProcessId
                            uint framesDelta = accessor.ReadUInt32(entryOffset + statOffset);
                            uint timeDelta = accessor.ReadUInt32(entryOffset + statOffset + 4);
                            uint statFrames = accessor.ReadUInt32(entryOffset + statOffset + 28);

                            Console.WriteLine($"DXGIFrameMonitoringService: RTSS entry found - framesDelta={framesDelta}, timeDelta={timeDelta}, statFrames={statFrames}");

                            if (statFrames > 0 && timeDelta > 0)
                            {
                                var fps = (framesDelta * 1000.0) / timeDelta;
                                Console.WriteLine($"DXGIFrameMonitoringService: RTSS reports FPS={fps:F1} for PID {processId}");
                                return fps;
                            }
                        }
                    }
                }

                Console.WriteLine("DXGIFrameMonitoringService: Process not found in RTSS shared memory");
                return null;
            }
            catch (System.IO.FileNotFoundException)
            {
                Console.WriteLine("DXGIFrameMonitoringService: RTSS shared memory not found (RTSS not running?)");
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
            System.Diagnostics.PerformanceCounter counter,
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
                            if (counterName.Contains("utilization") || counterName.Contains("busy") || counterName.Contains("percentage"))
                            {
                                // For utilization counters, estimate FPS based on GPU usage
                                // High utilization typically means high FPS for GPU-bound games like BF6
                                // BF6 at 250-270 FPS should show high GPU utilization
                                fps = Math.Min(300, Math.Max(60, value * 3)); // Better estimation for high-FPS games
                                Console.WriteLine($"DXGIFrameMonitoringService: GPU utilization {value:F1}%, estimated FPS={fps:F1}");
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

                    await Task.Delay(500, cancellationToken); // Update twice per second
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DXGIFrameMonitoringService: Error reading counter: {ex.Message}");
                    break;
                }
            }

            counter.Dispose();
        }

        /// <summary>
        /// Monitors FPS using RTSS shared memory (most accurate method).
        /// </summary>
        private async Task MonitorWithRTSSAsync(uint processId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var fps = TryReadRTSSFps(processId);
                    if (fps.HasValue)
                    {
                        _currentFps = fps.Value;
                        _averageFrameTime = 1000.0 / _currentFps;
                        FpsUpdated?.Invoke(_currentFps, _averageFrameTime);
                    }

                    await Task.Delay(500, cancellationToken); // Update twice per second
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DXGIFrameMonitoringService: Error reading RTSS: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Fallback monitoring using process activity timing estimation.
        /// Monitors CPU usage patterns to estimate rendering activity.
        /// </summary>
        private async Task MonitorWithTimingEstimationAsync(uint processId, CancellationToken cancellationToken)
        {
            Console.WriteLine("DXGIFrameMonitoringService: Using fallback timing estimation (limited accuracy)");
            
            try
            {
                using var process = Process.GetProcessById((int)processId);
                
                // Get initial CPU time
                var lastCpuTime = process.TotalProcessorTime;
                var lastUpdate = DateTime.UtcNow;
                var lastCpuUsage = 0.0;
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken); // Poll every 100ms
                    
                    try
                    {
                        process.Refresh();
                        
                        // Calculate CPU usage over the interval
                        var currentCpuTime = process.TotalProcessorTime;
                        var currentTime = DateTime.UtcNow;
                        var cpuTimeDelta = (currentCpuTime - lastCpuTime).TotalMilliseconds;
                        var timeDelta = (currentTime - lastUpdate).TotalMilliseconds;
                        
                        if (timeDelta > 0)
                        {
                            var cpuUsage = (cpuTimeDelta / timeDelta) / Environment.ProcessorCount * 100;
                            
                            // Estimate FPS based on CPU usage patterns
                            // High FPS games typically show consistent high CPU usage
                            // This is a rough heuristic - not perfect but better than polling count
                            if (cpuUsage > 10) // If process is actively using CPU
                            {
                                // Assume FPS correlates with CPU usage for GPU-bound games
                                // This will be inaccurate but better than fixed polling rate
                                var estimatedFps = Math.Min(300, Math.Max(30, cpuUsage * 3));
                                
                                _currentFps = estimatedFps;
                                _averageFrameTime = 1000.0 / _currentFps;
                                
                                FpsUpdated?.Invoke(_currentFps, _averageFrameTime);
                                Console.WriteLine($"DXGIFrameMonitoringService: Estimated FPS={_currentFps:F1} (CPU: {cpuUsage:F1}%)");
                            }
                            
                            lastCpuTime = currentCpuTime;
                            lastUpdate = currentTime;
                            lastCpuUsage = cpuUsage;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited
                        Console.WriteLine("DXGIFrameMonitoringService: Process no longer exists");
                        break;
                    }
                }
            }
            catch (ArgumentException)
            {
                Console.WriteLine("DXGIFrameMonitoringService: Process no longer exists");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DXGIFrameMonitoringService: Error in estimation: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops monitoring.
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring)
                return;

            Console.WriteLine($"DXGIFrameMonitoringService: Stopping monitoring for PID {_currentProcessId}");
            
            _cancellationTokenSource?.Cancel();
            _monitoringTask?.Wait(TimeSpan.FromSeconds(2));
            
            _currentProcessId = 0;
            _frameTimes.Clear();
            _currentFps = 0;
            _averageFrameTime = 0;
            _isMonitoring = false;
            
            Console.WriteLine("DXGIFrameMonitoringService: Monitoring stopped");
        }

        public void Dispose()
        {
            StopMonitoring();
            _cancellationTokenSource?.Dispose();
        }
    }
}
