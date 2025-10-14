using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.FPS.IPC;
using PresentMonFps;

namespace InfoPanel.FPS.HelperService
{
    /// <summary>
    /// Elevated helper service that runs PresentMon ETW monitoring and shares data via memory-mapped file.
    /// This service must run with administrator privileges to access ETW.
    /// </summary>
    public class FpsHelperService
    {
        private readonly FpsDataWriter _dataWriter;
        private uint _currentProcessId;
        private bool _isRunning;
        private bool _isMonitoringActive;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _monitoringCts;
        
        // Frame timing tracking
        private readonly Queue<double> _frameTimes = new();
        private const int MaxFrameHistory = 1000;
        private string _currentWindowTitle = string.Empty;
        
        public FpsHelperService()
        {
            _dataWriter = new FpsDataWriter();
        }
        
        /// <summary>
        /// Starts the helper service.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("=== InfoPanel FPS Helper Service Starting ===");
            Console.WriteLine($"Service Time: {DateTime.Now}");
            Console.WriteLine($"Process ID: {Environment.ProcessId}");
            Console.WriteLine($"Is Administrator: {IsAdministrator()}");
            
            if (!IsAdministrator())
            {
                Console.WriteLine("ERROR: Service must run with administrator privileges!");
                Console.WriteLine("Please run this service as administrator.");
                return;
            }
            
            // Create shared memory
            if (!_dataWriter.Create())
            {
                Console.WriteLine("ERROR: Failed to create shared memory!");
                return;
            }
            
            Console.WriteLine("Shared memory created successfully");
            Console.WriteLine($"Memory Name: {FpsData.SHARED_MEMORY_NAME}");
            
            _isRunning = true;
            _cts = new CancellationTokenSource();
            var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            
            Console.WriteLine("Helper service started - waiting for monitoring requests...");
            
            // Main service loop
            await ServiceLoopAsync(combinedCts.Token);
            
            Console.WriteLine("Helper service stopped");
        }
        
        /// <summary>
        /// Main service loop that monitors processes and updates shared memory.
        /// </summary>
        private async Task ServiceLoopAsync(CancellationToken cancellationToken)
        {
            var lastScan = DateTime.MinValue;
            
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    // Scan for gaming processes every 2 seconds
                    if ((DateTime.UtcNow - lastScan).TotalSeconds >= 2)
                    {
                        await ScanForGamingProcessesAsync();
                        lastScan = DateTime.UtcNow;
                    }
                    
                    // Write current state to shared memory
                    UpdateSharedMemory();
                    
                    await Task.Delay(100, cancellationToken); // 10Hz update rate
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in service loop: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
        
        /// <summary>
        /// Scans for gaming processes and starts monitoring if found.
        /// </summary>
        private async Task ScanForGamingProcessesAsync()
        {
            try
            {
                // Get foreground window process
                var foregroundProcess = GetForegroundProcess();
                
                if (foregroundProcess != null && ShouldMonitorProcess(foregroundProcess))
                {
                    uint pid = (uint)foregroundProcess.Id;
                    
                    if (pid != _currentProcessId)
                    {
                        Console.WriteLine($"Detected gaming process: {foregroundProcess.ProcessName} (PID: {pid})");
                        await StartMonitoringProcessAsync(pid, foregroundProcess.MainWindowTitle);
                    }
                }
                else if (_currentProcessId != 0)
                {
                    // Stop monitoring if no gaming process found
                    StopMonitoring();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning for processes: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets the foreground window process.
        /// </summary>
        private Process? GetForegroundProcess()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return null;
                
                GetWindowThreadProcessId(hwnd, out uint pid);
                return Process.GetProcessById((int)pid);
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Determines if a process should be monitored (gaming heuristics).
        /// </summary>
        private bool ShouldMonitorProcess(Process process)
        {
            string name = process.ProcessName.ToLowerInvariant();
            
            // Known gaming processes
            string[] gamingProcesses = 
            {
                "bf6", "battlefield", "valheim", "apex", "valorant", 
                "fortnite", "warzone", "modernwarfare", "cod", "destiny2",
                "cyberpunk", "witcher", "skyrim", "fallout", "gta5",
                "rdr2", "eldenring", "darksouls"
            };
            
            foreach (var gameName in gamingProcesses)
            {
                if (name.Contains(gameName))
                {
                    Console.WriteLine($"Gaming process detected: {name}");
                    return true;
                }
            }
            
            // Exclude system processes
            if (name.Contains("explorer") || name.Contains("dwm") || 
                name.Contains("infopanel") || name.Contains("system"))
            {
                return false;
            }
            
            // If has a main window and is using GPU, might be a game
            return process.MainWindowHandle != IntPtr.Zero;
        }
        
        /// <summary>
        /// Starts monitoring a specific process.
        /// </summary>
        private async Task StartMonitoringProcessAsync(uint processId, string windowTitle)
        {
            try
            {
                // Stop existing monitoring
                StopMonitoring();
                
                _currentProcessId = processId;
                _currentWindowTitle = windowTitle;
                _monitoringCts = new CancellationTokenSource();
                
                Console.WriteLine($"Starting PresentMon monitoring for PID {processId}...");
                
                // Start PresentMon using static API
                var fpsRequest = new FpsRequest { TargetPid = processId };
                
                _isMonitoringActive = true;
                
                // Start monitoring in background task
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FpsInspector.StartForeverAsync(
                            fpsRequest,
                            OnFrameDataReceived,
                            _monitoringCts.Token
                        );
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon monitoring error: {ex.Message}");
                        _isMonitoringActive = false;
                    }
                });
                
                Console.WriteLine($"PresentMon monitoring started successfully for {windowTitle}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting monitoring: {ex.Message}");
                StopMonitoring();
            }
        }
        
        /// <summary>
        /// Called when frame data is received from PresentMon.
        /// </summary>
        private void OnFrameDataReceived(FpsResult? result)
        {
            try
            {
                if (result == null || result.Fps <= 0)
                    return;
                
                // Calculate frame time from FPS
                double frameTime = 1000.0 / result.Fps;
                
                // Store frame time for statistics
                if (frameTime > 0)
                {
                    _frameTimes.Enqueue(frameTime);
                    if (_frameTimes.Count > MaxFrameHistory)
                        _frameTimes.Dequeue();
                }
                
                if (_frameTimes.Count % 60 == 0) // Log every 60 frames
                {
                    double avgFps = _frameTimes.Count > 0 ? 1000.0 / _frameTimes.Average() : 0;
                    Console.WriteLine($"FPS Update: {avgFps:F1} FPS, {frameTime:F2}ms frame time");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing frame data: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Stops monitoring the current process.
        /// </summary>
        private void StopMonitoring()
        {
            if (_isMonitoringActive)
            {
                Console.WriteLine($"Stopping monitoring for PID {_currentProcessId}");
                
                _monitoringCts?.Cancel();
                _isMonitoringActive = false;
            }
            
            _currentProcessId = 0;
            _currentWindowTitle = string.Empty;
            _frameTimes.Clear();
        }
        
        /// <summary>
        /// Updates shared memory with current FPS data.
        /// </summary>
        private void UpdateSharedMemory()
        {
            try
            {
                var data = new FpsData();
                data.ProcessId = _currentProcessId;
                data.IsMonitoring = (byte)(_isMonitoringActive ? 1 : 0);
                
                if (_isMonitoringActive && _frameTimes.Count > 0)
                {
                    var frameTimesArray = _frameTimes.ToArray();
                    
                    // Calculate FPS
                    double avgFrameTime = frameTimesArray.Average();
                    data.Fps = avgFrameTime > 0 ? 1000.0 / avgFrameTime : 0;
                    data.FrameTime = avgFrameTime;
                    
                    // Calculate 1% low FPS
                    Array.Sort(frameTimesArray);
                    int onePercentIndex = Math.Max(0, (int)(frameTimesArray.Length * 0.99));
                    double oneLowFrameTime = frameTimesArray[onePercentIndex];
                    data.OneLowFps = oneLowFrameTime > 0 ? 1000.0 / oneLowFrameTime : 0;
                    
                    // Set window title from cached value
                    data.SetWindowTitle(_currentWindowTitle);
                }
                else
                {
                    data.Fps = 0;
                    data.FrameTime = 0;
                    data.OneLowFps = 0;
                    data.SetWindowTitle(string.Empty);
                }
                
                _dataWriter.TryWrite(ref data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating shared memory: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Checks if running with administrator privileges.
        /// </summary>
        private static bool IsAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Stops the helper service.
        /// </summary>
        public void Stop()
        {
            Console.WriteLine("Stopping helper service...");
            
            _isRunning = false;
            _cts?.Cancel();
            
            StopMonitoring();
            _dataWriter.Dispose();
            
            Console.WriteLine("Helper service stopped");
        }
        
        // Windows API imports
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
    
    /// <summary>
    /// Main entry point for the helper service executable.
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "InfoPanel FPS Helper Service";
            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║       InfoPanel FPS Helper Service v1.0                ║");
            Console.WriteLine("║       Elevated ETW Monitoring Service                  ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            
            var service = new FpsHelperService();
            var cts = new CancellationTokenSource();
            
            // Handle Ctrl+C
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\nShutdown requested...");
                cts.Cancel();
            };
            
            try
            {
                await service.StartAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                service.Stop();
            }
            
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}
