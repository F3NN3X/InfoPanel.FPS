# RTSS Troubleshooting Guide

## Issue: RTSS Connected but No FPS Data

If you see in the logs:
- ✅ `RTSSIntegrationService: Successfully connected to RTSS!`
- ❌ `RTSSIntegrationService: No valid FPS data found in shared memory`

This means RTSS is running but not actively monitoring your game.

## Quick Fixes

### 1. Enable RTSS Overlay for Battlefield 6
1. Open **RivaTuner Statistics Server**
2. Look for **bf6.exe** (or Battlefield process) in the application list
3. If it's not there, click **Add** and browse to your Battlefield 6 executable
4. Set **Application detection level** to **High** or **Medium**
5. Enable **Show own statistics** checkbox
6. Set **On-Screen Display support** to **On**

### 2. RTSS Settings Check
Make sure these settings are enabled in RTSS:
- **Enable RTSSSharedMemorySupport** = On
- **Shared Memory Support** = On  
- **Application detection level** = Medium or High

### 3. Alternative: Use Global RTSS Monitoring
If game-specific detection fails:
1. In RTSS main window, set **Global** profile to active
2. Enable overlay globally instead of per-application
3. This will monitor all running applications

### 4. Verify RTSS is Actually Monitoring
1. Launch Battlefield 6
2. You should see RTSS overlay in-game showing FPS
3. If no overlay appears, RTSS isn't monitoring the game

### 5. Try Different RTSS Versions
- RTSS 7.3.4 is most compatible
- Some newer versions have compatibility issues with anti-cheat

## If Still Not Working

Run this batch script to check RTSS status:

```batch
@echo off
echo Checking RTSS processes...
tasklist /fi "imagename eq RTSSHooksLoader*.exe"
tasklist /fi "imagename eq RTSS.exe"
echo.
echo Checking shared memory...
powershell -Command "Get-Process | Where-Object {$_.ProcessName -like '*RTSS*'} | Select-Object ProcessName,Id"
pause
```

## Fallback Solution

If RTSS continues to have issues, you can switch back to PresentMon mode:

1. Edit `C:\ProgramData\InfoPanel\plugins\InfoPanel.FPS\InfoPanel.FPS.ini`
2. Change:
   ```ini
   useRTSS=false
   usePresentMon=true
   ```
3. Restart InfoPanel

Note: PresentMon may not work with kernel-level anti-cheat, but it's more reliable for supported games.