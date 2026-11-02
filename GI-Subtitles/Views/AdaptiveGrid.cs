using System;
using System.Windows;
using System.Windows.Controls;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Equal columns that fit the width, like CSS grid auto-fill: <see cref="MinColumnWidth"/> decides how many,
    /// <see cref="MaxColumns"/> caps it. Items in a row share the row's height, so cards line up.
    /// </summary>
    public sealed class AdaptiveGrid : Panel
    {
        public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
            nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
            nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
            nameof(ColumnGap), typeof(double), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap), typeof(double), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
        public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
        public double ColumnGap { get => (double)GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
        public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

        private int Columns(double width)
        {
            if (double.IsInfinity(width) || double.IsNaN(width)) return Math.Max(1, MaxColumns);
            int fit = (int)Math.Floor((width + ColumnGap) / (Math.Max(1, MinColumnWidth) + ColumnGap));
            return Math.Max(1, Math.Min(Math.Max(1, MaxColumns), Math.Min(fit, Math.Max(1, VisibleCount()))));
        }

        private int VisibleCount()
        {
            int n = 0;
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed) n++;
            return n;
        }

        private double ColumnWidth(double width, int columns) =>
            double.IsInfinity(width) ? MinColumnWidth : Math.Max(0, (width - ColumnGap * (columns - 1)) / columns);

        protected override Size MeasureOverride(Size available)
        {
            int columns = Columns(available.Width);
            double colWidth = ColumnWidth(available.Width, columns);
            double height = 0, rowHeight = 0;
            int index = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(colWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (++index % columns == 0)
                {
                    height += rowHeight + RowGap;
                    rowHeight = 0;
                }
            }
            if (index % columns != 0) height += rowHeight;
            else if (index > 0) height -= RowGap;
            double width = double.IsInfinity(available.Width) ? columns * colWidth + (columns - 1) * ColumnGap : available.Width;
            return new Size(index == 0 ? 0 : width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            int columns = Columns(final.Width);
            double colWidth = ColumnWidth(final.Width, columns);
            var row = new System.Collections.Generic.List<UIElement>(columns);
            double y = 0;

            void Flush()
            {
                if (row.Count == 0) return;
                double h = 0;
                foreach (var c in row) h = Math.Max(h, c.DesiredSize.Height);
                for (int i = 0; i < row.Count; i++)
                    row[i].Arrange(new Rect(i * (colWidth + ColumnGap), y, colWidth, h));
                y += h + RowGap;
                row.Clear();
            }

            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                row.Add(child);
                if (row.Count == columns) Flush();
            }
            Flush();
            return final;
        }
    }
}
