using System;
using System.Runtime.InteropServices;

public class TestMSIAfterburner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    
    [DllImport("kernel32.dll")]
    private static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offsetHigh, uint offsetLow, UIntPtr size);
    
    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr ptr);
    
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    
    public static unsafe void Main()
    {
        IntPtr h = OpenFileMapping(4, false, "MSIAfterburner");
        if (h == IntPtr.Zero) 
        {
            Console.WriteLine("Cannot open MSIAfterburner shared memory");
            return;
        }
        
        IntPtr ptr = MapViewOfFile(h, 4, 0, 0, UIntPtr.Zero);
        if (ptr == IntPtr.Zero) 
        {
            Console.WriteLine("Cannot map MSIAfterburner shared memory");
            CloseHandle(h);
            return;
        }
        
        Console.WriteLine("Successfully opened MSIAfterburner shared memory!");
        
        byte* mem = (byte*)ptr;
        
        // Print first 64 bytes as hex
        Console.Write("First 64 bytes: ");
        for (int i = 0; i < 64; i++)
        {
            Console.Write($"{mem[i]:X2} ");
        }
        Console.WriteLine();
        
        // Scan for FPS-like values
        Console.WriteLine("\nScanning for FPS values (30-500):");
        int found = 0;
        for (int i = 0; i < 200000 && found < 30; i += 4)
        {
            float val = *(float*)(mem + i);
            if (val >= 30 && val <= 500 && !float.IsNaN(val) && !float.IsInfinity(val))
            {
                Console.WriteLine($"  Offset {i}: {val:F2}");
                found++;
            }
        }
        
        if (found == 0)
        {
            Console.WriteLine("  No FPS values found!");
        }
        
        UnmapViewOfFile(ptr);
        CloseHandle(h);
    }
}
