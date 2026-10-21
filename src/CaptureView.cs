using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lumen.Audio;
using Lumen.Controls;

namespace Lumen
{
    internal sealed class CaptureView : UserControl
    {
        public event EventHandler BackRequested;

        private readonly AppState _state;
        private readonly TaskScheduler _uiScheduler;
        private readonly DispatcherTimer _timer;
        private readonly WaveformView _waveform;

        private readonly Button _backButton;
        private readonly Button _captureButton;
        private readonly TextBlock _captureGlyph;
        private readonly ScaleTransform _captureScale;
        private readonly TextBlock _captureStatus;
        private readonly TextBlock _captureHint;
        private readonly System.Windows.Shapes.Ellipse _captureStatusDot;
        private readonly Border _headerChip;
        private readonly TextBlock _headerChipText;
        private readonly System.Windows.Shapes.Ellipse _headerChipDot;
        private readonly TextBlock _elapsed;
        private readonly TextBlock _peak;
        private readonly Border _levelBar;
        private readonly TextBlock _formatText;
        private readonly Border _liveBadge;
        private readonly TextBlock _liveBadgeText;
        private readonly System.Windows.Shapes.Ellipse _liveBadgeDot;
        private readonly Border _streamStateChip;
        private readonly TextBlock _streamStateText;
        private readonly TextBlock _streamUrl;
        private readonly Button _copyButton;
        private readonly Button _openButton;
        private readonly Grid _waveformHost;

        private WasapiLoopbackCapture _capture;
        private bool _starting;
        private bool _uiLive;
        private bool _wantedCapture;
        private bool _shuttingDown;

        public CaptureView(AppState state)
        {
            _state = state;
            _uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            FrameworkElement root = XamlLoader.Load("CaptureView.xaml");
            Content = root;

            _backButton = (Button)root.FindName("BackButton");
            _captureButton = (Button)root.FindName("CaptureToggleButton");
            _captureGlyph = (TextBlock)root.FindName("CaptureGlyph");
            _captureScale = (ScaleTransform)root.FindName("CaptureButtonScale");
            _captureStatus = (TextBlock)root.FindName("CaptureStatusText");
            _captureHint = (TextBlock)root.FindName("CaptureHintText");
            _captureStatusDot = (System.Windows.Shapes.Ellipse)root.FindName("CaptureStatusDot");
            _headerChip = (Border)root.FindName("HeaderChip");
            _headerChipText = (TextBlock)root.FindName("HeaderChipText");
            _headerChipDot = (System.Windows.Shapes.Ellipse)root.FindName("HeaderChipDot");
            _elapsed = (TextBlock)root.FindName("ElapsedText");
            _peak = (TextBlock)root.FindName("PeakText");
            _levelBar = (Border)root.FindName("LevelBar");
            _formatText = (TextBlock)root.FindName("FormatText");
            _liveBadge = (Border)root.FindName("LiveBadge");
            _liveBadgeText = (TextBlock)root.FindName("LiveBadgeText");
            _liveBadgeDot = (System.Windows.Shapes.Ellipse)root.FindName("LiveBadgeDot");
            _streamStateChip = (Border)root.FindName("StreamStateChip");
            _streamStateText = (TextBlock)root.FindName("StreamStateText");
            _streamUrl = (TextBlock)root.FindName("StreamUrlText");
            _copyButton = (Button)root.FindName("CopyStreamButton");
            _openButton = (Button)root.FindName("OpenWebButton");
            _waveformHost = (Grid)root.FindName("WaveformHost");

            _waveform = new WaveformView();
            _waveformHost.Children.Add(_waveform);

            _backButton.Click += delegate { RaiseBack(); };
            _captureButton.Click += delegate { ToggleCapture(); };
            _copyButton.Click += delegate { CopyStreamUrl(); };
            _openButton.Click += delegate { OpenStreamUrl(); };

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += delegate { UpdateLiveUi(); };

            AttachCaptureHover();
            UpdateStreamLabels();
            SetLiveVisual(false, false, "ПАУЗА");
            _elapsed.Text = "00:00";
            _peak.Text = "— дБ";
            _formatText.Text = "48 000 Гц · стерео · 16 бит";
        }

        public void OnShown()
        {
            if (_shuttingDown) return;
            UpdateStreamLabels();
            _wantedCapture = true;
            if (_capture != null && _capture.IsRunning)
            {
                SetLiveVisual(true, false, null);
                _timer.Start();
                return;
            }

            if (_starting) return;
            DispatcherTimer starter = new DispatcherTimer();
            starter.Interval = TimeSpan.FromMilliseconds(260);
            starter.Tick += delegate
            {
                starter.Stop();
                if (_wantedCapture && !_shuttingDown) BeginCapture();
            };
            starter.Start();
        }

        public void OnHidden()
        {
            _wantedCapture = false;
            StopCapture(false);
        }

        public void Shutdown()
        {
            _shuttingDown = true;
            _wantedCapture = false;
            StopCapture(false);
            _timer.Stop();
        }

        private void RaiseBack()
        {
            EventHandler handler = BackRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ToggleCapture()
        {
            if (_uiLive)
            {
                _wantedCapture = false;
                StopCapture(true);
            }
            else
            {
                _wantedCapture = true;
                BeginCapture();
            }
        }

        private void BeginCapture()
        {
            if (_starting || _uiLive || _shuttingDown) return;
            _starting = true;
            _captureStatus.Text = "Подключение…";
            _captureHint.Text = "Открываем системный микшер";
            _captureStatusDot.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36));

            WasapiLoopbackCapture capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnCaptureData;
            capture.Failed += OnCaptureFailed;

            Task<CaptureFormat> task = Task.Run(delegate
            {
                return capture.Start();
            });

            task.ContinueWith(delegate(Task<CaptureFormat> completed)
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    OnCaptureStarted(capture, completed.Result);
                }
                else
                {
                    Exception error = completed.Exception == null
                        ? new Exception("Неизвестная ошибка WASAPI")
                        : completed.Exception.GetBaseException();
                    OnCaptureStartFailed(capture, error);
                }
            }, _uiScheduler);
        }

        private void OnCaptureData(byte[] pcm, int count, float peak)
        {
            _state.LiveAudio.Publish(pcm, count);
        }

        private void OnCaptureStarted(WasapiLoopbackCapture capture, CaptureFormat format)
        {
            _starting = false;
            if (_shuttingDown || !_wantedCapture)
            {
                Task.Run(delegate
                {
                    try { capture.Stop(); }
                    catch { }
                    try { capture.Dispose(); }
                    catch { }
                });
                return;
            }

            _capture = capture;
            _state.LiveAudio.Reset(format.SampleRate, format.Channels);
            _state.Server.IsLive = true;
            _state.Server.LiveSeconds = 0;
            _formatText.Text = format.Display;
            _captureStatusDot.Fill = new SolidColorBrush(Color.FromRgb(47, 191, 143));
            _captureStatus.Text = "Идёт захват";
            _captureHint.Text = "Нажмите, чтобы остановить эфир";
            SetLiveVisual(true, false, null);
            _timer.Start();
            _state.ShowMessage("Прямой эфир запущен");
        }

        private void OnCaptureStartFailed(WasapiLoopbackCapture capture, Exception error)
        {
            _starting = false;
            Log.Write("WASAPI start failed", error);
            try { capture.Dispose(); }
            catch { }
            if (_shuttingDown)
            {
                _capture = null;
                return;
            }

            _capture = null;
            _state.LiveAudio.Stop();
            _state.Server.IsLive = false;
            _state.Server.LiveSeconds = 0;
            SetLiveVisual(false, true, "ОШИБКА");
            _captureHint.Text = "Не удалось открыть устройство вывода";
            _captureStatus.ToolTip = error.Message;
            _state.ShowMessage("Не удалось включить захват системного звука");
        }

        private void StopCapture(bool showMessage)
        {
            _state.Server.IsLive = false;
            _state.Server.LiveSeconds = 0;
            _state.LiveAudio.Stop();
            _timer.Stop();

            WasapiLoopbackCapture capture = _capture;
            _capture = null;
            if (capture != null)
            {
                Task.Run(delegate
                {
                    try { capture.Stop(); }
                    catch { }
                    try { capture.Dispose(); }
                    catch { }
                });
            }

            _uiLive = false;
            _waveform.SetActive(false);
            _waveform.Clear();
            _elapsed.Text = "00:00";
            _peak.Text = "— дБ";
            _levelBar.Width = 0;
            _captureStatusDot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 160));
            _captureStatus.Text = "Пауза";
            _captureHint.Text = "Запустите захват, чтобы начать эфир";
            _captureGlyph.Text = "\uE768";
            _captureButton.ToolTip = "Запустить захват";
            SetLiveVisual(false, false, "ПАУЗА");
            if (showMessage) _state.ShowMessage("Эфир остановлен");
        }

        private void OnCaptureFailed(Exception error)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_shuttingDown) return;
                Log.Write("WASAPI stream stopped unexpectedly", error);
                _state.Server.IsLive = false;
                _state.Server.LiveSeconds = 0;
                _state.LiveAudio.Stop();
                _timer.Stop();
                WasapiLoopbackCapture failed = _capture;
                _capture = null;
                _uiLive = false;
                if (failed != null)
                {
                    Task.Run(delegate
                    {
                        try { failed.Dispose(); }
                        catch { }
                    });
                }
                SetLiveVisual(false, true, "ОШИБКА");
                _captureHint.Text = "Устройство вывода изменилось или недоступно";
                _captureStatus.ToolTip = error.Message;
                _state.ShowMessage("Захват системного звука прерван");
            }));
        }

        private void SetLiveVisual(bool live, bool error, string headerText)
        {
            _uiLive = live;
            _captureGlyph.Text = live ? "\uE71A" : "\uE768";
            _captureButton.ToolTip = live ? "Остановить захват" : "Запустить захват";
            _waveform.SetActive(live);

            if (live)
            {
                _headerChip.Background = new SolidColorBrush(Color.FromArgb(26, 244, 114, 182));
                _headerChipDot.Fill = new SolidColorBrush(Color.FromRgb(244, 114, 182));
                _headerChipText.Text = "В ЭФИРЕ";
                _headerChipText.Foreground = new SolidColorBrush(Color.FromRgb(244, 114, 182));

                _liveBadge.Background = new SolidColorBrush(Color.FromArgb(30, 244, 114, 182));
                _liveBadgeDot.Fill = new SolidColorBrush(Color.FromRgb(255, 175, 212));
                _liveBadgeText.Text = "LIVE";
                _liveBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 232));

                _streamStateChip.Background = new SolidColorBrush(Color.FromArgb(22, 34, 211, 238));
                _streamStateText.Text = "LIVE";
                _streamStateText.Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238));

                _captureStatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 211, 238));
                _captureStatus.Text = "Идёт захват";
                _captureHint.Text = "Нажмите, чтобы остановить эфир";
            }
            else
            {
                bool isError = error;
                Color chipColor = isError ? Color.FromRgb(229, 72, 77) : Color.FromRgb(142, 142, 160);
                _headerChip.Background = new SolidColorBrush(isError
                    ? Color.FromArgb(22, 229, 72, 77)
                    : Color.FromArgb(18, 142, 142, 160));
                _headerChipDot.Fill = new SolidColorBrush(chipColor);
                _headerChipText.Text = !string.IsNullOrEmpty(headerText) ? headerText : "ПАУЗА";
                _headerChipText.Foreground = new SolidColorBrush(isError
                    ? Color.FromRgb(248, 113, 113)
                    : Color.FromRgb(199, 199, 214));

                _liveBadge.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                _liveBadgeDot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 160));
                _liveBadgeText.Text = isError ? "ERROR" : "PAUSE";
                _liveBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(199, 199, 214));

                _streamStateChip.Background = new SolidColorBrush(Color.FromArgb(18, 142, 142, 160));
                _streamStateText.Text = "OFF";
                _streamStateText.Foreground = new SolidColorBrush(Color.FromRgb(142, 142, 160));

                if (isError)
                {
                    _captureStatusDot.Fill = new SolidColorBrush(Color.FromRgb(229, 72, 77));
                    _captureStatus.Text = "Ошибка";
                }
                else
                {
                    _captureStatusDot.Fill = new SolidColorBrush(Color.FromRgb(142, 142, 160));
                    _captureStatus.Text = "Пауза";
                    _captureHint.Text = "Запустите захват, чтобы начать эфир";
                }
            }
        }

        private void UpdateLiveUi()
        {
            if (!_uiLive) return;
            WasapiLoopbackCapture capture = _capture;
            if (capture == null || !capture.IsRunning)
            {
                return;
            }

            TimeSpan elapsed = capture.Elapsed;
            _elapsed.Text = FormatDuration(elapsed);
            _state.Server.LiveSeconds = elapsed.TotalSeconds;

            float peak = capture.LastPeak;
            if (peak > 0.00015f)
            {
                double db = 20.0 * Math.Log10(peak);
                if (db < -60) db = -60;
                _peak.Text = "\u2212" + Math.Abs(db).ToString("0") + " дБ";
            }
            else
            {
                _peak.Text = "\u2212\u221E дБ";
            }

            _waveform.Push(peak);

            Border track = _levelBar.Parent as Border;
            if (track != null && track.ActualWidth > 0)
            {
                double normalized = Math.Sqrt(Math.Max(0, Math.Min(1, peak)));
                _levelBar.Width = Math.Max(0, track.ActualWidth * normalized);
            }
        }

        private static string FormatDuration(TimeSpan value)
        {
            int total = (int)value.TotalSeconds;
            int hours = total / 3600;
            int minutes = (total % 3600) / 60;
            int seconds = total % 60;
            if (hours > 0)
            {
                return hours + ":" + (minutes < 10 ? "0" : "") + minutes + ":" +
                       (seconds < 10 ? "0" : "") + seconds;
            }
            return (minutes < 10 ? "0" : "") + minutes + ":" + (seconds < 10 ? "0" : "") + seconds;
        }

        private void UpdateStreamLabels()
        {
            string url = _state.StreamUrl;
            if (string.IsNullOrEmpty(url))
            {
                _streamUrl.Text = "Локальная сеть недоступна";
                _copyButton.IsEnabled = false;
                _openButton.IsEnabled = false;
            }
            else
            {
                _streamUrl.Text = url;
                _copyButton.IsEnabled = true;
                _openButton.IsEnabled = true;
                _streamUrl.ToolTip = "Откройте этот адрес на телефоне в той же Wi-Fi сети";
            }
        }

        private void CopyStreamUrl()
        {
            string url = _state.StreamUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Clipboard.SetText(url);
                _state.ShowMessage("Ссылка скопирована");
            }
            catch (Exception ex)
            {
                Log.Write("Clipboard failed", ex);
                _state.ShowMessage("Не удалось скопировать ссылку");
            }
        }

        private void OpenStreamUrl()
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

        private void AttachCaptureHover()
        {
            _captureButton.MouseEnter += delegate { AnimateCaptureScale(1.05, 160); };
            _captureButton.MouseLeave += delegate { AnimateCaptureScale(1.0, 190); };
            _captureButton.PreviewMouseLeftButtonDown += delegate { AnimateCaptureScale(0.95, 80); };
            _captureButton.PreviewMouseLeftButtonUp += delegate
            {
                AnimateCaptureScale(_captureButton.IsMouseOver ? 1.05 : 1.0, 120);
            };
        }

        private void AnimateCaptureScale(double value, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds));
            animation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            _captureScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            _captureScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }
}

