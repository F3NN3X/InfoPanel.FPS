# InfoPanel.FPS Plugin

A plugin for the InfoPanel app that reads FPS data directly from RivaTuner Statistics Server (RTSS) to monitor and display real-time performance metrics for fullscreen applications with anti-cheat compatibility.

## Overview

InfoPanel.FPS provides detailed performance statistics for running fullscreen applications, enabling users to monitor gaming performance in real-time through InfoPanel's interface. By leveraging RTSS's shared memory, the plugin achieves pixel-perfect FPS accuracy while remaining compatible with kernel-level anti-cheat systems. The plugin tracks FPS, frame times, and low percentile data, updating every second with efficient event-driven detection.

![InfoPanel.FPS Screenshot](https://i.imgur.com/VsyjMRh.png)

**Version:** 1.1.6  
**Author:** F3NN3X

## Features

* **Anti-Cheat Compatible**: Uses RTSS shared memory for non-invasive FPS monitoring, compatible with kernel-level anti-cheat systems (BattlEye, EAC, Vanguard, etc.)
* **Real-time Performance Monitoring**: Tracks and displays performance metrics for fullscreen applications with second-by-second updates.
* **Multiple Performance Metrics**:
  * Current Frames Per Second (FPS) - read directly from RTSS overlay
  * Frame time in milliseconds - calculated for pixel-perfect accuracy
  * 1% Low FPS (99th percentile) - 100-frame rolling window for stutter detection
* **Display Information**:
  * Main display resolution (e.g., "3840x2160")
  * Main display refresh rate (e.g., "240Hz")
  * GPU name detection
* **Window Title Reporting**: Shows the title of the currently monitored fullscreen application.
* **Efficient Resource Usage**:
  * Event-driven detection ensures immediate startup when fullscreen apps launch
  * Proper cleanup and metric clearing when fullscreen apps close
  * Optimized calculations with minimal resource overhead
  * Thread-safe sensor updates prevent crashes during rapid state changes
* **Multi-monitor Support**: Accurate fullscreen detection on multiple monitor setups.
* **Universal Game Support**: Works with any game without hardcoded process names or special handling.

## Requirements

* InfoPanel app (latest version recommended)
* Windows operating system
* **RivaTuner Statistics Server (RTSS)** - Required for FPS monitoring
  * Download from: [Guru3D - RivaTuner Statistics Server](https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html)
  * Or install MSI Afterburner (includes RTSS): [MSI Afterburner](https://www.msi.com/Landing/afterburner)
  * RTSS must be running for the plugin to capture FPS data

## RTSS Setup & Configuration

### What is RTSS?

RivaTuner Statistics Server (RTSS) is a powerful overlay application that provides on-screen display (OSD) functionality for monitoring hardware statistics and FPS in games. This plugin reads FPS data directly from RTSS's shared memory, making it compatible with anti-cheat protected games.

### Installation

1. **Download and Install RTSS**:
   - Standalone: Download from [Guru3D](https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html)
   - Or install MSI Afterburner which includes RTSS

2. **Configure RTSS**:
   - Launch RTSS (RivaTunerStatisticsServer.exe)
   - Make sure RTSS is running and enabled (system tray icon should be visible)
   - RTSS will automatically hook into games when they launch

3. **Verify Operation**:
   - Launch a fullscreen game
   - You should see the RTSS overlay in-game (if enabled)
   - InfoPanel.FPS will read the same FPS values RTSS displays

### Important Notes

- **RTSS Must Be Running**: The plugin requires RTSS to be running before launching games. If RTSS is not detected, no FPS data will be available.
  
- **Hook Timing**: RTSS may take a few seconds to hook into newly launched games (up to 60 seconds for first launch). During this time:
  - Window title may show "Nothing to capture"
  - FPS data will appear once RTSS hooks the game
  - This is normal behavior and not a plugin error

- **Overlay Not Required**: You don't need to have RTSS's on-screen overlay enabled. The plugin reads from shared memory regardless of overlay visibility.

- **Anti-Cheat Compatibility**: RTSS is widely accepted by anti-cheat systems because it uses non-invasive DirectX hooking. This plugin reads data passively from RTSS without any injection.

- **Pixel-Perfect Accuracy**: FPS values match RTSS overlay exactly, calculated from the same data source (RTSS Frames field at offset 276).

### Troubleshooting

**No FPS Data Displayed:**
- Verify RTSS is running (check system tray)
- Launch the game and wait 10-60 seconds for RTSS to hook
- Check RTSS settings to ensure game hooking is enabled
- Some games may need to be added to RTSS's application profile

**Window Title Shows "Nothing to capture":**
- This typically means RTSS hasn't hooked the game yet
- Wait a few more seconds - title will appear once RTSS connects
- If it persists, restart RTSS and relaunch the game

**For detailed RTSS troubleshooting, see [RTSS-TROUBLESHOOTING.md](RTSS-TROUBLESHOOTING.md)**

## To compile

* .NET runtime compatible with InfoPanel
* .NET 8.0 Windows SDK

## Installation

1. Download the latest release from GitHub.
2. Import into InfoPanel via the "Import Plugin" feature.
3. The plugin will automatically start monitoring fullscreen applications.

## Installation from Source

1. Clone or download this repository.
2. Build the project in a .NET environment.
3. Copy the compiled DLLs and dependencies to your InfoPanel plugins folder.

## Usage

1. **Ensure RTSS is Running**: Launch RivaTuner Statistics Server before starting games.
2. **Launch InfoPanel**: Start InfoPanel with the plugin loaded.
3. **Start Your Game**: Launch any fullscreen game or application.
4. **Automatic Detection**: The plugin automatically detects fullscreen applications and begins monitoring.
5. **View Metrics**: Real-time performance metrics appear in InfoPanel's UI:
   - Current FPS (matching RTSS overlay exactly)
   - Frame time in milliseconds
   - 1% Low FPS for stutter analysis
   - Window title, resolution, refresh rate, GPU name
6. **Automatic Cleanup**: Metrics reset when fullscreen applications are closed.

### Supported Games

This plugin works with **any game** that RTSS can hook, including those with anti-cheat protection:
- Battlefield 2042/6 (Javelin anti-cheat)
- Gray Zone Warfare
- Valorant (Vanguard anti-cheat)
- Apex Legends (EAC)
- PUBG (BattlEye)
- Rainbow Six Siege
- No Man's Sky
- Deadside
- And many more...

## Notes

* **RTSS Required**: This plugin requires RTSS to be running. Without RTSS, no FPS data will be available.
* **Hook Delay**: On first game launch, RTSS may take 10-60 seconds to hook the game. This is normal - be patient.
* **Accuracy**: FPS values are read directly from RTSS's Frames field, ensuring pixel-perfect accuracy matching the overlay.
* For detailed version history, please refer to the `CHANGELOG.md` file.
