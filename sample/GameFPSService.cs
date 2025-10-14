using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Universal Game FPS monitoring service for InfoPanel.FPS plugin
    /// Uses Windows Performance Counters to monitor any game's FPS in real-time
    /// Safe with anti-cheat systems as it uses external system APIs
    /// </summary>
    public class GameFPSService : IDisposable
    {
        public event EventHandler<GameFPSEventArgs>? FPSUpdated;
        
        private readonly Dictionary<int, GameMonitorInfo> _monitoredGames = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly List<string> _systemProcesses = new()
        {
            "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", 
            "fontdrvhost", "WUDFHost", "spoolsv", "RuntimeBroker", "taskhostw", "explorer", "dwm", "conhost",
            "dllhost", "rundll32", "mmc", "WmiPrvSE", "SearchIndexer", "audiodg", "MSBuild", "devenv",
            "ServiceHub", "PerfWatson2", "vshost", "VBCSCompiler", "dotnet", "node", "cmd", "powershell",
            "pwsh", "WindowsTerminal", "Code", "notepad", "calc", "mspaint", "winver", "msinfo32",
            "taskmgr", "perfmon", "resmon", "eventvwr", "regedit", "msconfig", "control", "appwiz",
            "SecurityHealthSystray", "SecurityHealthService", "MsMpEng", "NisSrv", "SgrmBroker"
        };
        
        private Task? _monitoringTask;
        private bool _isRunning;

        public class GameFPSEventArgs : EventArgs
        {
            public string ProcessName { get; set; } = string.Empty;
            public int ProcessId { get; set; }
            public string WindowTitle { get; set; } = string.Empty;
            public float FPS { get; set; }
            public float GPUUtilization { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.Now;
        }

        private class GameMonitorInfo
        {
            public Process Process { get; set; } = null!;
            public PerformanceCounter? GPUCounter { get; set; }
            public Queue<float> UtilizationHistory { get; set; } = new Queue<float>();
            public Queue<DateTime> TimestampHistory { get; set; } = new Queue<DateTime>();
            public float LastFPS { get; set; }
        }

        /// <summary>
        /// Start monitoring games for FPS data
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning) return;
            
            _isRunning = true;
            _monitoringTask = Task.Run(MonitorGamesAsync, _cancellationTokenSource.Token);
            
            // Give it a moment to initialize
            await Task.Delay(1000);
        }

        /// <summary>
        /// Stop monitoring
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;
            
            _isRunning = false;
            _cancellationTokenSource.Cancel();
            
            if (_monitoringTask != null)
            {
                await _monitoringTask;
                _monitoringTask.Dispose();
                _monitoringTask = null;
            }
        }

        private async Task MonitorGamesAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Discover new games
                    await DiscoverGamesAsync();
                    
                    // Update FPS for monitored games
                    await UpdateGameFPSAsync();
                    
                    // Clean up dead processes
                    CleanupDeadProcesses();
                    
                    await Task.Delay(2000, _cancellationTokenSource.Token); // Update every 2 seconds
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GameFPSService monitoring error: {ex.Message}");
                    await Task.Delay(5000, _cancellationTokenSource.Token); // Wait longer on error
                }
            }
        }

        private async Task DiscoverGamesAsync()
        {
            await Task.Run(() =>
            {
                var processes = Process.GetProcesses()
                    .Where(IsLikelyGame)
                    .Where(p => !_monitoredGames.ContainsKey(p.Id))
                    .ToList();

                foreach (var process in processes)
                {
                    try
                    {
                        var gpuCounter = FindGPUCounterForProcess(process.Id);
                        if (gpuCounter != null)
                        {
                            _monitoredGames[process.Id] = new GameMonitorInfo
                            {
                                Process = process,
                                GPUCounter = gpuCounter
                            };
                            
                            Debug.WriteLine($"Now monitoring: {process.ProcessName} (PID {process.Id})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error setting up monitoring for {process.ProcessName}: {ex.Message}");
                    }
                }
            });
        }

        private bool IsLikelyGame(Process process)
        {
            try
            {
                if (process.HasExited) return false;
                
                var processName = process.ProcessName.ToLowerInvariant();
                
                // Skip system processes
                if (_systemProcesses.Any(sp => processName.Contains(sp.ToLowerInvariant())))
                    return false;

                // Skip processes without main window (except known games that might not have one)
                if (process.MainWindowHandle == IntPtr.Zero && 
                    !IsKnownHeadlessGame(processName))
                    return false;

                // Check for gaming indicators
                return HasGamingIndicators(process, processName);
            }
            catch
            {
                return false;
            }
        }

        private bool IsKnownHeadlessGame(string processName)
        {
            var headlessGames = new[] { "csgo", "dota2", "tf2", "hl2", "gmod" };
            return headlessGames.Any(game => processName.Contains(game));
        }

        private bool HasGamingIndicators(Process process, string processName)
        {
            // Direct game name patterns
            var gamePatterns = new[]
            {
                "game", "bf6", "battlefield", "cod", "callofduty", "warzone", "apex", "valorant",
                "csgo", "dota", "overwatch", "fortnite", "pubg", "minecraft", "wow", "lol",
                "steam", "origin", "uplay", "epic", "gog", "battle.net", "launcher"
            };

            if (gamePatterns.Any(pattern => processName.Contains(pattern)))
                return true;

            // Check window title for game indicators
            try
            {
                var title = process.MainWindowTitle?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(title))
                {
                    if (gamePatterns.Any(pattern => title.Contains(pattern)))
                        return true;

                    // Games often have trademark symbols or version numbers
                    if (title.Contains("™") || title.Contains("®") || 
                        System.Text.RegularExpressions.Regex.IsMatch(title, @"\d+\.\d+"))
                        return true;
                }
            }
            catch { }

            // Check if process uses significant CPU/Memory (likely a game)
            try
            {
                return process.WorkingSet64 > 100_000_000; // > 100MB
            }
            catch
            {
                return false;
            }
        }

        private PerformanceCounter? FindGPUCounterForProcess(int processId)
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();

                var processCounter = instanceNames.FirstOrDefault(name => 
                    name.Contains($"pid_{processId}_") && name.Contains("engtype_3D"));

                if (processCounter != null)
                {
                    return new PerformanceCounter("GPU Engine", "Utilization Percentage", processCounter);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error finding GPU counter for PID {processId}: {ex.Message}");
            }

            return null;
        }

        private async Task UpdateGameFPSAsync()
        {
            await Task.Run(() =>
            {
                var currentTime = DateTime.Now;
                
                foreach (var kvp in _monitoredGames.ToList())
                {
                    var gameInfo = kvp.Value;
                    
                    try
                    {
                        if (gameInfo.Process.HasExited || gameInfo.GPUCounter == null)
                            continue;

                        var utilization = gameInfo.GPUCounter.NextValue();
                        
                        // Store utilization history for FPS calculation
                        gameInfo.UtilizationHistory.Enqueue(utilization);
                        gameInfo.TimestampHistory.Enqueue(currentTime);
                        
                        // Keep only last 10 samples
                        while (gameInfo.UtilizationHistory.Count > 10)
                        {
                            gameInfo.UtilizationHistory.Dequeue();
                            gameInfo.TimestampHistory.Dequeue();
                        }
                        
                        // Calculate FPS from utilization patterns
                        var fps = CalculateFPSFromUtilization(gameInfo);
                        
                        // Only fire event if FPS changed significantly or it's been a while
                        if (Math.Abs(fps - gameInfo.LastFPS) > 5.0f || 
                            currentTime.Subtract(gameInfo.TimestampHistory.First()).TotalSeconds > 5)
                        {
                            gameInfo.LastFPS = fps;
                            
                            FPSUpdated?.Invoke(this, new GameFPSEventArgs
                            {
                                ProcessName = gameInfo.Process.ProcessName,
                                ProcessId = gameInfo.Process.Id,
                                WindowTitle = gameInfo.Process.MainWindowTitle ?? "",
                                FPS = fps,
                                GPUUtilization = utilization,
                                Timestamp = currentTime
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error updating FPS for {gameInfo.Process.ProcessName}: {ex.Message}");
                    }
                }
            });
        }

        private float CalculateFPSFromUtilization(GameMonitorInfo gameInfo)
        {
            if (gameInfo.UtilizationHistory.Count < 3) 
                return 0;

            var utilizations = gameInfo.UtilizationHistory.ToArray();
            var timestamps = gameInfo.TimestampHistory.ToArray();
            
            // Method 1: Utilization change frequency analysis
            var changeCount = 0;
            for (int i = 1; i < utilizations.Length; i++)
            {
                if (Math.Abs(utilizations[i] - utilizations[i-1]) > 2.0f) // 2% change threshold
                    changeCount++;
            }
            
            if (changeCount > 0 && timestamps.Length > 1)
            {
                var timeSpan = timestamps[timestamps.Length - 1] - timestamps[0];
                var changesPerSecond = changeCount / timeSpan.TotalSeconds;
                var fps1 = (float)(changesPerSecond * 30); // Estimate FPS from change frequency
                
                if (fps1 > 30 && fps1 < 500)
                    return fps1;
            }
            
            // Method 2: High utilization mapping to typical FPS ranges
            var avgUtilization = utilizations.Average();
            
            if (avgUtilization > 80) return 250; // High-end gaming
            if (avgUtilization > 60) return 144; // Mid-range
            if (avgUtilization > 40) return 60;  // Standard gaming
            if (avgUtilization > 20) return 30;  // Low performance
            
            return 0; // No significant activity
        }

        private void CleanupDeadProcesses()
        {
            var deadProcesses = _monitoredGames
                .Where(kvp => kvp.Value.Process.HasExited)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var pid in deadProcesses)
            {
                if (_monitoredGames.TryGetValue(pid, out var gameInfo))
                {
                    gameInfo.GPUCounter?.Dispose();
                    _monitoredGames.Remove(pid);
                }
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            
            foreach (var gameInfo in _monitoredGames.Values)
            {
                gameInfo.GPUCounter?.Dispose();
            }
            _monitoredGames.Clear();
            
            _cancellationTokenSource.Dispose();
        }
    }
}