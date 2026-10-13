#nullable enable

using System.Text;

namespace SteamDashET;

internal static class ThirdPartyNotices
{
    private const string ResourcePrefix = "SteamDashET.Notices.";
    private static readonly Lazy<string> text = new(ReadNotices);

    internal static string Text => text.Value;

    private static string ReadNotices()
    {
        var assembly = typeof(ThirdPartyNotices).Assembly;
        string[] names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Third-party notices are missing from this build.");

        var result = new StringBuilder();
        foreach (string name in names)
        {
            result.AppendLine(name[ResourcePrefix.Length..]);
            result.AppendLine(new string('=', 72));
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded notice is missing: {name}");
            using var reader = new StreamReader(stream);
            result.AppendLine(reader.ReadToEnd().ReplaceLineEndings(Environment.NewLine));
            result.AppendLine();
        }
        return result.ToString();
    }
}
