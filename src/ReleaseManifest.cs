using System;
using System.Text.Json;

namespace GuildrunTargetingMod;

/// <summary>What GitHub said, and whether it is worth telling the player about.</summary>
/// <remarks>
/// Deliberately free of MelonLoader, Unity and networking, so the part of this feature that can be
/// wrong in a way nobody notices is the part that can be run and checked outside a game launch. The
/// version comparison in here is the whole reason : it is correct or silent, it is never loud, so a
/// mistake in it would present as an updater that simply never fires, which is indistinguishable
/// from an updater that never had anything to say.
///
/// Total by construction. Every failure a malformed or unexpected answer can cause returns null.
/// </remarks>
internal static class ReleaseManifest
{
    private const string ModOnlyMarker = "mod-only";

    internal sealed class Release
    {
        public readonly string Version;
        public readonly string DownloadUrl;

        public Release(string version, string downloadUrl)
        {
            Version = version;
            DownloadUrl = downloadUrl;
        }
    }

    /// <summary>
    /// The release to offer, or null when the answer is unusable, older, or the one already running.
    /// </summary>
    public static Release ParseNewer(string json, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tag_name", out JsonElement tagElement)) return null;
            if (tagElement.ValueKind != JsonValueKind.String) return null;
            string tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return null;

            // Tags are written v2.4.0 and versions 2.4.0. Trimmed rather than assumed, so a tag that
            // ever arrives without the letter still parses.
            string latestText = tag.TrimStart('v', 'V');
            // Compared as numbers, never as text. "2.10.0" sorts BEFORE "2.9.0" as a string, so a
            // string comparison would go quiet at exactly the release where it started to matter,
            // and would look like an updater with nothing to report.
            if (!Version.TryParse(latestText, out Version latest)) return null;
            if (!Version.TryParse(currentVersion, out Version current)) return null;
            // Strictly newer. A build ahead of the published release is the maintainer's own, and
            // inviting them to downgrade is worse than saying nothing.
            if (latest <= current) return null;

            return new Release(latestText, FindDownloadUrl(root, tag));
        }
        catch (JsonException)
        {
            // An answer that is not JSON at all. A captive portal returning a login page is the
            // ordinary way this happens and it is not worth a word to anyone.
            return null;
        }
    }

    /// <summary>
    /// The mod-only zip if the release carries one, the release page if it does not.
    /// </summary>
    /// <remarks>
    /// Never a URL built out of the tag. A guessed filename is a 404 that looks exactly like a
    /// download until it fails, and every copy of the mod already installed would keep guessing the
    /// old shape forever after the naming changed.
    /// </remarks>
    private static string FindDownloadUrl(JsonElement root, string tag)
    {
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                if (!asset.TryGetProperty("name", out JsonElement nameElement)) continue;
                if (nameElement.ValueKind != JsonValueKind.String) continue;
                string name = nameElement.GetString();
                if (name == null || name.IndexOf(ModOnlyMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (!asset.TryGetProperty("browser_download_url", out JsonElement urlElement)) continue;
                if (urlElement.ValueKind != JsonValueKind.String) continue;
                string url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }

        if (root.TryGetProperty("html_url", out JsonElement pageElement) &&
            pageElement.ValueKind == JsonValueKind.String)
        {
            string page = pageElement.GetString();
            if (!string.IsNullOrWhiteSpace(page)) return page;
        }
        return "https://github.com/Larsonix/GuildrunTargetingMod/releases/tag/" + tag;
    }
}
