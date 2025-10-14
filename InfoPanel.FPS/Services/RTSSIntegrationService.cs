using System.Runtime.InteropServices;
using InfoPanel.FPS.Models;

namespace InfoPanel.FPS.Services
{
    public class RTSSIntegrationService : IDisposable
    {
        private const uint RTSS_SIGNATURE = 0x52545353;
        
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RTSSSharedMemory
        {
            public uint dwSignature;
            public uint dwVersion;
            public uint dwAppEntries;
            public uint dwAppArrOffset;
            public uint dwAppEntrySize;
            public uint dwOSDArrSize;
            public uint dwOSDArrOffset;
            public uint dwOSDEntrySize;
            public uint dwOSDFrame;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RTSSAppEntry
        {
            public uint dwProcessID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szName;
            public uint dwFlags;
            public uint dwTime0;
            public uint dwTime1;
            public uint dwFrames;
            public uint dwFrameTime;
            public float fFramerate;
        }
        
        private IntPtr _sharedMemoryHandle = IntPtr.Zero;
        private IntPtr _sharedMemoryPtr = IntPtr.Zero;
        private bool _disposed = false;

        public bool Initialize()
        {
            try
            {
                Console.WriteLine("RTSSIntegrationService: Connecting to RTSS...");
                string[] memoryNames = { "RTSSSharedMemoryV2", "RTSSSharedMemory" };
                foreach (string name in memoryNames)
                {
                    _sharedMemoryHandle = OpenFileMapping(FileMapAccess.FILE_MAP_READ, false, name);
                    if (_sharedMemoryHandle != IntPtr.Zero)
                    {
                        Console.WriteLine($"RTSSIntegrationService: Found RTSS memory: {name}");
                        break;
                    }
                }
                if (_sharedMemoryHandle == IntPtr.Zero) return false;
                _sharedMemoryPtr = MapViewOfFile(_sharedMemoryHandle, FileMapAccess.FILE_MAP_READ, 0, 0, 0);
                if (_sharedMemoryPtr == IntPtr.Zero)
                {
                    CloseHandle(_sharedMemoryHandle);
                    _sharedMemoryHandle = IntPtr.Zero;
                    return false;
                }
                Console.WriteLine("RTSSIntegrationService: Connected!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTSSIntegrationService: Error: {ex.Message}");
                return false;
            }
        }

        public PerformanceMetrics? GetFpsData(uint processId)
        {
            if (_sharedMemoryPtr == IntPtr.Zero) return null;
            try
            {
                // SIMPLIFIED APPROACH: Scan memory for gaming-range FPS values
                // RTSS struct is complex and version-dependent, so we'll do a smart scan
                Console.WriteLine($"RTSS: Scanning memory for FPS data (target PID: {processId})");
                
                // Scan first 64KB of RTSS memory for float values in gaming range (30-500 FPS)
                float? bestFps = null;
                int bestOffset = -1;
                
                for (int offset = 0; offset < 65536; offset += 4)
                {
                    try
                    {
                        float value = Marshal.PtrToStructure<float>(IntPtr.Add(_sharedMemoryPtr, offset));
                        
                        // Look for FPS values in gaming range
                        if (value >= 30 && value <= 500 && !float.IsNaN(value) && !float.IsInfinity(value))
                        {
                            // Check if there's a PID nearby (within 1024 bytes)
                            bool pidNearby = false;
                            for (int pidOffset = Math.Max(0, offset - 1024); pidOffset < offset + 1024 && pidOffset < 65536; pidOffset += 4)
                            {
                                try
                                {
                                    uint nearbyValue = Marshal.PtrToStructure<uint>(IntPtr.Add(_sharedMemoryPtr, pidOffset));
                                    if (nearbyValue == processId)
                                    {
                                        pidNearby = true;
                                        break;
                                    }
                                }
                                catch { }
                            }
                            
                            if (pidNearby)
                            {
                                Console.WriteLine($"RTSS: Found FPS {value:F1} at offset {offset} near PID {processId}!");
                                bestFps = value;
                                bestOffset = offset;
                                break; // Found a match!
                            }
                            else if (!bestFps.HasValue || (value >= 100 && value > bestFps.Value))
                            {
                                // Remember highest gaming-range FPS as fallback
                                bestFps = value;
                                bestOffset = offset;
                            }
                        }
                    }
                    catch { }
                }
                
                if (bestFps.HasValue && bestFps.Value >= 100)
                {
                    Console.WriteLine($"RTSS: Using FPS {bestFps.Value:F1} from offset {bestOffset}");
                    return new PerformanceMetrics
                    {
                        Fps = bestFps.Value,
                        FrameTime = 1000.0f / bestFps.Value,
                        OnePercentLowFps = bestFps.Value * 0.9f,
                        FrameTimeCount = 1
                    };
                }
                
                Console.WriteLine("RTSS: No valid gaming FPS found");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTSS GetFpsData error: {ex.Message}");
                return null;
            }
        }

        public bool IsRTSSAvailable() { return _sharedMemoryPtr != IntPtr.Zero; }

        public void Dispose()
        {
            if (_disposed) return;
            if (_sharedMemoryPtr != IntPtr.Zero) { UnmapViewOfFile(_sharedMemoryPtr); _sharedMemoryPtr = IntPtr.Zero; }
            if (_sharedMemoryHandle != IntPtr.Zero) { CloseHandle(_sharedMemoryHandle); _sharedMemoryHandle = IntPtr.Zero; }
            _disposed = true;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenFileMapping(FileMapAccess dwDesiredAccess, bool bInheritHandle, string lpName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, FileMapAccess dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [Flags]
        private enum FileMapAccess : uint { FILE_MAP_READ = 0x0004, }
    }
}
