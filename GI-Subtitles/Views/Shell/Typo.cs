using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// WPF has no letter-spacing, so an eyebrow ("NA CO DZIEŃ") is set as upper case with hair spaces between the
    /// letters (about 0.08 em in Segoe UI) and an en space between words. Screen readers get the plain text through the
    /// automation name.
    /// </summary>
    public static class Typo
    {
        public static readonly DependencyProperty EyebrowProperty = DependencyProperty.RegisterAttached(
            "Eyebrow", typeof(string), typeof(Typo), new PropertyMetadata(null, OnEyebrowChanged));

        public static string GetEyebrow(DependencyObject d) => (string)d.GetValue(EyebrowProperty);
        public static void SetEyebrow(DependencyObject d, string value) => d.SetValue(EyebrowProperty, value);

        private static void OnEyebrowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb) return;
            string text = e.NewValue as string ?? string.Empty;
            tb.Text = Track(text, CultureInfo.CurrentUICulture);
            AutomationProperties.SetName(tb, text);
        }

        public static string Track(string text, CultureInfo culture)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            string upper = text.ToUpper(culture);
            var sb = new StringBuilder(upper.Length * 2);
            for (int i = 0; i < upper.Length; i++)
            {
                if (char.IsWhiteSpace(upper[i]))
                {
                    // A plain space is barely wider than the tracking and the words run together ("NACODZIEŃ").
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\u2002') sb.Append('\u2002');
                    continue;
                }
                sb.Append(upper[i]);
                if (i < upper.Length - 1 && !char.IsWhiteSpace(upper[i + 1]))
                    sb.Append('\u200a');
            }
            return sb.ToString().TrimEnd('\u2002');
        }
    }
}
