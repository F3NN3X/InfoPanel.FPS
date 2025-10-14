using InfoPanel.FPS.Services;

Console.WriteLine("=== RTSS BF6 Test ===");
Console.WriteLine($"Target PID: 75764 (bf6)");
Console.WriteLine();

var rtss = new RTSSIntegrationService();

if (rtss.Initialize())
{
    Console.WriteLine("✓ RTSS Connected!");
    Console.WriteLine();
    Console.WriteLine("Polling for BF6 FPS data (10 readings)...");
    Console.WriteLine();
    
    for (int i = 0; i < 10; i++)
    {
        var metrics = rtss.GetFpsData(75764);
        
        if (metrics != null)
        {
            Console.WriteLine($"Reading {i+1}: FPS={metrics.Fps:F1}, FrameTime={metrics.FrameTime:F2}ms, 1%Low={metrics.OnePercentLowFps:F1}");
        }
        else
        {
            Console.WriteLine($"Reading {i+1}: No data");
        }
        
        Thread.Sleep(1000);
    }
}
else
{
    Console.WriteLine("✗ RTSS Connection Failed!");
    Console.WriteLine("Make sure RTSS/MSI Afterburner is running");
}

rtss.Dispose();
Console.WriteLine();
Console.WriteLine("Test complete. Press any key to exit...");
Console.ReadKey();
