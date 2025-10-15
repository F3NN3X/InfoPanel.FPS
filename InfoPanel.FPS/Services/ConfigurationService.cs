using System.Globalization;

namespace InfoPanel.FPS.Services
{
    /// <summary>
    /// Service for reading configuration from InfoPanel.FPS.ini file.
    /// </summary>
    public class ConfigurationService
    {
        private readonly string _configFilePath;
        private readonly Dictionary<string, Dictionary<string, string>> _configData;

        public ConfigurationService()
        {
            // Try multiple possible locations for the config file
            var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? Environment.CurrentDirectory;
            
            // First try the assembly directory (where the plugin DLL is)
            _configFilePath = Path.Combine(assemblyDirectory, "InfoPanel.FPS.ini");
            
            Console.WriteLine($"ConfigurationService: Checking for config at: {_configFilePath}");
            
            // If not found there, try the InfoPanel plugin data directory
            if (!File.Exists(_configFilePath))
            {
                var infoPanelConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 
                    "InfoPanel", "plugins", "InfoPanel.FPS", "InfoPanel.FPS.ini");
                
                Console.WriteLine($"ConfigurationService: Config not found in assembly dir, checking: {infoPanelConfigPath}");
                
                if (File.Exists(infoPanelConfigPath))
                {
                    _configFilePath = infoPanelConfigPath;
                }
                else
                {
                    Console.WriteLine($"ConfigurationService: Config not found in either location, creating default at: {_configFilePath}");
                    CreateDefaultConfigFile();
                }
            }
            
            _configData = LoadConfiguration();
        }



        /// <summary>
        /// Whether RTSS monitoring should be used (always true in v1.1.6+).
        /// Legacy property kept for configuration compatibility.
        /// </summary>
        [Obsolete("RTSS is now the only monitoring method. This property is kept for config compatibility.")]
        public bool UsePresentMon => true; // Always true, RTSS is the only method

        /// <summary>
        /// Update interval in milliseconds.
        /// </summary>
        public int UpdateInterval => GetIntValue("Display", "updateInterval", 1000);

        /// <summary>
        /// Number of frames to use for smoothing calculations.
        /// </summary>
        public int SmoothingFrames => GetIntValue("Display", "smoothingFrames", 120);

        /// <summary>
        /// Loads configuration from INI file.
        /// </summary>
        private Dictionary<string, Dictionary<string, string>> LoadConfiguration()
        {
            var config = new Dictionary<string, Dictionary<string, string>>();

            try
            {
                if (!File.Exists(_configFilePath))
                {
                    Console.WriteLine($"ConfigurationService: Config file not found at {_configFilePath}, using defaults");
                    return config;
                }

                Console.WriteLine($"ConfigurationService: Loading configuration from {_configFilePath}");

                var lines = File.ReadAllLines(_configFilePath);
                string? currentSection = null;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    
                    // Skip empty lines and comments
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                        continue;

                    // Check for section headers
                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        currentSection = trimmedLine[1..^1];
                        if (!config.ContainsKey(currentSection))
                        {
                            config[currentSection] = new Dictionary<string, string>();
                        }
                        continue;
                    }

                    // Parse key-value pairs
                    if (currentSection != null && trimmedLine.Contains("="))
                    {
                        var parts = trimmedLine.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var value = parts[1].Trim();
                            config[currentSection][key] = value;
                        }
                    }
                }

                Console.WriteLine($"ConfigurationService: Loaded {config.Count} sections");
                
                // Log current settings safely
                try
                {
                    Console.WriteLine($"ConfigurationService: updateInterval={UpdateInterval}ms");
                }
                catch (Exception settingsEx)
                {
                    Console.WriteLine($"ConfigurationService: Error reading settings: {settingsEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ConfigurationService: Error loading config file: {ex.Message}");
                Console.WriteLine($"ConfigurationService: Stack trace: {ex.StackTrace}");
            }

            return config;
        }

        /// <summary>
        /// Gets a boolean value from configuration.
        /// </summary>
        private bool GetBoolValue(string section, string key, bool defaultValue)
        {
            if (_configData.TryGetValue(section, out var sectionData) && 
                sectionData.TryGetValue(key, out var value))
            {
                if (bool.TryParse(value, out var boolValue))
                {
                    return boolValue;
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets an integer value from configuration.
        /// </summary>
        private int GetIntValue(string section, string key, int defaultValue)
        {
            if (_configData.TryGetValue(section, out var sectionData) && 
                sectionData.TryGetValue(key, out var value))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    return intValue;
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// Creates a default configuration file.
        /// </summary>
        private void CreateDefaultConfigFile()
        {
            try
            {
                var defaultConfig = @"[FPS_Monitoring]
# InfoPanel.FPS v1.1.6+ uses RTSS (RivaTuner Statistics Server) exclusively
# RTSS must be running for FPS monitoring to work
# Download RTSS: https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html
# Or install MSI Afterburner (includes RTSS): https://www.msi.com/Landing/afterburner

[Display]
# Update interval in milliseconds (1000 = 1 second)
updateInterval=1000

# Number of frames to use for smoothing calculations (used for 1% low calculation)
smoothingFrames=100";

                // Create directory if it doesn't exist
                var directory = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_configFilePath, defaultConfig);
                Console.WriteLine($"ConfigurationService: Created default config file at: {_configFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ConfigurationService: Failed to create default config file: {ex.Message}");
            }
        }

        /// <summary>
        /// Reloads configuration from file.
        /// </summary>
        public void ReloadConfiguration()
        {
            _configData.Clear();
            var newConfig = LoadConfiguration();
            
            foreach (var section in newConfig)
            {
                _configData[section.Key] = section.Value;
            }
            
            Console.WriteLine("ConfigurationService: Configuration reloaded");
        }
    }
}