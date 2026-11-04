using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using GI_Subtitles.Services.Characters;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// A goal value ("2 500–3 000+") whose thousands gaps stay visible: the display font draws the narrow no-break
    /// space about 2 px wide, so a small spacer goes before each one, as the site does (0.08em). The runs keep the
    /// text exactly as given, and screen readers get it whole.
    /// Usage: <c>&lt;TextBlock local:GoalValueText.Text="{Binding Value}"/&gt;</c> instead of <c>Text=</c>.
    /// </summary>
    public static class GoalValueText
    {
        /// <summary>The spacer's width in em, the site's margin before each gap.</summary>
        public const double GapEm = 0.08;

        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(GoalValueText), new PropertyMetadata(null, OnTextChanged));

        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock block) return;
            string text = e.NewValue as string ?? string.Empty;
            block.Inlines.Clear();
            string[] parts = text.Split(StatGoalFormat.NarrowNoBreakSpace);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    var spacer = new FrameworkElement { Width = block.FontSize * GapEm, Focusable = false };
                    block.Inlines.Add(new InlineUIContainer(spacer) { BaselineAlignment = BaselineAlignment.Baseline });
                    block.Inlines.Add(new Run(StatGoalFormat.NarrowNoBreakSpace.ToString()));
                }
                if (parts[i].Length > 0) block.Inlines.Add(new Run(parts[i]));
            }
            AutomationProperties.SetName(block, text);
        }
    }
}
