# RTSS Troubleshooting Guide

This guide helps you diagnose and fix common issues with RivaTuner Statistics Server (RTSS) when using the InfoPanel.FPS plugin.

## Prerequisites

Before troubleshooting, ensure you have:
- ✅ RTSS or MSI Afterburner installed
- ✅ RTSS running (check system tray for RTSS icon)
- ✅ InfoPanel.FPS plugin installed and loaded

---

## Common Issues

### Issue 1: No FPS Data Displayed in InfoPanel

**Symptoms:**
- InfoPanel shows "Nothing to capture" or "0 FPS"
- Game is running fullscreen but no metrics appear

**Solutions:**

#### Step 1: Verify RTSS is Running
1. Check your system tray for the RTSS icon (red/white graph icon)
2. If not visible, launch **RivaTunerStatisticsServer.exe**
   - Default location: `C:\Program Files (x86)\RivaTuner Statistics Server\`
   - Or launch MSI Afterburner (which starts RTSS automatically)

#### Step 2: Verify RTSS is Monitoring Your Game
1. Launch your game in fullscreen mode
2. You should see the RTSS overlay in-game (if enabled)
3. If you see the overlay with FPS numbers, RTSS is working correctly
4. Wait 10-60 seconds after game launch for RTSS to hook the game

#### Step 3: Enable RTSS Overlay (if disabled)
1. Open **RivaTuner Statistics Server**
2. In the main window, locate **On-Screen Display support**
3. Set it to **On**
4. Ensure **Framerate** is checked under statistics

#### Step 4: Check RTSS Application Profile
1. In RTSS, look for your game in the application list (e.g., `bf6.exe`, `GZW.exe`)
2. If not listed, click **Add** and browse to your game's executable
3. Set **Application detection level** to **Medium** or **High**
4. Enable **Show own statistics**
5. Restart the game

---

### Issue 2: Window Title Shows "Nothing to capture"

**Symptoms:**
- FPS data is not showing, but you can see the RTSS overlay in your game

**Solutions:**

#### Wait for RTSS to Hook
- This is normal behavior on first game launch
- RTSS may take 10-60 seconds to hook into the game process
- The title and FPS data will appear automatically once RTSS connects
- **Be patient** - it will work once the hook is established

#### Restart RTSS and Game
1. Close your game
2. Right-click RTSS icon in system tray → Exit
3. Launch RTSS again (or MSI Afterburner)
4. Launch your game
5. Wait for hook to establish

---

### Issue 3: RTSS Overlay Not Appearing in Game

**Symptoms:**
- Game runs but no RTSS overlay visible
- InfoPanel.FPS shows no data

**Solutions:**

#### Check Global Settings
1. Open RTSS
2. In the main window (Global profile):
   - **On-Screen Display support** = **On**
   - **Framerate** checkbox = **Enabled**
3. Click **Setup** button next to On-Screen Display
4. Verify **Framerate** is in the list of active statistics

#### Check Application-Specific Settings
Some games require specific RTSS profiles:
1. In RTSS, find your game in the dropdown list
2. Select it and verify settings:
   - **Application detection level** = Medium or High
   - **On-Screen Display support** = Use global or On
3. Some anti-cheat games may block overlays but RTSS can still provide data

#### Compatibility Mode (for stubborn games)
1. Right-click your game's .exe → Properties
2. Go to Compatibility tab
3. Try enabling **Run as administrator**
4. Run RTSS as administrator as well

---

### Issue 4: FPS Data is Incorrect or Frozen

**Symptoms:**
- FPS shows a constant value (e.g., always 60 FPS)
- FPS doesn't update or seems wrong

**Solutions:**

#### Verify RTSS is Actually Hooked
1. Check that RTSS overlay shows live, changing FPS values in-game
2. If the overlay is frozen or absent, RTSS isn't properly hooked
3. Restart game and RTSS

#### Check for Multiple Overlays
- Running multiple overlay applications (Discord, Steam, GeForce Experience) can conflict
- Try disabling other overlays temporarily
- RTSS should be the primary overlay application

---

## Advanced Troubleshooting

### Check RTSS Processes
Open Task Manager and verify these processes are running:
- `RTSSHooksLoader64.exe` or `RTSSHooksLoader.exe`
- `RTSS.exe`

If missing, RTSS is not properly started.

### Reset RTSS Configuration
If nothing works:
1. Close RTSS completely
2. Navigate to: `C:\Program Files (x86)\RivaTuner Statistics Server\Profiles\`
3. Backup then delete `Global` profile file
4. Restart RTSS (it will recreate default settings)
5. Reconfigure as needed

---

## Recommended RTSS Settings

For best compatibility with InfoPanel.FPS:

### Global Settings:
- **On-Screen Display support**: On
- **Framerate**: Enabled
- **Application detection level**: Medium
- **Stealth mode**: Off

### Game-Specific Settings:
Most games work with global settings, but for anti-cheat games:
- **Application detection level**: High
- **On-Screen Display support**: On (even if overlay is blocked, data is still captured)

---

## Supported Games

RTSS works with most DirectX and Vulkan games, including anti-cheat protected titles:
- Battlefield series (including BF 2042/6 with Javelin anti-cheat)
- Valorant (Vanguard anti-cheat)
- Apex Legends (Easy Anti-Cheat)
- PUBG (BattlEye)
- Rainbow Six Siege
- Gray Zone Warfare
- Deadside
- No Man's Sky
- And many more...

---

## Still Having Issues?

If you've tried all the above steps and still have problems:

1. **Update RTSS**: Download the latest version from [Guru3D](https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html)
2. **Check Game Compatibility**: Some games with extreme anti-cheat may block all overlay tools
3. **Contact Support**: Open an issue on the [GitHub repository](https://github.com/F3NN3X/InfoPanel.FPS/issues) with:
   - Your RTSS version
   - Game name and version
   - Whether RTSS overlay appears in-game
   - Any error messages

---

## Tips for Best Experience

- **Launch Order**: Start RTSS → Start InfoPanel → Launch Game (for fastest hook)
- **Keep RTSS Updated**: Newer versions have better game compatibility
- **Overlay Not Required**: You don't need to see the RTSS overlay for InfoPanel.FPS to work - the plugin reads directly from shared memory
- **First Launch Delay**: The first time RTSS hooks a game can take 30-60 seconds - subsequent launches are faster