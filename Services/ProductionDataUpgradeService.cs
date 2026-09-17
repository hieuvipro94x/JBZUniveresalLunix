using System.IO;
using System.Globalization;
using System.Text.Json;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Carries station-local production state forward when a new version is placed
/// in a sibling Vx.y.z folder. Existing files in the new version are never
/// overwritten, so an upgrade cannot roll counters or LOT values backwards.
/// </summary>
public static class ProductionDataUpgradeService
{
    private static readonly string[] ProductionFiles =
    [
        "JBZUniveresalLunix.cfg",
        Path.Combine("Data", "JBZUniveresalLunix.db"),
        "PartCnt.txt"
    ];

    private static readonly string[] FastConfigurationFiles =
    [
        "JBZUniveresalLunix.cfg"
    ];

    public static IReadOnlyList<string> MigrateForCurrentVersion()
    {
        string targetDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return MigrateMissingProductionData(
            targetDirectory,
            FindPreviousVersionDirectories(targetDirectory));
    }

    public static IReadOnlyList<string> MigrateFastConfigurationForCurrentVersion()
    {
        string targetDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return MigrateMissingProductionData(
            targetDirectory,
            FindPreviousVersionDirectories(targetDirectory),
            FastConfigurationFiles);
    }

    public static IReadOnlyList<string> MigrateDeferredProductionDataForCurrentVersion()
    {
        string targetDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return MigrateMissingProductionData(
            targetDirectory,
            FindPreviousVersionDirectories(targetDirectory),
            ProductionFiles.Except(FastConfigurationFiles, StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> MigrateMissingProductionData(
        string targetDirectory,
        IEnumerable<string> candidateDirectories) =>
        MigrateMissingProductionData(targetDirectory, candidateDirectories, ProductionFiles);

    private static IReadOnlyList<string> MigrateMissingProductionData(
        string targetDirectory,
        IEnumerable<string> candidateDirectories,
        IEnumerable<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(candidateDirectories);

        string targetRoot = Path.GetFullPath(targetDirectory);
        string[] sources = candidateDirectories
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, targetRoot, StringComparison.OrdinalIgnoreCase))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var migrated = new List<string>();
        foreach (string relativePath in relativePaths)
        {
            string targetPath = Path.Combine(targetRoot, relativePath);
            if (File.Exists(targetPath))
                continue;

            string? sourcePath = sources
                .Select(source => Path.Combine(source, relativePath))
                .Where(File.Exists)
                .OrderByDescending(path => GetProductionProgress(relativePath, path))
                .FirstOrDefault();
            if (sourcePath is null)
                continue;

            CopyWithoutOverwrite(sourcePath, targetPath);
            migrated.Add(relativePath);
        }

        return migrated;
    }

    private static IEnumerable<string> FindPreviousVersionDirectories(string targetDirectory)
    {
        DirectoryInfo target = new(targetDirectory);
        Version? targetVersion = ParseVersionDirectory(target.Name);
        var candidates = new List<DirectoryInfo>();

        if (target.Parent is not null)
            candidates.AddRange(target.Parent.EnumerateDirectories("V*", SearchOption.TopDirectoryOnly));

        // Also support placing the EXE directly in a folder that contains the
        // older Vx.y.z deployment folders.
        candidates.AddRange(target.EnumerateDirectories("V*", SearchOption.TopDirectoryOnly));

        return candidates
            .Select(directory => new { Directory = directory, Version = ParseVersionDirectory(directory.Name) })
            .Where(item => item.Version is not null)
            .Where(item => targetVersion is null || item.Version! < targetVersion)
            .OrderByDescending(item => item.Version)
            .Select(item => item.Directory.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Version? ParseVersionDirectory(string name) =>
        name.Length > 1 &&
        (name[0] == 'V' || name[0] == 'v') &&
        Version.TryParse(name[1..], out Version? version)
            ? version
            : null;

    private static void CopyWithoutOverwrite(string sourcePath, string targetPath)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = targetPath + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static long GetProductionProgress(string relativePath, string path)
    {
        try
        {
            string fileName = Path.GetFileName(relativePath);
            if (fileName.Equals("production.settings.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("LastBarcode.txt", StringComparison.OrdinalIgnoreCase))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("LotNo", out JsonElement lot) &&
                    lot.TryGetInt64(out long lotNo))
                {
                    return Math.Max(0, lotNo);
                }
            }

            if (fileName.Equals("PartCnt.txt", StringComparison.OrdinalIgnoreCase))
            {
                long total = 0;
                foreach (string line in File.ReadLines(path))
                {
                    string[] fields = line.Split(
                        [' ', '\t'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (fields.Length >= 3 &&
                        long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) &&
                        count > 0)
                    {
                        total = checked(total + count);
                    }
                }
                return total;
            }

            // A populated SQLite/history or statistics file is larger than a
            // newly initialized one. Candidate order remains the version tie-breaker.
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
