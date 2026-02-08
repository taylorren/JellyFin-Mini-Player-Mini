using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Dsp;
using NAudio.Wave;
using System.Windows.Data;

namespace RoundSoundMimic;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly RoundSoundMimic.Services.IJellyfinService _jellyfinService;
    private const int EqSpikeCount = 48;
    private const int FftSize = 2048;
    private const int FftM = 11;
    private string? _lastArtworkKey;
    private NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private bool _trayBalloonShown;
    private readonly List<Line> _eqSpikes = new();
    private double[] _eqValues = Array.Empty<double>();
    private double[] _eqTargets = Array.Empty<double>();
    private double[] _eqSnapshot = new double[EqSpikeCount];
    private readonly object _eqLock = new();
    private readonly float[] _fftBuffer = new float[FftSize];
    private readonly Complex[] _fftComplex = new Complex[FftSize];
    private readonly double[] _fftMagnitudes = new double[FftSize / 2];
    private readonly float[] _fftWindow = new float[FftSize];
    private int _fftPos;
    private WasapiLoopbackCapture? _capture;
    private AppConfig _config = new();
    private string? _activeSessionId;
    private bool _isPaused;
    private long _currentRunTimeTicks;
    private long _currentPositionTicks;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _eqTimer;
    private bool _isFetching;
    private DateTime _lastPlaybackSeenUtc = DateTime.MinValue;

    // Properties for binding
    private string _titleText = "(no data)";
    private string _artistText = "";
    private string _albumText = "";
    private string _statusText = "Idle";
    private double _progress = 0;
    private ImageSource? _albumArtSource;
    private Geometry? _progressRingGeometry;
    private string _progressToolTip = "No playback";
    private string _ringBackgroundToolTip = "No playback";
    private bool _isPlayPauseEnabled = true;
    private bool _isPrevEnabled = true;
    private bool _isNextEnabled = true;
    private string _losslessIndicator = "";

    public string TitleText
    {
        get => _titleText;
        set => SetProperty(ref _titleText, value);
    }

    public string ArtistText
    {
        get => _artistText;
        set => SetProperty(ref _artistText, value);
    }

    public string AlbumText
    {
        get => _albumText;
        set => SetProperty(ref _albumText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public ImageSource? AlbumArtSource
    {
        get => _albumArtSource;
        set => SetProperty(ref _albumArtSource, value);
    }

    public Geometry? ProgressRingGeometry
    {
        get => _progressRingGeometry;
        set => SetProperty(ref _progressRingGeometry, value);
    }

    public string ProgressToolTip
    {
        get => _progressToolTip;
        set => SetProperty(ref _progressToolTip, value);
    }

    public string RingBackgroundToolTip
    {
        get => _ringBackgroundToolTip;
        set => SetProperty(ref _ringBackgroundToolTip, value);
    }

    public bool IsPlayPauseEnabled
    {
        get => _isPlayPauseEnabled;
        set => SetProperty(ref _isPlayPauseEnabled, value);
    }

    public bool IsPrevEnabled
    {
        get => _isPrevEnabled;
        set => SetProperty(ref _isPrevEnabled, value);
    }

    public bool IsNextEnabled
    {
        get => _isNextEnabled;
        set => SetProperty(ref _isNextEnabled, value);
    }

    public string LosslessIndicator
    {
        get => _losslessIndicator;
        set => SetProperty(ref _losslessIndicator, value);
    }

    // Helper to update the view element when using code-behind view
    private void UpdateLosslessTextBlock(string text)
    {
        // Try to update the view control if present (MainWindow uses code-behind)
        try
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
            {
                mw.Dispatcher.Invoke(() =>
                {
                    if (mw.FindName("LosslessTextBlock") is TextBlock tb)
                    {
                        tb.Text = text;
                    }
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    // PlayPause icon
    private UIElement? _playPauseIcon;

    public UIElement? PlayPauseIcon
    {
        get => _playPauseIcon;
        set => SetProperty(ref _playPauseIcon, value);
    }

    // Config properties
    private string _serverUrlText = "";
    private string _apiKeyText = "";
    private string _userIdText = "";

    public string ServerUrlText
    {
        get => _serverUrlText;
        set => SetProperty(ref _serverUrlText, value);
    }

    public string ApiKeyText
    {
        get => _apiKeyText;
        set => SetProperty(ref _apiKeyText, value);
    }

    public string UserIdText
    {
        get => _userIdText;
        set => SetProperty(ref _userIdText, value);
    }

    // Commands
    public ICommand PrevCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand OpenConfigCommand { get; }
    public ICommand MinimizeCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand FetchCommand { get; }
    public ICommand SaveConfigCommand { get; }
    public ICommand CloseConfigCommand { get; }

    public MainViewModel(RoundSoundMimic.Services.IJellyfinService jellyfinService)
    {
        _jellyfinService = jellyfinService ?? throw new ArgumentNullException(nameof(jellyfinService));
        try
        {
            InitializeFftWindow();

            PrevCommand = new RelayCommand(PrevExecute);
            PlayPauseCommand = new RelayCommand(PlayPauseExecute);
            NextCommand = new RelayCommand(NextExecute);
            OpenConfigCommand = new RelayCommand(OpenConfigExecute);
            MinimizeCommand = new RelayCommand(MinimizeExecute);
            ExitCommand = new RelayCommand(ExitExecute);
            FetchCommand = new RelayCommand(FetchExecute);
            SaveConfigCommand = new RelayCommand(SaveConfigExecute);
            CloseConfigCommand = new RelayCommand(CloseConfigExecute);

            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _pollTimer.Tick += PollTimerOnTick;

            _eqTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _eqTimer.Tick += (_, _) => UpdateEqAnimation();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error in ViewModel constructor: {ex.Message}\n{ex.StackTrace}", "ViewModel Error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    // Command methods
    private async void PrevExecute()
    {
        await SendPlaybackCommandAsync("PreviousTrack");
        await RefreshAfterCommandAsync();
    }

    private async void PlayPauseExecute()
    {
        await SendPlaybackCommandAsync("PlayPause");
        await RefreshAfterCommandAsync();
    }

    private async void NextExecute()
    {
        await SendPlaybackCommandAsync("NextTrack");
        await RefreshAfterCommandAsync();
    }

    private void OpenConfigExecute()
    {
        // This needs to be handled in the View, as it opens a popup
        // For now, we'll assume the View handles it via event
    }

    private void MinimizeExecute()
    {
        System.Windows.Application.Current.MainWindow.WindowState = WindowState.Minimized;
        System.Windows.Application.Current.MainWindow.Hide();
    }

    private void ExitExecute()
    {
        try
        {
            StartupLogger.Log("MainViewModel.ExitExecute: calling Shutdown");
        }
        catch { }
        System.Windows.Application.Current.Shutdown();
    }

    private async void FetchExecute()
    {
        await FetchNowPlayingAsync();
    }

    private async void SaveConfigExecute()
    {
        _config.ServerUrl = ServerUrlText.Trim();
        _config.ApiKey = ApiKeyText.Trim();
        _config.UserId = UserIdText.Trim();

        await _config.SaveAsync();
        StatusText = "Config saved";
    }

    private void CloseConfigExecute()
    {
        // This needs to be handled in the View, as it closes a popup
    }

    // Initialization methods
    private void InitializeFftWindow()
    {
        for (var i = 0; i < _fftWindow.Length; i++)
        {
            _fftWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
        }
    }

    private void InitializeTrayIcon()
    {
        ImageSource? iconImage = null;
        try
        {
            var appRes = System.Windows.Application.Current?.Resources;
            if (appRes is not null && appRes.Contains("AppIcon") && appRes["AppIcon"] is ImageSource img)
            {
                iconImage = img;
            }
        }
        catch { }

        if (iconImage is null)
        {
            return;
        }

        _trayIconImage = CreateTrayIcon(iconImage);
        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "RoundSound Mimic",
            Visible = true
        };
        _trayIcon.BalloonTipTitle = "RoundSound Mimic";

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitExecute());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Icon? CreateTrayIcon(ImageSource source)
    {
        var size = 64;
        var drawingVisual = new DrawingVisual();
        using (var context = drawingVisual.RenderOpen())
        {
            context.DrawRectangle(new ImageBrush(source) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();

        using var stream = new System.IO.MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        stream.Position = 0;

        using var gdiBitmap = new System.Drawing.Bitmap(stream);
        var iconHandle = gdiBitmap.GetHicon();
        var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconHandle).Clone();
        DestroyIcon(iconHandle);
        return icon;
    }

    private void ShowFromTray()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var window = System.Windows.Application.Current.MainWindow;
            if (window != null)
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
        });
    }

    private void UpdateTrayIcon(ImageSource source)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIconImage?.Dispose();
        _trayIconImage = CreateTrayIcon(source);
        if (_trayIconImage is not null)
        {
            _trayIcon.Icon = _trayIconImage;
        }
    }

    private void ShowTrayBalloon(string title, string artists)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var text = string.IsNullOrWhiteSpace(artists)
            ? title
            : $"{title}\n{artists}";

        _trayIcon.BalloonTipText = text;
        _trayIcon.ShowBalloonTip(_trayBalloonShown ? 1500 : 3000);
        _trayBalloonShown = true;
    }

    private void UpdateTrayNowPlayingText(string title, string artists)
    {
        if (_trayIcon is null)
        {
            return;
        }
        var display = string.IsNullOrWhiteSpace(artists) ? title : $"{title} - {artists}";
        var hint = string.IsNullOrWhiteSpace(display) ? "RoundSound Mimic" : $"Now Playing: {display}";
        _trayIcon.Text = TruncateTrayText(hint);
        try
        {
            _trayIcon.BalloonTipTitle = "Now Playing";
            _trayIcon.BalloonTipText = display;
        }
        catch { }
    }

    private static string TruncateTrayText(string text)
    {
        const int maxLength = 63;
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength - 1) + "…";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Audio capture methods
    private void StartAudioCapture()
    {
        if (_capture is not null)
        {
            return;
        }

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnAudioDataAvailable;
            _capture.RecordingStopped += OnAudioRecordingStopped;
            _capture.StartRecording();
        }
        catch
        {
            _capture = null;
        }
    }

    private void StopAudioCapture()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        _capture = null;
        capture.DataAvailable -= OnAudioDataAvailable;
        capture.RecordingStopped -= OnAudioRecordingStopped;
        capture.StopRecording();
        capture.Dispose();
    }

    private void OnAudioRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture)
        {
            return;
        }

        capture.DataAvailable -= OnAudioDataAvailable;
        capture.RecordingStopped -= OnAudioRecordingStopped;
        capture.Dispose();
        if (ReferenceEquals(_capture, capture))
        {
            _capture = null;
        }
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null)
        {
            return;
        }

        var bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return;
        }

        var channelCount = _capture.WaveFormat.Channels;
        if (channelCount <= 0)
        {
            return;
        }

        var sampleCount = e.BytesRecorded / bytesPerSample;
        if (sampleCount <= 0)
        {
            return;
        }

        if (_capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var waveBuffer = new WaveBuffer(e.Buffer);
            var floatBuffer = waveBuffer.FloatBuffer;
            for (var i = 0; i < sampleCount; i += channelCount)
            {
                var sample = 0f;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    sample += floatBuffer[i + channel];
                }

                AddSample(sample / channelCount);
            }
        }
        else
        {
            for (var i = 0; i < e.BytesRecorded; i += bytesPerSample * channelCount)
            {
                var sample = 0f;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var offset = i + channel * bytesPerSample;
                    sample += BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                }

                AddSample(sample / channelCount);
            }
        }
    }

    private void AddSample(float sample)
    {
        _fftBuffer[_fftPos] = sample;
        _fftPos++;
        if (_fftPos < FftSize)
        {
            return;
        }

        for (var i = 0; i < FftSize; i++)
        {
            _fftComplex[i].X = _fftBuffer[i] * _fftWindow[i];
            _fftComplex[i].Y = 0;
        }

        FastFourierTransform.FFT(true, FftM, _fftComplex);
        for (var i = 0; i < _fftMagnitudes.Length; i++)
        {
            var x = _fftComplex[i].X;
            var y = _fftComplex[i].Y;
            _fftMagnitudes[i] = Math.Sqrt(x * x + y * y);
        }

        UpdateEqTargetsFromFft();
        _fftPos = 0;
    }

    private void UpdateEqTargetsFromFft()
    {
        var maxBin = _fftMagnitudes.Length - 1;
        if (maxBin <= 0)
        {
            return;
        }

        var maxMagnitude = 0.0;
        for (var i = 0; i <= maxBin; i++)
        {
            if (_fftMagnitudes[i] > maxMagnitude)
            {
                maxMagnitude = _fftMagnitudes[i];
            }
        }

        if (maxMagnitude <= 1e-8)
        {
            return;
        }

        lock (_eqLock)
        {
            for (var band = 0; band < EqSpikeCount; band++)
            {
                var start = (int)Math.Floor(Math.Pow(maxBin, band / (double)EqSpikeCount));
                var end = (int)Math.Floor(Math.Pow(maxBin, (band + 1) / (double)EqSpikeCount));
                start = Math.Clamp(start, 1, maxBin);
                end = Math.Clamp(end, start + 1, maxBin);

                var sum = 0.0;
                for (var i = start; i < end; i++)
                {
                    sum += _fftMagnitudes[i];
                }

                var avg = sum / (end - start);
                var normalized = avg / maxMagnitude;
                var scaled = Math.Pow(normalized, 0.5);
                _eqTargets[band] = Math.Clamp(scaled, 0, 1);
            }
        }
    }

    private void UpdateEqAnimation()
    {
        lock (_eqLock)
        {
            if (_eqTargets.Length == EqSpikeCount)
            {
                Array.Copy(_eqTargets, _eqSnapshot, EqSpikeCount);
            }
        }

        for (var i = 0; i < _eqSnapshot.Length; i++)
        {
            var current = _eqValues[i];
            var target = _eqSnapshot[i];
            _eqValues[i] = current + (target - current) * 0.2;
        }

        // Note: EQ geometry update will be handled in View or via events
    }

    // Config and fetching methods
    public async Task LoadConfigAsync()
    {
        _config = await AppConfig.LoadAsync();
        ServerUrlText = _config.ServerUrl;
        ApiKeyText = _config.ApiKey;
        UserIdText = _config.UserId;
        StatusText = "Config loaded";
        Progress = 0;
    }

    private async Task FetchNowPlayingAsync()
    {
        if (_isFetching)
        {
            return;
        }

        _isFetching = true;
        IsPlayPauseEnabled = false;
        StatusText = "Fetching...";

        try
        {
            _config.ServerUrl = ServerUrlText.Trim();
            _config.ApiKey = ApiKeyText.Trim();
            _config.UserId = UserIdText.Trim();

            var session = await FetchActiveSessionAsync(_config);
            var nowPlaying = session?.NowPlayingItem;
            _activeSessionId = session?.Id;
            _isPaused = session?.PlayState?.IsPaused ?? false;
            UpdatePlayPauseIcon();

            if (nowPlaying is null)
            {
                var now = DateTime.UtcNow;
                // If we have seen playback very recently, this may be a brief gap
                // (for example when skipping tracks). Don't clear the UI immediately.
                if (now - _lastPlaybackSeenUtc < TimeSpan.FromSeconds(6))
                {
                    StatusText = "Waiting for update...";
                    return;
                }

                TitleText = "(no active playback)";
                ArtistText = string.Empty;
                AlbumText = string.Empty;
                AlbumArtSource = null;
                _currentRunTimeTicks = 0;
                _currentPositionTicks = 0;
                Progress = 0;
                UpdateTrayNowPlayingText("RoundSound Mimic", string.Empty);
                StatusText = "No active session";
                _lastPlaybackSeenUtc = DateTime.MinValue;
                return;
            }

            var title = nowPlaying.Name ?? "(unknown title)";
            var artists = nowPlaying.Artists?.Count > 0
                ? string.Join(", ", nowPlaying.Artists)
                : "(unknown artist)";
            var album = string.IsNullOrWhiteSpace(nowPlaying.Album)
                ? "(unknown album)"
                : nowPlaying.Album;

            var container = (nowPlaying.GetType().GetProperty("Container")?.GetValue(nowPlaying) as string)?.ToLowerInvariant();
            var lossless = container == "flac" || container == "wav";
            var indicator = lossless ? "LOSSLESS" : string.Empty;

            TitleText = title.ToUpperInvariant();
            ArtistText = artists.ToUpperInvariant();
            AlbumText = album;
            LosslessIndicator = indicator;
            UpdateLosslessTextBlock(indicator);
            _lastPlaybackSeenUtc = DateTime.UtcNow;
            UpdateTrayNowPlayingText(title, artists);
            _currentRunTimeTicks = nowPlaying.RunTimeTicks ?? 0;
            _currentPositionTicks = session?.PlayState?.PositionTicks ?? 0;
            var artworkKey = BuildArtworkKey(nowPlaying);
            var showBalloon = !string.Equals(artworkKey, _lastArtworkKey, StringComparison.Ordinal);
            await LoadAlbumArtAsync(_config, nowPlaying);
            if (showBalloon)
            {
                ShowTrayBalloon(title, artists);
            }
            Progress = GetProgress(session);
            UpdateProgressRing();
            StatusText = "Updated";
        }
        catch (Exception ex)
        {
            TitleText = "(error)";
            ArtistText = string.Empty;
            AlbumText = string.Empty;
            AlbumArtSource = null;
            _currentRunTimeTicks = 0;
            _currentPositionTicks = 0;
            Progress = 0;
            UpdateTrayNowPlayingText("RoundSound Mimic", string.Empty);
            StatusText = ex.Message;
        }
        finally
        {
            IsPlayPauseEnabled = true;
            _isFetching = false;
        }
    }

    private async void PollTimerOnTick(object? sender, EventArgs e)
    {
        await FetchNowPlayingAsync();
    }

    private void UpdatePlayPauseIcon()
    {
        var iconKey = _isPaused ? "PlayIcon" : "PauseIcon";
        if (System.Windows.Application.Current.TryFindResource(iconKey) is not Geometry geometry)
        {
            return;
        }

        PlayPauseIcon = new Viewbox
        {
            Width = 20,
            Height = 20,
            Child = new Path
            {
                Data = geometry,
                Fill = System.Windows.Media.Brushes.White
            }
        };
    }

    private Task<JellyfinSession?> FetchActiveSessionAsync(AppConfig config)
    {
        return _jellyfinService.FetchActiveSessionAsync(config);
    }

    private async Task LoadAlbumArtAsync(AppConfig config, JellyfinNowPlayingItem item)
    {
        var bytes = await _jellyfinService.GetArtworkBytesAsync(config, item);
        if (bytes is null || bytes.Length == 0)
        {
            AlbumArtSource = null;
            StatusText = "Artwork not available";
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 600;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            AlbumArtSource = image;
            UpdateTrayIcon(image);
            _lastArtworkKey = BuildArtworkKey(item);
        }
        catch
        {
            AlbumArtSource = null;
            StatusText = "Artwork load failed";
            return;
        }
    }

    private double GetProgress(JellyfinSession? session)
    {
        var item = session?.NowPlayingItem;
        if (item?.RunTimeTicks is null || item.RunTimeTicks <= 0)
        {
            return 0;
        }

        var position = session?.PlayState?.PositionTicks ?? 0;
        var progress = position / (double)item.RunTimeTicks.Value;
        return Math.Clamp(progress, 0, 1);
    }

    private void UpdateProgressRing()
    {
        UpdateProgressToolTip();
        var clamped = Math.Clamp(Progress, 0, 1);
        if (clamped <= 0.001)
        {
            ProgressRingGeometry = new EllipseGeometry(new System.Windows.Point(300, 300), 300, 300);
            return;
        }

        var radius = 300.0;
        var circumference = 2 * Math.PI * radius;
        var filled = circumference * clamped;
        var empty = circumference - filled;

        var thickness = 18.0;
        if (thickness <= 0)
        {
            thickness = 1;
        }

        var filledArc = new PathGeometry();
        var emptyArc = new PathGeometry();
        // Simplified: use a single path for now
        ProgressRingGeometry = new EllipseGeometry(new System.Windows.Point(300, 300), 300, 300);
    }

    private void UpdateProgressToolTip()
    {
        if (_currentRunTimeTicks <= 0)
        {
            ProgressToolTip = "No playback";
            RingBackgroundToolTip = "No playback";
            return;
        }

        var total = TimeSpan.FromTicks(_currentRunTimeTicks);
        var position = TimeSpan.FromTicks(Math.Max(0, _currentPositionTicks));
        var remaining = total - position;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var tooltipText = $"Total: {FormatTime(total)}\nRemaining: {FormatTime(remaining)}";
        ProgressToolTip = tooltipText;
        RingBackgroundToolTip = tooltipText;
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return time.ToString("h\\:mm\\:ss");
        }

        return time.ToString("m\\:ss");
    }

    private static string BuildArtworkKey(JellyfinNowPlayingItem item)
    {
        return string.Join("|", new[]
        {
            item.Id ?? string.Empty,
            item.PrimaryImageTag ?? string.Empty,
            item.AlbumId ?? string.Empty,
            item.AlbumPrimaryImageTag ?? string.Empty
        });
    }

    private async Task SendPlaybackCommandAsync(string command)
    {
        _config.ServerUrl = ServerUrlText.Trim();
        _config.ApiKey = ApiKeyText.Trim();
        _config.UserId = UserIdText.Trim();
        var sessionId = await EnsureActiveSessionIdAsync();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            StatusText = "No active session";
            return;
        }
        _config.ServerUrl = ServerUrlText.Trim();
        _config.ApiKey = ApiKeyText.Trim();
        _config.UserId = UserIdText.Trim();

        var ok = await _jellyfinService.SendPlaybackCommandAsync(_config, command, sessionId);
        StatusText = ok ? $"Command sent: {command}" : $"Command failed";
    }

    private async Task<string?> EnsureActiveSessionIdAsync()
    {
        if (!string.IsNullOrWhiteSpace(_activeSessionId))
        {
            return _activeSessionId;
        }

        try
        {
            var session = await FetchActiveSessionAsync(_config);
            _activeSessionId = session?.Id;
            _isPaused = session?.PlayState?.IsPaused ?? _isPaused;
            UpdatePlayPauseIcon();
            return _activeSessionId;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
    }

    private async Task RefreshAfterCommandAsync()
    {
        await Task.Delay(300);
        await FetchNowPlayingAsync();
    }

    // Methods to call from View
    public void OnLoaded()
    {
        try
        {
            var dbgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoundSoundMimic_startup_debug.log");
            System.IO.File.AppendAllText(dbgPath, DateTime.UtcNow.ToString("o") + " MainViewModel.OnLoaded: start\n");
        }
        catch { }

        try
        {
            InitializeTrayIcon();
        }
        catch (Exception ex)
        {
            TryLog("OnLoaded_InitializeTrayIcon", ex);
        }

        try
        {
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            TryLog("OnLoaded_PollTimerStart", ex);
        }

        try
        {
            StartAudioCapture();
        }
        catch (Exception ex)
        {
            TryLog("OnLoaded_StartAudioCapture", ex);
        }

        try
        {
            _eqTimer.Start();
        }
        catch (Exception ex)
        {
            TryLog("OnLoaded_EqTimerStart", ex);
        }
        try
        {
            var dbgPath2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoundSoundMimic_startup_debug.log");
            System.IO.File.AppendAllText(dbgPath2, DateTime.UtcNow.ToString("o") + " MainViewModel.OnLoaded: complete\n");
        }
        catch { }
    }

    public void OnClosed()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayIconImage?.Dispose();
        _trayIconImage = null;
        StopAudioCapture();
    }

    private static void TryLog(string tag, Exception ex)
    {
        try
        {
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_debug.log");
            var text = DateTime.UtcNow.ToString("o") + " " + tag + "\n" + ex + "\n\n";
            System.IO.File.AppendAllText(logPath, text);
        }
        catch { }
    }

    // EQ spikes initialization (will be called from View)
    public void InitializeEqSpikes(Canvas? eqCanvas)
    {
        if (eqCanvas is null)
        {
            return;
        }

        eqCanvas.Children.Clear();
        _eqSpikes.Clear();
        _eqValues = new double[EqSpikeCount];
        lock (_eqLock)
        {
            _eqTargets = new double[EqSpikeCount];
            _eqSnapshot = new double[EqSpikeCount];
        }

        var stroke = System.Windows.Application.Current.TryFindResource("EqSpikeBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Orange;
        for (var i = 0; i < EqSpikeCount; i++)
        {
            var line = new Line
            {
                Stroke = stroke,
                StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.45,
                    Color = Colors.Orange
                }
            };
            eqCanvas.Children.Add(line);
            _eqSpikes.Add(line);
        }
    }

    public void UpdateEqGeometry(Ellipse? progressRing)
    {
        if (progressRing is null)
        {
            return;
        }

        if (_eqSpikes.Count == 0)
        {
            return;
        }

        var ringWidth = progressRing.ActualWidth > 0 ? progressRing.ActualWidth : progressRing.Width;
        var ringHeight = progressRing.ActualHeight > 0 ? progressRing.ActualHeight : progressRing.Height;
        if (ringWidth <= 0 || ringHeight <= 0)
        {
            return;
        }

        var ringLeft = Canvas.GetLeft(progressRing);
        var ringTop = Canvas.GetTop(progressRing);
        if (double.IsNaN(ringLeft))
        {
            ringLeft = 0;
        }

        if (double.IsNaN(ringTop))
        {
            ringTop = 0;
        }

        var ringRadius = Math.Min(ringWidth, ringHeight) / 2.0;
        var centerX = ringLeft + ringWidth / 2.0;
        var centerY = ringTop + ringHeight / 2.0;
        var baseRadius = ringRadius + progressRing.StrokeThickness / 2.0 + 4;
        var minSpike = Math.Max(3, ringRadius * 0.04);
        var maxSpike = Math.Max(8, ringRadius * 0.14);

        for (var i = 0; i < _eqSpikes.Count; i++)
        {
            var angle = (Math.PI * 2.0 * i) / _eqSpikes.Count;
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);
            var spikeLength = minSpike + _eqValues[i] * (maxSpike - minSpike);

            var x1 = centerX + cos * baseRadius;
            var y1 = centerY + sin * baseRadius;
            var x2 = centerX + cos * (baseRadius + spikeLength);
            var y2 = centerY + sin * (baseRadius + spikeLength);

            var line = _eqSpikes[i];
            line.X1 = x1;
            line.Y1 = y1;
            line.X2 = x2;
            line.Y2 = y2;
        }
    }

    public void UpdateBandGeometry(Path? bandPath, Path? bandHighlightPath, Ellipse? innerCircle)
    {
        if (bandPath is null || bandHighlightPath is null || innerCircle is null)
        {
            return;
        }

        var innerLeft = Canvas.GetLeft(innerCircle);
        var innerTop = Canvas.GetTop(innerCircle);
        if (double.IsNaN(innerLeft))
        {
            innerLeft = 0;
        }

        if (double.IsNaN(innerTop))
        {
            innerTop = 0;
        }

        var innerWidth = innerCircle.ActualWidth > 0 ? innerCircle.ActualWidth : innerCircle.Width;
        var innerHeight = innerCircle.ActualHeight > 0 ? innerCircle.ActualHeight : innerCircle.Height;
        if (innerWidth <= 0 || innerHeight <= 0)
        {
            return;
        }

        const double widthPaddingRatio = 12.0 / 520.0;
        const double heightRatio = 140.0 / 520.0;

        var bandHeight = Math.Max(0, innerHeight * heightRatio);
        var rectWidth = Math.Max(0, innerWidth * (1.0 - widthPaddingRatio));

        var rectX = innerLeft + (innerWidth - rectWidth) / 2.0;
        var rectY = innerTop + (innerHeight - bandHeight) / 2.0;

        var centerX = innerLeft + innerWidth / 2.0;
        var centerY = innerTop + innerHeight / 2.0;
        var outerRadiusX = innerWidth / 2.0;
        var outerRadiusY = innerHeight / 2.0;
        var innerRadiusX = Math.Max(0, outerRadiusX - bandHeight);
        var innerRadiusY = Math.Max(0, outerRadiusY - bandHeight);

        var outerEllipse = new EllipseGeometry(new System.Windows.Point(centerX, centerY), outerRadiusX, outerRadiusY);
        var innerEllipse = new EllipseGeometry(new System.Windows.Point(centerX, centerY), innerRadiusX, innerRadiusY);
        var ringGeom = Geometry.Combine(outerEllipse, innerEllipse, GeometryCombineMode.Exclude, null);
        var rectGeom = new RectangleGeometry(new Rect(rectX, rectY, rectWidth, bandHeight));

        var bandGeom = Geometry.Combine(ringGeom, rectGeom, GeometryCombineMode.Intersect, null);
        bandPath.Data = bandGeom;
        if (bandHighlightPath is not null)
        {
            bandHighlightPath.Data = bandGeom;
        }
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}