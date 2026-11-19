using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>
    /// What the hint card did in one run of translation, for one INFO line when the run stops
    /// (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.9). Counts only: no text the player read, no
    /// coordinates, and nothing leaves this PC.
    /// </summary>
    public sealed class HintTally
    {
        /// <summary>A second press of the hide shortcut this soon after the echo undoes an accident.</summary>
        public const int UndoneWithinMs = 10_000;
        /// <summary>A tip did its job when its shortcut is pressed this soon after it.</summary>
        public const int TipKeyWithinMs = 60_000;

        private int _welcome, _asked, _pinned, _echoHidden, _echoBack, _echoStopped, _undone, _tipKeyUsed;
        private int _left, _top, _none, _stripOpened, _hidden, _never;
        private readonly SortedDictionary<string, int> _tips = new(System.StringComparer.Ordinal);

        public bool IsEmpty =>
            _welcome + _asked + _pinned + _echoHidden + _echoBack + _echoStopped + _left + _top + _none + _stripOpened + _hidden + _never + _tips.Count == 0;

        public void Shown(HintShowing what, HintEntry entry, HintPlace place)
        {
            if (place == HintPlace.Top) _top++; else _left++;
            switch (what)
            {
                case HintShowing.WelcomeSheet: _welcome++; break;
                case HintShowing.AskedSheet: _asked++; break;
                case HintShowing.PinnedSheet: _pinned++; break;
                case HintShowing.Echo when ReferenceEquals(entry, HintCatalog.EchoSubtitlesHidden): _echoHidden++; break;
                case HintShowing.Echo when ReferenceEquals(entry, HintCatalog.EchoSubtitlesBack): _echoBack++; break;
                case HintShowing.Echo when ReferenceEquals(entry, HintCatalog.EchoStopped): _echoStopped++; break;
                case HintShowing.Tip when entry != null: _tips[entry.Id] = _tips.GetValueOrDefault(entry.Id) + 1; break;
            }
        }

        public void NoPlace() => _none++;
        public void Undone() => _undone++;
        public void TipKeyUsed() => _tipKeyUsed++;
        public void StripOpened() => _stripOpened++;
        public void Hidden() => _hidden++;
        public void NeverAgain() => _never++;

        public string Describe(bool pinned)
        {
            string tips = _tips.Count == 0
                ? ""
                : " (" + string.Join(", ", _tips.Select(t => t.Key + " " + t.Value.ToString(CultureInfo.InvariantCulture)))
                  + string.Create(CultureInfo.InvariantCulture, $", key used within {TipKeyWithinMs / 1000} s: {_tipKeyUsed})");
            return string.Create(CultureInfo.InvariantCulture,
                $"Hints: sheet {_welcome + _asked + _pinned} (welcome {_welcome}, asked {_asked}, pinned {_pinned}), pinned {(pinned ? "yes" : "no")}; "
                + $"echoes {_echoHidden + _echoBack + _echoStopped} (hidden {_echoHidden}, back {_echoBack}, stopped {_echoStopped}, undone {_undone}); "
                + $"tips {_tips.Values.Sum()}{tips}; place left {_left}, top {_top}, none {_none}; "
                + $"strip opened {_stripOpened}, hidden {_hidden}, never-again {_never}");
        }
    }
}
