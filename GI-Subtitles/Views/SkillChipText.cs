using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace GI_Subtitles.Views
{
    /// <summary>The three looks a skill-type marker can have, the same for every game.</summary>
    public enum SkillChipKind { Burst, Skill, Normal, Other }

    /// <summary>
    /// Text with skill-type markers ("Kamisato Art: Hyouka (E)") drawn as small chips after the name, the same way in
    /// every list on the character page. Burst/Ultimate/Chain is filled in the element colour, Skill/Special outlined
    /// in it, Normal/Basic attack outlined in the secondary ink, any other type outlined quietly. Screen readers keep
    /// the plain text ("… (E)") as the automation name.
    /// Usage: <c>&lt;TextBlock local:SkillChipText.Text="{Binding Title}"/&gt;</c> instead of <c>Text=</c>.
    /// </summary>
    public static class SkillChipText
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(SkillChipText), new PropertyMetadata(null, OnTextChanged));

        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

        // The markers SkillMarkers writes: game shorthands and the rank markers of constellations, eidolons, mindscapes.
        private static readonly Regex Marker = new(
            @" \((Q|E|Atak|Basic|Skill|Ult|Special|Chain|Dodge|Assist|Talent|Technique|[CEM]\d)\)", RegexOptions.CultureInvariant);

        public static SkillChipKind KindOf(string marker) => marker switch
        {
            "Q" or "Ult" or "Chain" => SkillChipKind.Burst,
            "E" or "Skill" or "Special" => SkillChipKind.Skill,
            "Atak" or "Basic" => SkillChipKind.Normal,
            _ => SkillChipKind.Other,
        };

        /// <summary>The text cut into plain pieces and markers, in order (a marker piece has Text null).</summary>
        public static IReadOnlyList<(string Text, string Marker)> Split(string text)
        {
            var pieces = new List<(string, string)>();
            if (string.IsNullOrEmpty(text)) return pieces;
            int at = 0;
            foreach (Match m in Marker.Matches(text))
            {
                if (m.Index > at) pieces.Add((text.Substring(at, m.Index - at), null));
                pieces.Add((null, m.Groups[1].Value));
                at = m.Index + m.Length;
            }
            if (at < text.Length) pieces.Add((text.Substring(at), null));
            return pieces;
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock block) return;
            string text = e.NewValue as string ?? string.Empty;
            block.Inlines.Clear();
            foreach (var (piece, marker) in Split(text))
            {
                if (marker == null)
                {
                    block.Inlines.Add(new Run(piece));
                    continue;
                }
                block.Inlines.Add(new Run(" "));
                block.Inlines.Add(new InlineUIContainer(Chip(marker)) { BaselineAlignment = BaselineAlignment.Center });
            }
            // Screen readers get what the text said before the chips: "Kamisato Art: Hyouka (E)".
            AutomationProperties.SetName(block, text);
        }

        private static Border Chip(string marker)
        {
            var label = new TextBlock
            {
                Text = Shell.Typo.Track(marker, CultureInfo.InvariantCulture),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var chip = new Border
            {
                Child = label,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 0, 5, 0),
                MinHeight = 16,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Focusable = false,
            };
            switch (KindOf(marker))
            {
                case SkillChipKind.Burst:
                    chip.SetBinding(Border.BackgroundProperty, Page("ElementFill"));
                    chip.SetBinding(Border.BorderBrushProperty, Page("ElementFill"));
                    label.SetBinding(TextBlock.ForegroundProperty, Page("ElementInk"));
                    break;
                case SkillChipKind.Skill:
                    chip.Background = Brushes.Transparent;
                    chip.SetBinding(Border.BorderBrushProperty, Page("ElementText"));
                    label.SetBinding(TextBlock.ForegroundProperty, Page("ElementText"));
                    break;
                case SkillChipKind.Normal:
                    chip.Background = Brushes.Transparent;
                    chip.SetResourceReference(Border.BorderBrushProperty, "Char_FgMuted");
                    label.SetResourceReference(TextBlock.ForegroundProperty, "Char_FgMuted");
                    break;
                default:
                    chip.Background = Brushes.Transparent;
                    chip.SetResourceReference(Border.BorderBrushProperty, "Char_Ring");
                    label.SetResourceReference(TextBlock.ForegroundProperty, "Char_FgMuted");
                    break;
            }
            return chip;
        }

        // The page's view model (element colours) from inside any item template.
        private static Binding Page(string property) => new Binding("DataContext." + property)
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(UserControl), 1),
        };
    }
}
