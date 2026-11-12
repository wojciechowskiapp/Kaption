using System;
using System.Collections.Concurrent;
using System.Windows.Media;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// The subtitle overlay's bundled faces (Assets/Fonts, embedded): Source Sans 3 first, Inter Tight as the fallback.
    /// A bare family name from Config ("Source Sans 3") resolves to the embedded files with a fallback chain, so code
    /// that only knows the name (the overlay, the Settings preview, the promo renderer) draws the same face. Any other
    /// name is a system font and passes through unchanged.
    /// </summary>
    public static class OverlayFonts
    {
        public const string SourceSans = "Source Sans 3";
        public const string InterTight = "Inter Tight";

        /// <summary>Faces in Assets/Fonts with the weights they carry (the overlay never asks for others).</summary>
        public static readonly string[] Bundled = { SourceSans, InterTight };

        private static readonly ConcurrentDictionary<string, FontFamily> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>pack://application:,,,/Kaption;component/ (assembly-qualified, so it also resolves in tests).</summary>
        public static Uri PackBase => field ??= CreatePackBase();

        private static Uri CreatePackBase()
        {
            // The pack:// scheme is registered by Application's static constructor.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
            string asm = typeof(OverlayFonts).Assembly.GetName().Name;
            return new Uri($"pack://application:,,,/{asm};component/", UriKind.Absolute);
        }

        public static bool IsBundled(string name) =>
            name != null && Array.Exists(Bundled, b => string.Equals(b, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The family to draw with for a Config value. Bundled names get the embedded face plus the other bundled face
        /// and Segoe UI behind it (scripts the bundled files do not cover fall through to Windows' own fallback).
        /// </summary>
        public static FontFamily Resolve(string name)
        {
            string n = string.IsNullOrWhiteSpace(name) ? SubtitleOverlayDefaults.FontFamily : name.Trim();
            return Cache.GetOrAdd(n, key =>
            {
                if (!IsBundled(key)) return new FontFamily(key);
                string other = string.Equals(key, SourceSans, StringComparison.OrdinalIgnoreCase) ? InterTight : SourceSans;
                string canonical = string.Equals(key, SourceSans, StringComparison.OrdinalIgnoreCase) ? SourceSans : InterTight;
                try
                {
                    return new FontFamily(PackBase, $"./Assets/Fonts/#{canonical}, ./Assets/Fonts/#{other}, Segoe UI");
                }
                catch (Exception)
                {
                    return new FontFamily("Segoe UI");
                }
            });
        }

        /// <summary>
        /// A family as set on a control: bundled names without a base URI are swapped for the embedded face; anything
        /// already resolved (or a system font) is returned as is.
        /// </summary>
        public static FontFamily Resolve(FontFamily family)
        {
            if (family == null) return Resolve((string)null);
            if (family.BaseUri == null && IsBundled(family.Source)) return Resolve(family.Source);
            return family;
        }

        /// <summary>
        /// Size factor that keeps a saved size looking the same in every face: the configured size was chosen on Segoe UI
        /// (x-height 0.500, cap height 0.700). Source Sans 3 is a little smaller on the eye (0.491 / 0.660), Inter Tight
        /// larger (0.546 / 0.728). The mean of the two ratios, so 22 stays 22 to the reader.
        /// </summary>
        public static double FaceScale(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = SubtitleOverlayDefaults.FontFamily;
            if (string.Equals(name.Trim(), SourceSans, StringComparison.OrdinalIgnoreCase)) return 1.04;
            if (string.Equals(name.Trim(), InterTight, StringComparison.OrdinalIgnoreCase)) return 0.94;
            return 1.0;
        }
    }
}
