using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RoundSoundMimic.Services
{
    public class JellyfinService : IJellyfinService
    {
        private static readonly HttpClient Http = new();

        /// <summary>
        /// Jellyfin 12+ requires an <c>Authorization</c> header carrying a MediaBrowser
        /// authentication string (older releases accepted the <c>X-Emby-Token</c> header).
        /// </summary>
        private static string BuildAuthorizationHeader(AppConfig config)
        {
            return $"MediaBrowser Client=\"RoundSoundMimic\", Device=\"PC\", DeviceId=\"roundsound-mimic-device\", Version=\"1.0\", Token=\"{config.ApiKey}\"";
        }

        public async Task<JellyfinSession?> FetchActiveSessionAsync(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
            {
                return null;
            }

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}/Sessions?ActiveWithinSeconds=120";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", BuildAuthorizationHeader(config));

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var sessions = JsonSerializer.Deserialize<List<JellyfinSession>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<JellyfinSession>();

            // The server returns sessions in arbitrary order. When more than one
            // session reports a NowPlayingItem (e.g. a paused browser tab plus the
            // phone that is really playing), picking the first one makes the widget
            // show cover/title/artist of the WRONG session. The session that is
            // actually playing is the most recently active one, and an actively
            // playing session outranks a paused one.
            IEnumerable<JellyfinSession> candidates = sessions.Where(s =>
                s.NowPlayingItem is not null &&
                (string.IsNullOrWhiteSpace(config.UserId) ||
                 string.Equals(s.UserId, config.UserId, StringComparison.OrdinalIgnoreCase)));

            var session = candidates
                .OrderByDescending(s => s.PlayState?.IsPaused == false)
                .ThenByDescending(s => s.LastActivityDate ?? DateTime.MinValue)
                .FirstOrDefault();

            return session;
        }

        public async Task<byte[]?> GetArtworkBytesAsync(AppConfig config, JellyfinNowPlayingItem item)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl)) return null;

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var candidateRequests = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                var tag = string.IsNullOrWhiteSpace(item.PrimaryImageTag) ? string.Empty : $"&tag={Uri.EscapeDataString(item.PrimaryImageTag)}";
                candidateRequests.Add($"{baseUrl}/Items/{item.Id}/Images/Primary?maxWidth=600&maxHeight=600&quality=100{tag}");
            }

            if (!string.IsNullOrWhiteSpace(item.AlbumId))
            {
                var tag = string.IsNullOrWhiteSpace(item.AlbumPrimaryImageTag) ? string.Empty : $"&tag={Uri.EscapeDataString(item.AlbumPrimaryImageTag)}";
                candidateRequests.Add($"{baseUrl}/Items/{item.AlbumId}/Images/Primary?maxWidth=600&maxHeight=600&quality=100{tag}");
            }

            foreach (var url in candidateRequests)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Authorization", BuildAuthorizationHeader(config));
                    using var response = await Http.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                catch
                {
                    // try next
                }
            }

            return null;
        }

        public async Task<bool> SendPlaybackCommandAsync(AppConfig config, string command, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(sessionId)) return false;

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}/Sessions/{sessionId}/Playing/{command}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", BuildAuthorizationHeader(config));

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> SendCommandAsync(AppConfig config, string name, object? arguments, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(sessionId)) return false;

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}/Sessions/{sessionId}/Command";

            var body = new { Name = name, Arguments = arguments };
            var json = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", BuildAuthorizationHeader(config));
            request.Content = content;

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        public async Task<JellyfinNowPlayingItem?> FetchItemWithUserDataAsync(AppConfig config, string itemId)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}/Items/{itemId}?enableUserData=true";
            if (!string.IsNullOrWhiteSpace(config.UserId))
            {
                url += $"&userId={config.UserId}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", BuildAuthorizationHeader(config));

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var item = JsonSerializer.Deserialize<JellyfinNowPlayingItem>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return item;
        }

        public async Task<JellyfinSession?> EnsureActiveSessionIdAsync(AppConfig config, string? activeSessionId)
        {
            if (!string.IsNullOrWhiteSpace(activeSessionId))
            {
                // We could fetch the specific session by ID, but for simplicity we'll just find any active session
                return await FetchActiveSessionAsync(config);
            }

            try
            {
                var session = await FetchActiveSessionAsync(config);
                return session;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<bool> SetVolumeAsync(AppConfig config, string sessionId, int volume, bool isMuted)
        {
            return await SendCommandAsync(config, "SetVolume", new { Volume = volume }, sessionId);
        }

        public async Task<bool> SeekAsync(AppConfig config, string sessionId, long positionTicks)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(sessionId)) return false;

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}/Sessions/{sessionId}/Playing/Seek?seekPositionTicks={positionTicks}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", BuildAuthorizationHeader(config));

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> SetFavoriteAsync(AppConfig config, string itemId, bool isFavorite)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            var baseUrl = config.ServerUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}/UserFavoriteItems/{itemId}";
            if (!string.IsNullOrWhiteSpace(config.UserId))
            {
                url += $"?userId={Uri.EscapeDataString(config.UserId)}";
            }

            var method = isFavorite ? HttpMethod.Post : HttpMethod.Delete;
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Authorization", BuildAuthorizationHeader(config));

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
    }
}
