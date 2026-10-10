# Shared memory feed

The ProjectH2O OpenParrot build (`H2Overdrive.cpp`, `PublishDashboard`) publishes the game
state once per frame into the named shared memory **`Local\H2ODashboard`** (4096-byte
mapping; the struct uses the first 320 bytes). H2ODashboard reads it, but any program can:
open the mapping read-only and copy the struct out as described below.

The feed exists only while the game runs under that OpenParrot build. `tools/mock_feed.py`
writes the same layout with a fake race for testing.

## Reading a consistent frame

`sequence` (offset 12) is odd while the game is writing and even when a frame is complete.

1. Read `sequence`. If it is odd, try again.
2. Copy the 320 bytes.
3. Read `sequence` again. If it changed, discard the copy and try again.

Check `magic`, `version` and `size` once after opening: H2ODashboard accepts only
`0x48324F44`, `6`, `320`.

## Layout (version 6, 320 bytes, little-endian, 4-byte packing)

Boat offsets refer to the player's boat object in `sdaemon.exe`; addresses are absolute
(the exe has no relocations). Fields read from the boat are 0 outside a race.

| Offset | Type | Field | Meaning |
|---:|---|---|---|
| 0 | u32 | `magic` | `0x48324F44` (MSVC `'H2OD'`) |
| 4 | u32 | `version` | `6` |
| 8 | u32 | `size` | `320` |
| 12 | i32 | `sequence` | Odd while writing, even when done |
| 16 | f64 | `time` | Seconds since the feed started |
| 24 | u32 | `frame` | Frame counter |
| 28 | i32 | `inRace` | 1 while the player boat exists |
| 32 | i32 | `metric` | Operator setting "Units Of Measure": 1 = metric. Changes only the MPH/KPH label; the numbers are the same |
| 36 | f32 | `speed` | True speed in world units/s (boat `+0x13A4`) |
| 40 | f32[3] | `velocity` | Velocity vector (boat `+0x1300`) |
| 52 | f32 | `boost` | Boost fuel 0..12 (boat `+0x1478`) |
| 56 | f32 | `needle` | HUD speedometer needle position 0..1 (`0x78EBCC`) |
| 60 | u8[80] | `dash` | Cabinet dashboard brightness 0..10 (`0x78C4DC`), see below |
| 140 | i32 | `boostLevel` | 0 = empty, 1 = boost fuel, 2 = gold boost (boat `+0x1430`) |
| 144 | f32 | `goldBoost` | Gold boost seconds left, used up only while boosting (boat `+0x147C`) |
| 148 | i32 | `boosting` | Boat `+0x818 & 0x18000`: `0x8000` boost firing, `0x10000` super boost |
| 152 | f32 | `raceTime` | Player's race clock in seconds (stops at the finish) |
| 156 | f32 | `timeLeft` | Countdown in seconds (`0x78C0B0`) |
| 160 | i32 | `checkpoints` | Checkpoints passed this race (`0x78C070`); the finish line is not a checkpoint |
| 164 | f32[10] | `splits` | Race time at each checkpoint passed, rounded to 0.01 like the HUD |
| 204 | f32[10] | `bestSplits` | Best time per checkpoint for this track (`0x78C254`), what the HUD compares against. Slots past the course's last checkpoint can hold leftover values |
| 244 | i32 | `track` | Record slot: track 0..9, +10 when mirrored |
| 248 | i32 | `finished` | 1 once the player crossed the finish line |
| 252 | i32 | `place` | Player's place |
| 256 | f32 | `recordTime` | Track record in seconds, 0 = none |
| 260 | f32 | `pedal` | Throttle input as read (boat `+0x8C0`); the game uses `min(1, pedal * 1.1)` |
| 264 | f32 | `engineThrottle` | Smoothed throttle the engine uses, 0..1 (boat `+0x13B0`); drives the LED ring around the speed gauge |
| 268 | f32[10] | `rawSplits` | Race clock at each checkpoint, unrounded |
| 308 | f32 | `propInWater` | 1 = propeller under water (the engine pushes), 0 = in the air (boat `+0x13CC`) |
| 312 | f32 | `speedCap` | Current maximum speed, raised while boosting (boat `+0x874 / (+0x12E4)^2`) |
| 316 | f32 | `hSpeed` | Horizontal speed `|(+0x750, +0x758)|`, the value boost thrust is clamped on |

### `dash`: the cabinet dashboard

The same 80 bytes the game sends to the cabinet's speed/boost display, each a brightness
0..10. They are updated even without the hardware attached.

| Bytes | Content |
|---|---|
| 0-31 | Four seven-segment digits (the speed readout), 8 bytes each: segments a-g at +0..+6, decimal point at +7 |
| 32-55 | Bar meter 0: 24 LEDs around the speed gauge. Shows the throttle, not the speed |
| 56-79 | Bar meter 1: 24 LEDs, the boost bar |

The digits also show text messages and the final place, and `----` at low speed.

## Speed conversions

- HUD dial (0-180 scale): `mph = speed * 0.25228`
- Cabinet digits: `180 * ((needleInput * 4.11898) - 0.191986) / 3.44 + 5` with
  `needleInput = speed / 838.5394`, about `0.25703 * speed - 5.05`. The cabinet tops out
  near 210.5 because the needle is clamped at -5π/4.
- Times shown in game are truncated to hundredths, not rounded.

## Changes by version

| Version | Size | Added |
|---:|---:|---|
| 1 | 140 | Header, time, frame, inRace, metric, speed, velocity, boost, needle, dash |
| 2 | 244 | boostLevel, goldBoost, boosting, raceTime, timeLeft, checkpoints, splits, bestSplits |
| 3 | 260 | track, finished, place, recordTime |
| 4 | 268 | pedal, engineThrottle |
| 5 | 308 | rawSplits |
| 6 | 320 | propInWater, speedCap, hSpeed |
