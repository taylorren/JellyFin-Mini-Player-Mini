# User Guide for RoundSoundMimic

## Getting Started

RoundSoundMimic is a mini player for Jellyfin that shows your current playback in a compact, circular interface.

## Setup

To use the app, you'll need to connect it to your Jellyfin server.

### 1. Find Your Jellyfin Server URL

Your server URL is the address you use to access Jellyfin in your web browser. It typically looks like:
- `http://localhost:8096` (if running locally)
- `https://yourserver.com` (if hosted remotely)

### 2. Get Your API Key

1. Log into your Jellyfin server as an admin user.
2. Go to **Dashboard** > **API Keys**.
3. Click **+** to create a new API key.
4. Give it a name like "RoundSoundMimic" and click **Save**.
5. Copy the generated API key.

### 3. Optional: User ID

If you want to monitor a specific user's playback, you can find their user ID:
1. Go to **Dashboard** > **Users**.
2. Click on the user.
3. The user ID is shown in the URL or user details.

### 4. Configure the App

1. Launch RoundSoundMimic.
2. Enter your server URL, API key, and optional user ID.
3. Click **Save Config**.
4. Click **Fetch Now Playing** to start monitoring.

Your configuration is automatically saved to `%AppData%\RoundSoundMimic\config.json`.

## Features

- **Real-time Playback Display**: See what's currently playing.
- **Circular Progress**: Visual progress indicator.
- **Album Art**: Display of current track artwork.
- **Controls**: Play/pause, previous, and next buttons.
- **Format Info**: Shows audio format with color coding.
- **System Tray**: Minimizes to tray for unobtrusive use.

## Troubleshooting

- If no data appears, check your server URL and API key.
- Ensure your Jellyfin server is running and accessible.
- Verify the user has active playback sessions.