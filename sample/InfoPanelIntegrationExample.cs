using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using InfoPanel.FPS.Services;

namespace InfoPanel.FPS
{
    /// <summary>
    /// Example integration of GameFPSService into InfoPanel.FPS plugin
    /// This replaces your existing RTSS integration
    /// </summary>
    [Export(typeof(IInfoPanelPlugin))]
    public class FPSPlugin : IInfoPanelPlugin
    {
        private GameFPSService? _fpsService;
        private string _currentFPS = "0";
        private string _currentGame = "No Game";

        public string Name => "FPS Monitor";
        public string Description => "Real-time FPS monitoring for any game";
        
        // This will be displayed in your InfoPanel
        public string DisplayValue => $"{_currentGame}: {_currentFPS} FPS";

        public async Task InitializeAsync()
        {
            _fpsService = new GameFPSService();
            
            // Subscribe to FPS updates
            _fpsService.FPSUpdated += OnFPSUpdated;
            
            // Start monitoring
            await _fpsService.StartAsync();
        }

        private void OnFPSUpdated(object? sender, GameFPSService.GameFPSEventArgs e)
        {
            // Update display values
            _currentGame = e.ProcessName;
            _currentFPS = e.FPS.ToString("F0");
            
            // You can also filter for specific games
            if (IsTargetGame(e.ProcessName))
            {
                // Handle specific games like BF6
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {e.ProcessName}: {e.FPS:F1} FPS (GPU: {e.GPUUtilization:F1}%)");
            }
            
            // Trigger UI update (replace with your InfoPanel's update mechanism)
            NotifyDisplayUpdate();
        }

        private bool IsTargetGame(string processName)
        {
            var targetGames = new[] { "bf6", "battlefield", "cod", "valorant", "csgo" };
            return targetGames.Any(game => 
                processName.ToLowerInvariant().Contains(game));
        }

        private void NotifyDisplayUpdate()
        {
            // Replace this with your InfoPanel's property change notification
            // For example: PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
        }

        public async Task ShutdownAsync()
        {
            if (_fpsService != null)
            {
                await _fpsService.StopAsync();
                _fpsService.Dispose();
                _fpsService = null;
            }
        }

        public void Dispose()
        {
            Task.Run(async () => await ShutdownAsync()).Wait();
        }
    }

    // Placeholder interfaces - replace with your actual InfoPanel interfaces
    public interface IInfoPanelPlugin : IDisposable
    {
        string Name { get; }
        string Description { get; }
        string DisplayValue { get; }
        Task InitializeAsync();
        Task ShutdownAsync();
    }
}