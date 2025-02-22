# InfoPanel.FPS
FPS plugin for InfoPanel using PresentmonFPS

Plugin: InfoPanel.FPS

Version: 1.0.0

Author: F3NN3X

Description: An InfoPanel plugin that leverages PresentMonFps to monitor and display real-time performance metrics for fullscreen applications. Tracks Frames Per Second (FPS), current frame time in milliseconds, and 1% low FPS (99th percentile over 1000 frames). Updates every 1 second to align with InfoPanel’s default refresh rate. Includes retry logic and stall detection for robust operation.

Changelog:
- v1.0.0 (Feb 20, 2025): Initial stable release.
- Core Features: Detects fullscreen applications, monitors FPS in real-time, calculates current frame time, and computes 1% low FPS over a 1000-frame sliding window.
- Stability Enhancements: Implements 3 retry attempts with a 1-second delay for FpsInspector errors (e.g., HRESULT 0x800700B7 - "Cannot create a file when that file already exists"), and includes 15-second stall detection with automatic monitoring restarts.
- Simplifications: Removed earlier attempts at FPS smoothing, update throttling, and rounding to mitigate UI jitter, identified as an InfoPanel limitation rather than a plugin issue.

Note: A benign log error ("Array is variable sized and does not follow prefix convention") may appear but does not impact functionality.
