using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;
using NAudio.Dsp;
using NAudio.Wave;

namespace RoundSoundMimic;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new();
    private const int EqSpikeCount = 48;
    private const int FftSize = 2048;
    private const int FftM = 11;
    private Canvas? _drawingCanvas;
    private Canvas? _eqCanvas;
    private Popup? _configPopup;
    private Popup? _menuPopup;
    private FrameworkElement? _innerCircle;
    private Path? _bandPath;
    private string? _lastArtworkKey;
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;
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

    public MainWindow()
    {
        InitializeComponent();
        InitializeFftWindow();
        _drawingCanvas = FindName("DrawingCanvas") as Canvas;
        _eqCanvas = FindName("EqCanvas") as Canvas;
        _configPopup = FindName("ConfigPopup") as Popup;
        _menuPopup = FindName("MenuPopup") as Popup;
        _innerCircle = FindName("InnerCircle") as FrameworkElement;
        _bandPath = FindName("BandPath") as Path;
        InitializeTrayIcon();
        if (_drawingCanvas is not null)
        {
            _drawingCanvas.Loaded += (_, _) =>
            {
                InitializeEqSpikes();
                UpdateBandGeometry();
                UpdateEqGeometry();
            };
            _drawingCanvas.SizeChanged += (_, _) =>
            {
                UpdateBandGeometry();
                UpdateEqGeometry();
            };
        }

        if (_innerCircle is not null)
        {
            _innerCircle.SizeChanged += (_, _) => UpdateBandGeometry();
        }
        Loaded += OnLoaded;
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigAsync();
        _pollTimer.Start();
        InitializeEqSpikes();
        UpdateEqGeometry();
        StartAudioCapture();
        _eqTimer.Start();
        UpdateBandGeometry();
        UpdatePlayPauseIcon();
    }

    protected override void OnClosed(EventArgs e)
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
        base.OnClosed(e);
    }

    private void InitializeTrayIcon()
    {
        var iconImage = FindResource("AppIcon") as ImageSource;
        if (iconImage is null)
        {
            return;
        }

        _trayIconImage = CreateTrayIcon(iconImage);
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "RoundSound Mimic",
            Visible = true
        };
        _trayIcon.BalloonTipTitle = "RoundSound Mimic";

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Drawing.Icon? CreateTrayIcon(ImageSource source)
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

        using var gdiBitmap = new Drawing.Bitmap(stream);
        var iconHandle = gdiBitmap.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(iconHandle).Clone();
        DestroyIcon(iconHandle);
        return icon;
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
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

    private void InitializeFftWindow()
    {
        for (var i = 0; i < _fftWindow.Length; i++)
        {
            _fftWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
        }
    }

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

    private void InitializeEqSpikes()
    {
        if (_eqCanvas is null)
        {
            return;
        }

        _eqCanvas.Children.Clear();
        _eqSpikes.Clear();
        _eqValues = new double[EqSpikeCount];
        lock (_eqLock)
        {
            _eqTargets = new double[EqSpikeCount];
            _eqSnapshot = new double[EqSpikeCount];
        }

        var stroke = (Brush)FindResource("EqSpikeBrush");
        for (var i = 0; i < EqSpikeCount; i++)
        {
            var line = new Line
            {
                Stroke = stroke,
                StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _eqCanvas.Children.Add(line);
            _eqSpikes.Add(line);
        }
    }

    private void UpdateEqAnimation()
    {
        if (_eqSpikes.Count == 0)
        {
            InitializeEqSpikes();
        }

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

        UpdateEqGeometry();
    }

    private void UpdateEqGeometry()
    {
        if (_eqSpikes.Count == 0)
        {
            return;
        }

        var ringWidth = ProgressRing.ActualWidth > 0 ? ProgressRing.ActualWidth : ProgressRing.Width;
        var ringHeight = ProgressRing.ActualHeight > 0 ? ProgressRing.ActualHeight : ProgressRing.Height;
        if (ringWidth <= 0 || ringHeight <= 0)
        {
            return;
        }

        var ringLeft = Canvas.GetLeft(ProgressRing);
        var ringTop = Canvas.GetTop(ProgressRing);
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
        var baseRadius = ringRadius + ProgressRing.StrokeThickness / 2.0 + 4;
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

    private void UpdateBandGeometry()
    {
        if (_bandPath is null || _innerCircle is null)
        {
            return;
        }

        var innerLeft = Canvas.GetLeft(_innerCircle);
        var innerTop = Canvas.GetTop(_innerCircle);
        if (double.IsNaN(innerLeft))
        {
            innerLeft = 0;
        }

        if (double.IsNaN(innerTop))
        {
            innerTop = 0;
        }

        var innerWidth = _innerCircle.ActualWidth > 0 ? _innerCircle.ActualWidth : _innerCircle.Width;
        var innerHeight = _innerCircle.ActualHeight > 0 ? _innerCircle.ActualHeight : _innerCircle.Height;
        if (innerWidth <= 0 || innerHeight <= 0)
        {
            return;
        }

        const double widthPaddingRatio = 12.0 / 520.0;
        const double heightRatio = 140.0 / 520.0;

        var rectWidth = Math.Max(0, innerWidth * (1.0 - widthPaddingRatio));
        var rectHeight = Math.Max(0, innerHeight * heightRatio);

        var rectX = innerLeft + (innerWidth - rectWidth) / 2.0;
        var rectY = innerTop + (innerHeight - rectHeight) / 2.0;
        var rectRadius = rectHeight / 2.0;

        var rectGeom = new RectangleGeometry(new Rect(rectX, rectY, rectWidth, rectHeight), rectRadius, rectRadius);

        var centerX = innerLeft + innerWidth / 2.0;
        var centerY = innerTop + innerHeight / 2.0;
        var ellipseGeom = new EllipseGeometry(new Point(centerX, centerY), innerWidth / 2.0, innerHeight / 2.0);

        _bandPath.Data = Geometry.Combine(rectGeom, ellipseGeom, GeometryCombineMode.Intersect, null);
    }

    private async Task LoadConfigAsync()
    {
        _config = await AppConfig.LoadAsync();
        ServerUrlTextBox.Text = _config.ServerUrl;
        ApiKeyTextBox.Text = _config.ApiKey;
        UserIdTextBox.Text = _config.UserId;
        StatusTextBlock.Text = "Config loaded";
        UpdateProgressRing(0);
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _config.ServerUrl = ServerUrlTextBox.Text.Trim();
        _config.ApiKey = ApiKeyTextBox.Text.Trim();
        _config.UserId = UserIdTextBox.Text.Trim();

        await _config.SaveAsync();
        StatusTextBlock.Text = "Config saved";
    }

    private async void FetchButton_OnClick(object sender, RoutedEventArgs e)
    {
        await FetchNowPlayingAsync();
    }

    private async Task FetchNowPlayingAsync()
    {
        if (_isFetching)
        {
            return;
        }

        _isFetching = true;
        FetchButton.IsEnabled = false;
        StatusTextBlock.Text = "Fetching...";

        try
        {
            _config.ServerUrl = ServerUrlTextBox.Text.Trim();
            _config.ApiKey = ApiKeyTextBox.Text.Trim();
            _config.UserId = UserIdTextBox.Text.Trim();

            var session = await FetchActiveSessionAsync(_config);
            var nowPlaying = session?.NowPlayingItem;
            _activeSessionId = session?.Id;
            _isPaused = session?.PlayState?.IsPaused ?? false;
            UpdatePlayPauseIcon();

            if (nowPlaying is null)
            {
                TitleTextBlock.Text = "(no active playback)";
                ArtistTextBlock.Text = string.Empty;
                AlbumTextBlock.Text = string.Empty;
                AlbumArtBrush.ImageSource = null;
                _currentRunTimeTicks = 0;
                _currentPositionTicks = 0;
                UpdateProgressRing(0);
                UpdateTrayNowPlayingText("RoundSound Mimic", string.Empty);
                StatusTextBlock.Text = "No active session";
                return;
            }

            var title = nowPlaying.Name ?? "(unknown title)";
            var artists = nowPlaying.Artists?.Count > 0
                ? string.Join(", ", nowPlaying.Artists)
                : "(unknown artist)";
            var album = string.IsNullOrWhiteSpace(nowPlaying.Album)
                ? "(unknown album)"
                : nowPlaying.Album;

            TitleTextBlock.Text = title.ToUpperInvariant();
            ArtistTextBlock.Text = artists.ToUpperInvariant();
            AlbumTextBlock.Text = album;
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
            UpdateProgressRing(GetProgress(session));
            StatusTextBlock.Text = "Updated";
        }
        catch (Exception ex)
        {
            TitleTextBlock.Text = "(error)";
            ArtistTextBlock.Text = string.Empty;
            AlbumTextBlock.Text = string.Empty;
            AlbumArtBrush.ImageSource = null;
            _currentRunTimeTicks = 0;
            _currentPositionTicks = 0;
            UpdateProgressRing(0);
            UpdateTrayNowPlayingText("RoundSound Mimic", string.Empty);
            StatusTextBlock.Text = ex.Message;
        }
        finally
        {
            FetchButton.IsEnabled = true;
            _isFetching = false;
        }
    }

    private async void PollTimerOnTick(object? sender, EventArgs e)
    {
        await FetchNowPlayingAsync();
    }

    private async void PrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SendPlaybackCommandAsync("PreviousTrack");
        await RefreshAfterCommandAsync();
    }

    private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SendPlaybackCommandAsync("PlayPause");
        await RefreshAfterCommandAsync();
    }

    private void Window_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_menuPopup is null)
        {
            return;
        }

        _menuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void OpenConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_configPopup is null)
        {
            return;
        }

        _configPopup.IsOpen = true;
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        Hide();
    }

    private void Window_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractiveElement(source))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private static bool IsInteractiveElement(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ButtonBase or TextBoxBase or Popup)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void UpdatePlayPauseIcon()
    {
        if (PlayPauseButton is null)
        {
            return;
        }

        var iconKey = _isPaused ? "PlayIcon" : "PauseIcon";
        if (TryFindResource(iconKey) is not Geometry geometry)
        {
            return;
        }

        PlayPauseButton.Content = new Viewbox
        {
            Width = 20,
            Height = 20,
            Child = new Path
            {
                Data = geometry,
                Fill = Brushes.White
            }
        };
    }

    private async void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SendPlaybackCommandAsync("NextTrack");
        await RefreshAfterCommandAsync();
    }

    private static async Task<JellyfinSession?> FetchActiveSessionAsync(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            throw new InvalidOperationException("Server URL is required.");
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("API key is required.");
        }

        var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
        var url = $"{baseUrl}/Sessions?ActiveWithinSeconds=120";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Emby-Token", config.ApiKey);

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var sessions = JsonSerializer.Deserialize<List<JellyfinSession>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<JellyfinSession>();

        var session = sessions.FirstOrDefault(s =>
            s.NowPlayingItem is not null &&
            (string.IsNullOrWhiteSpace(config.UserId) ||
             string.Equals(s.UserId, config.UserId, StringComparison.OrdinalIgnoreCase)));

        return session;
    }

    private async Task LoadAlbumArtAsync(AppConfig config, JellyfinNowPlayingItem item)
    {
        var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
        var candidateRequests = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            var tag = string.IsNullOrWhiteSpace(item.PrimaryImageTag)
                ? string.Empty
                : $"&tag={Uri.EscapeDataString(item.PrimaryImageTag)}";
            candidateRequests.Add($"{baseUrl}/Items/{item.Id}/Images/Primary?maxWidth=600&maxHeight=600&quality=100{tag}");
        }

        if (!string.IsNullOrWhiteSpace(item.AlbumId))
        {
            var tag = string.IsNullOrWhiteSpace(item.AlbumPrimaryImageTag)
                ? string.Empty
                : $"&tag={Uri.EscapeDataString(item.AlbumPrimaryImageTag)}";
            candidateRequests.Add($"{baseUrl}/Items/{item.AlbumId}/Images/Primary?maxWidth=600&maxHeight=600&quality=100{tag}");
        }

        if (candidateRequests.Count == 0)
        {
            AlbumArtBrush.ImageSource = null;
            StatusTextBlock.Text = "No artwork id";
            return;
        }

        try
        {
            foreach (var url in candidateRequests)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Emby-Token", config.ApiKey);

                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var buffer = new System.IO.MemoryStream();
                await stream.CopyToAsync(buffer);
                var bytes = buffer.ToArray();

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 600;
                image.StreamSource = new System.IO.MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                AlbumArtBrush.ImageSource = image;
                UpdateTrayIcon(image);
                _lastArtworkKey = BuildArtworkKey(item);
                return;
            }

            AlbumArtBrush.ImageSource = null;
            StatusTextBlock.Text = "Artwork not available";
        }
        catch
        {
            AlbumArtBrush.ImageSource = null;
            StatusTextBlock.Text = "Artwork load failed";
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

    private void UpdateProgressRing(double progress)
    {
        UpdateProgressToolTip();
        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped <= 0.001)
        {
            ProgressRing.StrokeDashArray = new DoubleCollection { 0, 1 };
            ProgressRing.StrokeDashOffset = 0;
            return;
        }

        var radius = 300.0;
        var circumference = 2 * Math.PI * radius;
        var filled = circumference * clamped;
        var empty = circumference - filled;

        var thickness = ProgressRing.StrokeThickness;
        if (thickness <= 0)
        {
            thickness = 1;
        }

        ProgressRing.StrokeDashArray = new DoubleCollection
        {
            filled / thickness,
            empty / thickness
        };
        ProgressRing.StrokeDashOffset = 0;
    }

    private void UpdateProgressToolTip()
    {
        if (_currentRunTimeTicks <= 0)
        {
            ProgressRingToolTip.Content = "No playback";
            RingBackgroundToolTip.Content = "No playback";
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
        ProgressRingToolTip.Content = tooltipText;
        RingBackgroundToolTip.Content = tooltipText;
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
        _config.ServerUrl = ServerUrlTextBox.Text.Trim();
        _config.ApiKey = ApiKeyTextBox.Text.Trim();
        _config.UserId = UserIdTextBox.Text.Trim();
        var sessionId = await EnsureActiveSessionIdAsync();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            StatusTextBlock.Text = "No active session";
            return;
        }

        var baseUrl = _config.ServerUrl.Trim().TrimEnd('/');
        var url = $"{baseUrl}/Sessions/{sessionId}/Playing/{command}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Emby-Token", _config.ApiKey);

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            StatusTextBlock.Text = $"Command failed: {(int)response.StatusCode}";
            return;
        }

        StatusTextBlock.Text = $"Command sent: {command}";
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
            StatusTextBlock.Text = ex.Message;
            return null;
        }
    }

    private async Task RefreshAfterCommandAsync()
    {
        await Task.Delay(300);
        await FetchNowPlayingAsync();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_configPopup is not null)
        {
            _configPopup.IsOpen = false;
        }
    }
}