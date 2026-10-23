using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumen.Audio;
using Forms = System.Windows.Forms;

namespace Lumen
{
    internal sealed class LocalView : UserControl
    {
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".aiff", ".aif"
            };

        public event EventHandler BackRequested;

        /// <summary>Raised with title, artist and play state whenever the current track changes.</summary>
        public event Action<string, string, bool> NowPlayingChanged;

        private readonly AppState _state;
        private MfPlayAudioEngine _player;
        private string _playerError;
        private readonly DispatcherTimer _timer;
        private readonly TaskScheduler _uiScheduler;
        private readonly ObservableCollection<Track> _tracks = new ObservableCollection<Track>();

        private readonly Button _backButton;
        private readonly Button _chooseFolderButton;
        private readonly TextBlock _chooseFolderButtonText;
        private readonly Button _emptyChooseButton;
        private readonly TextBlock _folderPathText;
        private readonly ListBox _playlistBox;
        private readonly Border _emptyState;
        private readonly TextBlock _emptyTitle;
        private readonly TextBlock _emptyHint;
        private readonly TextBlock _trackCountText;
        private readonly TextBlock _nowTitle;
        private readonly TextBlock _nowArtist;
        private readonly TextBlock _nowPlayingBadge;
        private readonly System.Windows.Shapes.Ellipse _nowPlayingDot;
        private readonly Border _coverLayer;
        private readonly Grid _artDecor;
        private readonly Button _playButton;
        private readonly TextBlock _playGlyph;
        private readonly ScaleTransform _playScale;
        private readonly Button _previousButton;
        private readonly Button _nextButton;
        private readonly Slider _seekSlider;
        private readonly Slider _volumeSlider;
        private readonly TextBlock _currentTime;
        private readonly TextBlock _duration;
        private readonly TextBlock _volumeText;
        private readonly StackPanel _transportPanel;
        private readonly TextBlock _streamUrl;
        private readonly Button _copyStreamButton;
        private readonly Button _openStreamButton;

        private string _folder = "";
        private int _currentIndex = -1;
        private bool _isPlaying;
        private bool _isSeeking;
        private bool _suppressSelection;
        private bool _folderPromptShown;
        private bool _scanning;
        private bool _shuttingDown;
        private bool _endHandled;
        private DateTime _playStartedUtc = DateTime.UtcNow;
        private int _scanVersion;
        private int _artVersion;

        public LocalView(AppState state)
        {
            _state = state;
            _uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            FrameworkElement root = XamlLoader.Load("LocalView.xaml");
            Content = root;

            _backButton = (Button)root.FindName("BackButton");
            _chooseFolderButton = (Button)root.FindName("ChooseFolderButton");
            _chooseFolderButtonText = (TextBlock)root.FindName("ChooseFolderButtonText");
            _emptyChooseButton = (Button)root.FindName("EmptyChooseButton");
            _folderPathText = (TextBlock)root.FindName("FolderPathText");
            _playlistBox = (ListBox)root.FindName("PlaylistBox");
            _emptyState = (Border)root.FindName("EmptyState");
            _emptyTitle = (TextBlock)root.FindName("EmptyTitleText");
            _emptyHint = (TextBlock)root.FindName("EmptyHintText");
            _trackCountText = (TextBlock)root.FindName("TrackCountText");
            _nowTitle = (TextBlock)root.FindName("NowTitleText");
            _nowArtist = (TextBlock)root.FindName("NowArtistText");
            _nowPlayingBadge = (TextBlock)root.FindName("NowPlayingBadgeText");
            _nowPlayingDot = (System.Windows.Shapes.Ellipse)root.FindName("NowPlayingDot");
            _coverLayer = (Border)root.FindName("CoverLayer");
            _artDecor = (Grid)root.FindName("ArtDecor");
            _playButton = (Button)root.FindName("PlayButton");
            _playGlyph = (TextBlock)root.FindName("PlayGlyph");
            _playScale = (ScaleTransform)root.FindName("PlayButtonScale");
            _previousButton = (Button)root.FindName("PreviousButton");
            _nextButton = (Button)root.FindName("NextButton");
            _seekSlider = (Slider)root.FindName("SeekSlider");
            _volumeSlider = (Slider)root.FindName("VolumeSlider");
            _currentTime = (TextBlock)root.FindName("CurrentTimeText");
            _duration = (TextBlock)root.FindName("DurationText");
            _volumeText = (TextBlock)root.FindName("VolumeText");
            _transportPanel = (StackPanel)root.FindName("TransportPanel");
            _streamUrl = (TextBlock)root.FindName("StreamUrlText");
            _copyStreamButton = (Button)root.FindName("CopyStreamButton");
            _openStreamButton = (Button)root.FindName("OpenStreamButton");

            _playlistBox.ItemsSource = _tracks;
            _playlistBox.SelectionChanged += OnPlaylistSelectionChanged;

            _backButton.Click += delegate { RaiseBack(); };
            _chooseFolderButton.Click += delegate { ChooseFolder(); };
            _emptyChooseButton.Click += delegate { ChooseFolder(); };
            _playButton.Click += delegate { TogglePlayback(); };
            _previousButton.Click += delegate { PlayPrevious(); };
            _nextButton.Click += delegate { PlayNext(); };
            _copyStreamButton.Click += delegate { CopyStreamUrl(); };
            _openStreamButton.Click += delegate { OpenStreamUrl(); };
            _seekSlider.PreviewMouseLeftButtonDown += delegate { _isSeeking = true; };
            _seekSlider.PreviewMouseLeftButtonUp += delegate
            {
                SeekToSlider();
                _isSeeking = false;
            };
            _seekSlider.ValueChanged += delegate
            {
                if (_isSeeking) _currentTime.Text = FormatTime(_seekSlider.Value);
            };
            _seekSlider.LostMouseCapture += delegate
            {
                if (_isSeeking)
                {
                    SeekToSlider();
                    _isSeeking = false;
                }
            };

            AttachPlayHover();
            double volume = Math.Max(0, Math.Min(1, state.Settings.Volume));
            try
            {
                _player = new MfPlayAudioEngine();
                _player.MediaOpened += OnMediaOpened;
                _player.MediaEnded += OnMediaEnded;
                _player.Failed += OnMediaFailed;
                _playStartedUtc = DateTime.UtcNow;
                _player.Volume = (float)volume;
            }
            catch (Exception ex)
            {
                _playerError = ex.Message;
                Log.Write("MFPlay initialization failed", ex);
                _state.ShowMessage("Локальное воспроизведение недоступно");
            }

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += delegate { UpdateProgress(); };

            _volumeSlider.Value = volume;
            _volumeText.Text = ((int)Math.Round(volume * 100)).ToString();
            _volumeSlider.ValueChanged += delegate
            {
                double value = Math.Max(0, Math.Min(1, _volumeSlider.Value));
                if (_player != null) _player.Volume = (float)value;
                _volumeText.Text = ((int)Math.Round(value * 100)).ToString();
                _state.Settings.Volume = value;
            };

            UpdateStreamLabels();
            UpdateTransportEnabled(false);
            ShowEmpty("Здесь появится музыка",
                "Выберите папку — Lumen найдёт MP3, WAV, FLAC и другие треки.", "Выбрать папку", true);
            SetFolderText("Папка не выбрана");

            string saved = state.StartupFolder;
            if (string.IsNullOrEmpty(saved)) saved = state.Settings.FolderPath;
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
            {
                _folder = saved;
                _folderPromptShown = true;
                StartScan(saved, false);
            }
            else
            {
                state.Settings.FolderPath = "";
            }
        }

        public void OnShown()
        {
            if (_shuttingDown) return;
            if (string.IsNullOrEmpty(_folder) && !_scanning && !_folderPromptShown)
            {
                _folderPromptShown = true;
                DispatcherTimer starter = new DispatcherTimer();
                starter.Interval = TimeSpan.FromMilliseconds(260);
                starter.Tick += delegate
                {
                    starter.Stop();
                    if (string.IsNullOrEmpty(_folder)) ChooseFolder();
                };
                starter.Start();
            }
        }

        public void OnHidden()
        {
            if (_player != null) _state.Settings.Volume = _player.Volume;
            _state.Settings.Save();
        }

        public void Shutdown()
        {
            _shuttingDown = true;
            _timer.Stop();
            if (_player != null)
            {
                try { _player.Dispose(); }
                catch { }
                _player = null;
            }
            _state.Settings.Save();
        }

        public void FocusPlaylist()
        {
            if (_tracks.Count > 0) _playlistBox.Focus();
        }

        public void TogglePlayback()
        {
            if (_tracks.Count == 0) return;
            if (_currentIndex < 0)
            {
                PlayIndex(0);
                return;
            }
            if (_isPlaying)
            {
                if (_player != null) _player.Pause();
                _isPlaying = false;
                SetPlayingVisual(false, "ПАУЗА");
            }
            else
            {
                if (_player == null)
                {
                    _state.ShowMessage("Локальное воспроизведение недоступно");
                    return;
                }
                if (_endHandled ||
                    (_player.HasCachedDuration &&
                     _player.Position >= _player.CachedDuration - TimeSpan.FromMilliseconds(250)))
                {
                    _player.Seek(TimeSpan.Zero);
                    _seekSlider.Value = 0;
                }
                _endHandled = false;
                _player.Play();
                _isPlaying = true;
                _playStartedUtc = DateTime.UtcNow;
                SetPlayingVisual(true, "СЕЙЧАС ИГРАЕТ");
            }
        }

        public void PlayNext()
        {
            if (_tracks.Count == 0) return;
            int next = _currentIndex < 0 ? 0 : (_currentIndex + 1) % _tracks.Count;
            PlayIndex(next);
        }

        public void PlayPrevious()
        {
            if (_tracks.Count == 0) return;
            if (_player != null && _player.Position.TotalSeconds > 4 && _currentIndex >= 0)
            {
                _player.Seek(TimeSpan.Zero);
                _seekSlider.Value = 0;
                return;
            }
            int previous = _currentIndex <= 0 ? _tracks.Count - 1 : _currentIndex - 1;
            PlayIndex(previous);
        }

        private void RaiseBack()
        {
            EventHandler handler = BackRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ChooseFolder()
        {
            if (_scanning || _shuttingDown) return;
            Window owner = Window.GetWindow(this);
            IntPtr handle = owner == null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;

            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку с музыкой";
                dialog.ShowNewFolderButton = false;
                try
                {
                    string selected = !string.IsNullOrEmpty(_folder) && Directory.Exists(_folder)
                        ? _folder
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    if (!string.IsNullOrEmpty(selected) && Directory.Exists(selected))
                    {
                        dialog.SelectedPath = selected;
                    }
                }
                catch { }

                Forms.DialogResult result = handle != IntPtr.Zero
                    ? dialog.ShowDialog(new Win32Owner(handle))
                    : dialog.ShowDialog();

                if (result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    _folder = dialog.SelectedPath;
                    _folderPromptShown = true;
                    _state.Settings.FolderPath = _folder;
                    _state.Settings.Save();
                    StartScan(_folder, true);
                }
            }
        }

        private void StartScan(string folder, bool announce)
        {
            _scanVersion++;
            int version = _scanVersion;
            _scanning = true;
            _folder = folder;
            StopAndClearPlayback();
            _tracks.Clear();
            _trackCountText.Text = "0";
            _chooseFolderButton.IsEnabled = false;
            _chooseFolderButtonText.Text = "Сканирую…";
            SetFolderText(folder + "  ·  сканирование…");
            ShowEmpty("Сканирую папку…", "Ищу поддерживаемые аудиофайлы. Это может занять пару секунд.",
                "Подождите…", false);
            _state.Server.SetTracks(_tracks);

            Task<ScanResult> task = Task.Factory.StartNew<ScanResult>(
                delegate { return ScanCore(folder); },
                System.Threading.CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            task.ContinueWith(delegate(Task<ScanResult> completed)
            {
                if (_shuttingDown || version != _scanVersion) return;
                ScanResult result = completed.Result;
                CompleteScan(folder, result, announce);
            }, _uiScheduler);
        }

        private static ScanResult ScanCore(string folder)
        {
            ScanResult result = new ScanResult();
            try
            {
                CollectFiles(folder, result.Files, 0);
                result.Files.Sort(delegate(string a, string b)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(
                        Path.GetFileName(a), Path.GetFileName(b));
                });

                for (int i = 0; i < result.Files.Count; i++)
                {
                    if (i >= 5000)
                    {
                        result.Truncated = true;
                        break;
                    }
                    try
                    {
                        Track track = TrackCache.GetOrCreate(result.Files[i]);
                        if (track != null) result.Tracks.Add(track);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Track scan failed: " + result.Files[i], ex);
                    }
                }

                TrackCache.Save();
            }
            catch (Exception ex)
            {
                result.Error = ex;
                Log.Write("Folder scan failed: " + folder, ex);
            }
            return result;
        }

        private static void CollectFiles(string directory, List<string> files, int depth)
        {
            if (depth > 12 || files.Count > 5000) return;
            try
            {
                string[] entries = Directory.GetFiles(directory);
                for (int i = 0; i < entries.Length; i++)
                {
                    string extension = Path.GetExtension(entries[i]);
                    if (!string.IsNullOrEmpty(extension) && SupportedExtensions.Contains(extension))
                    {
                        files.Add(entries[i]);
                        if (files.Count > 5000) return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Cannot list files in " + directory + ": " + ex.Message);
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(directory);
                for (int i = 0; i < subdirectories.Length; i++)
                {
                    if (files.Count > 5000) return;
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(subdirectories[i]);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    CollectFiles(subdirectories[i], files, depth + 1);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Cannot list folders in " + directory + ": " + ex.Message);
            }
        }

        private void CompleteScan(string folder, ScanResult result, bool announce)
        {
            _scanning = false;
            _chooseFolderButton.IsEnabled = true;
            _chooseFolderButtonText.Text = "Сменить папку";

            _suppressSelection = true;
            _tracks.Clear();
            for (int i = 0; i < result.Tracks.Count; i++)
            {
                _tracks.Add(result.Tracks[i]);
            }
            _suppressSelection = false;

            _trackCountText.Text = _tracks.Count.ToString();
            SetFolderText(folder + "  ·  " + TrackWord(_tracks.Count));
            _state.Server.SetTracks(_tracks);

            if (result.Error != null)
            {
                ShowEmpty("Не удалось прочитать папку",
                    "Проверьте права доступа и попробуйте выбрать другую папку.", "Выбрать папку", true);
                return;
            }

            if (_tracks.Count == 0)
            {
                ShowEmpty("В папке нет музыки",
                    "Поддерживаются MP3, WAV, FLAC, M4A, AAC, WMA, OGG, OPUS и AIFF.", "Выбрать другую папку", true);
                if (announce) _state.ShowMessage("В выбранной папке не найдено аудиофайлов");
                return;
            }

            _emptyState.Visibility = Visibility.Collapsed;
            _playlistBox.Visibility = Visibility.Visible;
            _playlistBox.SelectedIndex = -1;
            if (announce)
            {
                _state.ShowMessage("Найдено " + TrackWord(_tracks.Count));
            }
            if (result.Truncated)
            {
                _state.ShowMessage("Показаны первые 5000 треков");
            }
        }

        private void PlayIndex(int index)
        {
            if (index < 0 || index >= _tracks.Count) return;
            Track track = _tracks[index];
            if (!File.Exists(track.Path))
            {
                _state.ShowMessage("Файл не найден: " + Path.GetFileName(track.Path));
                return;
            }

            if (_player == null)
            {
                _state.ShowMessage("Локальное воспроизведение недоступно: " + _playerError);
                return;
            }

            _artVersion++;
            _currentIndex = index;
            _endHandled = false;
            _playStartedUtc = DateTime.UtcNow;
            try { _player.Close(); }
            catch { }
            _seekSlider.Value = 0;
            _seekSlider.Maximum = 100;
            _currentTime.Text = "0:00";
            _duration.Text = "0:00";
            _nowTitle.Text = track.Title;
            _nowArtist.Text = track.Artist;
            SetCover(null);
            _nowPlayingBadge.Text = "ЗАГРУЗКА…";
            _nowPlayingDot.Fill = new SolidColorBrush(Color.FromRgb(34, 211, 238));
            UpdateTransportEnabled(true);

            try
            {
                _player.Open(track.Path);
                _player.Play();
                _isPlaying = true;
                SetPlayingVisual(true, "СЕЙЧАС ИГРАЕТ");
                _timer.Start();
            }
            catch (Exception ex)
            {
                Log.Write("Play failed: " + track.Path, ex);
                _isPlaying = false;
                SetPlayingVisual(false, "ОШИБКА");
                _state.ShowMessage("Не удалось открыть трек");
            }

            _suppressSelection = true;
            _playlistBox.SelectedIndex = index;
            _suppressSelection = false;
            _playlistBox.ScrollIntoView(track);
            LoadArtwork(track, _artVersion);
        }

        private void StopAndClearPlayback()
        {
            _timer.Stop();
            if (_player != null)
            {
                try { _player.Close(); }
                catch { }
            }
            _isPlaying = false;
            _currentIndex = -1;
            _seekSlider.Value = 0;
            _currentTime.Text = "0:00";
            _duration.Text = "0:00";
            _nowTitle.Text = "Ничего не играет";
            _nowArtist.Text = "Выберите трек из очереди";
            _nowPlayingBadge.Text = "ГОТОВО К ЗАПУСКУ";
            SetCover(null);
            SetPlayingVisual(false, "ГОТОВО К ЗАПУСКУ");
            UpdateTransportEnabled(false);
            _suppressSelection = true;
            _playlistBox.SelectedIndex = -1;
            _suppressSelection = false;
        }

        private void OnPlaylistSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection) return;
            int index = _playlistBox.SelectedIndex;
            if (index >= 0 && index < _tracks.Count)
            {
                PlayIndex(index);
            }
        }

        private void OnMediaOpened(object sender, EventArgs e)
        {
            if (_player != null && _player.HasCachedDuration)
            {
                double total = Math.Max(1, _player.CachedDuration.TotalSeconds);
                _seekSlider.Maximum = total;
                _seekSlider.Value = 0;
                _duration.Text = FormatTime(total);
            }
            _nowPlayingBadge.Text = "СЕЙЧАС ИГРАЕТ";
            _nowPlayingDot.Fill = new SolidColorBrush(Color.FromRgb(192, 132, 252));
        }

        private void OnMediaEnded(object sender, EventArgs e)
        {
            if (_endHandled) return;
            _endHandled = true;
            if (_tracks.Count == 0) return;
            if (_currentIndex + 1 < _tracks.Count)
            {
                PlayIndex(_currentIndex + 1);
            }
            else
            {
                _isPlaying = false;
                _timer.Stop();
                SetPlayingVisual(false, "ЗАВЕРШЕНО");
                _seekSlider.Value = _player != null && _player.HasCachedDuration
                    ? _player.CachedDuration.TotalSeconds
                    : 0;
            }
        }

        private void OnMediaFailed(object sender, PlaybackErrorEventArgs e)
        {
            string message = e.Error == null ? "" : e.Error.Message;
            Log.Write("MFPlay playback failed: " + message);
            _isPlaying = false;
            _timer.Stop();
            SetPlayingVisual(false, "ОШИБКА ФОРМАТА");
            _state.ShowMessage("Этот формат не поддерживается проигрывателем");
        }

        private void UpdateProgress()
        {
            try
            {
                MfPlayAudioEngine player = _player;
                if (_currentIndex < 0 || player == null) return;
                if (player.HasCachedDuration)
                {
                    double total = player.CachedDuration.TotalSeconds;
                    if (total > 0)
                    {
                        _seekSlider.Maximum = total;
                        _duration.Text = FormatTime(total);
                        if (!_isSeeking)
                        {
                            double position = Math.Min(player.Position.TotalSeconds, total);
                            _seekSlider.Value = position;
                            _currentTime.Text = FormatTime(position);
                        }
                    }
                }

                if (_isPlaying && !_endHandled && player.IsStopped &&
                    (DateTime.UtcNow - _playStartedUtc).TotalSeconds > 2)
                {
                    OnMediaEnded(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.Write("UpdateProgress failed", ex);
            }
        }

        private void SeekToSlider()
        {
            if (_currentIndex < 0 || _player == null) return;
            if (_player.HasCachedDuration)
            {
                _player.Seek(TimeSpan.FromSeconds(
                    Math.Max(0, Math.Min(_seekSlider.Value, _player.CachedDuration.TotalSeconds))));
            }
        }

        private void SetPlayingVisual(bool playing, string badge)
        {
            _playGlyph.Text = playing ? "\uE769" : "\uE768";
            _playButton.ToolTip = playing ? "Пауза" : "Воспроизвести";
            _nowPlayingBadge.Text = badge;
            if (badge == "ПАУЗА" || badge == "ОШИБКА" || badge == "ОШИБКА ФОРМАТА")
            {
                _nowPlayingDot.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            }
            NotifyNowPlaying();
        }

        private void NotifyNowPlaying()
        {
            Action<string, string, bool> handler = NowPlayingChanged;
            if (handler == null) return;
            Track track = (_currentIndex >= 0 && _currentIndex < _tracks.Count)
                ? _tracks[_currentIndex]
                : null;
            if (track == null)
            {
                handler("", "", false);
                return;
            }
            handler(track.Title ?? "", track.Artist ?? "", _isPlaying);
        }

        private void UpdateTransportEnabled(bool enabled)
        {
            _transportPanel.IsEnabled = enabled;
            _transportPanel.Opacity = enabled ? 1.0 : 0.55;
            _seekSlider.IsEnabled = enabled;
            _previousButton.IsEnabled = enabled;
            _nextButton.IsEnabled = enabled;
        }

        private void LoadArtwork(Track track, int version)
        {
            string path = track.Path;
            Task<byte[]> task = Task.Run(delegate { return Id3Reader.ReadPicture(path); });
            task.ContinueWith(delegate(Task<byte[]> completed)
            {
                if (_shuttingDown || version != _artVersion) return;
                byte[] data = completed.Status == TaskStatus.RanToCompletion ? completed.Result : null;
                if (data == null || data.Length == 0)
                {
                    SetCover(null);
                    return;
                }
                try
                {
                    BitmapImage image = new BitmapImage();
                    using (MemoryStream stream = new MemoryStream(data))
                    {
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 760;
                        image.StreamSource = stream;
                        image.EndInit();
                    }
                    image.Freeze();
                    SetCover(image);
                }
                catch (Exception ex)
                {
                    Log.Write("Cover decode failed", ex);
                    SetCover(null);
                }
            }, _uiScheduler);
        }

        private void SetCover(ImageSource image)
        {
            if (image == null)
            {
                _coverLayer.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
                _coverLayer.Background = null;
                _artDecor.Visibility = Visibility.Visible;
                return;
            }

            ImageBrush brush = new ImageBrush(image);
            brush.Stretch = Stretch.UniformToFill;
            _coverLayer.Background = brush;
            _artDecor.Visibility = Visibility.Collapsed;
            DoubleAnimation fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _coverLayer.BeginAnimation(OpacityProperty, fade);
        }

        private void UpdateStreamLabels()
        {
            string url = _state.StreamUrl;
            if (string.IsNullOrEmpty(url))
            {
                _streamUrl.Text = "Локальная сеть недоступна";
                _copyStreamButton.IsEnabled = false;
                _openStreamButton.IsEnabled = false;
            }
            else
            {
                _streamUrl.Text = url;
                _copyStreamButton.IsEnabled = true;
                _openStreamButton.IsEnabled = true;
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

        private void ShowEmpty(string title, string hint, string buttonText, bool buttonEnabled)
        {
            _emptyTitle.Text = title;
            _emptyHint.Text = hint;
            _emptyChooseButton.Content = buttonText;
            _emptyChooseButton.IsEnabled = buttonEnabled;
            _emptyState.Visibility = Visibility.Visible;
            _playlistBox.Visibility = Visibility.Collapsed;
        }

        private void SetFolderText(string text)
        {
            _folderPathText.Text = text;
            _folderPathText.ToolTip = _folder;
        }

        private static string TrackWord(int count)
        {
            int mod100 = count % 100;
            int mod10 = count % 10;
            if (mod100 >= 11 && mod100 <= 14) return count + " треков";
            if (mod10 == 1) return count + " трек";
            if (mod10 >= 2 && mod10 <= 4) return count + " трека";
            return count + " треков";
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            int total = (int)Math.Floor(seconds);
            int minutes = total / 60;
            int rest = total % 60;
            return minutes + ":" + (rest < 10 ? "0" : "") + rest;
        }

        private void AttachPlayHover()
        {
            _playButton.MouseEnter += delegate
            {
                AnimateScale(1.045, 150);
            };
            _playButton.MouseLeave += delegate
            {
                AnimateScale(1.0, 180);
            };
            _playButton.PreviewMouseLeftButtonDown += delegate
            {
                AnimateScale(0.95, 80);
            };
            _playButton.PreviewMouseLeftButtonUp += delegate
            {
                AnimateScale(_playButton.IsMouseOver ? 1.045 : 1.0, 120);
            };
        }

        private void AnimateScale(double value, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds));
            animation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            _playScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            _playScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private sealed class ScanResult
        {
            public readonly List<string> Files = new List<string>();
            public readonly List<Track> Tracks = new List<Track>();
            public Exception Error;
            public bool Truncated;
        }

        private sealed class Win32Owner : Forms.IWin32Window
        {
            private readonly IntPtr _handle;

            public Win32Owner(IntPtr handle)
            {
                _handle = handle;
            }

            public IntPtr Handle
            {
                get { return _handle; }
            }
        }
    }
}

