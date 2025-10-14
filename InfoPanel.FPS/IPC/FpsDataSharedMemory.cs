using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace InfoPanel.FPS.IPC
{
    /// <summary>
    /// Shared memory structure for FPS data communication between elevated service and plugin.
    /// Uses memory-mapped files for efficient cross-process communication.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct FpsData
    {
        /// <summary>
        /// Magic number to validate data integrity (0x46505344 = "FPSD" in ASCII).
        /// </summary>
        public uint Magic;
        
        /// <summary>
        /// Process ID being monitored.
        /// </summary>
        public uint ProcessId;
        
        /// <summary>
        /// Current FPS value.
        /// </summary>
        public double Fps;
        
        /// <summary>
        /// Average frame time in milliseconds.
        /// </summary>
        public double FrameTime;
        
        /// <summary>
        /// 1% low FPS (lowest 1% of frame times).
        /// </summary>
        public double OneLowFps;
        
        /// <summary>
        /// Timestamp of last update (UTC ticks).
        /// </summary>
        public long LastUpdateTicks;
        
        /// <summary>
        /// Window title (fixed 256 byte buffer for UTF-8 string).
        /// </summary>
        public fixed byte WindowTitle[256];
        
        /// <summary>
        /// Whether monitoring is active.
        /// </summary>
        public byte IsMonitoring;
        
        public const uint MAGIC_NUMBER = 0x46505344; // "FPSD"
        public const string SHARED_MEMORY_NAME = "Global\\InfoPanelFPSData";
        
        /// <summary>
        /// Creates a new FpsData with default values.
        /// </summary>
        public static unsafe FpsData CreateDefault()
        {
            var data = new FpsData
            {
                Magic = MAGIC_NUMBER,
                ProcessId = 0,
                Fps = 0,
                FrameTime = 0,
                OneLowFps = 0,
                LastUpdateTicks = DateTime.UtcNow.Ticks,
                IsMonitoring = 0
            };
            
            // Clear window title buffer
            for (int i = 0; i < 256; i++)
                data.WindowTitle[i] = 0;
                
            return data;
        }
        
        /// <summary>
        /// Gets the window title as a string.
        /// </summary>
        public unsafe string GetWindowTitle()
        {
            fixed (byte* ptr = WindowTitle)
            {
                int length = 0;
                while (length < 256 && ptr[length] != 0)
                    length++;
                
                if (length == 0)
                    return string.Empty;
                
                return Encoding.UTF8.GetString(ptr, length);
            }
        }
        
        /// <summary>
        /// Sets the window title from a string.
        /// </summary>
        public unsafe void SetWindowTitle(string title)
        {
            fixed (byte* ptr = WindowTitle)
            {
                // Clear buffer
                for (int i = 0; i < 256; i++)
                    ptr[i] = 0;
                
                if (string.IsNullOrEmpty(title))
                    return;
                
                byte[] titleBytes = Encoding.UTF8.GetBytes(title);
                int copyLength = Math.Min(titleBytes.Length, 255); // Leave room for null terminator
                
                for (int i = 0; i < copyLength; i++)
                    ptr[i] = titleBytes[i];
            }
        }
        
        /// <summary>
        /// Validates that the data structure is valid.
        /// </summary>
        public bool IsValid()
        {
            return Magic == MAGIC_NUMBER && 
                   (DateTime.UtcNow.Ticks - LastUpdateTicks) < TimeSpan.FromSeconds(5).Ticks;
        }
    }
    
    /// <summary>
    /// Reader for shared FPS data (used by InfoPanel plugin).
    /// </summary>
    public class FpsDataReader : IDisposable
    {
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private readonly object _lock = new();
        
        /// <summary>
        /// Opens the shared memory for reading.
        /// </summary>
        public bool Open()
        {
            lock (_lock)
            {
                try
                {
                    // Try to open existing shared memory
                    _mmf = MemoryMappedFile.OpenExisting(
                        FpsData.SHARED_MEMORY_NAME, 
                        MemoryMappedFileRights.Read);
                    
                    _accessor = _mmf.CreateViewAccessor(
                        0, 
                        Marshal.SizeOf<FpsData>(), 
                        MemoryMappedFileAccess.Read);
                    
                    Console.WriteLine("FpsDataReader: Successfully opened shared memory");
                    return true;
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine("FpsDataReader: Shared memory not found (helper service not running?)");
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FpsDataReader: Error opening shared memory: {ex.Message}");
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Reads the current FPS data from shared memory.
        /// </summary>
        public bool TryRead(out FpsData data)
        {
            data = FpsData.CreateDefault();
            
            lock (_lock)
            {
                try
                {
                    if (_accessor == null)
                    {
                        // Try to reopen if not connected
                        if (!Open())
                            return false;
                    }
                    
                    _accessor!.Read(0, out data);
                    
                    // Validate data
                    if (!data.IsValid())
                    {
                        Console.WriteLine("FpsDataReader: Invalid or stale data in shared memory");
                        return false;
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FpsDataReader: Error reading shared memory: {ex.Message}");
                    
                    // Close and try to reopen next time
                    Close();
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Closes the shared memory.
        /// </summary>
        public void Close()
        {
            lock (_lock)
            {
                _accessor?.Dispose();
                _accessor = null;
                
                _mmf?.Dispose();
                _mmf = null;
            }
        }
        
        public void Dispose()
        {
            Close();
        }
    }
    
    /// <summary>
    /// Writer for shared FPS data (used by elevated helper service).
    /// </summary>
    public class FpsDataWriter : IDisposable
    {
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private readonly object _lock = new();
        
        /// <summary>
        /// Creates the shared memory for writing.
        /// </summary>
        public bool Create()
        {
            lock (_lock)
            {
                try
                {
                    // Create shared memory accessible by all processes
                    _mmf = MemoryMappedFile.CreateNew(
                        FpsData.SHARED_MEMORY_NAME,
                        Marshal.SizeOf<FpsData>(),
                        MemoryMappedFileAccess.ReadWrite);
                    
                    _accessor = _mmf.CreateViewAccessor(
                        0,
                        Marshal.SizeOf<FpsData>(),
                        MemoryMappedFileAccess.ReadWrite);
                    
                    // Initialize with default data
                    var defaultData = FpsData.CreateDefault();
                    _accessor.Write(0, ref defaultData);
                    
                    Console.WriteLine("FpsDataWriter: Successfully created shared memory");
                    return true;
                }
                catch (IOException)
                {
                    Console.WriteLine("FpsDataWriter: Shared memory already exists, opening existing");
                    
                    // Try to open existing
                    try
                    {
                        _mmf = MemoryMappedFile.OpenExisting(
                            FpsData.SHARED_MEMORY_NAME,
                            MemoryMappedFileRights.ReadWrite);
                        
                        _accessor = _mmf.CreateViewAccessor(
                            0,
                            Marshal.SizeOf<FpsData>(),
                            MemoryMappedFileAccess.ReadWrite);
                        
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FpsDataWriter: Error opening existing shared memory: {ex.Message}");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FpsDataWriter: Error creating shared memory: {ex.Message}");
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Writes FPS data to shared memory.
        /// </summary>
        public bool TryWrite(ref FpsData data)
        {
            lock (_lock)
            {
                try
                {
                    if (_accessor == null)
                    {
                        Console.WriteLine("FpsDataWriter: Accessor not initialized");
                        return false;
                    }
                    
                    // Update timestamp and magic number
                    data.Magic = FpsData.MAGIC_NUMBER;
                    data.LastUpdateTicks = DateTime.UtcNow.Ticks;
                    
                    _accessor.Write(0, ref data);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FpsDataWriter: Error writing to shared memory: {ex.Message}");
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Closes the shared memory.
        /// </summary>
        public void Close()
        {
            lock (_lock)
            {
                _accessor?.Dispose();
                _accessor = null;
                
                _mmf?.Dispose();
                _mmf = null;
            }
        }
        
        public void Dispose()
        {
            Close();
        }
    }
}
