# InfoPanel FPS Helper Service

## Overview
The **InfoPanel FPS Helper Service** is an elevated Windows application that provides accurate FPS monitoring for anti-cheat protected games through PresentMon ETW (Event Tracing for Windows).

## Why Is This Needed?
- **Anti-cheat games** (Battlefield 6, Valorant, Apex Legends, etc.) use Easy Anti-Cheat (EAC) or BattlEye
- These anti-cheat systems block standard monitoring approaches
- **PresentMon ETW requires administrator privileges** to capture frame presentation events
- InfoPanel cannot run elevated due to security restrictions
- **Solution**: Separate elevated helper service shares FPS data with InfoPanel via shared memory

## Architecture
```
┌────────────────────────────────┐
│   InfoPanel (Non-Elevated)     │
│   - Runs normally               │
│   - Reads FPS from shared memory│
│   - Displays on panels          │
└────────────┬───────────────────┘
             │ Shared Memory
             │ (Memory-Mapped File)
┌────────────▼───────────────────┐
│  Helper Service (Elevated)     │
│  - Runs as Administrator       │
│  - PresentMon ETW monitoring   │
│  - Writes FPS to shared memory │
└────────────────────────────────┘
```

## Installation & Usage

### Step 1: Build the Helper Service
```powershell
cd E:\GitHub\MyRepos\InfoPanel.FPS
dotnet build -c Release
```

### Step 2: Locate the Helper Service
The helper service executable will be in:
```
InfoPanel.FPS.HelperService\bin\x64\Release\net8.0-windows\
```

**Important Files**:
- `InfoPanelFPSHelper.exe` - Main executable (must run as admin)
- `PresentMonFps.dll` - PresentMon library
- `InfoPanel.FPS.dll` - Shared code

### Step 3: Start the Helper Service
**Right-click** `InfoPanelFPSHelper.exe` → **Run as Administrator**

You should see:
```
╔════════════════════════════════════════════════════════╗
║       InfoPanel FPS Helper Service v1.0                ║
║       Elevated ETW Monitoring Service                  ║
╚════════════════════════════════════════════════════════╝

Service Time: [timestamp]
Process ID: [pid]
Is Administrator: True
Shared memory created successfully
Memory Name: Global\InfoPanelFPSData
Helper service started - waiting for monitoring requests...
```

### Step 4: Deploy Updated InfoPanel Plugin
```powershell
Copy-Item "InfoPanel.FPS\bin\Release\net8.0-windows\InfoPanel.FPS-v1.1.5\InfoPanel.FPS\*" `
          "C:\ProgramData\InfoPanel\plugins\InfoPanel.FPS\" -Recurse -Force
```

### Step 5: Start InfoPanel
Run InfoPanel normally (no admin needed). The plugin will automatically connect to the helper service via shared memory.

### Step 6: Test with BF6
1. Start Battlefield 6 (or any supported game)
2. Helper service will automatically detect the game and start monitoring
3. InfoPanel will display accurate FPS data

## Supported Games
The helper service automatically detects and monitors:
- **Battlefield series** (bf6, battlefield)
- **Valheim**
- **Apex Legends**
- **Valorant**
- **Fortnite, Warzone, Modern Warfare**
- **Destiny 2**
- **Cyberpunk 2077, Witcher, Skyrim, Fallout**
- **GTA 5, Red Dead Redemption 2**
- **Elden Ring, Dark Souls**
- And many more!

## Troubleshooting

### "ERROR: Service must run with administrator privileges!"
**Solution**: Right-click the exe and select "Run as Administrator"

### "Helper service not running - elevated monitoring unavailable"
**Solution**: Start `InfoPanelFPSHelper.exe` as administrator before launching InfoPanel

### "No data from helper service"
**Causes**:
1. Helper service crashed or wasn't started
2. No gaming process detected
3. Firewall blocking shared memory access

**Solution**: Check helper service console for error messages

### BF6 Not Being Detected
**Solution**: The helper service uses process name detection. If BF6 isn't detected, check that it appears in Task Manager as "bf6.exe"

## Auto-Start (Optional)
To automatically start the helper service on Windows boot:

### Option 1: Task Scheduler
1. Open **Task Scheduler**
2. Create Task → **Run with highest privileges**
3. Trigger: **At log on**
4. Action: Start `InfoPanelFPSHelper.exe`

### Option 2: Windows Service (Advanced)
Convert the helper to a Windows Service using NSSM (Non-Sucking Service Manager):
```powershell
nssm install InfoPanelFPSHelper "C:\Path\To\InfoPanelFPSHelper.exe"
nssm set InfoPanelFPSHelper AppElevate 1
nssm start InfoPanelFPSHelper
```

## Technical Details

### Shared Memory Protocol
- **Name**: `Global\InfoPanelFPSData`
- **Size**: 540 bytes (FpsData structure)
- **Update Rate**: 10Hz (100ms intervals)
- **Magic Number**: `0x46505344` ("FPSD")

### FpsData Structure
```csharp
struct FpsData {
    uint Magic;              // Validation marker
    uint ProcessId;          // Monitored process PID
    double Fps;              // Current FPS
    double FrameTime;        // Frame time in ms
    double OneLowFps;        // 1% low FPS
    long LastUpdateTicks;    // UTC timestamp
    byte[256] WindowTitle;   // Process name
    byte IsMonitoring;       // Monitoring status
}
```

### PresentMon Integration
- Uses **PresentMonFps v2.0.5** library
- Captures frame presentation events via **ETW**
- Tracks up to **1000 frame times** for percentile calculations
- **Requires admin** due to ETW system-level access

## Security Considerations
- Helper service **requires elevation** for ETW access
- **Does NOT inject into game processes** (anti-cheat safe)
- **Does NOT access game memory** (anti-cheat safe)
- Uses Windows ETW (legitimate system API)
- Shared memory uses **read-only access** from InfoPanel side

## Performance
- **CPU Usage**: < 0.5% (typical)
- **Memory**: ~20MB
- **Latency**: 100ms (10Hz update rate)
- **No impact on game performance**

## Uninstallation
1. Close helper service (Ctrl+C in console or close window)
2. Delete helper service files
3. Revert InfoPanel plugin to previous version if needed

## Support
For issues or questions:
- Check debug log in InfoPanel plugin
- Check helper service console output
- Review BF6 anti-cheat compatibility notes

## Version History
- **v1.0.0** (October 14, 2025): Initial release with BF6 anti-cheat support
