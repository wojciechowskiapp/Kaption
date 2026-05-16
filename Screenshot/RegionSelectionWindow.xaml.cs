using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Screenshot
{
    public partial class RegionSelectionWindow
    {
        private Point? _selectionStartPos;

        public RegionSelectionWindow()
        {
            InitializeComponent();

            Loaded += (s, e) => Activate();
        }

        public Rect? SelectedRegion { get; private set; }

        /// <summary>
        /// Fires on every mouse-move tick during an active drag, carrying the
        /// live selection rectangle in WINDOW-LOCAL WPF coordinates. The
        /// consumer is expected to convert to screen / physical pixels and
        /// decide whether the rectangle creates an overlap problem — then
        /// optionally call <see cref="SetBorderBrush"/> to swap the selection
        /// outline colour. Not raised after the mouse button goes up.
        /// </summary>
        public event Action<Rect> SelectionChanged;

        /// <summary>
        /// Paint the selection outline a different colour mid-drag. Used by
        /// the overlap-validation wiring so users see a RED rectangle the
        /// moment they start drawing a region that will trap the subtitle
        /// overlay inside it.
        /// </summary>
        public void SetBorderBrush(Brush brush)
        {
            if (brush == null) return;
            if (InnerBorder != null) InnerBorder.BorderBrush = brush;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            Close();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            _selectionStartPos = e.GetPosition(this);

            Mouse.Capture(this);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (!Equals(Mouse.Captured, this) || _selectionStartPos == null)
            {
                return;
            }

            SelectedRegion = new Rect(_selectionStartPos.Value, e.GetPosition(this));

            _selectionStartPos = null;

            Mouse.Capture(null);

            Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!Equals(Mouse.Captured, this) || _selectionStartPos == null)
            {
                return;
            }

            var position = e.GetPosition(this);

            var left = Math.Min(_selectionStartPos.Value.X, position.X);
            var top = Math.Min(_selectionStartPos.Value.Y, position.Y);

            Canvas.SetLeft(SelectionImage, -left);
            Canvas.SetTop(SelectionImage, -top);

            Canvas.SetLeft(SelectionBorder, left);
            Canvas.SetTop(SelectionBorder, top);
            SelectionBorder.Width = Math.Abs(position.X - _selectionStartPos.Value.X);
            SelectionBorder.Height = Math.Abs(position.Y - _selectionStartPos.Value.Y);

            // Emit the live rect so overlap-validation consumers can swap
            // the border brush on the fly. Local Window coords — caller
            // handles any conversion to screen / physical pixels. Wrapped in
            // a try so a misbehaving subscriber cannot break region drag.
            try
            {
                SelectionChanged?.Invoke(new Rect(left, top, SelectionBorder.Width, SelectionBorder.Height));
            }
            catch { }
        }
    }
}