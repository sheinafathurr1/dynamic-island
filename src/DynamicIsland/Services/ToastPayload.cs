using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DynamicIsland.Services;

/// <summary>What a toast notification says: who / what (title), the message, and an optional picture.</summary>
internal sealed record ToastContent(string Title, string Body, string? ImageSource, bool RoundImage, bool HasProgress);

/// <summary>
/// Reads the XML that apps hand to Windows for a toast notification (both the adaptive
/// "ToastGeneric" layout and the older ToastText / ToastImageAndText templates).
/// </summary>
internal static partial class ToastPayload
{
    public static ToastContent? Parse(string xml)
    {
        XElement? toast;
        try
        {
            toast = XDocument.Parse(xml).Root;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        if (toast?.Name.LocalName != "toast") return null;

        var bindings = toast.Element("visual")?.Elements("binding").ToList() ?? [];
        var binding = bindings.FirstOrDefault(b => (string?)b.Attribute("template") == "ToastGeneric") ?? bindings.FirstOrDefault();
        if (binding is null) return null;
        bool generic = (string?)binding.Attribute("template") == "ToastGeneric";

        // Top-level lines first; adaptive layouts may tuck everything into groups instead.
        var lines = Lines(binding.Elements("text"));
        if (lines.Count == 0) lines = Lines(binding.Descendants("text"));
        if (lines.Count == 0) return null;

        // The small round / square picture next to the text: a contact photo or a logo.
        var images = binding.Elements("image").ToList();
        var picture = images.FirstOrDefault(i => (string?)i.Attribute("placement") == "appLogoOverride")
            ?? (generic ? null : images.FirstOrDefault());

        return new ToastContent(
            lines[0],
            string.Join("  ·  ", lines.Skip(1)),
            (string?)picture?.Attribute("src"),
            (string?)picture?.Attribute("hint-crop") == "circle",
            binding.Element("progress") is not null);
    }

    private static List<string> Lines(IEnumerable<XElement> texts) => texts
        .Where(t => (string?)t.Attribute("placement") != "attribution")
        .Select(t => Whitespace().Replace(t.Value, " ").Trim())
        .Where(t => t.Length > 0)
        .ToList();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
