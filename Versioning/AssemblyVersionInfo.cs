using System.Reflection;

namespace JBZUniveresalLunix.Versioning;

/// <summary>
/// Thông tin version thực tế đã được đóng vào assembly/EXE.
/// Không hard-code version ở giao diện hoặc service khác: luôn đọc qua lớp này.
/// </summary>
public sealed record AssemblyVersionInfo(
    string Product,
    string Title,
    string ProductVersion,
    string AssemblyVersion,
    string FileVersion,
    string InformationalVersion)
{
    public static AssemblyVersionInfo Current { get; } = Read(Assembly.GetExecutingAssembly());

    public string DisplayVersion => $"V{ProductVersion}";

    public string FileTag => ProductVersion.Replace('.', '_');

    public string VersionedExeName => "JBZUniveresalLunix.exe";

    private static AssemblyVersionInfo Read(Assembly assembly)
    {
        string assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        string fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version ?? assemblyVersion;

        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? fileVersion;

        string product = assembly
            .GetCustomAttribute<AssemblyProductAttribute>()?
            .Product ?? "JBZUniveresalLunix";

        string title = assembly
            .GetCustomAttribute<AssemblyTitleAttribute>()?
            .Title ?? product;

        // ProductVersion dùng VersionPrefix/Version, không lấy suffix của InformationalVersion.
        string productVersion = NormalizeProductVersion(fileVersion);

        return new AssemblyVersionInfo(
            product,
            title,
            productVersion,
            assemblyVersion,
            fileVersion,
            informationalVersion);
    }

    private static string NormalizeProductVersion(string fileVersion)
    {
        if (!Version.TryParse(fileVersion, out Version? version))
            return fileVersion;

        // FileVersion là A.B.C.D, còn version hiển thị release là A.B.C.
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
