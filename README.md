# InfoPanel.FPS Plugin

**Version:** 1.0.2  
**Author:** F3NN3X  
**Description:** An InfoPanel plugin that leverages `PresentMonFps` to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds, and 1% low FPS (99th percentile over 1000 frames). Updates frame time and FPS more frequently (every 200ms) while maintaining 1% low FPS calculation stability.

## Changelog

### v1.0.2 (Feb 22, 2025)
- **Improved Frame Time Update Frequency**
  - **Performance:** Reduced `UpdateInterval` to 200ms from 1s to update FPS and frame time more frequently, enhancing responsiveness of the frame time display.

### v1.0.1 (Feb 22, 2025)
- **Enhanced Stability and Consistency**
  - **Consistency:** Aligned plugin name in constructor with header (`"InfoPanel.FPS"`) and improved description.
  - **Robustness:** Added null check for `FpsInspector` results to prevent potential null reference issues; resets frame time queue when switching to a new PID to ensure accurate 1% low FPS for the current application.
  - **Logging:** Improved retry logging to track when all retries are exhausted before forcing a restart.

### v1.0.0 (Feb 20, 2025)
- **Initial Stable Release**
  - **Core Features:** Detects fullscreen applications, monitors FPS in real-time, calculates current frame time, and computes 1% low FPS over a 1000-frame sliding window.
  - **Stability Enhancements:** Implements 3 retry attempts with a 1-second delay for `FpsInspector` errors (e.g., HRESULT 0x800700B7 - "Cannot create a file when that file already exists"), and includes 15-second stall detection with automatic monitoring restarts.
  - **Simplifications:** Removed earlier attempts at FPS smoothing, update throttling, and rounding to mitigate UI jitter, identified as an InfoPanel limitation rather than a plugin issue.

## Notes
- A benign log error (`"Array is variable sized and does not follow prefix convention"`) may appear but does not impact functionality.
