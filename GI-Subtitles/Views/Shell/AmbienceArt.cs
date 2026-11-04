using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Games;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The few ambience images the app may show (Assets/Ambience, embedded): one per game for the Start page's
    /// state card and one for the welcome screens. Decoded once off the UI thread, frozen and cached; a missing
    /// or broken asset yields null and the caller keeps its plain surface.
    /// </summary>
    public static class AmbienceArt
    {
        public const string Welcome = "creators-hero";
        private static readonly ConcurrentDictionary<string, Task<ImageSource>> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Asset name for any spelling of a listed game (its manifest's look.ambience); the first game's otherwise.</summary>
        public static string ForGame(string gameTag)
        {
            var registry = GameRegistry.Current;
            return (registry.FindLoose(gameTag) ?? registry.Default)?.Look?.Ambience ?? "genshin";
        }

        public static Task<ImageSource> LoadAsync(string asset) =>
            Cache.GetOrAdd(asset, a => Task.Run(() => Decode(new Uri($"pack://application:,,,/Assets/Ambience/{a}.jpg", UriKind.Absolute))));

        /// <summary>For tests and tools that run without the WPF application: decode from a file.</summary>
        public static ImageSource LoadFile(string path) => Decode(new Uri(path, UriKind.Absolute));

        private static ImageSource Decode(Uri uri)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Ambience image {uri} unavailable: {ex.Message}");
                return null;
            }
        }
    }
}
