// H2Overdrive dashboard skin API.
//
// In a skin:  <link rel="stylesheet" href="/h2o.css"> <script src="/h2o.js"></script>
//   h2o.onFrame(frame => { ... })      called for every game frame (~60 Hz)
//   h2o.onStatus(connected => { ... }) game connected / disconnected
//   h2o.params                          URLSearchParams of the page URL
//   h2o.fit(element)                    scale the element to fill the page, keeping its shape
//                                       (so an OBS Browser Source of any size has no empty margins;
//                                       ?fit=0 turns it off). Size the element itself in CSS pixels.
//   h2o.formatTime(seconds, decimals)   "1:23.45"
//   h2o.colourParam(name, cssVar)       if the URL has ?name=c1,c2,... (hex without # or CSS colour
//                                       names), set the CSS variable to that colour list (one colour
//                                       is repeated, so gradients become solid). Lets people restyle a
//                                       skin from the OBS URL. Use it as linear-gradient(90deg, var(--x))
//   h2o.setText(element, text)          show text with a fixed-width slot per character, so changing
//                                       numbers don't shift sideways (digits aren't all the same width
//                                       in most fonts); only touches the DOM when the text changes
//
// Frame fields:
//   connected, inRace, frame, time, metric, unit ("MPH" | "KPH")
//   speed.raw    true speed in world units/s (uncapped, unsmoothed)
//   speed.game   the game's scale, same as the cabinet digits but uncapped/unsmoothed
//   speed.dial   the HUD dial's scale (about 2% lower than speed.game)
//   velocity     [x, y, z] world units/s
//   boost        0..12
//   needle       HUD needle position 0..1 (smoothed, capped, vibrates like the game)
//   digits.text  cabinet digits as text, digits.segments: 32 brightness values (0..10),
//                4 digits x [a, b, c, d, e, f, g, decimal point]
//   meters.speed / meters.boost: 24 brightness values (0..10) each. Despite the name, the ring
//                around the cabinet's speed gauge shows the THROTTLE (smoothed), not the speed
//   speedBar / boostBar: number of lit LEDs (0..24)
//   throttle     throttle the game applies, 0..1, not smoothed (min(1, pedal * 1.1))
//   pedal        throttle input as read (the 1.1 lets a not-quite-floored pedal reach 100%)
//   engineThrottle  smoothed throttle the engine uses (what the speed-gauge LED ring shows)
//   boostLevel   0 = no fuel, 1 = boost fuel, 2 = gold boost
//   gold         true while gold boost is active (the boost bar doesn't drain)
//   goldTime     gold boost seconds left (only counts down while boosting)
//   boosting     true while the boost is firing
//   airborne     propeller out of the water (the engine doesn't push; the boost still does)
//   speedCap     current max speed in units/s (boosted while boosting)
//   hSpeed       horizontal speed in units/s
//   boostWasted  boost firing in the air at the limit, so it adds nothing: the game clamps the
//                horizontal speed to max(speed.raw, speedCap). On the water the boost is never wasted
//                (it holds the boosted cap). 1% tolerance; skins can recompute from the fields above
//   race.time        race clock in seconds
//   race.timeLeft    countdown in seconds
//   race.checkpoint  checkpoints passed this race
//   race.splits      one entry per checkpoint passed: { time, best, delta } in seconds
//                    (race time at the checkpoint, the game's best for that checkpoint,
//                    time - best: negative = AHEAD, positive = BEHIND; best/delta are null without a record)
//   race.best        the game's best split for each of the 10 slots (slots past the course's last
//                    checkpoint hold leftover values; use race.course)
//   race.track       record slot 0..19 (track 0..9, +10 = mirrored)
//   race.course      checkpoints on this track (learned from a finished race), null = not known yet
//   race.finished    player crossed the finish line (race.time then stays at the final time)
//   race.place       player's place
//   race.record      track record in seconds, null = none
//   race.finish      after the finish: { time, best (= record), delta }, else null
//   race.ta          time attack timing kept by the dashboard, in integer MILLISECONDS (unrounded
//                    race clock, not the game's truncated hundredths):
//                    { live, track, time, splits: [race time at each checkpoint passed], finish (null
//                    until the finish line), newPb, pb, wr }. pb (the personal best as it was when this
//                    run started) and wr (H2ODashboard.wr.ini) are { time, splits: [...] } or null.
//                    Outside a race: no run (time null, no splits), pb/wr for race.track.
//
// Background (URL ?bg=): transparent (default, for OBS Browser Source), key (solid colour,
// ?key=00ff00), panel (dark). Sets <html data-bg="..."> and the --h2o-key CSS variable.
(function () {
  const params = new URLSearchParams(location.search);
  const frameHandlers = [];
  const statusHandlers = [];
  let connected = false;
  let last = null;

  const root = document.documentElement;
  root.dataset.bg = params.get('bg') || 'transparent';
  const key = params.get('key');
  if (key) root.style.setProperty('--h2o-key', '#' + key.replace(/^#/, ''));

  function setConnected(value) {
    if (value === connected) return;
    connected = value;
    statusHandlers.forEach(h => h(connected));
  }

  const source = new EventSource('/data');
  source.onmessage = e => {
    last = JSON.parse(e.data);
    setConnected(last.connected);
    frameHandlers.forEach(h => h(last));
  };
  source.onerror = () => setConnected(false); // EventSource reconnects by itself

  function fit(element) {
    if (params.get('fit') === '0') return;
    // Keep the design size: in a flex page the browser would otherwise shrink the element when
    // the window is narrower than it, and the scale would be computed from the squashed size
    element.style.flexShrink = '0';
    const apply = () => {
      const w = element.offsetWidth, h = element.offsetHeight;
      if (!w || !h) return;
      const scale = Math.min(window.innerWidth / w, window.innerHeight / h);
      element.style.transformOrigin = 'center center';
      element.style.transform = `scale(${scale})`;
    };
    new ResizeObserver(apply).observe(element);
    window.addEventListener('resize', apply);
    apply();
  }

  function formatTime(seconds, decimals = 2) {
    const sign = seconds < 0 ? '-' : '';
    const scale = 10 ** decimals;
    const units = Math.round(Math.abs(seconds) * scale);
    const m = Math.floor(units / (60 * scale));
    const s = (units / scale - m * 60).toFixed(decimals);
    return m ? `${sign}${m}:${s.padStart(decimals ? decimals + 3 : 2, '0')}` : sign + s;
  }

  function colourParam(name, cssVar) {
    const value = params.get(name);
    if (!value) return;
    const colours = value.split(',').map(c => c.trim()).filter(Boolean)
      .map(c => /^[0-9a-f]{3,8}$/i.test(c) ? '#' + c : c)
      .filter(c => CSS.supports('color', c));
    if (!colours.length) return;
    if (colours.length === 1) colours.push(colours[0]);
    root.style.setProperty(cssVar, colours.join(', '));
  }

  const narrow = new Set([':', '.', ',', ' ', "'", '"']);
  function setText(element, text) {
    text = String(text);
    if (element.h2oText === text) return;
    element.h2oText = text;
    element.textContent = '';
    for (const ch of text) {
      const span = document.createElement('span');
      span.className = narrow.has(ch) ? 'h2o-ch h2o-narrow' : 'h2o-ch';
      span.textContent = ch;
      element.appendChild(span);
    }
  }

  window.h2o = {
    params,
    fit,
    formatTime,
    setText,
    colourParam,
    onFrame(handler) { frameHandlers.push(handler); if (last) handler(last); },
    onStatus(handler) { statusHandlers.push(handler); handler(connected); },
    get last() { return last; },
    get connected() { return connected; },
  };
})();
