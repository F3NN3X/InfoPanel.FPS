# InfoPanel.FPS Plugin

A plugin for the InfoPanel app that leverages `PresentMonFps` to monitor and display real-time performance metrics for fullscreen applications.

**NOTE!** This version is not compatible with games that use kernel level anitcheat, like Battlefield 6, Valorant etc. 
To capture FPS and metrics for games like that, you can use InfoPanel.RTSS. https://github.com/F3NN3X/InfoPanel.RTSS

## Overview

InfoPanel.FPS provides detailed performance statistics for running fullscreen applications, enabling users to monitor gaming performance in real-time through InfoPanel's interface. The plugin tracks FPS, frame times, and low percentile data, updating every second with efficient event-driven detection.

![InfoPanel.FPS Screenshot](https://i.imgur.com/VsyjMRh.png)

**Version:** 1.1.1  
**Author:** F3NN3X

## Features

* **Real-time Performance Monitoring**: Tracks and displays performance metrics for fullscreen applications with second-by-second updates.
* **Multiple Performance Metrics**:
  * Current Frames Per Second (FPS)
  * Frame time in milliseconds
  * 1% Low FPS (99th percentile) for stutter detection
* **Display Information**:
  * Main display resolution (e.g., "3840x2160")
  * Main display refresh rate (e.g., "240Hz")
* **Window Title Reporting**: Shows the title of the currently monitored fullscreen application.
* **Efficient Resource Usage**:
  * Event-driven detection ensures immediate startup when fullscreen apps launch
  * Proper cleanup and metric clearing when fullscreen apps close
  * Optimized calculations with minimal resource overhead
* **Multi-monitor Support**: Accurate fullscreen detection on multiple monitor setups.

## Requirements

* InfoPanel app (latest version recommended)
* Windows operating system

## To compile

* .NET runtime compatible with InfoPanel
* PresentMonFps dependency (included in release package)

## Installation

1. Download the latest release from GitHub.
2. Import into InfoPanel via the "Import Plugin" feature.
3. The plugin will automatically start monitoring fullscreen applications.

## Releasing

1. Bump `<Version>` in `InfoPanel.FPS/InfoPanel.FPS.csproj` and `Version` in `PluginInfo.ini`, and add a `CHANGELOG.md` entry for the new version.
2. Commit, then push a tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.

The `Release Build` workflow builds the plugin, checks the tag matches the csproj version, and publishes the zip as a GitHub release. The plugin API comes from the committed `Libs/InfoPanel.Plugins.dll`, so no local InfoPanel checkout is needed.

## Installation from Source

1. Clone or download this repository.
2. Build it: `dotnet build InfoPanel.FPS.sln -c Release` (.NET 8 SDK; the plugin API comes from `Libs/InfoPanel.Plugins.dll`).
3. The build writes the plugin folder to `InfoPanel.FPS\bin\Release\net8.0-windows\InfoPanel.FPS-v{version}\InfoPanel.FPS\` and its zip, `InfoPanel.FPS-v{version}.zip`, to `InfoPanel.FPS\bin\Release\net8.0-windows\`.
4. Copy the `InfoPanel.FPS` folder to `C:\ProgramData\InfoPanel\plugins\`, or import the zip in InfoPanel.

## Usage

* Launch InfoPanel with the plugin loaded.
* The plugin automatically detects and monitors fullscreen applications.
* View real-time performance metrics in InfoPanel's UI.
* Metrics reset when fullscreen applications are closed.

## Notes

* A benign log error (`"Array is variable sized and does not follow prefix convention"`) may appear but does not impact functionality.
* For detailed version history, please refer to the `CHANGELOG.md` file.
