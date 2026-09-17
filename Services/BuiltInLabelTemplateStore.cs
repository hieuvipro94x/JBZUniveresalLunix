using System.IO;
using System.Reflection;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>
/// Loads the three verified built-in label templates from the application
/// assembly so production deployment does not require a Labels directory.
/// Explicit custom template paths remain supported by LabelProfileResolver.
/// </summary>
public static class BuiltInLabelTemplateStore
{
    private const string ReferencePrefix = "embedded-label://";

    public static string ReferenceFor(string? templateType) =>
        ReferencePrefix + LabelProfileResolver.NormalizeTemplateType(templateType);

    public static bool IsReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryReferenceForProfile(string? profileId, out string reference)
    {
        string profile = profileId?.Trim() ?? string.Empty;
        if (profile.Equals(LabelSettings.LargeTemplate, StringComparison.OrdinalIgnoreCase) ||
            profile.Equals(LabelSettings.SmallTemplate, StringComparison.OrdinalIgnoreCase) ||
            profile.Equals(LabelSettings.SmallQrTemplate, StringComparison.OrdinalIgnoreCase))
        {
            reference = ReferenceFor(profile);
            return true;
        }

        reference = string.Empty;
        return false;
    }

    public static string Load(string reference)
    {
        if (!IsReference(reference))
            throw new InvalidDataException($"Not a built-in label reference: {reference}");

        string profile = reference[ReferencePrefix.Length..];
        if (!TryReferenceForProfile(profile, out _))
            throw new InvalidDataException($"Unknown built-in label profile: {profile}");

        string resourceName = $"JBZUniveresalLunix.Labels.{profile.ToUpperInvariant()}.txt";
        Assembly assembly = typeof(BuiltInLabelTemplateStore).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Built-in label resource not found: {resourceName}",
                resourceName);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string LoadOverride(LabelSettings settings, string? templateType)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string encoded = LabelProfileResolver.NormalizeTemplateType(templateType) switch
        {
            LabelSettings.SmallTemplate => settings.SmallTemplateOverrideBase64,
            LabelSettings.SmallQrTemplate => settings.SmallQrTemplateOverrideBase64,
            _ => settings.LargeTemplateOverrideBase64
        };
        if (string.IsNullOrWhiteSpace(encoded))
            return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Label template override in JBZUniveresalLunix.cfg is not valid Base64.", ex);
        }
    }

    public static void SaveOverride(LabelSettings settings, string? templateType, string template)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(template);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(template));
        switch (LabelProfileResolver.NormalizeTemplateType(templateType))
        {
            case LabelSettings.SmallTemplate:
                settings.SmallTemplateOverrideBase64 = encoded;
                break;
            case LabelSettings.SmallQrTemplate:
                settings.SmallQrTemplateOverrideBase64 = encoded;
                break;
            default:
                settings.LargeTemplateOverrideBase64 = encoded;
                break;
        }
    }

    public static void ClearOverride(LabelSettings settings, string? templateType)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (LabelProfileResolver.NormalizeTemplateType(templateType))
        {
            case LabelSettings.SmallTemplate:
                settings.SmallTemplateOverrideBase64 = string.Empty;
                break;
            case LabelSettings.SmallQrTemplate:
                settings.SmallQrTemplateOverrideBase64 = string.Empty;
                break;
            default:
                settings.LargeTemplateOverrideBase64 = string.Empty;
                break;
        }
    }
}
