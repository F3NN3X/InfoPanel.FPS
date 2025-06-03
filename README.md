# InfoPanel.FPS Plugin

A plugin for the InfoPanel app that leverages `PresentMonFps` to monitor and display real-time performance metrics for fullscreen applications.

## Overview

InfoPanel.FPS provides detailed performance statistics for running fullscreen applications, enabling users to monitor gaming performance in real-time through InfoPanel's interface. The plugin tracks FPS, frame times, and low percentile data, updating every second with efficient event-driven detection.

**Version:** 1.0.16  
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

* InfoPanel framework (latest version recommended)
* Windows operating system
* .NET runtime compatible with InfoPanel
* PresentMonFps dependency (included in release package)

## Installation

1. Download the latest release from GitHub.
2. Import into InfoPanel via the "Import Plugin" feature.
3. The plugin will automatically start monitoring fullscreen applications.

## Installation from Source

1. Clone or download this repository.
2. Build the project in a .NET environment.
3. Copy the compiled DLLs and dependencies to your InfoPanel plugins folder.

## Usage

* Launch InfoPanel with the plugin loaded.
* The plugin automatically detects and monitors fullscreen applications.
* View real-time performance metrics in InfoPanel's UI.
* Metrics reset when fullscreen applications are closed.

## Notes

* A benign log error (`"Array is variable sized and does not follow prefix convention"`) may appear but does not impact functionality.
* For detailed version history, please refer to the `CHANGELOG.md` file.
