# teamradio56 🏁🎙️

**Voice race-engineer radio for Le Mans Ultimate.** Reads telemetry in real
time, judges what matters, and calls it to you as team radio — in English or
Korean. Runs as a SimHub plugin (recommended) or a standalone Windows app.
(The name comes from Le Mans' iconic Garage 56.)

한국어 문서: [README.ko.md](README.ko.md)

The goal is a race engineer that speaks only when it matters and says what
it means — not a stream of raw numbers.

> "Car behind closing four tenths a lap. Gap 2.4 seconds. Just no mistakes."

## Features

- **Judgment calls, not readouts** — traffic and side-by-side spotting,
  race control (FCY, pit open, restarts, yellows, blue flags), automatic
  damage checks, fuel and tyre warnings, rival pit/pace intel, gap trends,
  session briefings. All rule-based and deterministic
- **Silence discipline** — if there's nothing worth saying, it says nothing.
  No per-lap chatter; per-type cooldowns and a chattiness preset
- **Natural voice with zero latency** — urgent calls come from pre-generated
  phrase pools with a bundled, pre-rendered voice (Kokoro), so nothing is
  synthesised mid-corner. Anything not in the cache falls back to online
  TTS (edge) and is cached for next time
- **Radio effect** — bandpass, saturation, band-limited noise and squelch
  for a real team-radio texture
- **English or Korean** radio, and a bilingual settings UI
- **Multiclass aware** — faster-class approach warnings, in-class positions
- **Protects game performance** — 5 Hz polling, audio on its own thread, no
  GPU-based TTS
- **One-way voice output** — no speech recognition or conversation

LLM-generated lines (free-form strategy/briefing commentary via the
Anthropic API) exist as an **experimental, opt-in** feature of the Python
engine and need your own API key. They are hidden in the SimHub UI until the
feature is finished; everything above works without them.

## Quick start

See [docs/INSTALL.md](docs/INSTALL.md) for full installation (including the
shared-memory plugin). For the SimHub plugin, see
[simhub/README.md](simhub/README.md) — after the first build, updating is a
double-click on `update.bat` (pull + build + copy DLLs).

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

### SimHub plugin & C# port (v0.8–v0.13)

- [x] SimHub plugin with a bilingual (한국어/English) settings UI; runs the
  Python engine as a child process, or the **built-in C# engine** — all nine
  analyzers, session briefing, phrase pools, cached audio (Kokoro), runtime
  edge synthesis fallback, Korean radio. Verified against the Python engine
  by a replay regression (identical accepted events, en/ko, down to the
  sentence text). Only the experimental LLM lines still require Python mode.

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
- Python 3.11+ (not needed for the SimHub builtin engine)
- LMU + [rF2 Shared Memory Map Plugin](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin) (The Iron Wolf)

## Credits

- Shared-memory plugin/layout: The Iron Wolf
- Python struct mapping reference: [pyRfactor2SharedMemory](https://github.com/TonyWhitley/pyRfactor2SharedMemory)

## License

[MIT](LICENSE)
