using System.Drawing;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Answers
{
    /// <summary>Whether answers can run for a game at a window size, and if not, why (the Settings line says which).</summary>
    public enum AnswerAvailability
    {
        Available,
        /// <summary>The manifest has no usable desktop.answers block: nobody measured this game's menu.</summary>
        NotForGame,
        /// <summary>The block exists, but this window's shape is not one it was checked on.</summary>
        NotForShape,
    }

    /// <summary>
    /// Where the game's option menu is, from desktop.answers and the game's client rectangle: canvas pixels from a
    /// window corner, scaled by the game's own rule (AssistGeometry). Pure, and it never guesses: a game without the
    /// block, a broken block or an unmeasured window shape gets no place.
    /// </summary>
    public static class AnswerGeometry
    {
        public static AnswerAvailability Availability(GameAnswers answers, Size client)
        {
            if (!IsUsable(answers)) return AnswerAvailability.NotForGame;
            return AssistGeometry.IsChecked(answers.Checked, client) ? AnswerAvailability.Available : AnswerAvailability.NotForShape;
        }

        /// <summary>Everything answers need is there and sane; anything less switches answers off for the game.</summary>
        private static bool IsUsable(GameAnswers answers) =>
            answers?.Canvas != null && answers.Checked != null && AssistGeometry.FitsCanvas(answers.Box, answers.Canvas)
            && answers.Ink is { Level: > 0 and < 255, LineHeight: > 0 } && answers.PillPad >= 0;

        /// <summary>
        /// The answers box in screen pixels for a game whose client area is <paramref name="client"/>, and how much
        /// larger than its canvas the game draws there (for the ink's line height and the panel padding).
        /// </summary>
        public static bool TryPlace(GameAnswers answers, Rectangle client, out Rectangle box, out double scale)
        {
            box = Rectangle.Empty;
            scale = 0;
            if (Availability(answers, client.Size) != AnswerAvailability.Available) return false;
            if (!AssistGeometry.TryPlace(answers.Box, answers.Canvas, client, 0, out box)) return false;
            scale = AssistGeometry.Scale(answers.Canvas, client.Size);
            return true;
        }
    }
}
