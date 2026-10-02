using System.Text.RegularExpressions;

namespace NeuralBridge.Web.Components.Session;

/// <summary>
/// Finds http(s) URLs in shared text. Only absolute http/https URIs survive (no javascript:,
/// data: or file: links), and results are rendered as encoded text with explicit,
/// user-clicked links. Nothing is ever opened automatically.
/// </summary>
public static partial class LinkDetector
{
    public const int MaxLinks = 20;

    public static IReadOnlyList<Uri> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<Uri>();
        foreach (Match match in UrlPattern().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                !string.IsNullOrEmpty(uri.Host) &&
                !found.Contains(uri))
            {
                found.Add(uri);
                if (found.Count == MaxLinks)
                {
                    break;
                }
            }
        }

        return found;
    }

    [GeneratedRegex(@"https?://[^\s<>""'`]{2,2048}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex UrlPattern();
}
