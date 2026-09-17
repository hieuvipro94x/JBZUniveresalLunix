namespace JBZUniveresalLunix.Versioning;

/// <summary>
/// API version dùng chung cho toàn bộ ứng dụng.
/// Mọi màn hình, log, history hoặc tên file cần version phải lấy từ đây.
/// </summary>
public static class AppVersion
{
    public static AssemblyVersionInfo Current => AssemblyVersionInfo.Current;

    public static string ProductVersion => Current.ProductVersion;

    public static string DisplayVersion => Current.DisplayVersion;

    public static string AssemblyVersion => Current.AssemblyVersion;

    public static string FileVersion => Current.FileVersion;

    public static string InformationalVersion => Current.InformationalVersion;

    public static string VersionedExeName => Current.VersionedExeName;
}
