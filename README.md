# InfoPanel.FPS Plugin

**Version:** 1.0.15
**Author:** F3NN3X
**Description:** An simple InfoPanel plugin that leverages `PresentMonFps` to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds and 1% low FPS (99th percentile). Updates every 1 second with efficient event-driven detection, ensuring immediate startup, reset on closure, and proper metric clearing.

## Changelog

## v1.0.15 (May 21, 2025)

- Improved fullscreen detection for multi-monitor setups.
- Used MonitorFromWindow for accurate fullscreen detection on the active monitor.
- Continued reporting primary monitor's resolution and refresh rate for consistency.

## v1.0.14 (May 21, 2025)

- Added Main Display Resolution and Main Display Refresh Rate Sensors
- Added PluginText sensor for main display resolution (e.g., 3840x2160) and PluginSensor for main display refresh rate (e.g., 240Hz).
- Fixed incorrect use of PluginSensor for main display resolution by switching to PluginText.
- Cached monitor info to minimize API calls.
- Modified plugin to always report the primary monitor's default resolution and refresh rate for both fullscreen and non-fullscreen cases, ensuring consistency on multi-monitor systems.
- Fixed sensor update logic to display primary monitor settings when no fullscreen app is detected, preventing 0x0 and 0Hz fallbacks.
- Improved fullscreen detection using MonitorFromWindow to accurately detect fullscreen apps on the active monitor, aligning with InfoPanel developer guide, while maintaining primary monitor reporting.

## v1.0.13 (Mar 22, 2025)

- **Added Window Title Sensor**
  - New sensor displays the title of the current fullscreen app for user-friendly identification.

## v1.0.12 (Mar 10, 2025)

- **Simplified Metrics**
  - Removed frame time variance sensor and related calculations for a leaner plugin.

### v1.0.11 (Feb 27, 2025)

- **Performance and Robustness Enhancements**
  - Reduced string allocations with format strings in logs.
  - Simplified `Initialize` by moving initial PID check to `StartInitialMonitoringAsync`.
  - Optimized `GetActiveFullscreenProcessId` to a synchronous method.
  - Enhanced `UpdateLowFpsMetrics` with single-pass min/max/histogram calculation.
  - Improved exception logging with full stack traces.
  - Added null safety for `_cts` checks.
  - Implemented finalizer for unmanaged resource cleanup.

### v1.0.10 (Feb 27, 2025)

- **Removed 0.1% Low FPS Calculation**
  - Simplified scope by eliminating 0.1% low metric from UI and calculations.

### v1.0.9 (Feb 24, 2025)

- **Fixed 1% Low Reset on Closure**
  - Ensured immediate `ResetSensorsAndQueue` before cancellation.
  - Cleared histogram to prevent stale percentiles.
  - Blocked post-cancel updates in `UpdateFrameTimesAndMetrics`.

## Notes

- A benign log error (`"Array is variable sized and does not follow prefix convention"`) may appear but does not impact functionality.
- Full changelog available on request or in future `CHANGELOG.md`.
