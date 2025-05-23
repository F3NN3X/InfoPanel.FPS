# InfoPanel.FPS Plugin

**Version:** 1.0.14
**Author:** F3NN3X
**Description:** An simple InfoPanel plugin that leverages `PresentMonFps` to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds and 1% low FPS (99th percentile). Updates every 1 second with efficient event-driven detection, ensuring immediate startup, reset on closure, and proper metric clearing.

## Changelog

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
