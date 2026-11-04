using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// What a game picker (the Settings pills, the setup window's choices) shows: the game's name, and for a beta
    /// game (games/&lt;id&gt;.json status) a small Beta badge, so nobody mistakes it for a game with subtitles ready.
    /// </summary>
    internal static class GamePickerContent
    {
        /// <summary>The badge's word (Game_BetaBadge; "Beta" in English and Polish alike).</summary>
        internal static string BetaWord => Application.Current?.TryFindResource("Game_BetaBadge") as string ?? "Beta";

        /// <summary>The display name for a live game; the name and a badge for a beta one.</summary>
        internal static object For(GameRegionProfile game)
        {
            if (!GameRegistry.IsBeta(game.GameId)) return game.DisplayName;
            var word = new TextBlock { Text = BetaWord, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            word.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = word,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = game.DisplayName, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(badge);
            return panel;
        }

        /// <summary>Puts <see cref="For"/> on a picker and names it for screen readers ("Wuthering Waves, Beta").</summary>
        internal static void Apply(ContentControl picker, GameRegionProfile game)
        {
            picker.Content = For(game);
            AutomationProperties.SetName(picker, GameRegistry.IsBeta(game.GameId) ? $"{game.DisplayName}, {BetaWord}" : game.DisplayName);
        }
    }
}
