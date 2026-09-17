using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Versioning;

namespace JBZUniveresalLunix.Services;

public static class ProgramIdentityService
{
    public const string ProgramName = "JBZUniveresalLunix";

    public static string VersionText => AppVersion.ProductVersion;

    public static string AssemblyVersionText => AppVersion.AssemblyVersion;

    public static string FileVersionText => AppVersion.FileVersion;

    public static string InformationalVersionText => AppVersion.InformationalVersion;

    // HtdrvName trong lịch sử là danh tính phần mềm tạo kết quả. Cấu hình
    // Card/USB/Relay không được ghép vào cột tên phần mềm của mẫu gốc.
    public static string BuildHtdrvName() => $"{ProgramName} V{VersionText}";
}
