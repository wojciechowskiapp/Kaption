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

        /// <summary>Config "FontFamily".</summary>
        public const string FontFamily = "Segoe UI";

        /// <summary>Config "SubtitleBgOpacity": alpha of the black panel, 0-255.</summary>
        public const int BackgroundOpacity = 176;

        /// <summary>Config "MaxOverlayWidth" (DIPs) and "MaxOverlayHeight" (0 = auto).</summary>
        public const int MaxOverlayWidth = 900;
        public const int MaxOverlayHeight = 0;

        /// <summary>Config "AutoShrinkText" and the floor it shrinks to.</summary>
        public const bool AutoShrinkText = true;
        public const double MinFontSize = 12;

        /// <summary>Config "UiRefreshInterval": UI timer period in ms.</summary>
        public const int UiRefreshIntervalMs = 150;

        /// <summary>No new text for this long hides the overlay.</summary>
        public const double IdleHideSeconds = 1.0;

        public const int IdleFadeOutMs = 500;
        public const int ContainerFadeInMs = 150;
        public const int TextFadeInMs = 180;

        /// <summary>Text that comes back within this window repaints without a fade.</summary>
        public const double RecentRestoreSeconds = 3.0;
    }
}
