using System;
using System.Windows;
using System.Windows.Controls;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.QuickTranslate;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>General tab: the quick-translate popup card. Values apply to the next popup.</summary>
    public partial class SettingsWindow
    {
        private bool _quickTranslateCardLoading;

        private void InitializeQuickTranslateCard()
        {
            _quickTranslateCardLoading = true;
            try
            {
                var s = QuickTranslateSettings.Load();
                QuickTranslateSecondsSlider.Value = s.Seconds;
                QuickTranslateFontSlider.Value = s.FontSize;
                QuickTranslateSecondsValue.Text = FormatSeconds(s.Seconds);
                QuickTranslateFontValue.Text = s.FontSize.ToString();
                switch (s.CloseMode)
                {
                    case QuickTranslateCloseMode.Timed: QuickTranslateModeTimed.IsChecked = true; break;
                    case QuickTranslateCloseMode.Manual: QuickTranslateModeManual.IsChecked = true; break;
                    default: QuickTranslateModeAuto.IsChecked = true; break;
                }
                QuickTranslateSecondsRow.Visibility = s.CloseMode == QuickTranslateCloseMode.Timed
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Quick translate card init: {ex.Message}");
            }
            finally
            {
                _quickTranslateCardLoading = false;
            }
        }

        private void QuickTranslateMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || !(rb.Tag is string mode)) return;
            if (QuickTranslateSecondsRow != null)
                QuickTranslateSecondsRow.Visibility = mode == nameof(QuickTranslateCloseMode.Timed)
                    ? Visibility.Visible : Visibility.Collapsed;
            // The little picture of the popup shows its countdown only when it closes by itself.
            if (QuickTranslatePreviewTimer != null)
                QuickTranslatePreviewTimer.Visibility = mode == nameof(QuickTranslateCloseMode.Manual)
                    ? Visibility.Collapsed : Visibility.Visible;
            if (_quickTranslateCardLoading) return;
            Config.Set(QuickTranslateSettings.CloseModeKey, mode);
        }

        private void QuickTranslateSecondsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int seconds = (int)Math.Round(e.NewValue);
            if (QuickTranslateSecondsValue != null) QuickTranslateSecondsValue.Text = FormatSeconds(seconds);
            if (_quickTranslateCardLoading || !IsLoaded) return;
            Config.Set(QuickTranslateSettings.SecondsKey, seconds);
        }

        private void QuickTranslateFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int size = (int)Math.Round(e.NewValue);
            if (QuickTranslateFontValue != null) QuickTranslateFontValue.Text = size.ToString();
            if (_quickTranslateCardLoading || !IsLoaded) return;
            Config.Set(QuickTranslateSettings.FontSizeKey, size);
        }

        private string FormatSeconds(int seconds) =>
            $"{seconds} {TryFindResource("Settings_QuickTranslate_Seconds_Unit") as string ?? "s"}";
    }
}
