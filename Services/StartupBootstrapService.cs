using JBZUniveresalLunix.Models;
using System.IO;

namespace JBZUniveresalLunix.Services;

public static class StartupBootstrapService
{
    public static void EnsureFastConfiguration()
    {
        AsyncFileLogService log = AsyncFileLogService.Current;
        try
        {
            IReadOnlyList<string> migrated = ProductionDataUpgradeService.MigrateFastConfigurationForCurrentVersion();
            if (migrated.Count > 0)
                log.Application($"Fast configuration inherited: {string.Join(", ", migrated)}");

            bool canonicalExisted = File.Exists(RuntimePaths.ConfigFile);
            ProductionSettings production = ProductionConfigService.Load();
            if (!canonicalExisted)
                ProductionConfigService.Save(production);
            else
                ProductionConfigService.EnsureSavedOnStartup(production);

            if (!canonicalExisted)
                log.Application($"CONFIG_MIGRATION old -> {RuntimePaths.ConfigFile}");
        }
        catch (Exception ex)
        {
            log.Error($"Fast configuration inheritance failed: {ex}");
        }
    }

    public static Task EnsureDeferredProductionFiles()
    {
        AsyncFileLogService log = AsyncFileLogService.Current;
        try
        {
            IReadOnlyList<string> migrated = ProductionDataUpgradeService.MigrateDeferredProductionDataForCurrentVersion();
            if (migrated.Count > 0)
                log.Application($"Deferred production data inherited: {string.Join(", ", migrated)}");

            ProductionSettings production = ProductionConfigService.Load();
            ProductionConfigService.EnsureSavedOnStartup(production);
            log.Application($"configuration ready: {RuntimePaths.ConfigFile}");

            MigrateLocalLegacyDatabase(log);
            var repository = new TestHistoryStore(RuntimePaths.DatabaseFile);
            log.Application($"database ready: {RuntimePaths.DatabaseFile}");

            var partCounter = new PartCounterStore(RuntimePaths.PartCounterFile);
            IReadOnlyList<PartCounterEntry> legacyCounters = partCounter.ReadAll();
            int importedCounters = repository.ImportPartCountersOnce(
                legacyCounters,
                RuntimePaths.PartCounterFile);
            if (importedCounters > 0)
                log.Application($"PARTCNT_IMPORT rows={importedCounters}");
            partCounter.MirrorAll(repository.GetAllProbeCounters());

            // C:\Pass_ và C:\Error_ trên máy cũ có thể chứa hàng trăm file.
            // Import chúng không phải điều kiện để kết nối bo/chọn mã; chờ tại
            // đây từng làm startup dừng trước T3 và khóa toàn bộ nút vận hành.
            log.Application(
                "Critical filesystem bootstrap completed. Legacy history import is available from the History page.");
        }
        catch (Exception ex)
        {
            log.Error($"Deferred startup bootstrap error: {ex}");
        }

        return Task.CompletedTask;
    }

    private static void MigrateLocalLegacyDatabase(AsyncFileLogService log)
    {
        if (File.Exists(RuntimePaths.DatabaseFile) || !File.Exists(RuntimePaths.LegacyDatabaseFile))
            return;

        Directory.CreateDirectory(RuntimePaths.DataDirectory);
        string temporaryPath = RuntimePaths.DatabaseFile + ".migration.tmp";
        try
        {
            File.Copy(RuntimePaths.LegacyDatabaseFile, temporaryPath, overwrite: false);
            File.Move(temporaryPath, RuntimePaths.DatabaseFile, overwrite: false);
            log.Application(
                $"DATABASE_MIGRATION {RuntimePaths.LegacyDatabaseFile} -> {RuntimePaths.DatabaseFile}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

}
