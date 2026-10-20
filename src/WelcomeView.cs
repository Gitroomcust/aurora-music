using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Lumen
{
    internal sealed class WelcomeView : UserControl
    {
        public event EventHandler LocalRequested;
        public event EventHandler CaptureRequested;

        private readonly AppState _state;
        private readonly Button _localCard;
        private readonly Button _captureCard;
        private readonly Button _openWebButton;
        private readonly ScaleTransform _localScale;
        private readonly ScaleTransform _captureScale;
        private readonly TranslateTransform _localTranslate;
        private readonly TranslateTransform _captureTranslate;
        private readonly TextBlock _streamStatus;
        private readonly TextBlock _streamUrl;
        private readonly Ellipse _networkDot;

        public WelcomeView(AppState state)
        {
            _state = state;
            FrameworkElement root = XamlLoader.Load("Welcome.xaml");
            Content = root;

            TextBlock greetingText = (TextBlock)root.FindName("GreetingText");
            if (greetingText != null) greetingText.Text = "Добро пожаловать";
            TextBlock userNameText = (TextBlock)root.FindName("UserNameText");
            if (userNameText != null) userNameText.Text = "";
            TextBlock userInitialText = (TextBlock)root.FindName("UserInitialText");
            if (userInitialText != null) userInitialText.Text = "\uE8D6";

            _localCard = (Button)root.FindName("LocalCardButton");
            _captureCard = (Button)root.FindName("CaptureCardButton");
            _openWebButton = (Button)root.FindName("OpenWebButton");
            _localScale = (ScaleTransform)root.FindName("LocalScale");
            _captureScale = (ScaleTransform)root.FindName("CaptureScale");
            _localTranslate = (TranslateTransform)root.FindName("LocalTranslate");
            _captureTranslate = (TranslateTransform)root.FindName("CaptureTranslate");
            _streamStatus = (TextBlock)root.FindName("StreamStatusText");
            _streamUrl = (TextBlock)root.FindName("StreamUrlText");
            _networkDot = (Ellipse)root.FindName("NetworkDot");

            _localCard.Click += OnLocalClick;
            _captureCard.Click += OnCaptureClick;
            _openWebButton.Click += OnOpenWebClick;
            AttachHover(_localCard, _localScale, _localTranslate);
            AttachHover(_captureCard, _captureScale, _captureTranslate);

            string url = state.StreamUrl;
            if (string.IsNullOrEmpty(url))
            {
                _streamStatus.Text = "Стриминг недоступен";
                _streamUrl.Text = "";
                _networkDot.Fill = new SolidColorBrush(Color.FromRgb(150, 143, 160));
                _openWebButton.IsEnabled = false;
            }
            else
            {
                _streamStatus.Text = "Стриминг по Wi-Fi";
                _streamUrl.Text = url;
                _openWebButton.IsEnabled = true;
            }
        }

        private void OnLocalClick(object sender, RoutedEventArgs e)
        {
            EventHandler handler = LocalRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void OnCaptureClick(object sender, RoutedEventArgs e)
        {
            EventHandler handler = CaptureRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void OnOpenWebClick(object sender, RoutedEventArgs e)
        {
            string url = _state.StreamUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("Open web player failed", ex);
                _state.ShowMessage("Не удалось открыть браузер");
            }
        }

        private static void AttachHover(Button button, ScaleTransform scale, TranslateTransform translate)
        {
            button.MouseEnter += delegate
            {
                Animate(scale, ScaleTransform.ScaleXProperty, 1.018, 180);
                Animate(scale, ScaleTransform.ScaleYProperty, 1.018, 180);
                Animate(translate, TranslateTransform.YProperty, -3.0, 180);
            };
            button.MouseLeave += delegate
            {
                Animate(scale, ScaleTransform.ScaleXProperty, 1.0, 220);
                Animate(scale, ScaleTransform.ScaleYProperty, 1.0, 220);
                Animate(translate, TranslateTransform.YProperty, 0.0, 220);
            };
            button.PreviewMouseLeftButtonDown += delegate
            {
                Animate(scale, ScaleTransform.ScaleXProperty, 0.985, 80);
                Animate(scale, ScaleTransform.ScaleYProperty, 0.985, 80);
            };
            button.PreviewMouseLeftButtonUp += delegate
            {
                Animate(scale, ScaleTransform.ScaleXProperty, button.IsMouseOver ? 1.018 : 1.0, 130);
                Animate(scale, ScaleTransform.ScaleYProperty, button.IsMouseOver ? 1.018 : 1.0, 130);
            };
        }

        private static void Animate(IAnimatable target, DependencyProperty property, double value, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation(value,
                TimeSpan.FromMilliseconds(milliseconds));
            animation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            target.BeginAnimation(property, animation);
        }
    }
}
