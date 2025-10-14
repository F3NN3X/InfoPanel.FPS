using System;
using System.Diagnostics;

namespace InfoPanel.FPS.Test
{
    class TestAntiCheat
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Testing Anti-Cheat Game Detection");
            Console.WriteLine("=================================");
            
            // Test with BF6 PID
            var bf6Process = Process.GetProcessesByName("bf6");
            if (bf6Process.Length > 0)
            {
                uint pid = (uint)bf6Process[0].Id;
                Console.WriteLine($"Found BF6 process: PID {pid}, Name: {bf6Process[0].ProcessName}");
                
                bool isAntiCheat = IsAntiCheatProtectedGame(pid);
                Console.WriteLine($"BF6 Anti-Cheat Detection Result: {isAntiCheat}");
            }
            else
            {
                Console.WriteLine("BF6 process not found");
            }
            
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        
        private static bool IsAntiCheatProtectedGame(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string processName = process.ProcessName.ToLowerInvariant();
                
                Console.WriteLine($"Checking process: '{processName}' (PID: {pid})");
                
                string[] antiCheatGames = 
                {
                    "bf6", "battlefield", "valorant", "apex", "fortnite", "pubg",
                    "rainbow6", "r6", "hunt", "deadbydaylight", "rust", "tarkov",
                    "destiny2", "warframe"
                };
                
                foreach (string gameName in antiCheatGames)
                {
                    if (processName.Contains(gameName))
                    {
                        Console.WriteLine($"MATCH FOUND: {processName} contains '{gameName}'");
                        return true;
                    }
                }
                
                Console.WriteLine($"No anti-cheat game match found for '{processName}'");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }
    }
}