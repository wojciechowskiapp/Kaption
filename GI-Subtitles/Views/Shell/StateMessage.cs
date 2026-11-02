using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GI_Subtitles.Views.Shell
{
    public enum StateTone { Neutral, Problem }

    /// <summary>
    /// The one empty or error state of the app: an icon on a soft disc, one sentence, one action. Neutral states (nothing
    /// here yet, nothing matches) use the accent; problems use the error colour. The same component on every page, so a
    /// state reads the same wherever it shows (Characters, packs, the log).
    /// </summary>
    public sealed class StateMessage : StackPanel
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon), typeof(Geometry), typeof(StateMessage), new PropertyMetadata(null, (d, e) => ((StateMessage)d)._glyph.Data = (Geometry)e.NewValue));
        public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
            nameof(Tone), typeof(StateTone), typeof(StateMessage), new PropertyMetadata(StateTone.Neutral, (d, e) => ((StateMessage)d).ApplyTone()));
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(StateMessage), new PropertyMetadata("", (d, e) => ((StateMessage)d).Sentence.Text = (string)e.NewValue ?? ""));
        public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
            nameof(ActionText), typeof(string), typeof(StateMessage), new PropertyMetadata(null, (d, e) => ((StateMessage)d).ApplyAction()));

        public Geometry Icon { get => (Geometry)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public StateTone Tone { get => (StateTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        /// <summary>The one sentence.</summary>
        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        /// <summary>The one action's label; the button hides without it.</summary>
        public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

        /// <summary>The action was taken (the button's Click).</summary>
        public event RoutedEventHandler ActionClick;

        private readonly Ellipse _disc = new() { Width = 48, Height = 48 };
        private readonly Path _glyph = new()
        {
            Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.5, 1.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        internal TextBlock Sentence { get; } = new() { Margin = new Thickness(0, 14, 0, 0) };
        internal Button Action { get; } = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };

        public StateMessage()
        {
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            MaxWidth = 440;
            var mark = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
            mark.Children.Add(_disc);
            mark.Children.Add(_glyph);
            Children.Add(mark);
            Sentence.SetResourceReference(StyleProperty, "EmptyStateText");
            Children.Add(Sentence);
            Action.SetResourceReference(StyleProperty, "SecondaryButton");
            Action.Click += (s, e) => ActionClick?.Invoke(this, e);
            Children.Add(Action);
            AutomationProperties.SetLiveSetting(Sentence, AutomationLiveSetting.Polite);
            ApplyTone();
        }

        private void ApplyTone()
        {
            bool problem = Tone == StateTone.Problem;
            _disc.SetResourceReference(Shape.FillProperty, problem ? "ErrorLightBrush" : "AccentLightBrush");
            _glyph.SetResourceReference(Shape.StrokeProperty, problem ? "ErrorBrush" : "AccentBrush");
        }

        private void ApplyAction()
        {
            Action.Content = ActionText;
            Action.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
