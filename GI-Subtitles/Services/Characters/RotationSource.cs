using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// The guide page a rotation was transcribed from: an absolute https URL of at most <see cref="MaxLength"/> characters
    /// without user info, shown as the site's known name ("KQM"), never as the URL. Same host table as the site
    /// (landing rotations.ts); keep the two in step. Pure; safe on any thread.
    /// </summary>
    public static class RotationSource
    {
        public const int MaxLength = 512;

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["keqingmains.com"] = "KQM",
            ["game8.co"] = "Game8",
            ["gamewith.net"] = "GameWith",
            ["mobalytics.gg"] = "Mobalytics",
            ["reddit.com"] = "Reddit",
            ["hoyolab.com"] = "HoYoLAB",
            ["prydwen.gg"] = "Prydwen",
            ["icy-veins.com"] = "Icy Veins",
            ["genshin.gg"] = "genshin.gg",
            ["genshin-builds.com"] = "Genshin Builds",
        };

        /// <summary>The URL when it qualifies, else "" (no source; the rotation itself still loads).</summary>
        public static string Normalize(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0 || url.Length > MaxLength) return "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "";
            if (uri.UserInfo.Length > 0 || uri.Host.Length == 0) return "";
            return url;
        }

        /// <summary>The URL's host, lower case ("www.keqingmains.com"); "" when the URL does not qualify.</summary>
        public static string Host(string url) =>
            Normalize(url) is string u && u.Length > 0 ? new Uri(u).Host.ToLowerInvariant() : "";

        /// <summary>"KQM" for keqingmains.com (also www. / old.), the bare host for a site the table does not know; "" for no source.</summary>
        public static string SiteName(string url)
        {
            string host = Host(url);
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            else if (host.StartsWith("old.", StringComparison.Ordinal)) host = host.Substring(4);
            return Names.TryGetValue(host, out string name) ? name : host;
        }
    }
}
