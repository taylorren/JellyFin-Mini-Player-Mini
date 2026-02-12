using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RoundSoundMimic;

public sealed class JellyfinSession
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("UserId")]
    public string? UserId { get; set; }

    [JsonPropertyName("NowPlayingItem")]
    public JellyfinNowPlayingItem? NowPlayingItem { get; set; }

    [JsonPropertyName("PlayState")]
    public JellyfinPlayState? PlayState { get; set; }
}

public sealed class JellyfinPlayState
{
    [JsonPropertyName("PositionTicks")]
    public long? PositionTicks { get; set; }

    [JsonPropertyName("IsPaused")]
    public bool? IsPaused { get; set; }
}

public sealed class JellyfinNowPlayingItem
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("PrimaryImageTag")]
    public string? PrimaryImageTag { get; set; }

    [JsonPropertyName("AlbumId")]
    public string? AlbumId { get; set; }

    [JsonPropertyName("AlbumPrimaryImageTag")]
    public string? AlbumPrimaryImageTag { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }

    [JsonPropertyName("Album")]
    public string? Album { get; set; }

    [JsonPropertyName("Artists")]
    public List<string>? Artists { get; set; }

    [JsonPropertyName("Container")]
    public string? Container { get; set; }

    [JsonPropertyName("UserData")]
    public JellyfinUserData? UserData { get; set; }
}

public sealed class JellyfinUserData
{
    [JsonPropertyName("PlayCount")]
    public int? PlayCount { get; set; }
}
