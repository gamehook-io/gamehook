global using MemoryAddress = System.UInt32;
using System.Reflection;

public static class BuildEnvironment
{
    // 0.0.0.0
    public static string AssemblyVersion
    {
        get
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            var attributes = entryAssembly?.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false);

            if (attributes != null && attributes.Length > 0)
            {
                return ((AssemblyFileVersionAttribute)attributes[0]).Version;
            }

            throw new Exception("Cannot determine application AssemblyVersion.");
        }
    }

    // 0.0.0.0+hash
    public static string AssemblyProductVersion
    {
        get
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            var attributes = entryAssembly?.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);

            if (attributes != null && attributes.Length > 0)
            {
                return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
            }

            throw new Exception("Cannot determine application AssemblyProductVersion.");
        }
    }

    public static string ConfigurationDirectory
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHook");
        }
    }

    public static string OutputPropertiesDirectory => Path.Combine(ConfigurationDirectory, "OutputProperties");
    public static string UserAppsettingsFilePath => Path.Combine(ConfigurationDirectory, "appsettings.user.json");
    public static string DebugAppsettingsFilePath => Path.Combine(ConfigurationDirectory, "appsettings.debug.json");

#if DEBUG
    public static bool IsDebug = true;
    public static bool IsTestingBuild => true;
    public static bool IsPublicBuild => false;
#else
    public static bool IsDebug = false;
    public static bool IsTestingBuild => AssemblyVersion == "0.0.0.0";
    public static bool IsPublicBuild => AssemblyVersion != "0.0.0.0";
#endif
}