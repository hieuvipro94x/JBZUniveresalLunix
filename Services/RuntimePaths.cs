using System.IO;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Canonical runtime layout. Production services must use these paths instead
/// of independently composing filenames from the application directory.
/// </summary>
public static class RuntimePaths
{
    public static string AppDirectory => Path.GetFullPath(AppContext.BaseDirectory);
    public static string ConfigFile => Path.Combine(AppDirectory, "JBZUniveresalLunix.cfg");
    public static string PartCounterFile => Path.Combine(AppDirectory, "PartCnt.txt");
    public static string LogFile => Path.Combine(AppDirectory, "JBZUniveresalLunix.log");
    public static string DataDirectory => Path.Combine(AppDirectory, "Data");
    public static string DatabaseFile => Path.Combine(DataDirectory, "JBZUniveresalLunix.db");
    public static string CrashDirectory => Path.Combine(AppDirectory, "Crash");
    public static string CrashReportFile => Path.Combine(CrashDirectory, "JBZUniveresalLunix.RPT");
    public static string ItemDirectory => @"C:\ITEM";
    public static string PassRoot => @"C:\Pass";
    public static string ErrorRoot => @"C:\Error";

    // Read-only migration inputs. New runtime writes never target these paths.
    public static string LegacyProductionJson => Path.Combine(AppDirectory, "production.settings.json");
    public static string LegacyConfigFile => Path.Combine(AppDirectory, "UniversalTester.cfg");
    public static string LegacyDatabaseFile => Path.Combine(AppDirectory, "Data", "History", "test-history.db");
    public static string LegacyPassRoot => @"C:\Pass_";
    public static string LegacyErrorRoot => @"C:\Error_";
}
