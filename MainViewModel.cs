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
    private string? _lastNotifiedTrackKey;
    private AppConfig _config = new();
    private string? _activeSessionId;
    private long _currentRunTimeTicks;
    private long _currentPositionTicks;
    private bool _isFetching;
    // Set when a manual (button-triggered) fetch arrives while another fetch is
    // already in flight, so the manual refresh is replayed instead of dropped.
    private bool _pendingManualFetch;
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
    private string? _currentItemId;
    private bool _isFavorite = false;
    private double _seekPercent = 0;
    private bool _isUpdatingSeek = false;
    private DateTime _lastManualSeekUtc = DateTime.MinValue;
    private int _seekRequestGeneration = 0;

    // Local playback-position tracking. While a track plays we avoid polling the
    // server every second; instead a lightweight timer extrapolates the position
    // from wall-clock time and only issues a network request at a track boundary
    // (song finished) or when the user takes a manual action (Next/Prev/Seek).
    private DispatcherTimer? _playbackTimer;
    private bool _isTrackingPlayback = false;
    private long _localRunTimeTicks = 0;
    private long _localPositionTicks = 0;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    // While set, the server is expected to report a track change imminently (a song
    // just finished naturally); the timer fetches quickly instead of dropping to the
    // slow idle heartbeat, so the next song's cover/title appear without delay.
    private bool _isAwaitingTrackTransition;
    private DateTime _trackTransitionStartedUtc = DateTime.MinValue;
    // The item that was playing when the transition retry was armed; the retry is
    // resolved once the server reports a different item (the next song).
    private string? _transitionFromItemId;

    /// <summary>Interval used for the idle heartbeat (nothing currently playing).</summary>
    private const double IdlePollIntervalSeconds = 30;
    /// <summary>Debounce window for scrubbing the seek bar before sending a seek request.</summary>
    private const int SeekDebounceMs = 350;
    /// <summary>
    /// How often (while playing) the server session metadata is revalidated to make
    /// sure the displayed cover/title/artist still match what is really playing.
    /// </summary>
    private const int RevalidateIntervalSeconds = 15;

    /// <summary>Poll cadence used after Next/Prev while waiting for the target client to switch songs.</summary>
    private const int PostCommandRefreshPollMs = 200;
    /// <summary>Upper bound for the post-command wait before falling back to a full refresh.</summary>
    private const int PostCommandRefreshTimeoutMs = 2000;

    /// <summary>
    /// Retry cadence while waiting for the server to report the next track after a
    /// song finishes naturally. The client's own report of the switch lags the local
    /// extrapolation by a moment, so the first boundary fetch usually still sees the
    /// old track (or a transient null gap) and must be retried quickly.
    /// </summary>
    private const double TrackTransitionPollSeconds = 2;
    /// <summary>How long the quick transition retry runs before reverting to the idle heartbeat.</summary>
    private const double TrackTransitionTimeoutSeconds = 30;

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

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    public ICommand FavoriteCommand { get; }

    /// <summary>
    /// User-accessible seek position as a percentage (0-100).
    /// Dragging the slider updates this property and triggers a seek command;
    /// polling updates it (guarded by _isUpdatingSeek) to reflect server position.
    /// </summary>
    public double SeekPercent
    {
        get => _seekPercent;
        set
        {
            if (SetProperty(ref _seekPercent, value))
            {
                OnPropertyChanged(nameof(SeekToolTip));
                if (!_isUpdatingSeek)
                {
                    _lastManualSeekUtc = DateTime.UtcNow;
                    _ = DebouncedSeekAsync(value);
                }
            }
        }
    }

    public string SeekToolTip
    {
        get
        {
            if (_currentRunTimeTicks <= 0) return "No playback";
            var posTicks = (long)(Math.Clamp(_seekPercent, 0, 100) / 100.0 * _currentRunTimeTicks);
            var pos = TimeSpan.FromTicks(posTicks);
            var total = TimeSpan.FromTicks(_currentRunTimeTicks);
            return $"{FormatTime(pos)} / {FormatTime(total)}";
        }
    }

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
        FavoriteCommand = new RelayCommand(ToggleFavoriteExecute);
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

    public async void ToggleFavoriteExecute()
    {
        if (string.IsNullOrWhiteSpace(_currentItemId))
        {
            StatusText = "No track to favorite";
            return;
        }

        _config.ServerUrl = ServerUrlText.Trim();
        _config.ApiKey = ApiKeyText.Trim();
        _config.UserId = UserIdText.Trim();

        var newValue = !IsFavorite;
        var ok = await _jellyfinService.SetFavoriteAsync(_config, _currentItemId, newValue);
        if (ok)
        {
            IsFavorite = newValue;
            StatusText = newValue ? "Added to favorites" : "Removed from favorites";
        }
        else
        {
            StatusText = "Favorite update failed";
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
        // Manual refresh from the (debug) fetch button: skip the grace window.
        await FetchNowPlayingAsync(isManualAction: true);
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
            // A fetch is already running. A button-triggered refresh must not be
            // silently dropped (otherwise pressing Next during a revalidation
            // leaves the UI stale until the next periodic check); remember it and
            // replay when the in-flight fetch completes.
            if (isManualAction)
            {
                _pendingManualFetch = true;
            }
            return;
        }

        _isFetching = true;
        _lastFetchUtc = DateTime.UtcNow;
        IsPlayPauseEnabled = false;
        StatusText = "Fetching...";

        try
        {
            await FetchNowPlayingCoreAsync(isManualAction);
        }
        finally
        {
            _isFetching = false;
            IsPlayPauseEnabled = true;

            // Replay a manual refresh that arrived while this fetch was running
            // (guard against unbounded loops: at most one replay per completion).
            if (_pendingManualFetch && !_isFetching)
            {
                _pendingManualFetch = false;
                _ = FetchNowPlayingAsync(isManualAction: true);
            }
        }
    }

    private async Task FetchNowPlayingCoreAsync(bool isManualAction)
    {
        try
        {
            _config.ServerUrl = ServerUrlText.Trim();
            _config.ApiKey = ApiKeyText.Trim();
            _config.UserId = UserIdText.Trim();

            var session = await FetchActiveSessionAsync(_config);
            var nowPlaying = session?.NowPlayingItem;
            // If a natural track-end retry is pending, resolve it once the server
            // reports a different item than the one that was playing before.
            ResolveTrackTransitionIfChanged(nowPlaying?.Id);
            _currentItemId = nowPlaying?.Id;
            _activeSessionId = session?.Id;
            IsPaused = session?.PlayState?.IsPaused ?? false;
            UpdateVolumeFromSession(session);
            UpdatePlayPauseIcon();

            if (nowPlaying is null)
            {
                var now = DateTime.UtcNow;
                // If we have seen playback very recently, this may be a brief gap
                // (for example when skipping tracks). Don't clear the UI immediately.
                // A user-initiated refresh (Next/Prev/Play/Pause) skips the grace
                // window so the UI updates to the new state right away.
                if (!isManualAction && now - _lastPlaybackSeenUtc < TimeSpan.FromSeconds(6))
                {
                    StatusText = "Waiting for update...";
                    return;
                }

                TitleText = "(no active playback)";
                ArtistText = string.Empty;
                AlbumText = string.Empty;
                AlbumArtSource = null;
                _currentItemId = null;
                IsFavorite = false;
                _currentRunTimeTicks = 0;
                _currentPositionTicks = 0;
                _localRunTimeTicks = 0;
                _localPositionTicks = 0;
                _isTrackingPlayback = false;
                UpdateSeekFromPosition(0, 0);
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
            // Re-arm local position tracking. Only track when there is remaining
            // time on a known-duration item, so an already-finished/stalled item
            // doesn't trigger a hot-loop of boundary fetches.
            _localRunTimeTicks = _currentRunTimeTicks;
            _localPositionTicks = Math.Min(_currentPositionTicks, Math.Max(0, _currentRunTimeTicks));
            _isTrackingPlayback = _localRunTimeTicks > 0 && _localPositionTicks < _localRunTimeTicks;
            if (_isTrackingPlayback)
            {
                _lastTickUtc = DateTime.UtcNow;
            }
            _currentPositionTicks = _localPositionTicks;
            var artworkKey = BuildArtworkKey(nowPlaying);
            var showBalloon = !string.Equals(artworkKey, _lastNotifiedTrackKey, StringComparison.Ordinal);

            // Artwork download and user-data (play count / favorite) are independent
            // of each other: run them concurrently instead of serially so the cover
            // and favorite state arrive in one round-trip time, not two.
            var artworkTask = LoadAlbumArtAsync(_config, nowPlaying);
            var userDataTask = string.IsNullOrWhiteSpace(nowPlaying.Id)
                ? Task.CompletedTask
                : _jellyfinService.FetchItemWithUserDataAsync(_config, nowPlaying.Id);
            await Task.WhenAll(artworkTask, userDataTask).ConfigureAwait(true);

            if (userDataTask is Task<JellyfinNowPlayingItem?> completedUserData)
            {
                var itemWithData = await completedUserData.ConfigureAwait(true);
                if (itemWithData?.UserData is JellyfinUserData userData)
                {
                    if (userData.PlayCount is int playCount)
                    {
                        PlayCountText = $"Played {playCount} time{(playCount == 1 ? "" : "s")}";
                    }
                    else
                    {
                        PlayCountText = "";
                    }
                    IsFavorite = userData.IsFavorite ?? false;
                }
            }
            else
            {
                PlayCountText = "";
                IsFavorite = false;
            }
            FormatText = nowPlaying.MediaStreams?.FirstOrDefault(s => s.Type == "Audio")?.Codec?.ToUpperInvariant() ?? nowPlaying.Container?.ToUpperInvariant() ?? "";
            if (showBalloon)
            {
                ShowTrayBalloon(title, artists);
                _lastNotifiedTrackKey = artworkKey;
            }
            Progress = GetProgress(session);
            UpdateProgressRing();
            UpdateSeekFromPosition(_currentPositionTicks, _currentRunTimeTicks);
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
    }

    /// <summary>
    /// Cheap revalidation while a track plays: fetches session metadata only (no
    /// artwork / user-data round trips) and checks whether the item the server
    /// reports still matches what the UI is displaying. Playback can change tracks
    /// inside the Jellyfin client itself; without this check the widget keeps
    /// showing the previous song's cover, title and artist until its own local
    /// extrapolation hits the track boundary. A changed item triggers a full
    /// refresh; a matching item only corrects local position drift (e.g. after a
    /// seek done in the client or system sleep).
    /// </summary>
    private async Task RevalidateNowPlayingAsync()
    {
        try
        {
            _config.ServerUrl = ServerUrlText.Trim();
            _config.ApiKey = ApiKeyText.Trim();
            _config.UserId = UserIdText.Trim();

            var session = await FetchActiveSessionAsync(_config).ConfigureAwait(true);
            var nowPlaying = session?.NowPlayingItem;
            if (nowPlaying is null)
            {
                // Playback may have just ended externally; let the normal fetch
                // path (with its grace window) clear the UI.
                await FetchNowPlayingAsync();
                return;
            }

            var serverItemId = nowPlaying.Id;
            if (!string.IsNullOrWhiteSpace(serverItemId) &&
                !string.Equals(serverItemId, _currentItemId, StringComparison.OrdinalIgnoreCase))
            {
                // The server is playing a different song than the UI shows -> full
                // refresh so cover, title, artist, album, favorite etc. match.
                await FetchNowPlayingAsync(isManualAction: true);
                return;
            }

            // Same item: correct local position drift only. Skip while the user is
            // scrubbing so the thumb doesn't fight them.
            if (DateTime.UtcNow - _lastManualSeekUtc >= TimeSpan.FromSeconds(2))
            {
                _currentRunTimeTicks = nowPlaying.RunTimeTicks ?? _currentRunTimeTicks;
                _localRunTimeTicks = _currentRunTimeTicks;
                _localPositionTicks = Math.Min(session?.PlayState?.PositionTicks ?? _localPositionTicks,
                    Math.Max(0, _localRunTimeTicks));
                _currentPositionTicks = _localPositionTicks;
                if (_localRunTimeTicks > 0)
                {
                    _isTrackingPlayback = _localPositionTicks < _localRunTimeTicks;
                }
                _lastTickUtc = DateTime.UtcNow;
                SyncUiFromLocalPosition();
            }
        }
        catch (Exception ex)
        {
            TryLog("RevalidateNowPlayingAsync", ex);
        }
    }

    /// <summary>
    /// Cheap session-metadata check used after transport commands so a changed item
    /// or play state is noticed without the full artwork / user-data pipeline.
    /// Returns the session the server currently reports, or null when nothing plays.
    /// </summary>
    private async Task<JellyfinSession?> PeekSessionSnapshotAsync()
    {
        try
        {
            return await FetchActiveSessionAsync(_config).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TryLog("PeekSessionSnapshotAsync", ex);
            return null;
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
        var bytes = await _jellyfinService.GetArtworkBytesAsync(config, item).ConfigureAwait(true);
        if (bytes is null || bytes.Length == 0)
        {
            // Keep the previous cover when the new one can't be fetched. Blanking
            // the art here would leave the widget showing a new title with the
            // previous (or no) cover, which reads as a cover/title mismatch.
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
        }
        catch
        {
            // Same as above: a decode failure shouldn't blank the current cover.
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

    /// <summary>
    /// Reflects the server-side playback position on the seek slider, without
    /// re-triggering a seek. Suppresses updates shortly after a manual seek so the
    /// thumb doesn't fight the user while they are scrubbing.
    /// </summary>
    private void UpdateSeekFromPosition(long positionTicks, long runTimeTicks)
    {
        // Don't overwrite the thumb while the user is actively scrubbing.
        if (DateTime.UtcNow - _lastManualSeekUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var percent = runTimeTicks > 0 ? Math.Clamp(positionTicks / (double)runTimeTicks * 100.0, 0, 100) : 0.0;
        _isUpdatingSeek = true;
        SeekPercent = percent;
        _isUpdatingSeek = false;
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

    private async Task DebouncedSeekAsync(double percent)
    {
        // Debounce: while the user is scrubbing, later drag values supersede earlier
        // ones. Only the most recent value that has "settled" for the debounce window
        // actually triggers a seek request, avoiding a flood of server calls.
        var generation = ++_seekRequestGeneration;
        await Task.Delay(SeekDebounceMs);
        if (generation != _seekRequestGeneration)
        {
            // A newer scrub value arrived; drop this stale request.
            return;
        }

        await DoSeekAsync(percent);
    }

    private async Task DoSeekAsync(double percent)
    {
        if (_currentRunTimeTicks <= 0)
        {
            return;
        }

        _config.ServerUrl = ServerUrlText.Trim();
        _config.ApiKey = ApiKeyText.Trim();
        _config.UserId = UserIdText.Trim();
        var sessionId = await EnsureActiveSessionIdAsync();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            StatusText = "No active session";
            return;
        }

        var ticks = (long)(Math.Clamp(percent, 0, 100) / 100.0 * _currentRunTimeTicks);
        var ok = await _jellyfinService.SeekAsync(_config, sessionId, ticks);
        if (ok)
        {
            StatusText = "Seeked";

            // Re-seed local position tracking from the seek target so the ring,
            // slider and extrapolated position reflect the new spot immediately
            // instead of continuing from the stale pre-seek position.
            _localPositionTicks = ticks;
            _localRunTimeTicks = _currentRunTimeTicks;
            _lastTickUtc = DateTime.UtcNow;
            _isTrackingPlayback = _localRunTimeTicks > 0 && _localPositionTicks < _localRunTimeTicks;
            _currentPositionTicks = _localPositionTicks;
            SyncUiFromLocalPosition();
        }
        else
        {
            StatusText = "Seek failed";
        }
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

    /// <summary>
    /// Refreshes the UI after a transport command (Next/Prev/PlayPause). The
    /// previous fixed 300 ms sleep raced the target client: if the client hadn't
    /// advanced yet, the fetch returned the OLD track and the cover/title stayed
    /// stale until the next periodic revalidation. Instead, poll the cheap session
    /// endpoint until the server actually reflects the command — bounded by
    /// PostCommandRefreshTimeoutMs — and only then do the full refresh.
    /// Signals checked: track changed, play state flipped, session disappeared,
    /// or (for Prev's "restart current track" behavior) position jumped backwards.
    /// </summary>
    private async Task RefreshAfterCommandAsync()
    {
        var previousItemId = _currentItemId;
        var previousIsPaused = IsPaused;
        var previousPositionTicks = _localPositionTicks;
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(PostCommandRefreshTimeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PostCommandRefreshPollMs).ConfigureAwait(true);

            var session = await PeekSessionSnapshotAsync().ConfigureAwait(true);
            var serverItemId = session?.NowPlayingItem?.Id;
            var serverIsPaused = session?.PlayState?.IsPaused;
            var serverPositionTicks = session?.PlayState?.PositionTicks;

            // Session gone (playback stopped), the target client switched songs,
            // play state flipped, or (Prev restart case) position jumped back
            // substantially -> the command took effect, do the full refresh.
            if (session is null ||
                serverIsPaused is null ||
                (!string.IsNullOrWhiteSpace(serverItemId) &&
                 !string.Equals(serverItemId, previousItemId, StringComparison.OrdinalIgnoreCase)) ||
                (serverIsPaused.HasValue && serverIsPaused.Value != previousIsPaused) ||
                // Prev on many Jellyfin clients restarts the current song instead of
                // going to the previous item; that shows up as position rewinding,
                // not as a new item. A >2 s backwards jump means "it happened".
                (serverPositionTicks.HasValue && previousPositionTicks > 0 &&
                 serverPositionTicks.Value < previousPositionTicks - TimeSpan.FromSeconds(2).Ticks))
            {
                break;
            }
        }

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

        StartPlaybackTimer();

        try
        {
            var dbgPath2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoundSoundMimic_startup_debug.log");
            System.IO.File.AppendAllText(dbgPath2, DateTime.UtcNow.ToString("o") + " MainViewModel.OnLoaded: complete\n");
        }
        catch { }
    }

    public void OnClosed()
    {
        StopPlaybackTimer();
    }

    private void StartPlaybackTimer()
    {
        if (_playbackTimer is not null)
        {
            return;
        }

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _playbackTimer.Start();
    }

    private void StopPlaybackTimer()
    {
        _playbackTimer?.Stop();
        _playbackTimer = null;
    }

    /// <summary>
    /// Lightweight, network-free tick. While a track is playing we extrapolate its
    /// position locally from the wall clock. Only at a track boundary do we reach
    /// out to the server (once) to pick up the next song or detect playback end.
    /// When idle we fall back to a slow heartbeat to notice externally-started playback.
    /// While playing we also periodically revalidate the server-reported item so the
    /// UI matches reality if tracks change inside the Jellyfin client itself.
    /// </summary>
    private async void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (_isTrackingPlayback)
        {
            if (IsPaused)
            {
                // Keep the baseline fresh so we don't jump on resume.
                _lastTickUtc = DateTime.UtcNow;
                return;
            }

            var now = DateTime.UtcNow;
            var elapsedSeconds = (now - _lastTickUtc).TotalSeconds;
            _lastTickUtc = now;
            if (elapsedSeconds > 0)
            {
                _localPositionTicks += (long)(elapsedSeconds * 10_000_000);
            }

            if (_localRunTimeTicks > 0 && _localPositionTicks >= _localRunTimeTicks)
            {
                // Track finished -> fetch once to advance to the next song. The
                // client's own report of the switch lags this moment, so arm the
                // quick retry loop: without it the single boundary fetch still
                // returned the old song (or a null gap) and the next song's cover/
                // title only appeared after the 30 s idle heartbeat kicked in.
                _localPositionTicks = _localRunTimeTicks;
                SyncUiFromLocalPosition();
                _isTrackingPlayback = false;
                ArmTrackTransitionRetry();
                await FetchNowPlayingAsync();
                return;
            }

            SyncUiFromLocalPosition();

            // Periodic revalidation: if playback changed tracks inside the Jellyfin
            // client (skip, playlist advance), the local extrapolation can't see it.
            // A cheap session-metadata check keeps cover/title/artist matching.
            if (now - _lastFetchUtc >= TimeSpan.FromSeconds(RevalidateIntervalSeconds))
            {
                await RevalidateNowPlayingAsync();
            }

            return;
        }

        // Playback has stopped or is between tracks. If a song just finished
        // naturally, the server's report of the next track is imminent: fetch on the
        // quick transition cadence so the new song's cover/title show up right away.
        // Otherwise fall back to the slow heartbeat that picks up playback started
        // outside this app.
        if (_isAwaitingTrackTransition)
        {
            var sinceTransition = DateTime.UtcNow - _trackTransitionStartedUtc;
            if (sinceTransition.TotalSeconds > TrackTransitionTimeoutSeconds)
            {
                // Never saw the next track within the timeout -> playback really
                // ended (or the client stalled); revert to the slow heartbeat.
                DisarmTrackTransitionRetry();
            }
            else if (DateTime.UtcNow - _lastFetchUtc >= TimeSpan.FromSeconds(TrackTransitionPollSeconds))
            {
                await FetchNowPlayingAsync();
            }
            return;
        }

        // Idle heartbeat: pick up playback started outside this app without a
        // busy 1s poll. This is a cooling period (30s) rather than a constant poll.
        if (DateTime.UtcNow - _lastFetchUtc >= TimeSpan.FromSeconds(IdlePollIntervalSeconds))
        {
            await FetchNowPlayingAsync();
        }
    }

    /// <summary>
    /// Marks that a song finished naturally and the server should report the next
    /// track imminently; the timer fetches on the quick transition cadence until it
    /// sees the new track, the timeout elapses, or playback resumes tracking.
    /// </summary>
    private void ArmTrackTransitionRetry()
    {
        if (_isAwaitingTrackTransition) return;
        _isAwaitingTrackTransition = true;
        _trackTransitionStartedUtc = DateTime.UtcNow;
        // Remember which song triggered the wait so the retry is resolved exactly
        // when the server starts reporting the NEXT one (not just any fetch).
        _transitionFromItemId = _currentItemId;
    }

    private void DisarmTrackTransitionRetry()
    {
        _isAwaitingTrackTransition = false;
        _trackTransitionStartedUtc = DateTime.MinValue;
        _transitionFromItemId = null;
    }

    /// <summary>
    /// Called from the fetch path: once the server reports an item that differs from
    /// the one that was playing when the transition retry armed, the wait is over.
    /// </summary>
    private void ResolveTrackTransitionIfChanged(string? serverItemId)
    {
        if (!_isAwaitingTrackTransition) return;
        if (!string.Equals(serverItemId, _transitionFromItemId, StringComparison.OrdinalIgnoreCase))
        {
            DisarmTrackTransitionRetry();
        }
    }

    private void SyncUiFromLocalPosition()
    {
        _currentPositionTicks = _localPositionTicks;
        Progress = _localRunTimeTicks > 0 ? Math.Clamp(_localPositionTicks / (double)_localRunTimeTicks, 0, 1) : 0;
        UpdateProgressRing();
        UpdateSeekFromPosition(_localPositionTicks, _localRunTimeTicks);
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