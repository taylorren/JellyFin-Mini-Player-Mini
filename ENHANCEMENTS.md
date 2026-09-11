# RoundSoundMimic — Enhancement Ideas

A living list of ideas for improving the remote-control player. Nothing here is
committed to a release promise; items are grouped by theme and roughly ordered
by expected value vs. effort.

## UI / UX

- **Time labels near the seek bar** — show `elapsed / remaining` as plain text
  under (or beside) the seek slider. Tooltips already exist, but visible labels
  are easier to glance at.
- **Drag-preview scrubbing** — while dragging the seek slider, show the target
  timestamp in a small flyout/bubble above the thumb, and only send the seek on
  release (current debounce sends after 350 ms of settle).
- **Empty states** — when nothing is playing, show a friendly idle message and
  dim the transport controls instead of leaving blank artwork + "(no data)".
- **Ring vs. bar redundancy** — the progress ring and the seek bar show the
  same value. Options: make the ring show *remaining* time in a different hue,
  or drop one of the two if the window feels busy.
- **Shuffle / repeat toggles** — surface Jellyfin's shuffle and repeat modes as
  small toggle buttons next to Prev/Next (requires `PlayState` command support).
- **Queue view** — a lightweight popup listing the current Jellyfin queue with
  tap-to-jump, powered by `GET /Sessions/{id}/Items` queue data.
- **Volume flyout** — collapse the volume slider into a speaker button that
  expands a vertical slider on hover, freeing horizontal space for the seek bar.
- **Tooltips on transport buttons** — Play/Pause, Prev, Next, Heart currently
  have no tooltips; add short ones (also helps accessibility).
- **Keyboard shortcuts** — Space = play/pause, arrows = prev/next or seek ±10 s,
  `F` = favorite. Guard against firing while the config popup has focus.
- **Tray menu parity** — tray context menu could include play/pause and next in
  addition to show/exit.

## Playback

- **Immediate visual seek feedback** — optimistic local position already
  re-seeds after a seek; a small "seeking…" state on the slider would make it
  clearer when the server is catching up.
- **Boundary fetch hardening** — if the boundary fetch fails (network hiccup),
  retry the heartbeat a couple of times before giving up and going idle.
- **Pause/resume drift check** — on resume, compare local extrapolation against
  a server position fetch every N seconds to correct drift (e.g., after a
  system sleep).
- **Multi-device session picker** — when more than one session is playing,
  let the user choose which session to control instead of taking the first.
- **Transcoding awareness** — surface `TranscodeReasons` from `NowPlayingItem`
  as a small badge so users know why audio quality is reduced.

## Reliability / maintainability

- **Fetch telemetry** — log fetch cadence (count per minute, failure rate) to a
  rolling debug file to validate that the polling reduction works as intended.
- **Single DispatcherTimer audit** — ViewModel now owns the playback timer and
  MainWindow owns the EQ timer; document which timer owns which concern to
  avoid regressions.
- **Unit-testable seek math** — extract percent↔ticks conversion into a static
  helper so it can be unit tested without WPF.

## Deployed already

- Local position extrapolation (1 s tick, no network) with server fetch only at
  track boundaries or manual actions; 30 s idle heartbeat.
- Debounced seeks (350 ms) with stale-request dropping (generation counter).
- Manual commands (Prev/PlayPause/Next) force an immediate fetch and bypass the
  6 s "waiting for update" grace window.
- Seek re-seeds local tracking from the seek target so ring/slider stay in sync.
- Vinyl/EQ rendering extracted from MainWindow into `Services/VinylVisualizer`.
