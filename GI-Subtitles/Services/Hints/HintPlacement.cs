using System;
using System.Collections.Generic;
using System.Drawing;
using GI_Subtitles.Services.Answers;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>The card's two places: above the dialogue at the left edge, or centred at the top.</summary>
    public enum HintPlace
    {
        Left,
        Top,
    }

    /// <summary>Where the card goes. <see cref="Compact"/>: the short form was needed to fit (a sheet without header and footer).</summary>
    public readonly record struct HintPlaced(HintPlace Place, bool Compact, Rectangle Bounds);

    /// <summary>
    /// What the card is placed against, all in physical pixels of one monitor. <see cref="Client"/> is the game's
    /// client area, or the screen's work area for a sheet asked for outside the game. <see cref="Dialogue"/> is the
    /// dialogue area (empty: none). <see cref="KeepClear"/> is everything else the card must not touch: the subtitle
    /// panel and Kaption's other windows, and <see cref="HintPlacement.GameAreas"/>. <see cref="Scale"/> is pixels
    /// per DIP.
    /// </summary>
    public readonly record struct HintScene(Rectangle Client, Rectangle Dialogue, IReadOnlyList<Rectangle> KeepClear, double Scale);

    /// <summary>
    /// Where the hint card goes (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.5). Pure. The card never lies
    /// on the dialogue, on a subtitle, on the answers or on anything Kaption reads: a place where it would is
    /// refused, the short form is tried there, then the other place, and when nothing fits nothing is shown.
    /// </summary>
    public static class HintPlacement
    {
        /// <summary>DIPs from the client's left edge.</summary>
        public const double EdgeDip = 16;
        /// <summary>DIPs between the card's bottom and the top of the dialogue area.</summary>
        public const double AboveDialogueDip = 24;
        /// <summary>The card keeps this many DIPs away from everything it must not touch.</summary>
        public const double ClearDip = 8;
        /// <summary>The left place is refused when the card's top would be above this share of the client's height: that is the game's quest tracker.</summary>
        public const double LeftTopLimit = 0.42;
        /// <summary>The top place's top edge, as a share of the client's height.</summary>
        public const double TopShare = 0.04;

        public const string LeftName = "left", TopName = "top";

        public static HintPlace ParsePlace(string saved) =>
            string.Equals(saved?.Trim(), TopName, StringComparison.OrdinalIgnoreCase) ? HintPlace.Top : HintPlace.Left;

        public static string NameOf(HintPlace place) => place == HintPlace.Top ? TopName : LeftName;

        /// <summary>
        /// Finds a place for a card of <paramref name="full"/> pixels, or of <paramref name="compact"/> pixels when the
        /// full one does not fit (empty: the card has no short form). <paramref name="first"/> is the place tried first.
        /// </summary>
        public static bool TryPlace(in HintScene scene, HintPlace first, Size full, Size compact, out HintPlaced placed)
        {
            HintPlace second = first == HintPlace.Left ? HintPlace.Top : HintPlace.Left;
            return TryAt(scene, first, full, compact, out placed) || TryAt(scene, second, full, compact, out placed);
        }

        /// <summary>
        /// For a card that is up: it stays where it is while it touches nothing, and is placed again (its own place
        /// first) when something came under it, such as a subtitle. False: it has to go.
        /// </summary>
        public static bool TryKeep(in HintScene scene, HintPlaced current, Size full, Size compact, out HintPlaced placed)
        {
            placed = current;
            return IsClear(scene, current.Bounds) || TryPlace(scene, current.Place, full, compact, out placed);
        }

        private static bool TryAt(in HintScene scene, HintPlace place, Size full, Size compact, out HintPlaced placed)
        {
            if (TryBounds(scene, place, full, out Rectangle bounds))
            {
                placed = new HintPlaced(place, false, bounds);
                return true;
            }
            if (!compact.IsEmpty && TryBounds(scene, place, compact, out bounds))
            {
                placed = new HintPlaced(place, true, bounds);
                return true;
            }
            placed = default;
            return false;
        }

        private static bool TryBounds(in HintScene scene, HintPlace place, Size card, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            Rectangle client = scene.Client;
            if (card.Width <= 0 || card.Height <= 0 || client.Width <= 0 || client.Height <= 0 || !(scene.Scale > 0)) return false;

            int x, y;
            if (place == HintPlace.Top)
            {
                x = client.Left + (client.Width - card.Width) / 2;
                y = client.Top + (int)Math.Round(client.Height * TopShare);
            }
            else
            {
                x = client.Left + (int)Math.Round(EdgeDip * scene.Scale);
                if (scene.Dialogue.IsEmpty)
                {
                    y = client.Top + (client.Height - card.Height) / 2;
                }
                else
                {
                    y = scene.Dialogue.Top - (int)Math.Round(AboveDialogueDip * scene.Scale) - card.Height;
                    if (y < client.Top + client.Height * LeftTopLimit) return false;
                }
            }

            var candidate = new Rectangle(x, y, card.Width, card.Height);
            if (!IsClear(scene, candidate)) return false;
            bounds = candidate;
            return true;
        }

        /// <summary>Whether a card at <paramref name="card"/> is inside the client and touches nothing it must keep clear of.</summary>
        public static bool IsClear(in HintScene scene, Rectangle card)
        {
            if (card.IsEmpty || !scene.Client.Contains(card)) return false;
            int clear = (int)Math.Ceiling(ClearDip * Math.Max(0, scene.Scale));
            Rectangle grown = Rectangle.Inflate(card, clear, clear);
            if (!scene.Dialogue.IsEmpty && grown.IntersectsWith(scene.Dialogue)) return false;
            if (scene.KeepClear != null)
                foreach (Rectangle area in scene.KeepClear)
                    if (!area.IsEmpty && grown.IntersectsWith(area)) return false;
            return true;
        }

        /// <summary>
        /// The most the subtitle panel can ever cover, so a card placed beside it now is still beside it when a long
        /// line comes (a pinned sheet does not jump to its other place in the middle of a dialogue).
        /// <paramref name="window"/> is the subtitle window as it is: the panel is centred in it, at most
        /// <paramref name="maxPanelWidth"/> wide, and grows from the edge next to the dialogue up to
        /// <paramref name="maxPanelHeight"/>. All in pixels.
        /// </summary>
        public static Rectangle PanelReserve(Rectangle window, bool growsUpward, int maxPanelWidth, int maxPanelHeight)
        {
            if (window.Width <= 0 || window.Height <= 0) return Rectangle.Empty;
            int width = maxPanelWidth > 0 ? Math.Min(window.Width, maxPanelWidth) : window.Width;
            int height = Math.Max(window.Height, maxPanelHeight);
            return new Rectangle(window.Left + (window.Width - width) / 2, growsUpward ? window.Bottom - height : window.Top, width, height);
        }

        /// <summary>
        /// What the game itself has where the card could go, from its manifest: the answers box, and while the stamina
        /// reader is on, the strip it searches and the screen watch's anchors (a card over them would blind the read).
        /// </summary>
        public static List<Rectangle> GameAreas(GameDesktop desktop, Rectangle client, bool staminaReaderOn)
        {
            var areas = new List<Rectangle>(2);
            if (desktop == null) return areas;
            if (AnswerGeometry.TryPlace(desktop.Answers, client, out Rectangle answers, out _)) areas.Add(answers);
            if (!staminaReaderOn) return areas;

            Rectangle strip = StaminaLocator.SearchArea(client, desktop.StaminaScreen);
            if (!strip.IsEmpty) areas.Add(strip);
            if (desktop.Assist?.Screens != null && AssistGeometry.IsChecked(desktop.Assist.Checked, client.Size))
                foreach (GameAssistScreen screen in desktop.Assist.Screens.Values)
                    foreach (GameAssistAnchor anchor in screen?.Anchors ?? Array.Empty<GameAssistAnchor>())
                        if (AssistGeometry.TryPlace(anchor, desktop.Assist.Canvas, client, ScreenProbe.Slack, out Rectangle patch)) areas.Add(patch);
            return areas;
        }
    }
}
