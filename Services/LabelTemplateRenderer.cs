using System.IO;
using System.Text.RegularExpressions;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static partial class LabelTemplateRenderer
{
    private static readonly IReadOnlyDictionary<string, string> TokenAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DATA"] = "PRINT_DATE",
            ["LOTNO"] = "LOT_NO",
            ["PRODUCT"] = "PRODUCT_NAME",
            ["PARTNO"] = "PART_NUMBER",
            ["PARTNAME"] = "PRODUCT_NAME"
        };

    public static string Render(string template, LabelPrintData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var model = new ProductModel
        {
            PartNumber = data.PartNumber,
            ProductName = data.PartName,
            VehicleType = data.VehicleType,
            CustomerCode = data.CustomerCode,
            Eco = data.Eco,
            Nco = data.Nco,
            Alc = data.Alc
        };
        IReadOnlyDictionary<string, string> variables =
            LabelVariableResolver.Resolve(model, data, new LabelSettings());
        return Render(template, variables, data.PartNumber);
    }

    public static string Render(
        LabelProfile profile,
        string template,
        IReadOnlyDictionary<string, string> variables,
        string partNumber)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Mode == LabelPrintMode.NeedsOriginalTrace)
            throw new InvalidDataException($"NEEDS_ORIGINAL_TRACE: label profile '{profile.Id}' has no verified payload definition.");

        return Render(template, variables, partNumber, profile.Id);
    }

    public static string Render(
        string template,
        IReadOnlyDictionary<string, string> variables,
        string partNumber = "",
        string profileId = "")
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // THT TEM_TO cũ truyền dòng Data Matrix bằng biểu thức ghép này. LOTNO
        // nay luôn là LOT hiển thị không hậu tố; đổi riêng toàn bộ biểu thức mã
        // vạch sang BARCODE để hậu tố ALC /1 hoặc /2 chỉ đi vào Data Matrix.
        string normalizedTemplate = LegacyTemToBarcodeRegex().Replace(template, "{BARCODE}");
        string rendered = BraceTokenRegex().Replace(normalizedTemplate, match =>
            Resolve(match, match.Groups["name"].Value, variables, unresolved));
        rendered = DollarTokenRegex().Replace(rendered, match =>
            Resolve(match, match.Groups["name"].Value, variables, unresolved));

        if (unresolved.Count > 0)
        {
            string tokens = string.Join(",", unresolved.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            string message = $"LABEL_RENDER_FAILED PartNumber={partNumber} Profile={profileId} Unresolved={tokens}";
            AsyncFileLogService.Current.Error($"[LABEL][ERROR] {message}");
            throw new InvalidDataException(message);
        }

        return rendered;
    }

    public static string NormalizeEplJob(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string normalized = payload
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        string[] commandLines = normalized
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("'", StringComparison.Ordinal))
            .ToArray();

        string job = string.Join("\r\n", commandLines).Trim('\r', '\n');
        return job + "\r\n";
    }

    private static string Resolve(
        Match match,
        string tokenName,
        IReadOnlyDictionary<string, string> variables,
        ISet<string> unresolved)
    {
        string variableName = TokenAliases.TryGetValue(tokenName, out string? alias)
            ? alias
            : tokenName;
        if (variables.TryGetValue(variableName, out string? value))
            return value ?? string.Empty;

        unresolved.Add(match.Value);
        return match.Value;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex BraceTokenRegex();

    [GeneratedRegex(@"\$(?<name>[A-Za-z][A-Za-z0-9_]*)(?:\$)?", RegexOptions.CultureInvariant)]
    private static partial Regex DollarTokenRegex();

    [GeneratedRegex(
        @"(?:\{PART_NUMBER\}|\$PARTNO\$?)\$DATA\$\$LOTNO\$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyTemToBarcodeRegex();
}
