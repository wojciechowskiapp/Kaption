using System;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Views
{
    /// <summary>The farm card's link to Settings › Characters: MainWindow shows the card, the page holds its settings.</summary>
    public partial class SettingsWindow
    {
        /// <summary>Set by MainWindow: shows today's farm card (the block's "Show the card").</summary>
        public Action OnShowFarmCard { get; set; }

        /// <summary>After "Don't show" on the card: the block's switch follows.</summary>
        internal void RefreshFarmBlock() => CharactersPage?.FarmBlock.Refresh(Config.Get("Game", Game) ?? "Genshin");

        /// <summary>"Add UID" on the card: the Characters page with the UID field open and focused.</summary>
        internal void ShowFarmBlockUid()
        {
            ShowCharactersPage();
            CharactersPage?.FarmBlock.Refresh(Config.Get("Game", Game) ?? "Genshin");
            CharactersPage?.FarmBlock.OpenSettings(focusUid: true);
        }
    }
}
