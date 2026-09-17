using System.IO;
using System.Text;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public static class LabelTemplateProvider
{
    static LabelTemplateProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Load(LabelProfile profile, string embeddedTemplate)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!string.IsNullOrWhiteSpace(embeddedTemplate))
            return embeddedTemplate;

        if (!string.IsNullOrWhiteSpace(profile.InlineTemplate))
            return profile.InlineTemplate;

        if (profile.Mode == LabelPrintMode.NeedsOriginalTrace)
            return string.Empty;

        if (BuiltInLabelTemplateStore.IsReference(profile.TemplatePath))
            return BuiltInLabelTemplateStore.Load(profile.TemplatePath);

        if (string.IsNullOrWhiteSpace(profile.TemplatePath))
            throw new InvalidDataException($"Profile '{profile.Id}' has no template source.");
        if (!File.Exists(profile.TemplatePath))
            throw new FileNotFoundException($"Label template not found for profile '{profile.Id}'.", profile.TemplatePath);

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(
                profile.EncodingName,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Unsupported label encoding '{profile.EncodingName}'.", ex);
        }

        return File.ReadAllText(profile.TemplatePath, encoding);
    }
}
