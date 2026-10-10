"""Fake H2Overdrive dashboard feed, for working on skins without the game.

Creates the shared memory `Local\\H2ODashboard` (layout version 6, see
docs/shared-memory.md) and plays a looping race at 60 Hz: speed, throttle,
boost and gold boost, short jumps, checkpoints with split times, the finish,
then a few seconds outside the race. Start H2ODashboard, then run:

    python tools/mock_feed.py

Run only one writer at a time (not together with the game), or the gauges
flicker between the two.
"""
import math
import mmap
import struct
import time

MAGIC, VERSION, SIZE = 0x48324F44, 6, 320
LAYOUT = struct.Struct('<IIIi d Iii f3fff 80s ififfi 10f10f iiif ff 10f fff')
assert LAYOUT.size == SIZE

CHECKPOINT_EVERY = 24.0   # seconds between checkpoints
CHECKPOINTS = 5
FINISH = 150.0            # race time at the finish
BEST = [23.81, 47.95, 71.62, 95.40, 119.33]
RECORD = 149.12
OUTSIDE = 5.0             # seconds between races

# seven-segment bits (a..g) for the cabinet speed digits
SEGMENTS = {'0': 0x3F, '1': 0x06, '2': 0x5B, '3': 0x4F, '4': 0x66, '5': 0x6D,
            '6': 0x7D, '7': 0x07, '8': 0x7F, '9': 0x6F, '-': 0x40, ' ': 0}


def dashboard_buffer(text, meter0, meter1):
    """80 brightness bytes: 4 digits x 8 segments, then two 24-LED bars."""
    dash = bytearray(80)
    cells = []  # (character, decimal point after it)
    for ch in text:
        if ch == '.' and cells:
            cells[-1] = (cells[-1][0], True)
        else:
            cells.append((ch, False))
    cells = ([(' ', False)] * 4 + cells)[-4:]
    for i, (ch, point) in enumerate(cells):
        bits = SEGMENTS.get(ch, 0)
        for s in range(7):
            if bits & (1 << s):
                dash[i * 8 + s] = 10
        if point:
            dash[i * 8 + 7] = 10
    for start, value in ((32, meter0), (56, meter1)):
        for i in range(round(max(0.0, min(1.0, value)) * 24)):
            dash[start + i] = 10
    return bytes(dash)


def main():
    shm = mmap.mmap(-1, 4096, tagname='Local\\H2ODashboard')
    seq, frame = 0, 0
    t0 = time.perf_counter()
    boost, gold = 0.0, 0.0
    print('Writing Local\\H2ODashboard (Ctrl+C to stop)')
    while True:
        now = time.perf_counter() - t0
        cycle = now % (FINISH + 4.0 + OUTSIDE)
        in_race = cycle < FINISH + 4.0
        race_time = min(cycle, FINISH)
        finished = in_race and cycle >= FINISH
        dt = 1 / 60

        # speed and throttle
        target = 0.0 if finished else 760 + 140 * math.sin(race_time / 6.0) + 60 * math.sin(race_time * 1.7)
        speed = max(0.0, target) if in_race else 0.0
        airborne = in_race and not finished and (race_time % 7.0) > 6.3
        pedal = 0.0 if finished else 0.95
        throttle = 0.0 if finished else min(1.0, pedal * 1.1)

        # boost fills while driving, fires every 10 s; gold boost in the middle of the race
        boosting = in_race and not finished and (race_time % 10.0) > 7.5 and boost > 0
        if boosting:
            boost = max(0.0, boost - 4.0 * dt)
            gold = max(0.0, gold - dt)
        elif in_race and not finished:
            boost = min(12.0, boost + 1.5 * dt)
        if in_race and 60.0 < race_time < 60.0 + dt:
            gold = 4.0
        if not in_race:
            boost, gold = 0.0, 0.0
        level = 2 if gold > 0 else 1 if boost > 0 else 0

        # checkpoints and splits
        passed = min(CHECKPOINTS, int(race_time // CHECKPOINT_EVERY)) if in_race else 0
        raw = [CHECKPOINT_EVERY * (i + 1) - 0.0137 * (i + 1) for i in range(passed)] + [0.0] * (10 - passed)
        splits = [math.floor(x * 100) / 100 for x in raw]
        time_left = max(0.0, 60.0 + 20.0 * passed - race_time) if in_race else 0.0

        cabinet_mph = min(0.25703 * speed - 5.05, 210.5)  # the real cabinet tops out here
        text = '----' if speed < 40 else f'{cabinet_mph:.1f}'
        dash = dashboard_buffer(text, throttle, boost / 12.0)
        cap = 1100.0 if boosting else 900.0

        values = (MAGIC, VERSION, SIZE, 0, now, frame, int(in_race), 0,
                  speed, speed, 0.0, 0.0, boost, min(1.0, speed / 838.5394), dash,
                  level, gold, 0x8000 if boosting else 0, race_time if in_race else 0.0, time_left, passed,
                  *splits, *(BEST + [0.0] * 5),
                  3, int(finished), 1 if in_race else 0, RECORD,
                  pedal if in_race else 0.0, throttle if in_race else 0.0,
                  *raw,
                  0.0 if airborne or not in_race else 1.0, cap if in_race else 0.0, speed)
        payload = LAYOUT.pack(*values)

        seq += 1                                   # odd: writing
        shm[12:16] = struct.pack('<i', seq)
        shm[0:12] = payload[0:12]
        shm[16:SIZE] = payload[16:SIZE]
        seq += 1                                   # even: done
        shm[12:16] = struct.pack('<i', seq)

        frame += 1
        time.sleep(max(0.0, t0 + frame / 60 - time.perf_counter()))


if __name__ == '__main__':
    main()
