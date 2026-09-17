using System.IO;
using System.Text.RegularExpressions;

namespace JBZUniveresalLunix.Services;

public sealed record JbzPartFiles(string PartNumber, string ModelPath, string SetupPath);

public static class JbzPartFileResolver
{
    public static JbzPartFiles Resolve(string partNumber, string? appDirectory = null,
        string? userDirectory = null)
    {
        string part = partNumber.Trim();
        if (!Regex.IsMatch(part, "^[A-Za-z0-9_-]+$"))
            throw new InvalidDataException("Mã hàng chỉ được chứa chữ, số, dấu gạch ngang hoặc gạch dưới.");
        string application = Path.GetFullPath(appDirectory ?? RuntimePaths.AppDirectory);
        string profile = Path.GetFullPath(userDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (string root in new[] { application, profile }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? model = Find(root, "Models", "model", part + ".model");
            if (model is null) continue;
            string? setup = Find(root, "Setups", "setup", part + ".setup");
            if (setup is null)
                throw new FileNotFoundException($"Đã tìm thấy {part}.model nhưng thiếu {part}.setup trong {root}.");
            _ = JbzSetupParser.Load(setup, model);
            return new(part, model, setup);
        }
        throw new FileNotFoundException($"Không tìm thấy {part}.model trong Models của {application} hoặc {profile}.");
    }

    private static string? Find(string root, string plural, string singular, string fileName)
    {
        foreach (string folder in new[] { plural, singular })
        {
            string path = Path.Combine(root, folder, fileName);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }
}
