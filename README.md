# Jellyfin HTSP Tuner

Native **HTSP** Live TV for Jellyfin, backed by [Tvheadend](https://tvheadend.org/).

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

## Two ways to use it

The plugin registers on both of Jellyfin's Live TV extension points — pick whichever fits:

1. **Integrated service** — configure Tvheadend on the plugin's own settings page. Channels,
   EPG, and DVR timers appear automatically, all over HTSP. Single Tvheadend server.
2. **Tuner device** — go to **Live TV → Add Tuner Device** and pick **HTSP Tuner**. Supports
   **multiple tuners** (one per Tvheadend server). Adding a tuner **auto-registers a matching
   "HTSP" guide** under *TV Guide Data Providers*, so EPG works with no manual channel mapping.

Use one model or the other, not both against the same server (they would list channels twice).

## Features

- **Native HTSP streaming** — `subscribe` → `muxpkt` → in-process MPEG-TS remux. No HTTP
  passthrough, no external muxer.
- **Accurate stream metadata** — codec, resolution, aspect, language and audio layout come
  straight from `subscriptionStart`. A short, one-time probe of the plugin's *own* muxed output
  then fills in the fields HTSP cannot carry — **interlacing, HDR/colour, bit depth,
  profile/level** — so Jellyfin makes correct direct-play, deinterlace and transcode decisions.
  This never opens a **second tuner subscription** (it reads bytes already buffered), and the
  result is cached per channel.
- **Subtitle passthrough** — DVB subtitles and DVB teletext are carried through with the right
  PMT descriptors and show up as selectable subtitle tracks.
- **Multiple audio tracks + languages** — every audio stream is exposed with its ISO-639
  language and hearing/visually-impaired flags.
- **Bounded in-memory buffer** — each channel is buffered in a fixed-size ring (default
  **100 MB**, configurable). When a client falls behind it drops the oldest data rather than
  filling the disk or stalling the tuner.
- **Stream sharing** — multiple clients on one channel share a single Tvheadend subscription;
  a fresh viewer joins at the last key frame for a clean, quick start.
- **Radio channels** — audio-only services are surfaced as radio.
- **Live EPG** — guide data is pushed over HTSP async metadata, not polled per channel.
- **DVR timers + series rules** — create/cancel Tvheadend timers and autorec (series) rules
  through Jellyfin's recording UI.
- **Channel tags**, **fast same-mux switching**, and a **Test connection** button on the
  settings page so a wrong host/credential shows in the UI instead of the log.

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

Open **Dashboard → Plugins → HTSP Tuner**, enter your Tvheadend host, ports and credentials,
and click **Test connection** to confirm. Then either use the integrated service as-is, or add
an **HTSP Tuner** device under **Live TV → Add Tuner Device** — see *Two ways to use it* above.

## What works / what doesn't yet

**Works (verified end-to-end against real Tvheadend + Jellyfin 10.11):**

- HTSP subscription and in-process remux; H.264 / HEVC / MPEG-2 video with EAC3 / AC3 / MP2 /
  AAC audio decode cleanly through Jellyfin (AAC is re-framed to ADTS).
- Codec / geometry / language metadata from `subscriptionStart`, enriched by a one-time probe
  of our own output for interlacing, HDR/colour, bit depth and profile/level.
- DVB subtitle + teletext passthrough; multiple audio tracks with languages.
- Live EPG (tens of thousands of events), channel tags, radio channels.
- Tuner-device model with multiple tuners and auto-registered HTSP guide.
- Stream sharing; surfacing Tvheadend errors (e.g. `noFreeAdapter`) as real failures instead
  of hanging.

**Not done yet / rough edges:**

- **Recordings *playback*** (browsing finished Tvheadend recordings in Jellyfin) is not wired
  up yet; **scheduling** recordings and series rules does work.
- A tuner/signal-status dashboard is not surfaced in the UI (the data is collected).
- HEVC (incl. 4K HDR) remuxes and plays on HEVC-capable clients; **browsers cannot decode
  HEVC**, so those clients fall back to a server transcode — enable hardware transcoding for
  smooth 4K. Trick-play and thumbnails are left to the transcoder.
- Interlaced channels are reported as such, so capable clients deinterlace locally; a browser
  will deinterlace via a server transcode (there is no combing-free copy path for a browser).
- Tested against HTSP **v43**; older protocol versions are out of scope.

## License

[GPL-3.0](LICENSE), matching the Jellyfin plugin convention.
