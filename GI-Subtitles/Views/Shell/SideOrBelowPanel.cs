using System;
using System.Windows;
using System.Windows.Controls;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Two children: the first keeps its own width, the second sits beside it when at least <see cref="SideMinWidth"/>
    /// fits, otherwise under it at full width (Wygląd napisów: the preview with the looks beside it on a wide window,
    /// the looks in a row under it on a narrow one).
    /// </summary>
    public sealed class SideOrBelowPanel : Panel
    {
        public static readonly DependencyProperty SideMinWidthProperty = DependencyProperty.Register(nameof(SideMinWidth), typeof(double),
            typeof(SideOrBelowPanel), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
            typeof(SideOrBelowPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double SideMinWidth { get => (double)GetValue(SideMinWidthProperty); set => SetValue(SideMinWidthProperty, value); }
        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

        /// <summary>True when the second child sits beside the first.</summary>
        public bool IsSideBySide { get; private set; }

        private UIElement Main => InternalChildren.Count > 0 ? InternalChildren[0] : null;
        private UIElement Side => InternalChildren.Count > 1 ? InternalChildren[1] : null;

        protected override Size MeasureOverride(Size available)
        {
            if (Main == null) return new Size();
            Main.Measure(new Size(available.Width, double.PositiveInfinity));
            var main = Main.DesiredSize;
            if (Side == null) return main;
            double room = available.Width - main.Width - Gap;
            IsSideBySide = double.IsInfinity(available.Width) || room >= SideMinWidth;
            if (IsSideBySide)
            {
                double w = double.IsInfinity(available.Width) ? SideMinWidth : room;
                Side.Measure(new Size(w, double.PositiveInfinity));
                return new Size(main.Width + Gap + Math.Max(w, Side.DesiredSize.Width), Math.Max(main.Height, Side.DesiredSize.Height));
            }
            Side.Measure(new Size(available.Width, double.PositiveInfinity));
            return new Size(Math.Max(main.Width, Side.DesiredSize.Width), main.Height + Gap + Side.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (Main == null) return final;
            var main = Main.DesiredSize;
            Main.Arrange(new Rect(0, 0, main.Width, main.Height));
            if (Side == null) return final;
            if (IsSideBySide)
                Side.Arrange(new Rect(main.Width + Gap, 0, Math.Max(0, final.Width - main.Width - Gap), Math.Max(main.Height, Side.DesiredSize.Height)));
            else
                Side.Arrange(new Rect(0, main.Height + Gap, final.Width, Side.DesiredSize.Height));
            return final;
        }
    }
}
