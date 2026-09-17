using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class ErrorLogService
{
    public static void SaveIfEnabled(
        ProductionSettings settings,
        ProductModel model,
        long lotNo,
        CompletedTestResult result)
    {
        // Compatibility entry point only. Detailed faults are already stored
        // transactionally in SQLite TestFaults and exported to C:\Error. Do not
        // create a second JSON source under Data\ErrorLogs.
        if (!result.Passed)
        {
            AsyncFileLogService.Current.Application(
                $"LEGACY_ERROR_JSON_SKIPPED part={model.PartNumber} lot={lotNo}",
                AppLogLevel.Diagnostic);
        }
    }
}
