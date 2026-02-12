using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NAudio.CoreAudioApi;

namespace RoundSoundMimic;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly RoundSoundMimic.Services.IJellyfinService _jellyfinService;
    private readonly RoundSoundMimic.Services.ITrayIconService _trayIconService;
    private string? _lastArtworkKey;
    private AppConfig _config = new();
    private string? _activeSessionId;
    private long _currentRunTimeTicks;
    private long _currentPositionTicks;
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
    private DoubleCollection? _progressDashArray;
    private string _progressToolTip = "No playback";
    private string _ringBackgroundToolTip = "No playback";
    private bool _isPlayPauseEnabled = true;
    private bool _isPrevEnabled = true;
    private bool _isNextEnabled = true;
    private bool _isPaused = false;
    private string _playCountText = "";
    private string _formatText = "";
    private Brush? _formatBrush;
    private int _volume = 50;
    private int _volumeBeforeMute = 50;
    private bool _isMuted = false;
    private bool _isUpdatingVolume = false;
    private DateTime _lastManualVolumeChangeUtc = DateTime.MinValue;

    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                OnPropertyChanged(nameof(VolumeToolTip));
                if (!_isUpdatingVolume)
                {
                    _lastManualVolumeChangeUtc = DateTime.UtcNow;
                    _ = SetVolumeAsync(value, _isMuted);
                }
                if (_isMuted && value > 0)
                {
                    IsMuted = false;
                }
            }
        }
    }

    public string VolumeToolTip => $"Volume: {Volume}%";

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public ICommand MuteCommand { get; }

    public string PlayCountText
    {
        get => _playCountText;
        set => SetProperty(ref _playCountText, value);
    }

    public string FormatText
    {
        get => _formatText;
        set => SetProperty(ref _formatText, value);
    }

    public Brush? FormatBrush
    {
        get => _formatBrush;
        set => SetProperty(ref _formatBrush, value);
    }

    private static bool IsLosslessFormat(string? format)
    {
        if (string.IsNullOrEmpty(format)) return false;
        var losslessFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FLAC", "WAV", "ALAC", "APE", "WV", "TAK", "TTA", "AIFF", "DSD", "DSF", "DFF"
        };
        return losslessFormats.Contains(format);
    }

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

    public DoubleCollection? ProgressDashArray
    {
        get => _progressDashArray;
        set => SetProperty(ref _progressDashArray, value);
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

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    // Helper to update the view element when using code-behind view
    private void UpdateFormatTextBlock(string text)
    {
        // Try to update the view control if present (MainWindow uses code-behind)
        try
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
            {
                mw.Dispatcher.Invoke(() =>
                {
                    if (mw.FindName("FormatTextBlock") is TextBlock tb)
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

    public MainViewModel(RoundSoundMimic.Services.IJellyfinService jellyfinService, RoundSoundMimic.Services.ITrayIconService trayIconService)
    {
        _jellyfinService = jellyfinService ?? throw new ArgumentNullException(nameof(jellyfinService));
        _trayIconService = trayIconService ?? throw new ArgumentNullException(nameof(trayIconService));

        PrevCommand = new RelayCommand(PrevExecute);
        PlayPauseCommand = new RelayCommand(PlayPauseExecute);
        NextCommand = new RelayCommand(NextExecute);
        OpenConfigCommand = new RelayCommand(OpenConfigExecute);
        MinimizeCommand = new RelayCommand(MinimizeExecute);
        ExitCommand = new RelayCommand(ExitExecute);
        FetchCommand = new RelayCommand(FetchExecute);
        SaveConfigCommand = new RelayCommand(SaveConfigExecute);
        CloseConfigCommand = new RelayCommand(CloseConfigExecute);
        MuteCommand = new RelayCommand(MuteExecute);
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
    public async void PrevExecute()
    {
        await SendPlaybackCommandAsync("PreviousTrack");
        await RefreshAfterCommandAsync();
    }

    public async void PlayPauseExecute()
    {
        await SendPlaybackCommandAsync("PlayPause");
        await RefreshAfterCommandAsync();
    }

    public async void NextExecute()
    {
        await SendPlaybackCommandAsync("NextTrack");
        await RefreshAfterCommandAsync();
    }

    public void MuteExecute()
    {
        _lastManualVolumeChangeUtc = DateTime.UtcNow;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device != null)
            {
                var volumeControl = device.AudioEndpointVolume;
                volumeControl.Mute = !IsMuted;
                IsMuted = volumeControl.Mute;
                
                // Update volume display if unmuting
                if (!IsMuted)
                {
                    var currentVolume = (int)(volumeControl.MasterVolumeLevelScalar * 100);
                    _isUpdatingVolume = true;
                    Volume = currentVolume;
                    _isUpdatingVolume = false;
                }
                else
                {
                    _volumeBeforeMute = Volume;
                    _isUpdatingVolume = true;
                    Volume = 0;
                    _isUpdatingVolume = false;
                }
                
                OnPropertyChanged(nameof(Volume));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to toggle mute: {ex.Message}");
        }
    }

    public void OpenConfigExecute()
    {
        // This needs to be handled in the View, as it opens a popup
        // For now, we'll assume the View handles it via event
    }

    public void MinimizeExecute()
    {
        System.Windows.Application.Current.MainWindow.WindowState = WindowState.Minimized;
        System.Windows.Application.Current.MainWindow.Hide();
    }

    public void ExitExecute()
    {
        try
        {
            StartupLogger.Log("MainViewModel.ExitExecute: calling Shutdown");
        }
        catch { }
        System.Windows.Application.Current.Shutdown();
    }

    public async void FetchExecute()
    {
        await FetchNowPlayingAsync();
    }

    public async Task SaveConfigAsync()
    {
        _config.ServerUrl = ServerUrlText.Trim();
        _config.ApiKey = ApiKeyText.Trim();
        _config.UserId = UserIdText.Trim();

        await _config.SaveAsync();
        StatusText = "Config saved";
    }
    
    private async void SaveConfigExecute()
    {
        await SaveConfigAsync();
    }

    public void CloseConfigExecute()
    {
        // This needs to be handled in the View, as it closes a popup
        // For now, we'll just leave this as a placeholder
        // The actual closing of the popup will be handled in the view
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

    private async Task FetchNowPlayingAsync(bool isManualAction = false)
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
            IsPaused = session?.PlayState?.IsPaused ?? false;
            UpdateVolumeFromSession(session);
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
                PlayCountText = "";
                FormatText = "";
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

            TitleText = title.ToUpperInvariant();
            ArtistText = artists.ToUpperInvariant();
            AlbumText = album;
            _lastPlaybackSeenUtc = DateTime.UtcNow;
            UpdateTrayNowPlayingText(title, artists);
            _currentRunTimeTicks = nowPlaying.RunTimeTicks ?? 0;
            _currentPositionTicks = session?.PlayState?.PositionTicks ?? 0;
            var artworkKey = BuildArtworkKey(nowPlaying);
            var showBalloon = !string.Equals(artworkKey, _lastArtworkKey, StringComparison.Ordinal);
            await LoadAlbumArtAsync(_config, nowPlaying);
            if (!string.IsNullOrWhiteSpace(nowPlaying.Id))
            {
                var itemWithData = await _jellyfinService.FetchItemWithUserDataAsync(_config, nowPlaying.Id);
                if (itemWithData?.UserData?.PlayCount is int playCount)
                {
                    PlayCountText = $"Played {playCount} time{(playCount == 1 ? "" : "s")}";
                }
                else
                {
                    PlayCountText = "";
                }
            }
            else
            {
                PlayCountText = "";
            }
            FormatText = nowPlaying.MediaStreams?.FirstOrDefault(s => s.Type == "Audio")?.Codec?.ToUpperInvariant() ?? nowPlaying.Container?.ToUpperInvariant() ?? "";
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
            PlayCountText = "";
            FormatText = "";
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


    private void UpdatePlayPauseIcon()
    {
        var iconKey = IsPaused ? "PlayIcon" : "PauseIcon";
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
        
        // Total length of the ellipse stroke path is 2 * PI * radius
        // For a 600x600 ellipse, the center of the 18-unit stroke is at radius 300
        // Circumference = 2 * PI * 300 = 1884.955
        // StrokeDashArray values are relative to StrokeThickness (18)
        // Full circle in dash units = 1884.955 / 18 = 104.72
        const double totalDashUnits = 104.719755; 
        
        var filledUnits = clamped * totalDashUnits;
        var emptyUnits = totalDashUnits - filledUnits;

        // Update DashArray for the progress ring
        ProgressDashArray = new DoubleCollection { filledUnits, emptyUnits > 0 ? emptyUnits : 0 };
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

        ProgressToolTip = $"Remaining: {FormatTime(remaining)}";
        RingBackgroundToolTip = $"Total: {FormatTime(total)}";
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

    // Tray icon methods - delegate to service
    private void UpdateTrayIcon(ImageSource source)
    {
        _trayIconService.UpdateTrayIcon(source);
    }

    private void ShowTrayBalloon(string title, string artists)
    {
        _trayIconService.ShowTrayBalloon(title, artists);
    }

    private void UpdateTrayNowPlayingText(string title, string artists)
    {
        _trayIconService.UpdateTrayNowPlayingText(title, artists);
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

    private async Task SetVolumeAsync(int volume, bool isMuted)
    {
        await Task.Run(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device != null)
                {
                    var volumeControl = device.AudioEndpointVolume;
                    volumeControl.MasterVolumeLevelScalar = volume / 100.0f;
                    volumeControl.Mute = isMuted;
                }
            }
            catch (Exception ex)
            {
                // Log error but don't show to user since this is background operation
                System.Diagnostics.Debug.WriteLine($"Failed to set system volume: {ex.Message}");
            }
        });
    }

    internal void UpdateVolumeFromSession(JellyfinSession? session)
    {
        // Update from system volume
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device != null)
            {
                var volumeControl = device.AudioEndpointVolume;
                var currentVolume = (int)(volumeControl.MasterVolumeLevelScalar * 100);
                var currentMuted = volumeControl.Mute;

                if (DateTime.UtcNow - _lastManualVolumeChangeUtc > TimeSpan.FromSeconds(2))
                {
                    _isUpdatingVolume = true;
                    Volume = currentVolume;
                    _isUpdatingVolume = false;
                    IsMuted = currentMuted;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to get system volume: {ex.Message}");
        }
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
            IsPaused = session?.PlayState?.IsPaused ?? IsPaused;
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
        await FetchNowPlayingAsync(isManualAction: true);
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
            var dbgPath2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoundSoundMimic_startup_debug.log");
            System.IO.File.AppendAllText(dbgPath2, DateTime.UtcNow.ToString("o") + " MainViewModel.OnLoaded: complete\n");
        }
        catch { }
    }

    public void OnClosed()
    {
        // Audio capture and tray icon are handled by MainWindow
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