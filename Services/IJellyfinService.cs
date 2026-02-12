using System.Threading.Tasks;

namespace RoundSoundMimic.Services
{
    public interface IJellyfinService
    {
        Task<JellyfinSession?> FetchActiveSessionAsync(AppConfig config);
        Task<byte[]?> GetArtworkBytesAsync(AppConfig config, JellyfinNowPlayingItem item);
        Task<bool> SendPlaybackCommandAsync(AppConfig config, string command, string sessionId);
        Task<JellyfinSession?> EnsureActiveSessionIdAsync(AppConfig config, string? activeSessionId);
        Task<JellyfinNowPlayingItem?> FetchItemWithUserDataAsync(AppConfig config, string itemId);
    }
}
