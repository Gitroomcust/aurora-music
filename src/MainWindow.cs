using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lumen
{
    internal sealed class MainWindow : Window
    {
        private readonly AppState _state;
        private readonly Grid _viewHost;
        private readonly Grid _titleBar;
        private readonly Border _toastBorder;
        private readonly TextBlock _toastText;
        private readonly WelcomeView _welcome;
        private readonly LocalView _local;
        private readonly CaptureView _capture;

        private readonly DispatcherTimer _toastTimer;
        private UserControl _currentView;
        private bool _closing;

        public MainWindow(AppState state)
        {
            _state = state;
            if (System.Threading.SynchronizationContext.Current == null)
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            }
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 1108;
            Height = 700;
            Title = "Lumen";
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Opacity = 0;

            FrameworkElement root = XamlLoader.Load("Shell.xaml");
            Content = root;
            _viewHost = (Grid)root.FindName("ViewHost");
            _titleBar = (Grid)root.FindName("TitleBar");
            _toastBorder = (Border)root.FindName("ToastBorder");
            _toastText = (TextBlock)root.FindName("ToastText");

            Button minimize = (Button)root.FindName("MinimizeButton");
            Button close = (Button)root.FindName("CloseButton");
            minimize.Click += delegate { WindowState = WindowState.Minimized; };
            close.Click += delegate { Close(); };
            _titleBar.MouseLeftButtonDown += OnTitleBarMouseDown;

            _state.Notify = ShowToast;
            _welcome = new WelcomeView(state);
            _local = new LocalView(state);
            _capture = new CaptureView(state);

            _welcome.LocalRequested += delegate { ShowLocal(); };
            _welcome.CaptureRequested += delegate { ShowCapture(); };
            _local.BackRequested += delegate { ShowWelcome(); };
            _capture.BackRequested += delegate { ShowWelcome(); };

            _toastTimer = new DispatcherTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(2.6);
            _toastTimer.Tick += delegate
            {
                _toastTimer.Stop();
                DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                _toastBorder.BeginAnimation(OpacityProperty, fade);
            };

            Loaded += OnLoaded;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;

            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    Assembly.GetExecutingAssembly().Location))
                {
                    if (icon != null)
                    {
                        Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    }
                }
            }
            catch { }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log.Write("MainWindow loaded.");
            Rect area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);

            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)));

            if (_state.StartupView == "local")
            {
                ShowLocal();
            }
            else if (_state.StartupView == "capture")
            {
                ShowCapture();
            }
            else
            {
                ShowWelcome();
            }
        }

        private void ShowWelcome()
        {
            ShowView(_welcome, false);
        }

        private void ShowLocal()
        {
            if (_currentView == _capture) _capture.OnHidden();
            if (_currentView == _local)
            {
                _local.FocusPlaylist();
                return;
            }
            ShowView(_local, true);
            _local.OnShown();
        }

        private void ShowCapture()
        {
            if (_currentView == _local) _local.OnHidden();
            if (_currentView == _capture)
            {
                _capture.OnShown();
                return;
            }
            ShowView(_capture, true);
            _capture.OnShown();
        }

        private void ShowView(UserControl view, bool forward)
        {
            if (_currentView == view)
            {
                return;
            }

            if (_currentView == _local) _local.OnHidden();
            if (_currentView == _capture) _capture.OnHidden();

            Log.Write("Show view: " + view.GetType().Name);
            _viewHost.Children.Clear();
            _viewHost.Children.Add(view);
            _currentView = view;

            view.Opacity = 0;
            TranslateTransform transform = new TranslateTransform(0, forward ? 18 : -12);
            view.RenderTransform = transform;

            DoubleAnimation fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            view.BeginAnimation(OpacityProperty, fade);

            DoubleAnimation move = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            move.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.YProperty, move);
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (FindButton(e.OriginalSource as DependencyObject) != null) return;
            if (e.ClickCount == 2) return;
            try { DragMove(); }
            catch { }
        }

        private static Button FindButton(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null)
            {
                Button button = current as Button;
                if (button != null) return button;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_currentView != _welcome) ShowWelcome();
                e.Handled = true;
                return;
            }

            if ((e.Key == Key.Space || e.Key == Key.MediaPlayPause) && _currentView == _local)
            {
                _local.TogglePlayback();
                e.Handled = true;
                return;
            }

            if (_currentView == _local)
            {
                if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    _local.PlayNext();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    _local.PlayPrevious();
                    e.Handled = true;
                }
            }
        }

        private void ShowToast(string message)
        {
            if (_closing || string.IsNullOrWhiteSpace(message)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(ShowToast), message);
                return;
            }

            _toastText.Text = message;
            _toastBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_closing) return;
            _closing = true;
            Log.Write("MainWindow closed.");
            try { _local.Shutdown(); }
            catch (Exception ex) { Log.Write("Local view shutdown failed", ex); }
            try { _capture.Shutdown(); }
            catch (Exception ex) { Log.Write("Capture view shutdown failed", ex); }
            try { _state.Settings.Save(); }
            catch { }
            try { _state.Server.Dispose(); }
            catch { }
        }
    }
}
