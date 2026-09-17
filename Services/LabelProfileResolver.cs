using System.IO;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class LabelProfileResolver
{
    public static LabelProfile Resolve(ProductModel model, LabelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);

        string templateType = NormalizeTemplateType(settings.TemplateType);
        string profileId = FirstNotEmpty(model.LabelTemplate.ProfileId, templateType, settings.FormatName, "UNRESOLVED");
        string encoding = string.IsNullOrWhiteSpace(settings.EncodingName)
            ? "us-ascii"
            : settings.EncodingName.Trim();

        if (!string.IsNullOrWhiteSpace(model.LabelTemplate.RawTemplate))
        {
            return new LabelProfile(
                profileId,
                DetectLanguage(model.LabelTemplate.RawTemplate),
                EncodingName: encoding,
                Copies: ResolveCopies(model, settings));
        }

        string explicitTemplate = ResolvePath(settings.TemplatePath);
        if (!string.IsNullOrWhiteSpace(explicitTemplate))
        {
            string helper = ResolvePath(settings.ExternalHelperPath);
            return new LabelProfile(
                profileId,
                string.IsNullOrWhiteSpace(helper) ? LabelPrintMode.ExternalTemplate : LabelPrintMode.ExternalHelper,
                TemplatePath: explicitTemplate,
                ExternalHelperPath: helper,
                ExternalHelperArgument: settings.ExternalHelperArgument ?? string.Empty,
                ExternalPrintFile: settings.ExternalPrintFile ?? string.Empty,
                EncodingName: encoding,
                Copies: ResolveCopies(model, settings));
        }

        if (string.IsNullOrWhiteSpace(model.LabelTemplate.ProfileId))
        {
            string builtInTemplate = ResolveBuiltInTemplatePath(templateType);
            return new LabelProfile(
                templateType,
                LabelPrintMode.ExternalTemplate,
                TemplatePath: builtInTemplate,
                EncodingName: encoding,
                Copies: 1,
                InlineTemplate: BuiltInLabelTemplateStore.LoadOverride(settings, templateType));
        }

        string discovered = FindExactProfileTemplate(profileId);
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            string helper = ResolvePath(settings.ExternalHelperPath);
            string inlineTemplate = BuiltInLabelTemplateStore.IsReference(discovered)
                ? BuiltInLabelTemplateStore.LoadOverride(settings, profileId)
                : string.Empty;
            return new LabelProfile(
                profileId,
                string.IsNullOrWhiteSpace(helper) ? LabelPrintMode.ExternalTemplate : LabelPrintMode.ExternalHelper,
                TemplatePath: discovered,
                ExternalHelperPath: helper,
                ExternalHelperArgument: settings.ExternalHelperArgument ?? string.Empty,
                ExternalPrintFile: settings.ExternalPrintFile ?? string.Empty,
                EncodingName: encoding,
                Copies: ResolveCopies(model, settings),
                InlineTemplate: inlineTemplate);
        }

        return new LabelProfile(
            profileId,
            LabelPrintMode.NeedsOriginalTrace,
            EncodingName: encoding,
            Copies: ResolveCopies(model, settings),
            VerificationStatus: "NEEDS_ORIGINAL_TRACE");
    }

    public static LabelPrintMode DetectLanguage(string payload)
    {
        string trimmed = payload.TrimStart();
        if (trimmed.StartsWith("^XA", StringComparison.Ordinal))
            return LabelPrintMode.RawZpl;
        if (trimmed.StartsWith("N", StringComparison.Ordinal) ||
            trimmed.StartsWith("FR\"", StringComparison.Ordinal))
            return LabelPrintMode.RawEpl;
        return LabelPrintMode.ExternalTemplate;
    }

    private static int ResolveCopies(ProductModel model, LabelSettings settings) => 1;

    public static string NormalizeTemplateType(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (string.Equals(normalized, LabelSettings.SmallTemplate, StringComparison.OrdinalIgnoreCase))
            return LabelSettings.SmallTemplate;
        if (string.Equals(normalized, LabelSettings.SmallQrTemplate, StringComparison.OrdinalIgnoreCase))
            return LabelSettings.SmallQrTemplate;
        return LabelSettings.LargeTemplate;
    }

    public static string ResolveBuiltInTemplatePath(string? templateType) =>
        BuiltInLabelTemplateStore.ReferenceFor(templateType);

    private static string FindExactProfileTemplate(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return string.Empty;

        if (BuiltInLabelTemplateStore.TryReferenceForProfile(profileId, out string builtInReference))
            return builtInReference;

        string[] candidates =
        [
            // A THT file may explicitly name one of the verified built-in profiles
            // (for example TEM_BE_QR). Resolve that exact profile beside the EXE
            // before falling back to legacy/custom profile directories.
            Path.Combine(AppContext.BaseDirectory, "Labels", profileId + ".txt"),
            Path.Combine(AppContext.BaseDirectory, "BarForm", profileId + ".txt"),
            Path.Combine(AppContext.BaseDirectory, "Labels", "Legacy", profileId + ".epl"),
            Path.Combine(AppContext.BaseDirectory, "Labels", "Legacy", profileId + ".zpl"),
            Path.Combine(AppContext.BaseDirectory, "config", "labels", profileId + ".txt")
        ];
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
