# teamradio56 🏁🎙️

**A race engineer in your ear for Le Mans Ultimate.**

teamradio56 is a SimHub plugin that watches your race and talks to you on
team radio, in English. It calls cars alongside, faster classes coming
through, flags, damage, fuel and the gaps that matter. When nothing matters,
it stays quiet. No Python, no extra apps: you switch on one setting in LMU,
install the plugin, download the voice pack once from the settings screen,
and drive.

한국어: [README.ko.md](README.ko.md)

A few minutes of a race sound like this:

> "Radio check. Race day, here we go."
> "Car left!"
> "Faster traffic, Hypercar in 2 seconds."
> "Impact. Checking wheels and pressures."
> "Check done. Just marks. Wheels, tyres, pressures all fine. Carry on."
> "FCY pit open. Box box."
> "Chequered flag. P3, that's a podium!"

(The name comes from Garage 56, the Le Mans entry for experimental cars.)

---

## What it calls

It speaks only when something is worth saying. There is no lap-by-lap
chatter unless you turn on the optional reports.

| Situation | What you hear (examples) |
|---|---|
| **Session start** | A radio check, then a briefing that fits the session. In a race that means your start position in class, fuel, then the formation lap and the green call: *"Formation lap, get heat in the tyres and brakes. Hold your slot, I'll call the green."* |
| **Race start** | For the first seconds after the green, left and right calls only, so you can concentrate on turn one: *"Through the start it's left and right calls only."* |
| **Spotter** | *"Car left!"*, *"Car right!"*, *"Three wide, you're the meat!"*, *"Clear left."* |
| **Traffic** | Faster classes coming up behind: *"Faster traffic, LMP2 in 3 seconds."* Slower cars ahead: *"Backmarker ahead, GT3. Easy does it."*, *"Car ahead is a lap down. Not a fight."* A stopped car ahead in a race: *"Stopped car ahead. Change your line early."* |
| **Flags and race control** | *"FCY. Eighty kph, no overtaking."*, *"Yellow in sector 2. No overtaking, be ready to lift."*, *"Blue flag. Let him by on the straight."* Plus restarts, the pit limiter, and time-remaining and final-lap calls. |
| **Damage** | On impact: *"Impact. Checking wheels and pressures."* About eight seconds later you get the result. Slow punctures are spotted for you: *"Front left losing pressure. Slow puncture. We change it next stop."* A lost wheel, a bent alignment and aero damage are called too. |
| **Car condition** | Water and oil temperature, brake temperature: *"Brakes averaging 820 degrees. Brake a touch earlier, cool them."* |
| **Fuel and pit stops** | *"Fuel critical. Box within 2 laps."*, *"Box box, box box."*, a fuel-save target when you are borderline on a no-stop run, *"Pit window open. Box within 3 laps."* Fuel is in litres. |
| **Rivals and gaps** | *"Gap behind 2.4 seconds. Opening up. Good."*, *"P4 in class, car ahead, just pitted. I'll call the gap when he's out."*, *"Car behind in class is 0.7 a lap quicker. With us in 8 laps. Be ready."* |
| **Chequered flag and after** | The flag call depends on your result (win, class win, podium, places gained or lost): *"Chequered flag. P3, that's a podium!"* Then a safety call on the cool-down lap and a short summary. After you finish, only safety calls remain. |
| **Optional reports** (off by default) | For driving with the HUD off: your lap time every lap (*"Last lap 2 01.8. Best lap."*) and a position, gaps, fuel and tyres report every few laps. |

### Multiclass

- Other classes are called by name: **Hypercar, LMP2, LMP3, GTE, GT3.**
- Only **faster** classes get a warning as they come up behind you, plus a
  car of your own class that is coming round to lap you. Slower classes ahead
  are backmarkers.
- A car of **your own class** is never called by its class name. In an
  LMP3-only race you hear *"Car behind."*, not *"LMP3 right behind."*, and a
  leader coming to lap you is *"Traffic behind, 3 seconds."*. A car of your
  own class on the same lap is a battle, not traffic.
- If the game reports a class the plugin does not recognise, the car is
  called without a class name. It only gets a warning if it is clearly
  faster on lap times.

---

## What you need

- **Windows 10 or 11**
- **Le Mans Ultimate**, version 1.2 or later. teamradio56 reads the data LMU
  shares by itself, so there is nothing to add to the game. You only switch
  it on once (see [Install](#install), step 1).
- **[SimHub](https://www.simhubdash.com/)**
- An internet connection only for downloading updates and the voice pack.

**Optional:** the **rF2 Shared Memory Map Plugin** by The Iron Wolf. Earlier
versions of teamradio56 needed it. Now it is a fallback: install it only if
your LMU is older than 1.2 or the connection doesn't work without it. If you
already have it, keep it. teamradio56 then also takes the pit speed limit and
the penalty messages from it. Without it, the pit limiter warning uses a
default limit and penalty calls don't name the penalty.

You do **not** need Python or anything else from this repository.

---

## Install

A step-by-step guide with more detail and troubleshooting is in
[docs/INSTALL.md](docs/INSTALL.md) (in Korean). The short version:

**1. Let LMU share its data** (once)

1. In LMU, open **Settings → Gameplay** and turn on **Enable Plugins**.
2. Quit LMU completely and start it again. The setting only takes effect
   after a full restart.

That's all on the game side. If the plugin later stays on
**○ waiting for game** even in a session, or your LMU is older than 1.2,
install the [rF2 Shared Memory Map Plugin](#optional-rf2-shared-memory-map-plugin)
as well.

**2. teamradio56**

1. Download `teamradio56-plugin-x.y.z.zip` from the
   [latest release](https://github.com/hyungwookc07/teamradio56/releases/latest).
   That one file is all you need. You can ignore the other files, and the
   voice pack installs from inside the plugin. The zip contains two files: `TeamRadio56.Core.dll` and `TeamRadio56.SimHub.dll`.
   (Optional: the release notes list the zip's SHA-256 under "파일 검증" (file
   verification), with the commands to check your download.)
2. Close SimHub. Copy **both** DLLs into the SimHub install folder itself,
   not a subfolder. The default is `C:\Program Files (x86)\SimHub\`.
3. Start SimHub. When it asks whether to enable the new plugin, tick
   **teamradio56**. (If it doesn't ask, enable it under SimHub
   **Settings → Plugins**.)
4. **teamradio56** now appears in SimHub's left menu. Click it to open its
   settings screen.

### Optional: rF2 Shared Memory Map Plugin

Only if you need the fallback (see [What you need](#what-you-need)). Skip
this if you already have it enabled.

1. Download `rFactor2SharedMemoryMapPlugin64.dll` from the
   [Iron Wolf releases](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin/releases).
2. Copy it into `<game folder>\Plugins\`, where `<game folder>` is usually
   `<Steam library>\steamapps\common\Le Mans Ultimate`.
3. Start LMU once with the DLL in place, then quit. This creates the
   settings entry.
4. With the game closed, open
   `<game folder>\UserData\player\CustomPluginVariables.JSON`, find the
   `"rFactor2SharedMemoryMapPlugin64.dll"` entry and change `" Enabled"` to
   `1`. Keep the space before `Enabled`. Don't paste anything in; just change
   the value. The other lines in that entry can stay as they are. It should
   look like this:

   ```
   "rFactor2SharedMemoryMapPlugin64.dll": {
     " Enabled": 1,
     ...
   ```

If both are available, teamradio56 uses LMU's own data and takes only the
pit speed limit and the penalty messages from the plugin.

---

## First run

1. **Switch the settings screen to English.** It opens in Korean. Use the
   language dropdown at the top (it shows **한국어**) and pick **English**.
2. **Install the voice pack.** Until you do, a red banner at the top says
   there is no voice pack, and every call appears as a subtitle only, with no
   sound. Scroll down to **Updates** and press **[Update voice pack]**, then
   confirm. If the button is greyed out, press **[Check for updates]** first.
   The download is under 200 MB. You can watch the progress or **[Cancel]**.
   When it finishes, the radio switches over by itself, with no restart
   needed.
3. **Check the sound.** Press **[Test speak]** in the **Status** section. You
   should hear *"Box box, box box."* in your default Windows audio device.
4. **Drive.** Once you are in a session in LMU, the lamp in **Status** turns
   to **● LMU connected** (instead of **○ waiting for game**). The line under
   it says where the data comes from: **Data: LMU built-in shared memory**
   (no DLL needed) or **Data: rF2 Shared Memory Map Plugin (DLL)**. Live
   position, lap and fuel appear below that, together with the most recent
   radio calls (up to eight).

The volume follows Windows. If the radio is too loud or quiet, adjust
SimHub in the Windows volume mixer.

---

## Updating

You can update teamradio56 from inside SimHub. No need to download anything
by hand. (New versions are published on the
[Releases page](https://github.com/hyungwookc07/teamradio56/releases).)

- Open the teamradio56 settings, go to **Updates** and press
  **[Check for updates]**. You see your version and the latest one, your
  voice pack and the latest one, and what's new.
- **[Update plugin]** installs the new version. **Restart SimHub** afterwards
  to use it.
- **[Update voice pack]** lights up when a newer voice pack is out. It
  applies by itself when the download finishes.
- With **Check on startup** ticked (the default), the plugin checks once a
  day, shortly after SimHub starts, and shows a banner when something new is
  available. **It only checks.** Nothing is installed until you press the
  button yourself and answer **Yes**.

Downloads are verified before anything is replaced. If a plugin update
fails halfway, your old version is put back. Updates only go forward; to go
back to an older version, download it from the Releases page and copy its
two DLLs by hand.

---

## Settings

Changes are saved as you make them. Press **[Restart engine]** in the
**Engine** section to apply them to the radio; a note next to the button
shows while there are changes to apply (Voice output applies right away).
Hover over a setting, or a section title marked **ⓘ**, to see a short
explanation.

| Section | Setting | Default | What it does |
|---|---|---|---|
| **Status** | [Test speak], [Open log], [Open settings file] | | Sound check, the log file, the settings file. |
| **Engine** | Voice pack folder | empty | Leave empty to find the voice pack automatically. |
| | [Restart engine] | | Applies changed settings. |
| **Voice** | Voice output | on | Off = subtitles and recent radio only, no sound. |
| **Chatter** | Preset | normal | **quiet**: calls spaced further apart, essentials only. **chatty**: more frequent calls. Urgent calls (car alongside, impact, puncture, pit limiter) come through on every preset. |
| **Traffic / Spotter** | Alongside window | 4.6 m | How close a car must be to count as alongside (4.6 m is about one car length). |
| | Start spotter mode | 45 s | After the start, left and right calls only for this long. |
| | Swap left/right | off | Tick it if "left" and "right" sound reversed. |
| | Race only | on | Traffic and spotter calls in races only. In LMU online practice and qualifying you drive in your own private session, but the game still reports other players' cars, so without this you'd get calls about cars that aren't on your track. Untick it if you share the track in practice. |
| **HUD-replacement reports** | Lap time every lap | off | *"Last lap 2 01.8. Best lap."* |
| | Status report | 0 (off) | Position, gaps, fuel and tyres every N laps. |
| **Behaviour** | Speak only while driving | on | Silent in the monitor and menus. |
| **Updates** | [Check for updates], [Update plugin], [Update voice pack], [Cancel], [Releases page] | | See [Updating](#updating). |
| | Check on startup | on | Checks for a new version at most once a day. |

### On-screen subtitles and spotter bars (optional)

For SimHub Dash Studio overlays, the plugin provides properties (all under `TeamRadio56Plugin.`) such as
`TeamRadio56Plugin.LastRadio` (the last call as text),
`LastRadioAgeSec` (seconds since it was said), `SpotterLeft` /
`SpotterRight`, `GapAheadSec` / `GapBehindSec`, `ClassPosition` and
`HasDamage`. No ready-made dashboard is included; you build your own. The
full list is in the overlay section of [simhub/README.md](simhub/README.md#오버레이-simhub-프로퍼티)
(in Korean).

---

## Troubleshooting

**No sound at all, and a red banner at the top.**
No voice pack is installed, or its download was interrupted. Press
**[Update voice pack]** in **Updates** (press **[Check for updates]** first if
it is greyed out).

**Some calls show "(not spoken)" in Recent radio.**
That line isn't in your installed voice pack yet, so it's shown as text only.
Update the voice pack. If it keeps happening,
please report it with your log.

**[Test speak] is silent even though the voice pack is installed.**
Check SimHub's volume in the Windows volume mixer and your default audio
device. If the line under the button says "the voice pack couldn't play it —
silent", open the log (**[Open log]**) and include it when you report. If it
says "no voice pack — silent", press **[Update voice pack]**.

**[Test speak] works, but the radio is silent on track.**
Check that **Voice output** (under **Voice**) is ticked. [Test speak] plays
even when it is off.

**The lamp stays on "○ waiting for game".**
LMU isn't sharing its data yet. Check that **Settings → Gameplay → Enable
Plugins** is on in LMU and that you restarted the game completely after
turning it on. You also need to be in a session, not in the main menu. If it
still doesn't connect, or your LMU is older than 1.2, install the
[rF2 Shared Memory Map Plugin](#optional-rf2-shared-memory-map-plugin). If you
use that plugin, check that the DLL is in the game's `Plugins` folder and that
`" Enabled": 1` (with the space) is set.

**The status says LMU's built-in shared memory "doesn't look as expected".**
A game update has probably changed how LMU shares its data. teamradio56 then
stops reading it, so it never acts on wrong values. Until a fixed version is
out, install the
[rF2 Shared Memory Map Plugin](#optional-rf2-shared-memory-map-plugin);
teamradio56 switches to it by itself. Enable Plugins is already on in this
case, so you don't need to change that setting. teamradio56 checks the data
again when you restart the game, so you don't need to restart SimHub. Please
report it with your log.

**No traffic calls in practice or qualifying.**
**Race only** is on by default. Untick it under **Traffic / Spotter** if you
share the track with others.

**Nothing is said while I'm in the monitor or menus.**
That's **Speak only while driving**. It's on by default.

**Left and right are the wrong way round.**
Tick **Swap left/right**.

**teamradio56 isn't in SimHub's plugin list.**
Both DLLs must be directly in the SimHub folder, not in a subfolder. Restart
SimHub afterwards.

**I hear the radio twice, in two different voices.**
An older stand-alone version of teamradio56 is also running. If you added
`launch_with_lmu.bat` to LMU's Steam launch options, remove it. Check Task
Manager for a leftover `python.exe` or `teamradio56.exe`.

**An update shows an error.**
- *cannot reach the internet*: you're offline, or a firewall or proxy is
  blocking GitHub.
- *release file not found*: a new release is being uploaded. Try again in a
  little while.
- *no write permission for the folder*: run SimHub as administrator once for
  the update, or install from the Releases page by hand.
- *the downloaded file is corrupt*: just try again. Your current version is
  unchanged.

**SimHub doesn't load teamradio56 after an update.**
Close SimHub and open `teamradio56_backup.txt` in the SimHub folder. Each
line reads `backup -> original`. For every line, delete the right-hand file
and rename the left-hand file to that name. Or simply copy the two DLLs from
an older release.

**Why is some text in Korean?**
The developer is Korean. The log file and the live status line in **Status**
are still written in Korean, even with the English settings screen.

**Reporting a problem.** Press **[Open log]** and attach `teamradio56.log`
(it lives in your SimHub folder) to a
[GitHub issue](https://github.com/hyungwookc07/teamradio56/issues).

---

## What it doesn't do

- **English radio only, one voice.** There is no voice choice and no Korean
  radio. The settings screen is in Korean and English.
- **One-way radio.** No voice commands, no speech recognition, no
  conversation.
- **No volume slider.** Use the Windows volume mixer.
- **No virtual energy, weather forecast or pit-strategy calls.** teamradio56
  doesn't use that data.

It's light on your PC: the voice clips are made in advance, so nothing heavy
runs while you drive.

---

## Privacy

- Your telemetry stays on your PC. Nothing about your driving is sent
  anywhere.
- The only time the plugin goes online is the update check and downloads.
  It reads a small text file from this project's GitHub Releases and, when you
  press an update button, downloads the plugin or voice pack from there. No
  account or login is used.
- Untick **Check on startup** and it never goes online unless you press a
  button.

---

## For developers

This repository also contains the Python reference app that the plugin's
behaviour is checked against and that renders the voice pack, plus the
plugin's source. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and
[simhub/README.md](simhub/README.md) (both in Korean).

## Credits

- rF2 Shared Memory Map Plugin: The Iron Wolf
- Shared memory layout reference: [pyRfactor2SharedMemory](https://github.com/TonyWhitley/pyRfactor2SharedMemory)
- LMU built-in shared memory layout: cross-checked against
  [pyLMUSharedMemory](https://github.com/TinyPedal/pyLMUSharedMemory) and
  [lmu-pitwall](https://github.com/Swizzjack/lmu-pitwall) (both MIT). The
  reader is our own code.
- The voice pack is rendered with the Kokoro text-to-speech model.

## License

[MIT](LICENSE)
