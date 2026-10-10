# Making skins

A skin is a web page. H2ODashboard serves it from a local web server and streams the game
state to it about 60 times a second. The same page works in the app's windows, as a
click-through overlay over the game, and as an OBS Browser Source.

## Where skins live

Each skin is a folder with an `index.html`, in the `skins` folder next to `H2ODashboard.exe`
(in this repo: `skins/`, copied to the output when building):

```
skins/
  myskin/
    index.html
    gauge.svg        any other files the page uses
    font.woff2
```

The folder name is the skin's name in the app's skin list. The list is read when the app
starts, so restart it after adding a skin. Changes to an existing skin's files show up when
the page reloads (nothing is cached).

The built-in skins (`cabinet`, `speedrun`, `speedrunplus`, `splits`, `timeattack`) are good
starting points: copy one into a new folder and edit it.

## A minimal skin

```html
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>H2Overdrive - My skin</title>
<meta name="h2o-size" content="400x160">
<link rel="stylesheet" href="/h2o.css">
<style>
  body { display: flex; align-items: center; justify-content: center;
         font-family: "Segoe UI", system-ui, sans-serif; color: #fff;
         text-shadow: 0 0 3px #000, 0 0 6px #000; }
  .panel { width: 360px; padding: 20px; }
  .speed { font-size: 96px; font-weight: 800; line-height: 1; }
  .idle { opacity: 0.35; }
</style>
<script src="/h2o.js"></script>
</head>
<body>
<div class="panel" id="panel">
  <div class="speed" id="speed">0</div>
  <div id="unit">MPH</div>
</div>
<script>
h2o.fit(document.getElementById('panel'));
h2o.onFrame(f => {
  document.getElementById('panel').classList.toggle('idle', !f.inRace);
  h2o.setText(document.getElementById('speed'), f.speed.dial.toFixed(0));
  document.getElementById('unit').textContent = f.unit;
});
</script>
</body>
</html>
```

Every skin needs the two lines that load the skin API: `<link rel="stylesheet"
href="/h2o.css">` and `<script src="/h2o.js"></script>`. Load your own files with paths
relative to the skin folder (`gauge.svg`, `./font.woff2`).

## The skin API (`/h2o.js`)

| Call | What it does |
|---|---|
| `h2o.onFrame(frame => ...)` | Called for every game frame (~60 Hz) with the frame object below. Called right away with the last frame if there is one |
| `h2o.onStatus(connected => ...)` | Called when the game connects or disconnects, and once right away |
| `h2o.last`, `h2o.connected` | The latest frame and connection state |
| `h2o.params` | `URLSearchParams` of the page URL, for your own options (`?decimals=1`) |
| `h2o.fit(element)` | Scales the element to fill the page, keeping its shape. Lay the element out at a fixed size in CSS pixels; `?fit=0` turns scaling off |
| `h2o.setText(element, text)` | Shows text with one fixed-width slot per character, so changing numbers don't shift sideways. Only touches the page when the text changes |
| `h2o.formatTime(seconds, decimals = 2)` | `83.456` -> `"1:23.46"` |
| `h2o.colourParam(name, cssVar)` | If the URL has `?name=c1,c2,...` (hex without `#`, or CSS colour names), sets the CSS variable to that list. Use it as `linear-gradient(90deg, var(--my-colours))`; one colour gives a solid fill |

`h2o.setText` puts each character in a `span.h2o-ch` (narrow characters like `.` and `:`
also get `h2o-narrow`). `/h2o.css` sizes them; override `.h2o-ch` in your skin to change
the slot widths.

## Frame fields

Speeds are in world units per second unless noted.

| Field | Meaning |
|---|---|
| `connected` | The game is running and publishing data. While `false`, don't rely on the other fields (they are zeros or the last values seen); frames also stop arriving, so react in `onStatus` |
| `inRace` | The player's boat exists (in a race) |
| `frame`, `time` | Frame counter and seconds since the game started publishing |
| `metric`, `unit` | Operator "Units Of Measure" setting, and `"MPH"` or `"KPH"`. Only the label changes; the game shows the same numbers in both |
| `speed.dial` | Speed on the in-game HUD dial's scale (0 when stopped) |
| `speed.game` | Speed on the cabinet digits' scale, about 2% higher than `dial`, not capped |
| `speed.raw` | True speed in units/s |
| `velocity` | `[x, y, z]` |
| `hSpeed` | Horizontal speed |
| `needle` | HUD needle position 0..1 (smoothed and capped, vibrates like the game's) |
| `boost` | Boost fuel 0..12 |
| `boostLevel` | 0 = no fuel, 1 = boost fuel, 2 = gold boost |
| `boosting` | The boost is firing |
| `gold`, `goldTime` | Gold boost active (the boost bar doesn't drain), and its seconds left (only counts down while boosting) |
| `throttle` | Throttle the game applies this frame, 0..1, not smoothed |
| `pedal` | Throttle input as read |
| `engineThrottle` | Smoothed throttle (what the cabinet's speed-gauge LED ring shows) |
| `airborne` | Propeller out of the water: the engine doesn't push |
| `speedCap` | Current maximum speed, raised while boosting |
| `boostWasted` | Boost firing in the air at the speed limit, so it adds nothing |
| `digits.text` | The cabinet's 4-digit readout as text (speed, messages, final place, `----` at low speed) |
| `digits.segments` | 32 brightness values 0..10: 4 digits x `[a, b, c, d, e, f, g, decimal point]` |
| `meters.speed`, `meters.boost` | The cabinet's two 24-LED bars, brightness 0..10 each. `meters.speed` (the ring around the speed gauge) shows the throttle, not the speed |
| `speedBar`, `boostBar` | Number of lit LEDs in each bar, 0..24 |
| `race.time` | Race clock in seconds (stops at the finish) |
| `race.timeLeft` | Countdown in seconds |
| `race.checkpoint` | Checkpoints passed this race. The finish line is not a checkpoint |
| `race.splits` | One `{ time, best, delta }` per checkpoint passed: race time there, the game's best for that checkpoint, and `time - best` (negative = ahead). `best`/`delta` are `null` without a record |
| `race.best` | The game's best time for each of the 10 checkpoint slots. Slots past the course's last checkpoint can hold leftover values, so use `race.course` |
| `race.course` | Number of checkpoints on this track, learned from a finished race; `null` until then |
| `race.track` | Track slot 0..19 (track 0..9, +10 when mirrored) |
| `race.finished`, `race.place` | Player crossed the finish line, and their place |
| `race.record` | Track record in seconds, `null` = none |
| `race.finish` | After the finish: `{ time, best, delta }` against the record, else `null` |
| `race.ta` | Time attack timing kept by the dashboard, in integer **milliseconds**: `{ live, track, time, splits, finish, newPb, pb, wr }`. `pb` and `wr` are `{ time, splits }` or `null` |

Times shown by the game are truncated to hundredths, not rounded. To match the HUD, cut
values off rather than rounding them (the `splits` and `timeattack` skins show how).

To see a live frame, open `http://127.0.0.1:8765/data.json` (or your `--port`) in a browser.

## Background, size and OBS

**Background.** Don't give `html` or `body` a background colour; `/h2o.css` sets it from
the URL's `?bg=` option:

| `?bg=` | Background | Use |
|---|---|---|
| `transparent` (default) | None | Overlays and OBS Browser Source |
| `key` | Solid colour, `?key=00ff00` (default green) | Window capture with a chroma key |
| `panel` | Dark blue | Looking at it in a normal window |

The page gets `<html data-bg="...">`, so a skin can adapt, for example hiding a "Waiting
for H2Overdrive..." message on a transparent background:
`html[data-bg="transparent"] .waiting { display: none; }`.

**Size.** Declare the size the skin is designed for:

```html
<meta name="h2o-size" content="560x250">
```

The app shows it as the "OBS size" to use for a Browser Source. Then call
`h2o.fit(panel)` on your main element so it scales uniformly to whatever size the window,
overlay or Browser Source has. An SVG skin can use `preserveAspectRatio="xMidYMid meet"`
instead.

**Overlays.** Overlay windows are transparent and click-through. In edit mode (Ctrl+Shift+D)
the app adds the class `h2o-edit` to `<html>` and `/h2o.css` draws a frame so the overlay
can be moved and resized. Use `html.h2o-edit` in your CSS if the skin needs to look different
while being positioned.

**Readability.** Over a game or stream background, an outline keeps text readable:
`text-shadow: 0 0 3px #000, 0 0 6px #000;`.

## URL options

Give users options through the URL instead of making them edit the skin. They can then set
them in the OBS Browser Source URL. Read them with `h2o.params`, and document them in a
comment at the top of the skin's script (the built-in skins all do this):

```js
// URL options:
//   ?decimals=0|1|2   digits after the point (default 0)
const decimals = parseInt(h2o.params.get('decimals') ?? '0', 10) || 0;
```

For colours, put them in CSS variables at the top of the skin's `<style>` and call
`h2o.colourParam` so the URL can override them:

```js
h2o.colourParam('barColours', '--bar-colours');   // ?barColours=ff0000,ffff00
```

## Testing without the game

`tools/mock_feed.py` (Python 3, no extra packages) writes a fake race into the same shared
memory the game uses. Start H2ODashboard, then run:

```
python tools/mock_feed.py
```

It loops a 150-second race: speed changes, throttle, boost filling and firing, gold boost,
short jumps, five checkpoints with splits, the finish, then a few seconds outside the race.
Don't run it while the game is running.

You can also open the skin in a normal browser, for example
`http://127.0.0.1:8765/skins/myskin/?bg=panel`, and use the browser's developer tools.
`http://127.0.0.1:8765/` lists all skins.

## Good to know

- Everything is served from `127.0.0.1` only. Put fonts and images in the skin folder
  rather than loading them from the internet, so the skin works offline.
- The server knows these file types: `.html .js .css .json .png .jpg .gif .webp .svg .woff
  .woff2 .ttf`.
- `onFrame` runs 60 times a second: update only what changed, and prefer `transform`,
  `width` or `opacity` changes over rebuilding the page. `h2o.setText` already skips
  unchanged text.
- Fade the skin out (`inRace` false) or show a waiting message (`onStatus`) so it doesn't
  look frozen between races or before the game starts.
