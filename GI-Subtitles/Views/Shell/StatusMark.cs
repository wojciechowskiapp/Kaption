using System.Windows;
using System.Windows.Controls;

namespace GI_Subtitles.Views.Shell
{
    public enum MarkState { Pending, Working, Done, Warning }

    /// <summary>A 22 px status glyph for checklists: hollow ring, spinning arc, check, or warning. Template: Shell.xaml.</summary>
    public class StatusMark : Control
    {
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(MarkState), typeof(StatusMark), new PropertyMetadata(MarkState.Pending));

        public MarkState State { get => (MarkState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    }
}
