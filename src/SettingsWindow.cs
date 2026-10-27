using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace CodexQuotaTray
{
    /// <summary>Live, independently scoped preferences for the native tray icon and floating ring.</summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private FloatingRingVisual _preview;
        private Image _trayPreview;
        private TextBlock _diameterCaption;
        private bool _allowClose;
        private bool _refreshingControls;
        private CheckBox _floatingVisibilityToggle;
        private Grid _header;
        private Grid _body;
        private Border _footer;
        private TextBlock _headerKicker;
        private Border _liveTag;
        private ScrollViewer _previewPane;
        private ColumnDefinition _previewGap;
        private ColumnDefinition _previewColumn;
        private readonly List<Grid> _labeledRows = new List<Grid>();
        private readonly Brush _ink = ColorBrush("#14233B");
        private readonly Brush _muted = ColorBrush("#64748B");
        private readonly Brush _line = ColorBrush("#E1E8F0");

        public event Action SettingsChanged;
        public event Action ExitRequested;

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Title = T("Codex 额度 · 显示设置", "Codex Quota · Display settings");
            Width = 1000;
            Height = 830;
            // Effective minima are reduced to fit the active monitor before the window is shown.
            MinWidth = 640;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Background = ColorBrush("#F3F6FA");
            Foreground = _ink;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 13;
            UseLayoutRounding = true;
            Resources = (ResourceDictionary)XamlReader.Parse(ControlStyles);
            BuildContent();
            Closing += OnClosing;
            Activated += delegate { RefreshFromSettings(); FitToCurrentWorkArea(); };
            SizeChanged += delegate { UpdateResponsiveLayout(); };
            Loaded += delegate { FitToCurrentWorkArea(); };
            DpiChanged += delegate
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitToCurrentWorkArea));
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key != Key.Escape) return;
                Hide();
                args.Handled = true;
            };
            SourceInitialized += delegate { FitToCurrentWorkArea(); };
            RefreshPreviews();
        }

        private void FitToCurrentWorkArea()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var work = Forms.Screen.FromHandle(hwnd).WorkingArea;
            var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
            const double margin = 16;
            var availableWidth = Math.Max(1, work.Width / scale - margin * 2);
            var availableHeight = Math.Max(1, work.Height / scale - margin * 2);
            MinWidth = Math.Min(640, availableWidth);
            MinHeight = Math.Min(360, availableHeight);
            if (WindowState == WindowState.Normal)
            {
                Width = Math.Clamp(Width, MinWidth, availableWidth);
                Height = Math.Clamp(Height, MinHeight, availableHeight);
                // Clamp in physical coordinates after WPF has applied its new logical size.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!IsVisible || WindowState != WindowState.Normal || !GetWindowRect(hwnd, out var bounds)) return;
                    var currentWork = Forms.Screen.FromHandle(hwnd).WorkingArea;
                    var inset = (int)Math.Ceiling(margin * Math.Max(96u, GetDpiForWindow(hwnd)) / 96d);
                    var x = Math.Clamp(bounds.Left, currentWork.Left + inset,
                        Math.Max(currentWork.Left + inset, currentWork.Right - inset - (bounds.Right - bounds.Left)));
                    var y = Math.Clamp(bounds.Top, currentWork.Top + inset,
                        Math.Max(currentWork.Top + inset, currentWork.Bottom - inset - (bounds.Bottom - bounds.Top)));
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
                }));
            }
            UpdateResponsiveLayout();
        }

        private void UpdateResponsiveLayout()
        {
            if (_body == null || _previewPane == null) return;
            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;
            var narrow = width < 820;
            var shortWindow = height < 620;
            var compact = narrow || shortWindow;
            _previewPane.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            _previewGap.Width = new GridLength(narrow ? 0 : 20);
            _previewColumn.Width = new GridLength(narrow ? 0 : 250);
            _liveTag.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            _headerKicker.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
            _header.Margin = compact ? new Thickness(16, 12, 16, 12) : new Thickness(30, 25, 30, 20);
            _body.Margin = compact ? new Thickness(16, 0, 16, 12) : new Thickness(30, 0, 30, 20);
            _footer.Padding = compact ? new Thickness(16, 9, 16, 9) : new Thickness(30, 14, 30, 14);
            foreach (var row in _labeledRows)
            {
                var stacked = width < 560;
                var label = (TextBlock)row.Children[0];
                var control = row.Children[1];
                Grid.SetColumnSpan(label, stacked ? 2 : 1);
                Grid.SetColumn(control, stacked ? 0 : 1);
                Grid.SetColumnSpan(control, stacked ? 2 : 1);
                Grid.SetRow(control, stacked ? 1 : 0);
                label.Margin = stacked ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 8, 0);
            }
        }

        public void RefreshFromSettings()
        {
            _refreshingControls = true;
            try
            {
                if (_floatingVisibilityToggle != null)
                    _floatingVisibilityToggle.IsChecked = _settings.FloatingVisible;
                RefreshPreviews();
            }
            finally { _refreshingControls = false; }
        }

        public void ClosePermanently()
        {
            _allowClose = true;
            Close();
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (_allowClose) return;
            args.Cancel = true;
            Hide();
        }

        private void BuildContent()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var header = _header = new Grid { Margin = new Thickness(30, 25, 30, 20) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headings = new StackPanel();
            _headerKicker = Text(T("按你的习惯，调整每一处", "MAKE IT YOURS"), 11, ColorBrush("#0284C7"), FontWeights.SemiBold);
            headings.Children.Add(_headerKicker);
            headings.Children.Add(Text(T("显示设置", "Display settings"), 28, _ink, FontWeights.SemiBold, new Thickness(0, 5, 0, 5)));
            headings.Children.Add(Text(T("更改即时生效，并自动保存。", "Changes apply instantly and save automatically."), 13, _muted));
            header.Children.Add(headings);
            var liveTag = _liveTag = new Border
            {
                Background = ColorBrush("#E2F6EC"), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center,
                Child = Text(T("●  实时预览", "●  Live preview"), 12, ColorBrush("#21754D"), FontWeights.Medium)
            };
            Grid.SetColumn(liveTag, 1);
            header.Children.Add(liveTag);
            root.Children.Add(header);

            var body = _body = new Grid { Margin = new Thickness(30, 0, 30, 20) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _previewGap = new ColumnDefinition { Width = new GridLength(20) };
            _previewColumn = new ColumnDefinition { Width = new GridLength(250) };
            body.ColumnDefinitions.Add(_previewGap);
            body.ColumnDefinitions.Add(_previewColumn);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var settings = new StackPanel();
            settings.Children.Add(BuildFloatingCard());
            settings.Children.Add(BuildTrayCard());
            var scroll = new ScrollViewer
            {
                Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0),
                CanContentScroll = false
            };
            body.Children.Add(scroll);
            var previewScroll = _previewPane = new ScrollViewer
            {
                Content = BuildPreview(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetColumn(previewScroll, 2);
            body.Children.Add(previewScroll);

            var footer = _footer = new Border
            {
                BorderBrush = _line, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.White,
                Padding = new Thickness(30, 14, 30, 14)
            };
            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition());
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Child = footerGrid;
            var quit = new Button
            {
                Content = T("退出程序", "Quit app"), HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent, Foreground = _muted, BorderBrush = Brushes.Transparent,
                Padding = new Thickness(0, 8, 8, 8)
            };
            quit.Click += delegate { ExitRequested?.Invoke(); };
            footerGrid.Children.Add(quit);
            var close = new Button
            {
                Content = T("完成", "Done"), MinWidth = 100, IsDefault = true,
                Background = ColorBrush("#0284C7"), BorderBrush = ColorBrush("#0284C7"), Foreground = Brushes.White
            };
            close.Click += delegate { Hide(); };
            Grid.SetColumn(close, 1);
            footerGrid.Children.Add(close);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            UpdateResponsiveLayout();
        }

        private Border BuildFloatingCard()
        {
            var stack = new StackPanel();
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel();
            titles.Children.Add(Text(T("悬浮圆环", "Floating ring"), 17, _ink, FontWeights.SemiBold));
            titles.Children.Add(Text(T("置顶显示，可拖到桌面任意位置", "Always on top. Drag it anywhere on your desktop."), 12, _muted,
                margin: new Thickness(0, 4, 8, 0)));
            heading.Children.Add(titles);
            var enabled = new CheckBox
            {
                Content = T("显示", "Show"), IsChecked = _settings.FloatingVisible,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0), Cursor = Cursors.Hand
            };
            _floatingVisibilityToggle = enabled;
            AutomationProperties.SetName(enabled, T("显示悬浮圆环", "Show floating ring"));
            enabled.Checked += delegate { if (_refreshingControls) return; _settings.FloatingVisible = true; Commit(); };
            enabled.Unchecked += delegate { if (_refreshingControls) return; _settings.FloatingVisible = false; Commit(); };
            Grid.SetColumn(enabled, 1);
            heading.Children.Add(enabled);
            stack.Children.Add(heading);
            stack.Children.Add(Divider());

            var sizes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 5) };
            foreach (var diameter in new[] { 128, 256, 512 })
            {
                var radio = new RadioButton
                {
                    GroupName = "FloatingDiameter", Content = diameter + " px", IsChecked = _settings.FloatingDiameter == diameter,
                    Style = (Style)Resources["SizeChoice"], Margin = new Thickness(0, 0, diameter == 512 ? 0 : 8, 0), Tag = diameter
                };
                AutomationProperties.SetName(radio, T("悬浮圆环直径 ", "Floating diameter ") + diameter + T(" 像素", " pixels"));
                radio.Checked += delegate(object sender, RoutedEventArgs args)
                {
                    _settings.FloatingDiameter = (int)((RadioButton)sender).Tag;
                    Commit();
                };
                sizes.Children.Add(radio);
            }
            stack.Children.Add(Text(T("实际直径", "Actual diameter"), 12, _muted, FontWeights.Medium, new Thickness(0, 0, 0, 9)));
            stack.Children.Add(sizes);
            stack.Children.Add(Text(T("始终使用实际像素大小，不随屏幕缩放改变", "Physical pixels stay exact at every display scale."), 11, _muted,
                margin: new Thickness(0, 3, 0, 14)));
            stack.Children.Add(ColorRow(T("圆环颜色", "Ring color"), () => _settings.FloatingRingColor, value => _settings.FloatingRingColor = value));
            stack.Children.Add(ColorRow(T("数字颜色", "Number color"), () => _settings.FloatingFontColor, value => _settings.FloatingFontColor = value));
            stack.Children.Add(SliderRow(T("圆环线宽", "Ring thickness"), 2, 32, _settings.FloatingRingWidth,
                value => _settings.FloatingRingWidth = value, value => value.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(SliderRow(T("圆环比例", "Ring scale"), 50, 100, _settings.FloatingRingScalePercent,
                value => _settings.FloatingRingScalePercent = value, value => value + "%"));
            stack.Children.Add(SliderRow(T("字号", "Number size"), 32, 160, _settings.FloatingFontSize,
                value => _settings.FloatingFontSize = value, value => value.ToString(CultureInfo.InvariantCulture)));
            var weights = new ComboBox
            {
                MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6), Foreground = _ink, Background = Brushes.White
            };
            var choices = new[]
            {
                (400, T("常规  ·  400", "Regular  ·  400")), (500, T("适中  ·  500", "Medium  ·  500")),
                (600, T("半粗  ·  600", "Semibold  ·  600")), (700, T("粗体  ·  700", "Bold  ·  700")),
                (800, T("特粗  ·  800", "Extra bold  ·  800"))
            };
            foreach (var choice in choices)
            {
                var item = new ComboBoxItem { Content = choice.Item2, Tag = choice.Item1 };
                weights.Items.Add(item);
                if (FloatingRingVisual.ClampWeight(_settings.FloatingFontWeight) == choice.Item1) weights.SelectedItem = item;
            }
            AutomationProperties.SetName(weights, T("数字字重", "Number weight"));
            weights.SelectionChanged += delegate
            {
                if (weights.SelectedItem is not ComboBoxItem item) return;
                _settings.FloatingFontWeight = (int)item.Tag;
                Commit();
            };
            stack.Children.Add(LabeledRow(T("字重", "Number weight"), weights));
            stack.Children.Add(Text(T("线宽与字号以 256 px 为基准，随直径等比缩放。", "Thickness and type scale proportionally from a 256 px baseline."), 11, _muted,
                margin: new Thickness(0, 8, 0, 0)));
            return Card(stack);
        }

        private Border BuildTrayCard()
        {
            var stack = new StackPanel();
            stack.Children.Add(Text(T("托盘图标", "Tray icon"), 17, _ink, FontWeights.SemiBold));
            stack.Children.Add(Text(T("单独调整，不影响悬浮圆环", "Independent from your floating ring."), 12, _muted,
                margin: new Thickness(0, 4, 0, 0)));
            stack.Children.Add(Divider());
            stack.Children.Add(ColorRow(T("圆环颜色", "Ring color"), () => _settings.TrayRingColor, value => _settings.TrayRingColor = value));
            stack.Children.Add(ColorRow(T("数字颜色", "Number color"), () => _settings.TrayFontColor, value => _settings.TrayFontColor = value));
            stack.Children.Add(SliderRow(T("圆环线宽", "Ring thickness"), 1, 6, _settings.TrayRingWidth,
                value => _settings.TrayRingWidth = value, value => value.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(Text(T("保留原有数字字体与字号，保证托盘可读性。", "The original tray font and size are preserved."), 11, _muted,
                margin: new Thickness(0, 8, 0, 0)));
            return Card(stack);
        }

        private StackPanel BuildPreview()
        {
            var stack = new StackPanel();
            var preview = new StackPanel();
            preview.Children.Add(Text(T("悬浮效果", "FLOATING PREVIEW"), 11, _muted, FontWeights.SemiBold));
            _preview = new FloatingRingVisual(_settings) { Width = 192, Height = 192, Margin = new Thickness(0, 22, 0, 15) };
            _preview.SetVisual(73, true, false);
            // Neutral desktop surface keeps both bright and dark custom colors easy to inspect.
            var stage = new Border
            {
                Background = ColorBrush("#1B2638"), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 15, 0, 15),
                Child = _preview
            };
            preview.Children.Add(stage);
            _diameterCaption = Text(string.Empty, 13, _ink, FontWeights.SemiBold);
            _diameterCaption.TextAlignment = TextAlignment.Center;
            preview.Children.Add(_diameterCaption);
            var previewNote = Text(T("预览已缩放以适应此面板", "Preview scaled to fit this panel"), 11, _muted,
                margin: new Thickness(0, 5, 0, 3));
            previewNote.TextAlignment = TextAlignment.Center;
            preview.Children.Add(previewNote);
            preview.Children.Add(Divider());
            preview.Children.Add(Text(T("预览数值", "Preview value"), 12, _muted, FontWeights.Medium));
            var values = new UniformGrid { Columns = 3, Margin = new Thickness(0, 9, 0, 0) };
            foreach (var remaining in new[] { 100, 73, 12 })
            {
                var choice = new RadioButton
                {
                    Content = remaining.ToString(CultureInfo.InvariantCulture), GroupName = "PreviewValue", Tag = remaining,
                    IsChecked = remaining == 73, Style = (Style)Resources["SizeChoice"],
                    Margin = new Thickness(0, 0, remaining == 12 ? 0 : 6, 0)
                };
                choice.Checked += delegate(object sender, RoutedEventArgs args)
                {
                    _preview.SetVisual((int)((RadioButton)sender).Tag, true, false);
                };
                values.Children.Add(choice);
            }
            preview.Children.Add(values);
            stack.Children.Add(Card(preview, 18));

            var tray = new StackPanel();
            tray.Children.Add(Text(T("托盘预览", "TRAY PREVIEW"), 11, _muted, FontWeights.SemiBold));
            _trayPreview = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 13, 0, 13) };
            RenderOptions.SetBitmapScalingMode(_trayPreview, BitmapScalingMode.HighQuality);
            tray.Children.Add(new Border
            {
                Background = ThemePalette.SystemUsesLightTaskbar() ? ColorBrush("#EDF1F5") : ColorBrush("#202732"),
                CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 13, 0, 12), Child = _trayPreview
            });
            tray.Children.Add(Text(T("显示位置由 Windows 托盘管理", "Windows manages the tray icon’s position."), 11, _muted));
            stack.Children.Add(Card(tray, 18));

            var hint = new StackPanel { Margin = new Thickness(4, 2, 4, 0) };
            hint.Children.Add(Text(T("轻松移动", "Move it naturally"), 13, _ink, FontWeights.SemiBold));
            hint.Children.Add(Text(T("按住悬浮圆环即可拖动。松手后保存位置。", "Drag anywhere inside the floating circle. Its position saves when you release."), 12, _muted,
                margin: new Thickness(0, 6, 0, 0)));
            stack.Children.Add(hint);
            return stack;
        }

        private FrameworkElement ColorRow(string label, Func<DrawingColor> get, Action<DrawingColor> set)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var swatch = new Button { Padding = new Thickness(0), MinHeight = 32, Margin = new Thickness(0, 0, 7, 0) };
            var hex = new TextBox
            {
                Text = Hex(get()), FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 6, 10, 6), MinWidth = 91, MaxLength = 7, Margin = new Thickness(0, 0, 7, 0)
            };
            var choose = new Button { Content = T("选择", "Choose"), Padding = new Thickness(12, 6, 12, 6) };
            void RefreshColor()
            {
                swatch.Background = new SolidColorBrush(Color.FromRgb(get().R, get().G, get().B));
                swatch.BorderBrush = _line;
                hex.Text = Hex(get());
                hex.BorderBrush = _line;
            }
            void PickColor(object sender, RoutedEventArgs args)
            {
                using var dialog = new Forms.ColorDialog { FullOpen = true, Color = get(), AnyColor = true };
                if (dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
                set(DrawingColor.FromArgb(255, dialog.Color));
                RefreshColor();
                Commit();
            }
            RefreshColor();
            swatch.Click += PickColor;
            choose.Click += PickColor;
            hex.TextChanged += delegate
            {
                var value = hex.Text.Trim().TrimStart('#');
                if (value.Length == 6 && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                {
                    var color = DrawingColor.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                    hex.BorderBrush = _line;
                    if (get().ToArgb() == color.ToArgb()) return;
                    set(color);
                    swatch.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
                    Commit();
                }
                else hex.BorderBrush = ColorBrush("#E18585");
            };
            hex.LostKeyboardFocus += delegate { RefreshColor(); };
            hex.ToolTip = T("输入十六进制颜色，例如 #43C2DD", "Enter a hex color, for example #43C2DD");
            AutomationProperties.SetName(hex, label + T(" 十六进制值", " hex value"));
            AutomationProperties.SetName(swatch, label + T(" 选择颜色", " color picker"));
            AutomationProperties.SetName(choose, label + T(" 选择颜色", " color picker"));
            grid.Children.Add(swatch);
            Grid.SetColumn(hex, 1);
            grid.Children.Add(hex);
            Grid.SetColumn(choose, 2);
            grid.Children.Add(choose);
            return LabeledRow(label, grid);
        }

        private FrameworkElement SliderRow(string label, int minimum, int maximum, int initial, Action<int> set, Func<int, string> format)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            var slider = new Slider
            {
                Minimum = minimum, Maximum = maximum, Value = Math.Clamp(initial, minimum, maximum), TickFrequency = 1,
                IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = Math.Max(1, (maximum - minimum) / 8),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                IsMoveToPointEnabled = true, Foreground = ColorBrush("#0284C7")
            };
            AutomationProperties.SetName(slider, label);
            var valueLabel = Text(format((int)slider.Value), 12, _ink, FontWeights.SemiBold);
            valueLabel.TextAlignment = TextAlignment.Center;
            var valueBox = new Border
            {
                Background = ColorBrush("#EFF4F9"), CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 6, 4, 6),
                Child = valueLabel
            };
            slider.ValueChanged += delegate
            {
                var value = (int)Math.Round(slider.Value);
                set(value);
                valueLabel.Text = format(value);
                Commit();
            };
            grid.Children.Add(slider);
            Grid.SetColumn(valueBox, 1);
            grid.Children.Add(valueBox);
            return LabeledRow(label, grid);
        }

        private Grid LabeledRow(string label, UIElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 11) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock { Text = label, Foreground = _ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            _labeledRows.Add(grid);
            return grid;
        }

        private Border Card(UIElement child, double padding = 22) => new Border
        {
            Background = Brushes.White, BorderBrush = _line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(padding), Margin = new Thickness(0, 0, 0, 16), Child = child
        };

        private Border Divider() => new Border { Height = 1, Background = _line, Margin = new Thickness(0, 17, 0, 17) };

        private static TextBlock Text(string value, double size, Brush color, FontWeight? weight = null, Thickness? margin = null) => new TextBlock
        {
            Text = value, FontSize = size, Foreground = color, FontWeight = weight ?? FontWeights.Normal,
            Margin = margin ?? new Thickness(0), TextWrapping = TextWrapping.Wrap
        };

        private void Commit()
        {
            RefreshPreviews();
            SettingsChanged?.Invoke();
        }

        private void RefreshPreviews()
        {
            _preview?.Refresh();
            if (_diameterCaption != null) _diameterCaption.Text = _settings.FloatingDiameter + " × " + _settings.FloatingDiameter + " px";
            if (_trayPreview == null) return;
            using var bitmap = RingIconFactory.Render(73, true, ThemePalette.FromSettings(_settings), false, _settings, 32);
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32,
                    null, data.Scan0, data.Stride * bitmap.Height, data.Stride);
                image.Freeze();
                _trayPreview.Source = image;
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static string Hex(DrawingColor color) => "#" + (color.ToArgb() & 0xffffff).ToString("X6", CultureInfo.InvariantCulture);
        private string T(string chinese, string english) => _settings.IsEnglish ? english : chinese;
        private static SolidColorBrush ColorBrush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(value);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        private sealed class NativeOwner : Forms.IWin32Window
        {
            public NativeOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        private const string ControlStyles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='{x:Type Button}'>
    <Setter Property='Background' Value='#F7F9FC'/><Setter Property='BorderBrush' Value='#DAE3ED'/>
    <Setter Property='BorderThickness' Value='1'/><Setter Property='Foreground' Value='#263A52'/>
    <Setter Property='Padding' Value='14,8'/><Setter Property='Cursor' Value='Hand'/>
    <Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'>
      <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
              BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' Padding='{TemplateBinding Padding}'>
        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.82'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.65'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#38BDF8'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.45'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type TextBox}'>
    <Setter Property='Background' Value='White'/><Setter Property='BorderBrush' Value='#E1E8F0'/>
    <Setter Property='BorderThickness' Value='1'/><Setter Property='Foreground' Value='#263A52'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TextBox}'>
      <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='7'>
        <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
      </Border>
      <ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#38BDF8'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='SizeChoice' TargetType='{x:Type RadioButton}'>
    <Setter Property='Background' Value='#F6F8FB'/><Setter Property='BorderBrush' Value='#E1E8F0'/>
    <Setter Property='Foreground' Value='#62748B'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type RadioButton}'>
      <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='5,10'>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsChecked' Value='True'><Setter TargetName='Surface' Property='Background' Value='#EAF6FF'/><Setter TargetName='Surface' Property='BorderBrush' Value='#38A5DF'/><Setter Property='Foreground' Value='#0369A1'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#77BBDD'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderThickness' Value='2'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }
}
