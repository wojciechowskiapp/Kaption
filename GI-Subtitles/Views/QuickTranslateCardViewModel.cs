using System;
using CommunityToolkit.Mvvm.ComponentModel;
using GI_Subtitles.Core.Settings;
using GI_Subtitles.Services.QuickTranslate;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Settings › Subtitle style › the Ctrl+Q popup card: how the popup closes, after how long, and its text size.
    /// Values apply to the next popup (QuickTranslateSettings.Load reads them fresh). The first Settings page on the
    /// MVVM pattern; the rules for the next ones are in .plan/in-progress/MVVM-PILOT.md.
    /// </summary>
    public sealed partial class QuickTranslateCardViewModel : ObservableObject
    {
        private readonly ISettingsStore _store;
        private readonly bool _loaded; // false while the constructor shows the saved values: showing is not a choice

        public QuickTranslateCardViewModel(ISettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            var saved = QuickTranslateSettings.Load(store);
            Mode = saved.CloseMode;
            Seconds = saved.Seconds;
            FontSize = saved.FontSize;
            _loaded = true;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAuto), nameof(IsTimed), nameof(IsManual), nameof(IsSecondsRowVisible), nameof(IsPreviewTimerVisible))]
        public partial QuickTranslateCloseMode Mode { get; set; }

        /// <summary>Seconds the timed popup stays; the slider snaps to whole numbers within the settings' bounds.</summary>
        [ObservableProperty]
        public partial int Seconds { get; set; }

        [ObservableProperty]
        public partial int FontSize { get; set; }

        // One per radio button. A group unchecking its old choice writes false back through the binding: only a
        // true picks a mode, so one click is one change.
        public bool IsAuto { get => Mode == QuickTranslateCloseMode.Auto; set { if (value) Mode = QuickTranslateCloseMode.Auto; } }
        public bool IsTimed { get => Mode == QuickTranslateCloseMode.Timed; set { if (value) Mode = QuickTranslateCloseMode.Timed; } }
        public bool IsManual { get => Mode == QuickTranslateCloseMode.Manual; set { if (value) Mode = QuickTranslateCloseMode.Manual; } }

        public bool IsSecondsRowVisible => Mode == QuickTranslateCloseMode.Timed;

        /// <summary>The small picture of the popup shows its countdown only when it closes by itself.</summary>
        public bool IsPreviewTimerVisible => Mode != QuickTranslateCloseMode.Manual;

        // Saved in the shapes 2.6.2 and older wrote: the mode by its name, the sizes as ints (players roll back).
        partial void OnModeChanged(QuickTranslateCloseMode value)
        {
            if (_loaded) _store.Set(QuickTranslateSettings.CloseModeKey, value.ToString());
        }

        partial void OnSecondsChanged(int value)
        {
            if (_loaded) _store.Set(QuickTranslateSettings.SecondsKey, value);
        }

        partial void OnFontSizeChanged(int value)
        {
            if (_loaded) _store.Set(QuickTranslateSettings.FontSizeKey, value);
        }
    }
}
