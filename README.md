# teamradio56 🏁🎙️

**Voice race-engineer radio for Le Mans Ultimate.** Reads telemetry in real
time, judges what matters, and calls it to you as team radio in English.
Runs as a lightweight SimHub plugin — no Python needed (it requires the rF2
Shared Memory Map Plugin, and the voice pack installs from the settings screen).
The Python app in this repository is the reference implementation and the tool
that renders the voice pack. (The name comes from Le Mans' iconic Garage 56.)

한국어 문서: [README.ko.md](README.ko.md)

The goal is a race engineer that speaks only when it matters and says what
it means — not a stream of raw numbers.

> "Gap behind 2.4 seconds. Closing four tenths a lap. Just no mistakes."

## Features

- **Judgment calls, not readouts** — traffic and side-by-side spotting,
  race control (FCY, pit open, restarts, yellows, blue flags), automatic
  damage checks, fuel and tyre warnings, rival pit/pace intel, gap trends,
  session briefings. All rule-based and deterministic
- **Silence discipline** — if there's nothing worth saying, it says nothing.
  No per-lap chatter; per-type cooldowns and a chattiness preset
- **Natural voice with zero latency** — urgent calls come from pre-generated
  phrase pools with a pre-rendered voice pack (Kokoro), so nothing is
  synthesised mid-corner. Every safety call (spotter, flags, damage, box,
  critical fuel) is in the pack
- **One voice, never mixed** — the plugin speaks only with the Kokoro voice
  pack. A line that isn't in the pack is shown as a subtitle (and in recent
  radio) instead of being read in another voice. With no voice pack
  installed every call is subtitle-only, and the settings screen tells you to
  press *Update voice pack*
- **Radio effect** — bandpass, saturation, band-limited noise and squelch
  for a real team-radio texture
- **English radio** with a bilingual (Korean/English) settings UI
- **Multiclass aware** — faster-class approach warnings, in-class positions
- **Protects game performance** — 5 Hz polling, audio on its own thread, no
  GPU-based TTS
- **One-way voice output** — no speech recognition or conversation

The SimHub plugin is deliberately lean (0.18 "lite"): it has no Python engine
mode, no online (edge) or Windows voices, no voice mixing, no LLM settings and
no Korean radio. Those experiments — LLM-generated lines via the Anthropic API
(your own key), Korean radio, edge/ElevenLabs voices, post-lap sector feedback
— live only in the Python app, which is kept for development and for rendering
the voice pack. Old plugin settings for them are ignored.

## Quick start

See [docs/INSTALL.md](docs/INSTALL.md) for full installation (including the
shared-memory plugin). For the SimHub plugin, see
[simhub/README.md](simhub/README.md). The plugin always runs its built-in C#
engine (no Python needed); after installing, press *Update voice pack* once —
until a voice pack is installed the radio is subtitles only. Don't run the
Python app at the same time as the plugin.

**Updating the SimHub plugin**: from 0.16 the plugin updates itself from
[GitHub Releases](https://github.com/hyungwookc07/teamradio56/releases) —
open the teamradio56 settings in SimHub → **Updates** → *Check for updates*,
then *Update plugin* (restart SimHub afterwards) or *Update voice pack*.
Nothing installs without a click. If your plugin has no **Updates** section
yet (0.15 or older, or a source build), install the latest release (0.17 or
newer) once by hand (copy the two DLLs from the release zip into the SimHub
folder). 0.17 uses a new voice pack (`kokoro-v016`, about 175 MB): when the
plugin finds no pack or an older one, it offers *Update voice pack* — run it;
the engine restarts by itself and uses the new pack right away. Until then,
lines that aren't in the installed pack are shown as subtitles instead of being
spoken (with no pack at all, everything is). From 0.18 the plugin no longer
looks for an audio cache next to a Python engine; if you used one, let *Update
voice pack* install a fresh copy next to the DLLs or point *Voice pack folder*
at it. Building from source still works with a double-click on `update.bat`
(pull + build + copy DLLs).

Releases (plugin zip + `teamradio56-release.txt` manifest) are made in one
step: push a `vX.Y.Z` tag matching `TeamRadio56Plugin.Version` on a `main`
commit, or create that release in the GitHub UI. GitHub Actions builds the
plugin against the real SimHub assemblies; if the sentence set changed it first
renders and publishes a new voice pack (its own `audio-<pack>` pre-release,
about 2 hours extra) and commits the pack descriptor to `main`; then it fills
the release. Where tags can't be pushed, pushing a `main` commit to a
`release/vX.Y.Z` branch publishes the same way (the tag is created on publish).
Push a `release-dry/**` branch for a dry run; the `audio-pack`
workflow can still be run on its own. If a run fails no plugin release is left
behind; fix the cause and use *Re-run failed jobs* if it was outside the code
(network, permissions, approval). A re-run builds the same tag commit, so for a
code fix commit to `main`, delete the tag and push it again on `main`'s latest
commit (for a release created in the GitHub UI, delete that release and its
tag, then create the release again). A voice pack that was already published
is not rendered again. Right now `v0.17.0` is an empty release created by hand;
0.17.1 is the first real one. Maintainers: see the release section of
[simhub/README.md](simhub/README.md).

The Python app (development, reference implementation and voice-pack tools —
not needed for the plugin):

```bat
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
copy config.yaml.example config.yaml
python main.py
```

Testing without the game:

```bat
python tools\make_test_replay.py data\test.jsonl
python main.py --replay data\test.jsonl --speed 5
```

## Layout

```
main.py            # main loop (5 Hz polling + lap-event dispatch)
telemetry.py       # shared-memory reader / replay / recording
rf2data.py         # rF2 shared-memory ctypes structs (based on pyRfactor2SharedMemory)
state.py           # session state + lap history
analyzers/
  fuel.py          # burn rate / laps remaining / pit window
  tyres.py         # temp imbalance / wear trend / life estimate
  pace.py          # lap-time trend / gap rates
  traffic.py       # nearby-car detection (5 Hz)
events.py          # priority event queue (dedup, cooldowns)
voice.py           # line generation (pre-generated pools / live LLM)
tts.py             # TTS playback thread + audio cache
config.py          # config.yaml loader
messages.py        # sentence tables (en/ko) + slot helpers
simhub/            # SimHub plugin + C# engine port (see simhub/README.md)
```

## Milestones

- [x] v0.1 — shared-memory hookup, fuel/laptime/gap console output, waits for
  the game, replay mode
- [x] v0.2 — lap-complete analysis (fuel/pace) + event queue + template TTS
- [x] v0.3 — traffic analyzer + pre-generated phrase pools + audio cache
- [x] v0.4 — live LLM lines (Anthropic API, persona/narrative) — experimental
- [x] v0.5 — tyre analyzer, race JSON save, PyInstaller packaging

### Naturalness (v0.6)

(LLM items here are experimental and opt-in.)

- [x] Per-car traffic state machine — speaks only on state transitions, and
  chains lines about the same car into a narrative ("closing" → "alongside" →
  "past you"); multiple cars get one combined sentence ordered by threat
- [x] Tone tags on the phrase pools (casual/urgent) +
  `scripts/generate_variants.py` (LLM bulk regeneration)
- [x] Bridge technique — an urgent cached call (zero latency) followed
  asynchronously by an LLM explanation, auto-discarded if the situation has
  passed. Playback micro-randomisation (0–300 ms, ±5 % volume)
- [x] Race narrative context (ongoing issues) + LLM strategy engine
  (triggered only at decision points, hourly call budget — under 30 calls in
  a 2-hour race)
- [x] Stronger silence discipline (LLM PASS = stay quiet) + speech log
  `data/speech_log.jsonl`
- [x] Training mode — sector-delta feedback after each lap, LLM debrief at
  session end, trend comments vs. your history when revisiting a track

### Race information (v0.7)

- [x] Race control — FCY/safety car deployment, pit-open and restart calls
  (the money calls in endurance racing), local sector yellows, green/chequered,
  time-remaining milestones, final lap, pit-limiter warning
- [x] Session briefing like a real WEC/IMSA engineer — always opens with a radio
  check (no track name) and differs per session: practice (length, track/rain,
  build-up run plan), qualifying (find a gap, track limits, "make it count" — no
  fuel), warm-up (car check), race (grid slot of N in class, fuel, formation lap
  and green call, start spotter, calm first lap); mid-join variants. Every
  sentence slot has 2–5 variants picked by an FNV-1a hash of the snapshot, so
  Python and C# say the same words while sessions still differ
- [x] Chequered flag and after — the flag call is tiered by result (win, class
  win, podium, places gained / about the same / lost vs. the class start
  position, or result-only after a mid-join) and says the real class position.
  Then a cool-down lap call (with a "faster cars still racing" note when
  needed), one summary (fastest lap in class or our best-lap rank) and a
  pit-in line. After our finish, strategy, fuel, tyre, rival, position and
  periodic report calls go quiet; only safety calls remain (pit limiter,
  yellows, wheel loss, cars alongside / faster cars approaching)
- [x] Class standings — real (in-class) positions in multiclass, called only
  on change
- [x] Blue flag / backmarkers — dual detection from the mFlag edge + lap
  delta. Yield guidance when a leader on another lap arrives; "not a battle"
  notice for backmarkers you're catching
- [x] Rival intel — detects same-class rivals pitting (undercut/overcut
  judgment), pace comparison with the cars around you ("seven tenths a lap
  quicker, with us in about eight laps")
- [x] Car condition — water/oil temps, overheating, brake-temp warnings
- [x] Automatic damage check — 8 s after an impact, sweeps dents / wheels /
  punctures / pressures from data and reports ("just marks, carry on" /
  "losing pressure, prepare to box"). Instant call on wheel loss; continuous
  slow-puncture watch — the tool does the checking, not the driver
- [x] Fuel-save coaching — target-burn delta guidance in no-stop borderline
  situations
- [x] Wet/dry crossover — tyre-switch decision trigger from track wetness
- [x] Optional HUD-replacement radio — for immersive HUD-off driving, the
  `reports` settings give per-lap laptime calls + a position/gap/fuel/tyre
  report every N laps. Off by default

### SimHub plugin & C# port (v0.8–v0.18)

- [x] v0.8–v0.17 — SimHub plugin with a bilingual (한국어/English) settings UI;
  it ran the Python engine as a child process, or the **built-in C# engine** —
  all nine analyzers, session briefing, phrase pools, cached audio (Kokoro),
  runtime edge synthesis for the edge voice, Korean radio. Verified against the
  Python engine by a replay regression (identical accepted events, en/ko, down
  to the sentence text). The experimental LLM lines required Python mode.
- [x] v0.18 "lite" — the plugin keeps only the built-in engine, English radio
  and the Kokoro voice pack (plus the in-app updater, overlay and bilingual
  settings UI). Python engine mode, edge/Windows voices, voice mixing, LLM
  settings and Korean radio were removed from the plugin (they remain in the
  Python app); with no voice pack the radio is subtitles only and the settings
  screen points to *Update voice pack*. The replay regression now compares
  English only.

Items not in shared memory (virtual energy, weather forecast, pit strategy)
will be supplemented from LMU's built-in REST API (the localhost server the
game UI uses) — `resttelemetry.py` has an auto-disabling poller ready, and
`tools/probe_rest.py` collects real responses with the game running to pin
down the parsing. Hybrid battery state exists in neither source.

Shared-memory quirks confirmed on the real game (the code defends against
each):

- `mEstimatedLapTime`: every vehicle gets the same track default → unused;
  faster-class detection uses the class hierarchy instead
  (Hypercar > LMP2 > GTE > GT3)
- `mSectorFlag`: garbage values (e.g. 11) appear constantly → only 0→1..2
  transitions (edges) are trusted
- Extended `mCurrentPitSpeedLimit`: sometimes left at 0 → 80 km/h fallback
- `mWear`: a fresh tyre reads 1.0 (remaining-life convention) — confirmed
- No ghost-vehicle flag → stopped/slow cars are filtered by speed estimation;
  private practice/qualifying still streams phantom participants, so traffic
  calls default to race sessions only
- Session clock keeps running during the pre-start grid wait → the briefing
  uses the game phase, not elapsed time, to tell a race start from a mid-join

Future ideas: two-way radio via STT, multi-stint fuel-plan optimisation.

## Requirements

- Windows 10/11 (shared-memory mode) — replay mode is OS-independent
- Python 3.11+ — only for the Python app and the voice-pack tools, not for the SimHub plugin
- LMU + [rF2 Shared Memory Map Plugin](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin) (The Iron Wolf)

## Credits

- Shared-memory plugin/layout: The Iron Wolf
- Python struct mapping reference: [pyRfactor2SharedMemory](https://github.com/TonyWhitley/pyRfactor2SharedMemory)

## License

[MIT](LICENSE)
