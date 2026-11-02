using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The text on a ValueSlider's thumb: the value in <see cref="FormatProperty"/>, or <see cref="AutoTextProperty"/>
    /// ("Auto") while the slider sits at its minimum, for settings where the minimum means "let Kaption decide".
    /// </summary>
    public static class SliderValue
    {
        public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
            "Format", typeof(string), typeof(SliderValue), new PropertyMetadata(null, OnFormatChanged));

        public static readonly DependencyProperty AutoTextProperty = DependencyProperty.RegisterAttached(
            "AutoText", typeof(string), typeof(SliderValue), new PropertyMetadata(null, (d, _) => Update(d as RangeBase)));

        private static readonly DependencyPropertyKey TextKey = DependencyProperty.RegisterAttachedReadOnly(
            "Text", typeof(string), typeof(SliderValue), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty TextProperty = TextKey.DependencyProperty;

        public static string GetFormat(DependencyObject d) => (string)d.GetValue(FormatProperty);
        public static void SetFormat(DependencyObject d, string value) => d.SetValue(FormatProperty, value);
        public static string GetAutoText(DependencyObject d) => (string)d.GetValue(AutoTextProperty);
        public static void SetAutoText(DependencyObject d, string value) => d.SetValue(AutoTextProperty, value);
        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

        private static void OnFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not RangeBase range) return;
            range.ValueChanged -= OnValueChanged;
            range.ValueChanged += OnValueChanged;
            Update(range);
        }

        private static void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Update(sender as RangeBase);

        /// <summary>The thumb text for a value (also the slider's automation value).</summary>
        public static string Describe(double value, double minimum, string format, string autoText) =>
            !string.IsNullOrEmpty(autoText) && value <= minimum
                ? autoText
                : string.Format(CultureInfo.CurrentCulture, string.IsNullOrEmpty(format) ? "{0:0}" : format, value).Replace('-', '−');

        private static void Update(RangeBase range)
        {
            if (range == null) return;
            range.SetValue(TextKey, Describe(range.Value, range.Minimum, GetFormat(range), GetAutoText(range)));
        }
    }
}
