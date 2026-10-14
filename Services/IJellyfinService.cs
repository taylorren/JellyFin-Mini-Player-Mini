using System.Threading.Tasks;

namespace RoundSoundMimic.Services
{
    public interface IJellyfinService
    {
        Task<JellyfinSession?> FetchActiveSessionAsync(AppConfig config);
        Task<byte[]?> GetArtworkBytesAsync(AppConfig config, JellyfinNowPlayingItem item);
        Task<bool> SendPlaybackCommandAsync(AppConfig config, string command, string sessionId);
        Task<JellyfinNowPlayingItem?> FetchItemWithUserDataAsync(AppConfig config, string itemId);
        Task<bool> SeekAsync(AppConfig config, string sessionId, long positionTicks);
        Task<bool> SetFavoriteAsync(AppConfig config, string itemId, bool isFavorite);
    }
}
