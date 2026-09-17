using System.IO;
using System.Security.Cryptography;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class ModelFileIdentityService
{
    public static void Capture(ProductModel model, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        string path = string.IsNullOrWhiteSpace(sourcePath)
            ? model.SourcePath
            : sourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        model.SourcePath = fullPath;
        model.SourceHash = Convert.ToHexString(SHA256.HashData(stream));
        model.SourceLength = info.Length;
        model.SourceModifiedAt = info.LastWriteTime;
    }
}
