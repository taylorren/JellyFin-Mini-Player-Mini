using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RoundSoundMimic;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new();
    private Canvas? _drawingCanvas;
    private FrameworkElement? _innerCircle;
    private Path? _bandPath;
    private AppConfig _config = new();
    private string? _activeSessionId;
    private bool _isPaused;
    private readonly DispatcherTimer _pollTimer;
    private bool _isFetching;

    public MainWindow()
    {
        InitializeComponent();
        _drawingCanvas = FindName("DrawingCanvas") as Canvas;
        _innerCircle = FindName("InnerCircle") as FrameworkElement;
        _bandPath = FindName("BandPath") as Path;
        if (_drawingCanvas is not null)
        {
            _drawingCanvas.Loaded += (_, _) => UpdateBandGeometry();
            _drawingCanvas.SizeChanged += (_, _) => UpdateBandGeometry();
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
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigAsync();
        _pollTimer.Start();
        UpdateBandGeometry();
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
            PlayPauseButton.Content = _isPaused ? "▶" : "⏸";

            if (nowPlaying is null)
            {
                TitleTextBlock.Text = "(no active playback)";
                ArtistTextBlock.Text = string.Empty;
                AlbumTextBlock.Text = string.Empty;
                AlbumArtBrush.ImageSource = null;
                UpdateProgressRing(0);
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
            await LoadAlbumArtAsync(_config, nowPlaying);
            UpdateProgressRing(GetProgress(session));
            StatusTextBlock.Text = "Updated";
        }
        catch (Exception ex)
        {
            TitleTextBlock.Text = "(error)";
            ArtistTextBlock.Text = string.Empty;
            AlbumTextBlock.Text = string.Empty;
            AlbumArtBrush.ImageSource = null;
            UpdateProgressRing(0);
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
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 600;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                AlbumArtBrush.ImageSource = image;
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

    private async Task SendPlaybackCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId))
        {
            StatusTextBlock.Text = "No active session";
            return;
        }

        var baseUrl = _config.ServerUrl.Trim().TrimEnd('/');
        var url = $"{baseUrl}/Sessions/{_activeSessionId}/Playing/{command}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Emby-Token", _config.ApiKey);

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            StatusTextBlock.Text = $"Command failed: {(int)response.StatusCode}";
        }
    }

    private async Task RefreshAfterCommandAsync()
    {
        await Task.Delay(300);
        await FetchNowPlayingAsync();
    }
}