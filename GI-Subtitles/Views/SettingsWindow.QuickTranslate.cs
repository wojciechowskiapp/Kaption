using GI_Subtitles.Core.Settings;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Subtitle style tab: the quick-translate popup card. The card is bound to <see cref="QuickTranslateCardViewModel"/>;
    /// the window only gives it its view model.
    /// </summary>
    public partial class SettingsWindow
    {
        private void InitializeQuickTranslateCard() =>
            QuickTranslateCard.DataContext = new QuickTranslateCardViewModel(ConfigSettingsStore.Instance);
    }
}
