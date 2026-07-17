<p align="center">
  <img src="logo.png" alt="Jellyfin HTSP Tuner" width="450">
</p>

<h1 align="center">Jellyfin HTSP Tuner</h1>

<p align="center">
  Native <b>HTSP</b> Live TV for Jellyfin, backed by <a href="https://tvheadend.org/">Tvheadend</a>.
</p>

<p align="center">
  <a href="https://github.com/vk496/jellyfin-htsp-tuner/releases"><img src="https://img.shields.io/github/v/release/vk496/jellyfin-htsp-tuner?color=00a4dc" alt="Release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-00a4dc" alt="Jellyfin 10.11+">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/vk496/jellyfin-htsp-tuner" alt="License"></a>
</p>

## Why this plugin exists

Jellyfin's official
[Tvheadend plugin](https://github.com/jellyfin/jellyfin-plugin-tvheadend) speaks HTSP
**only to read metadata** (channels, EPG, recordings) and then pulls the actual video over
Tvheadend's plain **HTTP** endpoint. That leaves the richest part of HTSP — the live muxed
subscription — on the table, and it is a long-standing open request there:
[jellyfin-plugin-tvheadend#104](https://github.com/jellyfin/jellyfin-plugin-tvheadend/issues/104).

**This plugin does the real thing.** It opens an HTSP `subscribe`, receives the elementary
streams (`muxpkt`), and remuxes them into MPEG-TS **inside Jellyfin**. Nothing is fetched over
HTTP for playback.

## How to use it

Add it like any other tuner: **Live TV → Add Tuner Device → HTSP Tuner**. You can add
**multiple tuners** (one per Tvheadend server). Validating a tuner **auto-registers a matching
"HTSP" guide** under *TV Guide Data Providers*, so EPG works with no manual channel mapping.

The plugin does nothing until you add a tuner — there is no separate "integrated service" mode.
Jellyfin's web UI cannot yet render per-tuner config fields for a plugin tuner, so the plugin's
own **settings page holds the connection details** (host, ports, credentials) that a tuner falls
back to. Set them there; the day Jellyfin allows per-tuner fields, those take precedence
automatically.

## Features

- **Native HTSP streaming** — `subscribe` → `muxpkt` → in-process MPEG-TS remux. No HTTP
  passthrough, no external muxer.
- **Accurate stream metadata** — codec, resolution, aspect, language and audio layout come
  straight from `subscriptionStart`. A short, one-time probe of the plugin's *own* muxed output
  then fills in the fields HTSP cannot carry — **interlacing, HDR/colour, bit depth,
  profile/level** — so Jellyfin makes correct direct-play, deinterlace and transcode decisions.
  This never opens a **second tuner subscription** (it reads bytes already buffered), and the
  result is cached per channel.
- **DVB bitmap subtitles** — carried through with the right PMT descriptors and burned in on
  transcode. (DVB *teletext* subtitles are intentionally dropped — see *Workarounds* below.)
- **Multiple audio tracks + languages** — every audio stream is exposed with its ISO-639
  language, in a fixed video-then-audio order so clients cannot mis-number the tracks, and
  audio-description tracks are labelled as such.
- **Bounded in-memory buffer** — each channel is buffered in a fixed-size ring (default
  **100 MB**, configurable). When a client falls behind it drops the oldest data rather than
  filling the disk or stalling the tuner.
- **Stream sharing** — multiple clients on one channel share a single Tvheadend subscription;
  a fresh viewer joins at the last key frame for a clean, quick start.
- **Radio channels** — audio-only services are surfaced as radio.
- **Live EPG** — guide data is pushed over HTSP async metadata, not polled per channel.
- **Recording** — Jellyfin's own DVR records the tuner stream, like any tuner. (Tvheadend-native
  DVR is intentionally not done: it would require an `ILiveTvService`, which cannot coexist with
  this plugin's `ITunerHost` without listing every channel twice.)
- **Live tuner status** — the settings page shows each active subscription: where it is tuned
  (adapter / mux), signal quality, and dropped-frame counters.
- **Channel tags**, **fast same-mux switching**, and a **Test connection** button on the
  settings page so a wrong host/credential shows in the UI instead of the log.

## Workarounds for Jellyfin limitations

A few things the plugin does deliberately, only because Jellyfin (server or web client) cannot
currently handle the stream as-is. Each is a stopgap that should disappear if the matching gap is
closed upstream — they are collected here so they are not mistaken for bugs.

- **DVB teletext subtitles are dropped, not published.** Tvheadend carries them, but Jellyfin's
  subtitle extraction runs ffmpeg with `-c:s srt` and neither `-txt_format text` (libzvbi defaults
  to *bitmap* output, which an SRT encoder rejects) nor `-txt_page subtitle` (so it would dump
  every teletext page). The result is that selecting a teletext subtitle **breaks playback**, on
  any source, not just this plugin. Rather than offer a track that always errors, the muxer drops
  teletext entirely. *Upstream fix:* have Jellyfin add those two flags when extracting a
  `dvb_teletext` stream. DVB *bitmap* subtitles are unaffected and work normally.
- **EPG artwork is proxied over HTSP.** Tvheadend reports programme (and channel) images as
  `imagecache/<id>` paths, not URLs, which Jellyfin cannot fetch. The plugin serves them from its
  own endpoint, reading the bytes over the HTSP connection it already holds — so no HTTP access to
  Tvheadend is needed. *Upstream fix:* none needed; this is inherent to HTSP.
- **Subtitle codec names are pre-normalised.** The plugin reports DVB subtitles as `DVBSUB`
  (Jellyfin's own probe name) rather than ffmpeg's `dvb_subtitle`, because we bypass Jellyfin's
  probe and it classifies the raw name as *text* — sending a bitmap subtitle down the text-extract
  path and breaking it.
- **The guide refresh is rate-limited, not event-driven.** Tvheadend pushes EPG changes live, but
  Jellyfin only offers a full, all-channels guide rebuild (`IGuideManager.RefreshGuide`), which
  takes minutes. So a push triggers a refresh at most once every N minutes (default 12 h, under
  *Advanced*). *Upstream fix:* a per-channel guide-update API.
- **The HTSP guide provider shows as "Unknown" in the UI.** Jellyfin-web hard-codes the names of
  guide-provider types, so a plugin-supplied one has no label. The tuner side has no such problem.
  *Upstream fix:* a `ListingProviders/Types` endpoint (prepared, pending upstream).

## Requirements

- **Jellyfin 10.11 or newer** (built for `net9.0`; `targetAbi` is `10.11.0.0`).
- A reachable **Tvheadend** with HTSP enabled (default port **9982**), protocol **v43+**.
- A Tvheadend user with streaming access. Empty username/password works if Tvheadend allows
  anonymous access.

## Installation

### Recommended — add the plugin repository (gives you automatic updates)

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Click **＋** to add a repository. Enter any name (e.g. `HTSP Tuner`) and this URL:

   ```
   https://raw.githubusercontent.com/vk496/jellyfin-htsp-tuner/main/manifest.json
   ```

3. Save, then open **Dashboard → Plugins → Catalog**, find **HTSP Tuner** under **Live TV**,
   and click **Install**.
4. **Restart Jellyfin** when prompted.

Future releases appear as ordinary plugin updates in the same catalog.

### Manual — download a release

Grab the latest `htsp_tuner_<version>.zip` from the
[Releases page](https://github.com/vk496/jellyfin-htsp-tuner/releases), extract it into your
Jellyfin `plugins` directory as `HtspTuner_<version>/`, and restart Jellyfin.

### From source (dev inner loop)

[`tools/deploy.sh`](tools/deploy.sh) builds, stages into a running Jellyfin, restarts it, and
reports the plugin status. Nothing host-specific is baked in — it reads everything from the
environment and fails loudly if a required value is missing:

```bash
export JELLYFIN_TOKEN=<your Jellyfin API key>    # required
export PLUGIN_DIR=/path/to/jellyfin/plugins      # required
export JELLYFIN_URL=http://localhost:8096         # optional (this is the default)
./tools/deploy.sh
```

## After installing

1. Open **Dashboard → Plugins → HTSP Tuner**, enter your Tvheadend host, ports and credentials,
   and click **Test connection** to confirm, then **Save**.
2. Go to **Live TV → Add Tuner Device → HTSP Tuner** and save it (leave the URL blank to use the
   settings from step 1). The channels and an HTSP guide appear automatically.

## What works / what doesn't yet

**Works (verified end-to-end against real Tvheadend + Jellyfin 10.11):**

- HTSP subscription and in-process remux; H.264 / HEVC / MPEG-2 video with EAC3 / AC3 / MP2 /
  AAC audio decode cleanly through Jellyfin (AAC is re-framed to ADTS).
- Codec / geometry / language metadata from `subscriptionStart`, enriched by a one-time probe
  of our own output for interlacing, HDR/colour, bit depth and profile/level.
- DVB bitmap subtitle passthrough; multiple audio tracks with languages and a fixed track order.
- Live EPG (tens of thousands of events) with programme artwork, channel tags, radio channels.
- Tuner-device model with multiple tuners and auto-registered HTSP guide.
- Stream sharing; surfacing Tvheadend errors (e.g. `noFreeAdapter`) as real failures instead
  of hanging.

**Not done yet / rough edges:**

- **Recording** goes through Jellyfin's own DVR (it records the tuner stream). Tvheadend-native
  DVR is out of scope — see *Recording* under Features for why.
- **DVB teletext subtitles** are dropped (Jellyfin cannot render them) — see *Workarounds*.
- HEVC (incl. 4K HDR) remuxes and plays on HEVC-capable clients; **browsers cannot decode
  HEVC**, so those clients fall back to a server transcode — enable hardware transcoding for
  smooth 4K. Trick-play and thumbnails are left to the transcoder.
- Interlaced channels are reported as such, so capable clients deinterlace locally; a browser
  will deinterlace via a server transcode (there is no combing-free copy path for a browser).
- Tested against HTSP **v43**; older protocol versions are out of scope.

## License

[GPL-3.0](LICENSE), matching the Jellyfin plugin convention.
