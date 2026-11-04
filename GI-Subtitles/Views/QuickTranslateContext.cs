using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.QuickTranslate;

namespace GI_Subtitles.Views
{
    /// <summary>What Esc does in the Ctrl+Q popup.</summary>
    public enum PopupEscape
    {
        Collapse,
        Close,
    }

    /// <summary>How one translated line relates to the recognised character.</summary>
    public readonly struct LineMark
    {
        public LineMark(bool fromCard, string badge)
        {
            FromCard = fromCard;
            Badge = badge ?? "";
        }

        /// <summary>The line is one of the character's skill titles.</summary>
        public bool FromCard { get; }
        /// <summary>The skill's type, as the badge shows it ("E", "Q", "Atak", "C3"); "" for none.</summary>
        public string Badge { get; }
    }

    /// <summary>
    /// The character Kaption recognised under a Ctrl+Q selection: the strip under the translation, and whether the
    /// character's page is open inline. Esc closes an open page first, then the popup.
    /// </summary>
    public sealed class QuickTranslateContext : INotifyPropertyChanged
    {
        private readonly CharacterRecord _record;
        private bool _isExpanded;

        public QuickTranslateContext(CharacterOverviewModel model, CharacterRecord record, IReadOnlyList<string> lines)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _record = record;
            Skill = (lines ?? Array.Empty<string>())
                .Select(line => SkillNamed(line))
                .FirstOrDefault(s => s != null);
        }

        public CharacterOverviewModel Model { get; }
        /// <summary>The first of the character's skills the selection shows, or null.</summary>
        public OverviewSkill Skill { get; }

        public string Name => Model.Name;
        /// <summary>"5★ Geo", "S Ice": rarity and element, short.</summary>
        public string Facts => string.Join(" ", new[] { RarityShort(Model), Model.Element }.Where(s => !string.IsNullOrEmpty(s)));
        public string PortraitPath => Model.IconPath;
        public string Initial => CharacterOverviewViewModel.InitialOf(Model.Name);
        public bool HasSkill => Skill != null;
        /// <summary>"Fluttering Hasode (E)": the name the game shows, with its type.</summary>
        public string SkillTitle => Skill == null ? "" : SkillMarkers.WithMarker(EnglishOf(Skill), Skill.Marker);
        public string SkillIconPath => Skill?.IconPath;
        public Geometry SkillGlyph => Skill == null ? null : SkillGlyphs.For(Skill.Kind);

        /// <summary>The section to open the page at: where the recognised skill sits.</summary>
        public string SectionToOpen =>
            Skill == null ? null
            : SkillMarkers.IsRankKind(Skill.Kind) ? "Ranks"
            : "Skills";

        public bool IsExpanded
        {
            get => _isExpanded;
            private set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
            }
        }

        public bool IsCollapsed => !_isExpanded;

        public event PropertyChangedEventHandler PropertyChanged;

        public void Expand() => IsExpanded = true;

        public void Collapse() => IsExpanded = false;

        /// <summary>Esc: an open page closes first; with the translation showing, the popup closes.</summary>
        public PopupEscape Escape()
        {
            if (!IsExpanded) return PopupEscape.Close;
            Collapse();
            return PopupEscape.Collapse;
        }

        /// <summary>For each translated line: whether it is one of the character's skill titles, and its type.</summary>
        public IReadOnlyList<LineMark> MarksFor(IReadOnlyList<QuickTranslateSegment> segments)
        {
            var marks = new LineMark[segments?.Count ?? 0];
            for (int i = 0; i < marks.Length; i++)
            {
                var skill = SkillNamed(segments[i].Source);
                marks[i] = skill == null ? default : new LineMark(true, (skill.Marker ?? "").Trim('(', ')'));
            }
            return marks;
        }

        private OverviewSkill SkillNamed(string line)
        {
            int index = CharacterDetector.SkillIndexFor(_record, line);
            if (index < 0) return null;
            string english = _record.Skills[index].Name;
            return Model.Skills.FirstOrDefault(s => EnglishOf(s) == english);
        }

        private static string EnglishOf(OverviewSkill s) => string.IsNullOrEmpty(s.EnglishName) ? s.Name : s.EnglishName;

        internal static string RarityShort(CharacterOverviewModel m) =>
            m.Rarity <= 0 ? ""
            : string.Equals(m.Game, "zzz", StringComparison.OrdinalIgnoreCase) ? m.RarityText
            : m.Rarity + "★";
    }
}
