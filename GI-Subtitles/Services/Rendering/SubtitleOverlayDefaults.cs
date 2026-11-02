namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Default look and timing of the subtitle overlay, used by <c>MainWindow</c>
    /// when the matching Config key is unset. Public so the benchmark harness can
    /// render promo footage with the same values instead of a copy.
    /// </summary>
    public static class SubtitleOverlayDefaults
    {
        /// <summary>Config "Size": subtitle font size in DIPs.</summary>
        public const int FontSize = 22;

        /// <summary>
        /// Config "FontFamily". Bundled with the app (Assets/Fonts) and resolved by <see cref="OverlayFonts"/>;
        /// "Segoe UI" was the default before 2.6 and is read as this one (see <see cref="OverlayStyle.FromConfig"/>).
        /// </summary>
        public const string FontFamily = OverlayFonts.SourceSans;

        /// <summary>The default before 2.6. Saved by every 2.5 build at launch, so it does not mean a choice.</summary>
        public const string LegacyFontFamily = "Segoe UI";

        /// <summary>Config "SubtitleBgOpacity": alpha of the black panel, 0-255.</summary>
        public const int BackgroundOpacity = 176;

        /// <summary>Config "MaxOverlayWidth" (DIPs) and "MaxOverlayHeight" (0 = auto).</summary>
        public const int MaxOverlayWidth = 900;
        public const int MaxOverlayHeight = 0;

        /// <summary>Config "AutoShrinkText" and the floor it shrinks to.</summary>
        public const bool AutoShrinkText = true;
        public const double MinFontSize = 12;

        /// <summary>A line wraps to at most this many lines before the text shrinks (auto-shrink on).</summary>
        public const int PreferredMaxLines = 2;

        /// <summary>Config "UiRefreshInterval": UI timer period in ms.</summary>
        public const int UiRefreshIntervalMs = 150;

        /// <summary>No new text for this long hides the overlay.</summary>
        public const double IdleHideSeconds = 1.0;

        /// <summary>Motion (2.6): in 120 ms, out 160 ms; 0 when Windows animations are off.</summary>
        public const int IdleFadeOutMs = 160;
        public const int ContainerFadeInMs = 120;
        public const int TextFadeInMs = 120;

        /// <summary>The panel follows a new line's size in this long instead of jumping (reduced motion: at once).</summary>
        public const int ResizeMs = 140;

        /// <summary>Text that comes back within this window repaints without a fade.</summary>
        public const double RecentRestoreSeconds = 3.0;
    }
}
