using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Kaption's title bar (the brand mark, a title, a muted subtitle such as the version, caption buttons). Lives at
    /// the top of a window's content so ShellWindow.UseChrome can drop the native frame. Template: Themes/Shell.xaml.
    /// </summary>
    [TemplatePart(Name = "PART_Minimize", Type = typeof(ButtonBase))]
    [TemplatePart(Name = "PART_Maximize", Type = typeof(ButtonBase))]
    [TemplatePart(Name = "PART_Close", Type = typeof(ButtonBase))]
    public class TitleBar : Control
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("Kaption"));

        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
            nameof(Subtitle), typeof(string), typeof(TitleBar), new PropertyMetadata(null));

        public static readonly DependencyProperty CanMinimizeProperty = DependencyProperty.Register(
            nameof(CanMinimize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

        public static readonly DependencyProperty CanMaximizeProperty = DependencyProperty.Register(
            nameof(CanMaximize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

        public static readonly DependencyProperty CanCloseProperty = DependencyProperty.Register(
            nameof(CanClose), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

        public static readonly DependencyProperty IsMaximizedProperty = DependencyProperty.Register(
            nameof(IsMaximized), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

        /// <summary>Optional content between the title and the caption buttons (kept hit-testable).</summary>
        public static readonly DependencyProperty ExtraContentProperty = DependencyProperty.Register(
            nameof(ExtraContent), typeof(object), typeof(TitleBar), new PropertyMetadata(null));

        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
        public bool CanMinimize { get => (bool)GetValue(CanMinimizeProperty); set => SetValue(CanMinimizeProperty, value); }
        public bool CanMaximize { get => (bool)GetValue(CanMaximizeProperty); set => SetValue(CanMaximizeProperty, value); }
        public bool CanClose { get => (bool)GetValue(CanCloseProperty); set => SetValue(CanCloseProperty, value); }
        public bool IsMaximized { get => (bool)GetValue(IsMaximizedProperty); private set => SetValue(IsMaximizedProperty, value); }
        public object ExtraContent { get => GetValue(ExtraContentProperty); set => SetValue(ExtraContentProperty, value); }

        private Window _host;

        public TitleBar()
        {
            Loaded += (_, __) => Attach(Window.GetWindow(this));
            Unloaded += (_, __) => Attach(null);
        }

        private void Attach(Window host)
        {
            if (_host != null) _host.StateChanged -= OnHostStateChanged;
            _host = host;
            if (_host == null) return;
            _host.StateChanged += OnHostStateChanged;
            IsMaximized = _host.WindowState == WindowState.Maximized;
        }

        private void OnHostStateChanged(object sender, System.EventArgs e) =>
            IsMaximized = _host?.WindowState == WindowState.Maximized;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Hook("PART_Minimize", () => { if (_host != null) SystemCommands.MinimizeWindow(_host); });
            Hook("PART_Maximize", () =>
            {
                if (_host == null) return;
                if (_host.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_host);
                else SystemCommands.MaximizeWindow(_host);
            });
            Hook("PART_Close", () => _host?.Close());
        }

        private void Hook(string part, System.Action action)
        {
            if (GetTemplateChild(part) is ButtonBase b)
            {
                b.Click += (_, __) => action();
                if (string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.ToolTip is string tip)
                    AutomationProperties.SetName(b, tip);
            }
        }
    }
}
