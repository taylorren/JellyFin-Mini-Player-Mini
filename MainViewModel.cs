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
    // Pending tray balloon. A track change schedules a balloon, but it is only
    // shown once the track has settled for BalloonSettleMs, so rapid Next/Prev
    // clicks collapse into a single notification for the track the user lands on.
    private DispatcherTimer? _balloonTimer;
    private string? _pendingBalloonKey;
    private string _pendingBalloonTitle = string.Empty;
    private string _pendingBalloonArtists = string.Empty;
    private DateTime _lastBalloonShownUtc = DateTime.MinValue;
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
    private DoubleCollection? _progressDashArray;
    private string _progressToolTip = "No playback";
    private string _ringBackgroundToolTip = "No playback";
    private bool _isPlayPauseEnabled = true;
    private bool _isPaused = false;
    private bool _hasPlayback = false;
    private string _playCountText = "";
    private string _formatText = "";
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

    // Timestamp of the session snapshot that produced the current IsPaused value.
    // The newest snapshot wins: a full refresh keeps working (artwork, user data) for
    // a second or more after reading its snapshot, and a probe read in the meantime
    // carries more current information about the play state.
    private DateTime _lastPlayStateReadUtc = DateTime.MinValue;

    // The playback timer drives a 1-second session probe. A LAN Jellyfin server is
    // cheap to query, so position/progress are read directly from it instead of
    // being extrapolated locally. _localRunTimeTicks/_localPositionTicks are kept
    // only to re-seed tracking right after a seek or transport command.
    private DispatcherTimer? _playbackTimer;
    private long _localRunTimeTicks = 0;
    private long _localPositionTicks = 0;

    /// <summary>Debounce window for scrubbing the seek bar before sending a seek request.</summary>
    private const int SeekDebounceMs = 350;

    /// <summary>
    /// Poll cadence used after a transport command while waiting for the target client
    /// to report the change. Only the (cheap) session endpoint is queried, and the
    /// loop exits as soon as the change is visible, so this only costs requests when
    /// the client is slow.
    /// </summary>
    private const int PostCommandRefreshPollMs = 200;
    /// <summary>
    /// Upper bound for the post-command wait before falling back to a full refresh.
    /// A client can take several seconds to report the new track (loading the next
    /// file, buffering), and giving up early handed the job to the 1-second probe,
    /// which made "Next" feel slow by roughly the polling interval.
    /// </summary>
    private const int PostCommandRefreshTimeoutMs = 4000;

    /// <summary>How long a track must stay current before its tray balloon is shown.</summary>
    private const int BalloonSettleMs = 1500;
    /// <summary>Minimum spacing between tray balloons (guards against bursts).</summary>
    private const int BalloonMinIntervalMs = 5000;

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

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
            }
        }
    }

    /// <summary>True while a track is loaded (drives the idle state).</summary>
    public bool HasPlayback
    {
        get => _hasPlayback;
        set
        {
            if (SetProperty(ref _hasPlayback, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
            }
        }
    }

    /// <summary>True while a track is playing (loaded and not paused); drives the vinyl spin.</summary>
    public bool IsPlaying => _hasPlayback && !_isPaused;

    /// <summary>
    /// Applies the paused flag carried by a session snapshot, but only when that
    /// snapshot is newer than the one the current value came from. A full refresh
    /// reads its snapshot and then spends time downloading artwork and user data; a
    /// probe (or a later refresh) that read the session after it is more current, so
    /// letting the older snapshot win would flip the icon back to Play and re-freeze
    /// the vinyl. A snapshot without the flag leaves the value untouched.
    /// </summary>
    private void ApplyPlayState(bool? isPaused, DateTime snapshotUtc)
    {
        if (isPaused is not bool paused || snapshotUtc < _lastPlayStateReadUtc)
        {
            return;
        }

        _lastPlayStateReadUtc = snapshotUtc;
        IsPaused = paused;
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
        await RefreshAfterCommandAsync(expectPlayStateChange: false);
    }

    public async void PlayPauseExecute()
    {
        await SendPlaybackCommandAsync("PlayPause");
        await RefreshAfterCommandAsync(expectPlayStateChange: true);
    }

    public async void NextExecute()
    {
        await SendPlaybackCommandAsync("NextTrack");
        await RefreshAfterCommandAsync(expectPlayStateChange: false);
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
        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null)
        {
            return;
        }

        window.WindowState = WindowState.Minimized;
        window.Hide();
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
            var sessionReadUtc = DateTime.UtcNow;
            var nowPlaying = session?.NowPlayingItem;
            _currentItemId = nowPlaying?.Id;
            _activeSessionId = session?.Id;
            // Only take the play state the server actually reported, and only when
            // this snapshot is the newest one we have seen. During a track-transition
            // gap the session can be missing (the client sent PlaybackStopped but has
            // not reported the next item yet); assuming "playing" there would flash
            // the Pause icon and restart the vinyl spin in the middle of a pause.
            ApplyPlayState(session?.PlayState?.IsPaused, sessionReadUtc);

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
                HasPlayback = false;
                // Idle is not "paused": clear the flag so the icon does not stay
                // on Play just because the last session happened to be paused.
                IsPaused = false;
                CancelPendingTrayBalloon();
                ArtistText = string.Empty;
                AlbumText = string.Empty;
                AlbumArtSource = null;
                _currentItemId = null;
                IsFavorite = false;
                _currentRunTimeTicks = 0;
                _currentPositionTicks = 0;
                _localRunTimeTicks = 0;
                _localPositionTicks = 0;
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
            HasPlayback = true;
            UpdateTrayNowPlayingText(title, artists);
            _currentRunTimeTicks = nowPlaying.RunTimeTicks ?? 0;
            _currentPositionTicks = session?.PlayState?.PositionTicks ?? 0;
            // Seed the local position used to re-seed tracking after a seek.
            _localRunTimeTicks = _currentRunTimeTicks;
            _localPositionTicks = Math.Min(_currentPositionTicks, Math.Max(0, _currentRunTimeTicks));
            _currentPositionTicks = _localPositionTicks;
            var artworkKey = BuildArtworkKey(nowPlaying);
            FormatText = nowPlaying.MediaStreams?.FirstOrDefault(s => s.Type == "Audio")?.Codec?.ToUpperInvariant() ?? nowPlaying.Container?.ToUpperInvariant() ?? "";

            // Everything the transport UI needs is already in hand, so publish the
            // ring, the seek bar and the play state BEFORE the artwork download and
            // the user-data round trip. Those can take a second or more (server-side
            // cover transcode, remote server) and gating the transport UI on them is
            // what made "Next" feel unresponsive after the track had already switched.
            Progress = GetProgress(session);
            UpdateProgressRing();
            UpdateSeekFromPosition(_currentPositionTicks, _currentRunTimeTicks);

            // The system-volume read goes through NAudio/COM endpoint enumeration and
            // only drives the volume slider, so it is started (never awaited) here,
            // after the track state above.
            UpdateVolumeFromSession(session);

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
            // Announce the track only once it has settled so a burst of Next/Prev
            // clicks (or session churn after a resume) cannot flood the tray.
            ScheduleTrayBalloon(artworkKey, title, artists);
            StatusText = "Updated";
        }
        catch (Exception ex)
        {
            TitleText = "(error)";
            HasPlayback = false;
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
    /// <summary>
    /// Queues a tray balloon for a newly-seen track. The balloon is only raised
    /// after the track has stayed current for <see cref="BalloonSettleMs"/> ms so a
    /// burst of Next/Prev clicks announces just the track the user settles on.
    /// </summary>
    private void ScheduleTrayBalloon(string artworkKey, string title, string artists)
    {
        if (string.Equals(artworkKey, _lastNotifiedTrackKey, StringComparison.Ordinal))
        {
            // Already announced this track.
            return;
        }

        // A newer track supersedes any pending balloon.
        _pendingBalloonKey = artworkKey;
        _pendingBalloonTitle = title;
        _pendingBalloonArtists = artists;

        if (_balloonTimer is null)
        {
            _balloonTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BalloonSettleMs) };
            _balloonTimer.Tick += (_, _) => FlushTrayBalloon();
        }

        _balloonTimer.Stop();
        _balloonTimer.Start();
    }

    private void CancelPendingTrayBalloon()
    {
        _pendingBalloonKey = null;
        _balloonTimer?.Stop();
    }

    private void FlushTrayBalloon()
    {
        _balloonTimer?.Stop();

        var key = _pendingBalloonKey;
        if (key is null)
        {
            return;
        }

        var title = _pendingBalloonTitle;
        var artists = _pendingBalloonArtists;
        _pendingBalloonKey = null;

        if (string.Equals(key, _lastNotifiedTrackKey, StringComparison.Ordinal))
        {
            return;
        }

        // Mark it seen either way so it is never announced later; only raise the
        // balloon if enough time has passed since the previous one (this is what
        // stops a post-sleep burst of track changes from stacking up toasts).
        _lastNotifiedTrackKey = key;
        if (DateTime.UtcNow - _lastBalloonShownUtc < TimeSpan.FromMilliseconds(BalloonMinIntervalMs))
        {
            return;
        }

        _lastBalloonShownUtc = DateTime.UtcNow;
        ShowTrayBalloon(title, artists);
    }

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

    /// <summary>
    /// Starts a refresh of the volume slider from the system volume. The read goes
    /// through NAudio/COM endpoint enumeration, which can stall for hundreds of
    /// milliseconds (and seconds on machines with a Bluetooth or disconnected
    /// endpoint), so it runs on a worker thread and applies its result on the UI
    /// thread. It never blocks the track/play-state pipeline.
    /// </summary>
    internal void UpdateVolumeFromSession(JellyfinSession? session)
    {
        _ = RefreshSystemVolumeAsync();
    }

    private async Task RefreshSystemVolumeAsync()
    {
        try
        {
            var (currentVolume, currentMuted) = await Task.Run(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var volumeControl = device.AudioEndpointVolume;
                return ((int)(volumeControl.MasterVolumeLevelScalar * 100), volumeControl.Mute);
            }).ConfigureAwait(true);

            if (DateTime.UtcNow - _lastManualVolumeChangeUtc > TimeSpan.FromSeconds(2))
            {
                _isUpdatingVolume = true;
                Volume = currentVolume;
                _isUpdatingVolume = false;
                IsMuted = currentMuted;
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
            ApplyPlayState(session?.PlayState?.IsPaused, DateTime.UtcNow);
            return _activeSessionId;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Refreshes the UI after a transport command (Next/Prev/PlayPause). The previous
    /// fixed 300 ms sleep raced the target client: if the client hadn't advanced yet,
    /// the fetch returned the OLD track and the cover/title stayed stale until the
    /// next periodic revalidation. Instead, poll the cheap session endpoint until the
    /// server actually reflects the command — bounded by PostCommandRefreshTimeoutMs —
    /// and only then do the full refresh. The first peek is immediate, so a client that
    /// reports quickly is not delayed by a poll interval.
    /// </summary>
    /// <param name="expectPlayStateChange">
    /// True for PlayPause, where a flipped paused flag is the evidence that the command
    /// landed. For Next/Prev a flipped flag is not evidence: clients briefly report the
    /// outgoing item as paused while they load the next file, and treating that as
    /// "done" refreshed the OLD track and left the widget on it until the next probe.
    /// </param>
    private async Task RefreshAfterCommandAsync(bool expectPlayStateChange)
    {
        var previousItemId = _currentItemId;
        var previousIsPaused = IsPaused;
        var previousPositionTicks = _localPositionTicks;
        var commandSentUtc = DateTime.UtcNow;
        var deadline = commandSentUtc + TimeSpan.FromMilliseconds(PostCommandRefreshTimeoutMs);
        var detectedUtc = DateTime.MinValue;

        while (true)
        {
            var session = await PeekSessionSnapshotAsync().ConfigureAwait(true);
            var serverItemId = session?.NowPlayingItem?.Id;
            var serverIsPaused = session?.PlayState?.IsPaused;
            var serverPositionTicks = session?.PlayState?.PositionTicks;

            var itemChanged = !string.IsNullOrWhiteSpace(serverItemId) &&
                              !string.Equals(serverItemId, previousItemId, StringComparison.OrdinalIgnoreCase);
            var playStateFlipped = serverIsPaused.HasValue && serverIsPaused.Value != previousIsPaused;
            // Prev on many Jellyfin clients restarts the current song instead of
            // going to the previous item; that shows up as position rewinding,
            // not as a new item. A >2 s backwards jump means "it happened".
            var positionRewound = serverPositionTicks.HasValue && previousPositionTicks > 0 &&
                                  serverPositionTicks.Value < previousPositionTicks - TimeSpan.FromSeconds(2).Ticks;

            // Session gone (playback stopped), the target client switched songs, the
            // play state flipped (PlayPause only), or the position jumped back -> the
            // command took effect, so do the full refresh.
            if (session is null ||
                serverIsPaused is null ||
                itemChanged ||
                positionRewound ||
                (expectPlayStateChange && playStateFlipped))
            {
                detectedUtc = DateTime.UtcNow;
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(PostCommandRefreshPollMs).ConfigureAwait(true);
        }

        await FetchNowPlayingAsync(isManualAction: true);

        // One line per transport command: it separates "the client/server had not
        // moved yet" from "we were slow", which is otherwise invisible in a bug report.
        var detected = detectedUtc == DateTime.MinValue
            ? "not seen (timeout)"
            : $"{(detectedUtc - commandSentUtc).TotalMilliseconds:0} ms";
        StartupLogger.Log(
            $"Transport refresh: change detected after {detected}, UI refreshed after {(DateTime.UtcNow - commandSentUtc).TotalMilliseconds:0} ms " +
            $"(item {previousItemId ?? "-"} -> {_currentItemId ?? "-"}, paused {previousIsPaused} -> {IsPaused})");
    }

    // Methods to call from View
    public void OnLoaded()
    {
        StartupLogger.Log("MainViewModel.OnLoaded: start");
        StartPlaybackTimer();
        StartupLogger.Log("MainViewModel.OnLoaded: complete");
    }

    public void OnClosed()
    {
        StopPlaybackTimer();
        _balloonTimer?.Stop();
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
    /// Simple 1-second probe. Every tick we ask the server for the active session.
    /// When the now-playing item changes (a song ended and the next one began, or
    /// playback started/stopped) we run a full refresh so the cover, title, artist
    /// and album update immediately. When the item is unchanged we keep the play
    /// state, the progress ring and the seek bar in sync from the server-reported
    /// position. The cheap part of the probe deliberately runs even while a full
    /// refresh is in flight: that refresh can spend a second or more downloading
    /// artwork, and the icon / vinyl spin / ring must not sit on stale values for
    /// that whole time.
    /// </summary>
    private async void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        await ProbeNowPlayingAsync();
    }

    private async Task ProbeNowPlayingAsync()
    {
        try
        {
            _config.ServerUrl = ServerUrlText.Trim();
            _config.ApiKey = ApiKeyText.Trim();
            _config.UserId = UserIdText.Trim();

            var session = await FetchActiveSessionAsync(_config).ConfigureAwait(true);
            var sessionReadUtc = DateTime.UtcNow;
            var nowPlaying = session?.NowPlayingItem;
            var serverItemId = nowPlaying?.Id;

            var itemChanged = !string.Equals(serverItemId, _currentItemId, StringComparison.OrdinalIgnoreCase);
            var playbackStarted = nowPlaying is not null && _currentItemId is null;
            var playbackEnded = nowPlaying is null && _currentItemId is not null;

            // Full refresh when the track changed or playback started/stopped. This
            // is what keeps the cover/title/artist in sync when a song finishes and
            // the next one begins. A refresh already in flight is left to finish (it
            // covers the same change); starting a second one would only add load.
            if ((itemChanged || playbackStarted || playbackEnded) && !_isFetching)
            {
                await FetchNowPlayingAsync();
                return;
            }

            if (nowPlaying is not null)
            {
                // The play state belongs to the session, not to one track, so it is
                // applied even while a refresh for a new item is still downloading.
                // Without this the icon and the vinyl spin could sit on a transient
                // "paused" for the whole artwork download. The newest snapshot wins
                // (see ApplyPlayState).
                ApplyPlayState(session?.PlayState?.IsPaused, sessionReadUtc);

                // Position/seek are only synced for the track the UI is already
                // showing; otherwise the ring would jump to the new track before its
                // cover and title arrive.
                if (!itemChanged && DateTime.UtcNow - _lastManualSeekUtc >= TimeSpan.FromSeconds(2))
                {
                    _currentRunTimeTicks = nowPlaying.RunTimeTicks ?? _currentRunTimeTicks;
                    _currentPositionTicks = session?.PlayState?.PositionTicks ?? 0;
                    Progress = GetProgress(session);
                    UpdateProgressRing();
                    UpdateSeekFromPosition(_currentPositionTicks, _currentRunTimeTicks);
                }
            }
        }
        catch (Exception ex)
        {
            TryLog("ProbeNowPlayingAsync", ex);
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
        StartupLogger.Log($"{tag}: {ex}");
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