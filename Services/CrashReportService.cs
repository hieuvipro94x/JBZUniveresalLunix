using System.IO;
using System.Text;
using JBZUniveresalLunix.Versioning;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Minimal crash-safe writer. The Crash directory and RPT are created lazily,
/// only for a confirmed technical exception (unhandled application error or
/// an actual failure of the main tester hardware). Leak-machine and label-printer
/// connection errors are intentionally excluded from RPT reporting.
/// </summary>
public static class CrashReportService
{
    private static readonly object Gate = new();

    public static void Write(Exception exception, string source, string? runtimeContext = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            string report = IsDeviceConnectionFault(source) &&
                            !AsyncFileLogService.Current.FileLoggingEnabled
                ? BuildOperatorConnectionReport()
                : BuildTechnicalReport(exception, source, runtimeContext);

            lock (Gate)
            {
                Directory.CreateDirectory(RuntimePaths.CrashDirectory);
                File.AppendAllText(
                    RuntimePaths.CrashReportFile,
                    report,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // A crash reporter cannot safely throw back into an unhandled
            // exception boundary. The main logger remains the secondary path.
        }
    }

    private static bool IsDeviceConnectionFault(string? source) =>
        source?.StartsWith("Hardware.DeviceFault.", StringComparison.OrdinalIgnoreCase) == true;

    private static string BuildOperatorConnectionReport() =>
        new StringBuilder()
            .AppendLine("============================================================")
            .Append("Thời gian: ").AppendLine(DateTime.Now.ToString("O"))
            .Append("Phiên bản: ").AppendLine(AppVersion.DisplayVersion)
            .AppendLine("Loại lỗi: LỖI KẾT NỐI THIẾT BỊ")
            .AppendLine("Hướng dẫn: Kiểm tra nguồn và cáp kết nối, sau đó khởi động lại chương trình.")
            .AppendLine()
            .ToString();

    private static string BuildTechnicalReport(
        Exception exception,
        string? source,
        string? runtimeContext) =>
        new StringBuilder()
            .AppendLine("============================================================")
            .Append("Crash Time: ").AppendLine(DateTime.Now.ToString("O"))
            .Append("AppVersion: ").AppendLine(AppVersion.DisplayVersion)
            .Append("Source: ").AppendLine(source ?? string.Empty)
            .Append("ExceptionType: ").AppendLine(exception.GetType().FullName ?? exception.GetType().Name)
            .Append("ExceptionMessage: ").AppendLine(exception.Message)
            .Append("StackTrace: ").AppendLine(exception.StackTrace ?? string.Empty)
            .Append("InnerException: ").AppendLine(exception.InnerException?.ToString() ?? string.Empty)
            .Append("MachineName: ").AppendLine(Environment.MachineName)
            .Append("OSVersion: ").AppendLine(Environment.OSVersion.ToString())
            .Append("ProcessArchitecture: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
            .Append("RuntimeContext: ").AppendLine(runtimeContext ?? string.Empty)
            .AppendLine()
            .ToString();
}
