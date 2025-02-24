# InfoPanel.FPS Plugin

**Version:** 1.0.9  
**Author:** F3NN3X  
**Description:** An optimized InfoPanel plugin that leverages `PresentMonFps` to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds, 1% low FPS (99th percentile), 0.1% low FPS (99.9th percentile), and frame time variance over 1000 frames. Updates every 1 second with efficient event-driven detection, ensuring immediate startup, proper focus handling, and instant reset on closure.

## Changelog

### v1.0.9 (Feb 24, 2025)
- **Fixed 1% Low Reset on Closure**
  - **Reset Logic:** Ensured immediate `ResetSensorsAndQueue` before cancellation to clear all metrics, including 1% low.
  - **Histogram:** Cleared in `ResetSensorsAndQueue` to prevent stale percentile data.
  - **Update Prevention:** Blocked post-cancel updates in `UpdateFrameTimesAndMetrics` for instant reset.

### v1.0.8 (Feb 24, 2025)
- **Fixed Initial Startup and Reset Delays**
  - **Startup:** Moved event hook to `Initialize`, added immediate PID check for instant monitoring.
  - **Reset:** Forced immediate sensor reset on cancellation, improved shutdown speed.

### v1.0.7 (Feb 24, 2025)
- **Further Optimizations for Efficiency**
  - **Monitoring Flag:** Added volatile `_isMonitoring` to prevent redundant monitoring attempts.
  - **Histogram:** Pre-allocated array in `UpdateLowFpsMetrics` to reduce GC pressure.
  - **Event Hook:** Initialized in field for one-time setup (reverted in v1.0.8).

### v1.0.6 (Feb 24, 2025)
- **Fixed Monitoring Restart on Focus Regain**
  - **Event Handling:** Updated to restart `FpsInspector` when the same PID regains focus.
  - **Debounce:** Adjusted to ensure re-focus events are caught.

### v1.0.5 (Feb 24, 2025)
- **Optimized Performance and Structure**
  - **Event Efficiency:** Debounced event hook re-initializations to 500ms.
  - **Reset Logic:** Unified sensor resets into `ResetSensorsAndQueue`.
  - **Percentiles:** Switched to circular buffer with histogram for O(1) approximations.
  - **Task Management:** Streamlined async calls, removed unnecessary `Task.Run`.
  - **Memory:** Replaced `ConcurrentQueue` with circular buffer.
  - **Threading:** Simplified updates.
  - **Variance:** Implemented Welford’s running variance algorithm.
  - **Retry Logic:** Simplified to a single async loop.
  - **Detection:** Streamlined fullscreen checks.
  - **PID Validation:** Simplified to lightweight checks.

### v1.0.4 (Feb 24, 2025)
- **Added Event Hooks and New Metrics**
  - Introduced `SetWinEventHook` for window detection, added 0.1% low FPS and variance, improved fullscreen detection with `DwmGetWindowAttribute`.

### v1.0.3 (Feb 24, 2025)
- **Stabilized Resets, 1% Low FPS, and Update Smoothness**
  - **Reset:** Added PID check in `UpdateAsync`, ensured `FpsInspector` stops on pid == 0.
  - **1% Low:** Fixed calculation sticking, updated per frame.
  - **Stability:** Unified updates with 1s throttling.

### v1.0.2 (Feb 22, 2025)
- **Improved Frame Time Update Frequency**
  - **Performance:** Reduced `UpdateInterval` to 200ms from 1s.

### v1.0.1 (Feb 22, 2025)
- **Enhanced Stability and Consistency**
  - **Consistency:** Aligned plugin name and improved description.
  - **Robustness:** Added null checks and queue resets on PID switch.
  - **Logging:** Enhanced retry logging.

### v1.0.0 (Feb 20, 2025)
- **Initial Stable Release**
  - **Core Features:** Monitors FPS, frame time, and 1% low FPS over 1000 frames.
  - **Stability:** Added retries and stall detection.

## Notes
- A benign log error (`"Array is variable sized and does not follow prefix convention"`) may appear but does not impact functionality.
