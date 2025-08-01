# CHANGELOG

## v1.0.17 (July 12, 2025)

- **Improved Resolution Display Format**
  - Changed resolution format to include spaces (e.g., "3840 x 2160" instead of "3840x2160") for better readability.
  - Updated all instances of resolution display for consistency.

## v1.0.16 (June 3, 2025)

- **Added GPU Name Sensor**
  - New PluginText sensor displays the name of the system's graphics card in the UI.
  - Added System.Management reference for WMI queries to detect GPU information.
- **Improved Build Configuration**
  - Ensured all dependencies are placed in the root output folder without subdirectories.
  - Added post-build target to automatically move DLLs from subdirectories to the root folder.
  - Fixed dependency management for better plugin compatibility.

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

## v1.0.11 (Feb 27, 2025)

- **Performance and Robustness Enhancements**
  - Reduced string allocations with format strings in logs.
  - Simplified `Initialize` by moving initial PID check to `StartInitialMonitoringAsync`.
  - Optimized `GetActiveFullscreenProcessId` to a synchronous method.
  - Optimized `UpdateLowFpsMetrics` with single-pass min/max/histogram calculation.
  - Enhanced exception logging with full stack traces.
  - Added null safety for `_cts` checks.
  - Implemented finalizer for unmanaged resource cleanup.

## v1.0.10 (Feb 27, 2025)

- **Removed 0.1% Low FPS Calculation**
  - Simplified scope by eliminating 0.1% low metric from UI and calculations.

## v1.0.9 (Feb 24, 2025)

- **Fixed 1% Low Reset on Closure**
  - Ensured immediate `ResetSensorsAndQueue` before cancellation to clear all metrics.
  - Cleared histogram in `ResetSensorsAndQueue` to prevent stale percentiles.
  - Blocked post-cancel updates in `UpdateFrameTimesAndMetrics`.

## v1.0.8 (Feb 24, 2025)

- **Fixed Initial Startup and Reset Delays**
  - Moved event hook setup to `Initialize` for proper timing.
  - Added immediate PID check in `Initialize` for instant startup.
  - Forced immediate sensor reset on cancellation, improving shutdown speed.

## v1.0.7 (Feb 24, 2025)

- **Further Optimizations for Efficiency**
  - Added volatile `_isMonitoring` flag to prevent redundant monitoring attempts.
  - Pre-allocated histogram array in `UpdateLowFpsMetrics` to reduce GC pressure.
  - Initially moved event hook setup to field initializer (reverted in v1.0.8).

## v1.0.6 (Feb 24, 2025)

- **Fixed Monitoring Restart on Focus Regain**
  - Updated event handling to restart `FpsInspector` when the same PID regains focus.
  - Adjusted debounce to ensure re-focus events are caught reliably.

## v1.0.5 (Feb 24, 2025)

- **Optimized Performance and Structure**
  - Debounced event hook re-initializations to 500ms for efficiency.
  - Unified sensor resets into `ResetSensorsAndQueue`.
  - Switched to circular buffer with histogram for O(1) percentile approximations.
  - Streamlined async calls, removed unnecessary `Task.Run`.
  - Replaced `ConcurrentQueue` with circular buffer for memory efficiency.
  - Simplified threading model for updates.
  - Implemented Welford’s running variance algorithm.
  - Simplified retry logic to a single async loop.
  - Streamlined fullscreen detection logic.
  - Simplified PID validation with lightweight checks.

## v1.0.4 (Feb 24, 2025)

- **Added Event Hooks and New Metrics**
  - Introduced `SetWinEventHook` for window detection.
  - Added 0.1% low FPS and variance metrics (0.1% later removed in v1.0.10).
  - Improved fullscreen detection with `DwmGetWindowAttribute`.

## v1.0.3 (Feb 24, 2025)

- **Stabilized Resets, 1% Low FPS, and Update Smoothness**
  - Added PID check in `UpdateAsync` to ensure `FpsInspector` stops on pid == 0.
  - Fixed 1% low FPS calculation sticking, updated per frame.
  - Unified updates via `FpsInspector` with 1s throttling for smoothness.

## v1.0.2 (Feb 22, 2025)

- **Improved Frame Time Update Frequency**
  - Reduced `UpdateInterval` to 200ms from 1s for more frequent updates.

## v1.0.1 (Feb 22, 2025)

- **Enhanced Stability and Consistency**
  - Aligned plugin name in constructor with header (`"InfoPanel.FPS"`) and improved description.
  - Added null check for `FpsInspector` results; resets frame time queue on PID switch.
  - Improved retry logging for exhausted attempts.

## v1.0.0 (Feb 20, 2025)

- **Initial Stable Release**
  - Core features: Detects fullscreen apps, monitors FPS in real-time, calculates frame time and 1% low FPS over 1000 frames.
  - Stability enhancements: Implements 3 retries with 1-second delays for `FpsInspector` errors, 15-second stall detection with restarts.
  - Removed early smoothing attempts due to InfoPanel UI limitations.
