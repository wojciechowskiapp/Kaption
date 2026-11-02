using System;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// The read-more sites the character data links to, by host suffix. Each has a small mark drawn by the app
    /// (Themes/Chips.xaml), so opening a page makes no request to those sites; an unknown host gets a link glyph.
    /// </summary>
    public static class LinkHosts
    {
        public const string Generic = "link";
        public const string YouTube = "youtube";

        // Host suffix, icon key, monogram ("" for a drawn glyph). Every host in the shipped links data is here.
        private static readonly (string Suffix, string Key, string Monogram)[] Known =
        {
            ("game8.co", "game8", "8"),
            ("game8.jp", "game8", "8"),
            ("keqingmains.com", "kqm", "K"),
            ("fandom.com", "fandom", "F"),
            ("hoyolab.com", "hoyolab", "H"),
            ("prydwen.gg", "prydwen", "P"),
            ("reddit.com", "reddit", "r"),
            ("youtube.com", YouTube, ""),
            ("youtu.be", YouTube, ""),
        };

        /// <summary>The icon key for a link: a known site's, else <see cref="Generic"/>.</summary>
        public static string KeyFor(string url) => Find(url).Key;

        /// <summary>The letter drawn in a known site's tile; "" for drawn glyphs and unknown hosts.</summary>
        public static string MonogramFor(string url) => Find(url).Monogram;

        private static (string Key, string Monogram) Find(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return (Generic, "");
            string host = uri.Host.ToLowerInvariant();
            foreach (var (suffix, key, monogram) in Known)
            {
                // Whole labels only: "fandom.com" and "x.fandom.com", never "notfandom.com".
                if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)) return (key, monogram);
            }
            return (Generic, "");
        }
    }
}
