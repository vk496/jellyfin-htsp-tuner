<p align="center">
  <img src="logo.png" alt="Jellyfin HTSP Tuner" width="450">
</p>

<h1 align="center">Jellyfin HTSP Tuner</h1>

<p align="center">
  Live TV from <a href="https://tvheadend.org/">Tvheadend</a> in Jellyfin, streamed over HTSP.
</p>

<p align="center">
  <a href="https://github.com/vk496/jellyfin-htsp-tuner/releases"><img src="https://img.shields.io/github/v/release/vk496/jellyfin-htsp-tuner?color=00a4dc" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-10.10%20%7C%2010.11%20%7C%2012-00a4dc" alt="Jellyfin 10.10, 10.11, 12">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/vk496/jellyfin-htsp-tuner" alt="License"></a>
</p>

The official Tvheadend plugin uses HTSP only for the channel list and guide, then fetches the
video over plain HTTP. This plugin takes the video over HTSP too, and remuxes it to MPEG-TS
inside Jellyfin. You get accurate stream info, every audio track, DVB subtitles, and one
subscription shared by everyone watching the same channel.

It adds itself as a normal tuner: **Live TV → Add Tuner Device → HTSP Tuner**.

## Features

- **Streams over HTSP.** No HTTP passthrough, no external muxer.
- **Correct direct play decisions.** Codec, resolution, interlacing, HDR, bit depth and audio
  layout are all reported, so clients direct-play what they can and transcode only when needed.
- **Every audio track**, with its language. Audio description is labelled as such.
- **DVB subtitles** (bitmap) carried through.
- **Stream sharing.** Several viewers on one channel use one Tvheadend subscription.
- **Fast channel switching** within the same multiplex.
- **Live guide.** Tvheadend pushes EPG changes; a matching guide provider is set up for you.
- **Recording** through Jellyfin's DVR: schedules, series, artwork.
- **Pictures for programmes that have none**, grabbed from the broadcast with the channel logo.
  Captures use the lowest tuner priority, so a viewer always wins.
- **Radio channels**, **channel tags**, **multiple Tvheadend servers**.
- **Tuner status** on the settings page: what is tuned where, signal, errors.

## Requirements

- Jellyfin **10.10**, **10.11** or **12**. The right build is picked for your server.
- Tvheadend with HTSP enabled (port 9982), HTSP protocol v43 or newer.
- A Tvheadend user allowed to stream (or anonymous access).

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories**, click **+**, and add:

   ```
   https://raw.githubusercontent.com/vk496/jellyfin-htsp-tuner/main/manifest.json
   ```

2. Install **HTSP Tuner** from the **Catalog** (under Live TV) and restart Jellyfin.
3. Open **Dashboard → Plugins → HTSP Tuner**, enter your Tvheadend host and login, click
   **Test connection**, then **Save**.
4. Go to **Live TV → Add Tuner Device**, pick **HTSP Tuner** and save. Leave the URL empty to use
   the settings from step 3, or enter `host:port` for another Tvheadend server.

Channels and the guide show up on their own. Updates arrive through the same catalog.

Without the repository: download the zip for your server from
[Releases](https://github.com/vk496/jellyfin-htsp-tuner/releases) (the `_jellyfin-10.10` one
for 10.10, the other for 10.11 and 12), extract it into a new folder in Jellyfin's `plugins`
directory, and restart.

## Known limitations

- **Teletext subtitles are dropped.** Jellyfin can't extract them, and picking one breaks
  playback, so the plugin doesn't offer them. Bitmap DVB subtitles work.
- **Tvheadend's own DVR is not used.** Recordings are made by Jellyfin from the live stream.
- **Guide updates are batched.** Jellyfin can only rebuild the whole guide, so pushed changes
  trigger a refresh at most every 12 hours by default (configurable).
- **The guide provider is listed as "Unknown"** under TV Guide Data Providers. Jellyfin's web
  UI only names its built-in providers.
- **Browsers can't play HEVC or deinterlace**, so those streams are transcoded for browsers.
  Apps on TVs and phones usually play them directly.

## Development

[`tools/deploy.sh`](tools/deploy.sh) builds the plugin, copies it into a running Jellyfin and
restarts it:

```bash
export JELLYFIN_TOKEN=<api key> PLUGIN_DIR=/path/to/jellyfin/plugins
./tools/deploy.sh
```

## License

[GPL-3.0](LICENSE)
