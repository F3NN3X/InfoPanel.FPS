using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DravusSensorPanel.Models.Sensors;
using DravusSensorPanel.Models.Units;
using DravusSensorPanel.Repositories;
using LibreHardwareMonitor.Hardware;

namespace DravusSensorPanel.Services.InfoExtractors;

/// <summary>
///     Extracts FPS from the RivaTuner Statistics Server shared memory (RTSSSharedMemoryV1‑V3)
/// </summary>
public unsafe class RtssHardwareExtractor : InfoExtractor, IDisposable {
    private const string FpsUnitId = "system-fps";
    private const string FpsSourceId = "rtss-fps";


    public static readonly Dictionary<string, Unit> UnitsByName = new() {
        { FpsUnitId, new UnitWithoutConversion(FpsUnitId, "Frames per Second", "FPS") },
    };

    public override string SourceName => "RTSS";

    private const uint ExpectedSignature = 0x52545353;

    private readonly string[] _sharedMemoryNames = {
        "RTSSSharedMemoryV3",
        "RTSSSharedMemoryV2",
        "RTSSSharedMemoryV1",
    };

    private IntPtr _hMapFile = IntPtr.Zero;
    private byte* _pBase = null;
    private bool _started;

    public RtssHardwareExtractor(SensorRepository sensorRepository) : base(sensorRepository) {
        SensorRepository = sensorRepository;
    }

#region P/Invoke ------------------------------------------------------

    [Flags]
    private enum FileMapAccess : uint {
        // ReSharper disable once InconsistentNaming
        FILE_MAP_READ = 0x0004,
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenFileMapping(FileMapAccess access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr hMapping,
        FileMapAccess access,
        uint offHigh,
        uint offLow,
        UIntPtr bytesToMap);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr addr);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

#endregion

#region Shared‑memory structures -------------------------------------

    // 36‑byte header (first 9 DWORDs) plus FrameTimeUs at 0x2E0 (new in RTSS 7.3)
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    private struct SharedMemoryHeader {
        [FieldOffset(0x000)]
        public uint Signature;

        [FieldOffset(0x004)]
        public uint Version;

        [FieldOffset(0x008)]
        public uint AppEntrySize;

        [FieldOffset(0x00C)]
        public uint AppArrOffset;

        [FieldOffset(0x010)]
        public uint AppArrSize;

        [FieldOffset(0x014)]
        public uint OSDEntrySize;

        [FieldOffset(0x018)]
        public uint OSDArrOffset;

        [FieldOffset(0x01C)]
        public uint OSDArrSize;

        [FieldOffset(0x2E0)]
        public uint FrameTimeUs; // 0 when unsupported
    }

    // Layout of one element in the Application array (size reported in header)
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct AppEntry {
        public uint ProcessId;
        public fixed byte ProcessName[260];
        public uint Flags;
        public uint Time0; // milliseconds
        public uint Time1; // milliseconds
        public uint Frames;
        public uint FrameTime; // µs (>= 7.3) or 0
    }

#endregion

#region IInfoExtractor ------------------------------------------------

    public override List<Sensor> Start() {
        if ( !_started ) {
            _started = true;

            SensorRepository.AddSensor(new NumberSensor {
                Id = Guid.NewGuid().ToString(),
                Source = SourceName,
                SourceId = FpsSourceId,
                Type = SensorType.Data,
                Hardware = "System",
                Name = "FPS",
                Unit = UnitsByName[FpsUnitId],
                InfoExtractor = this,
            });
        }

        foreach ( string name in _sharedMemoryNames ) {
            _hMapFile = OpenFileMapping(FileMapAccess.FILE_MAP_READ, false, name);
            if ( _hMapFile != IntPtr.Zero ) {
                Console.WriteLine($"[RTSS] mapped shared‑memory: {name}");
                break;
            }
        }

        if ( _hMapFile == IntPtr.Zero ) {
            return SensorRepository.GetAllSensors(SourceName);
        }

        _pBase = ( byte* ) MapViewOfFile(_hMapFile, FileMapAccess.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        if ( _pBase == null ) {
            CloseHandle(_hMapFile);
            return SensorRepository.GetAllSensors(SourceName);
        }

        return Extract();
    }

    protected override void InternalUpdate() {
        if ( !_started ) {
            Start();
        }
        else {
            Extract();
        }
    }

    public override void Dispose() {
        if ( _pBase != null ) {
            UnmapViewOfFile(( IntPtr ) _pBase);
            _pBase = null;
        }

        if ( _hMapFile != IntPtr.Zero ) {
            CloseHandle(_hMapFile);
            _hMapFile = IntPtr.Zero;
        }
    }

#endregion

#region Internals -----------------------------------------------------

    private List<Sensor> Extract() {
        if ( _pBase == null ) return SensorRepository.GetAllSensors(SourceName);

        var header = Marshal.PtrToStructure<SharedMemoryHeader>(( IntPtr ) _pBase);
        if ( header.Signature != ExpectedSignature ) return SensorRepository.GetAllSensors(SourceName);

        if ( header.AppArrSize == 0 || header.AppEntrySize == 0 ) {
            return SensorRepository.GetAllSensors(SourceName);
        }

        byte* appBase = _pBase + header.AppArrOffset;
        uint entries = header.AppArrSize;

        for ( uint i = 0; i < entries; i++ ) {
            IntPtr entryPtr = ( IntPtr ) ( appBase + i * header.AppEntrySize );
            var entry = Marshal.PtrToStructure<AppEntry>(entryPtr);

            if ( entry.ProcessId == 0 ) continue;

            uint targetPid;
            IntPtr hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out targetPid);

            if ( entry.ProcessId != targetPid ) {
                continue;
            }

            NewFps(entry.Frames);
        }

        return SensorRepository.GetAllSensors(SourceName);
    }

    private void NewFps(float fps) {
        var s = SensorRepository.FindSensor<NumberSensor>(SourceName, FpsSourceId);
        s.UpdateValue(fps, DateTime.Now, true);
    }

#endregion
}
