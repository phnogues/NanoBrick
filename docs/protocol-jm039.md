# Grey remote `JM039TX` → receivers with a 1–4 selector

Decoded with [`tools/Sniffer`](../tools/Sniffer). Implemented in
[`src/NanoBrick/Jm039.cs`](../src/NanoBrick/Jm039.cs) and checked against 17 real frames in
[`src/NanoBrick.Tests`](../src/NanoBrick.Tests).

## Radio

- **Plain nRF24** (no XN297 scrambling): GFSK 1 Mbit/s, preamble `AA`, 5-byte address **`88 B4 87 2E CB`**
  (on-air order; the nRF24 `TX_ADDR` register is written in reverse), static payload, no ESB field,
  no auto-ack.
- nRF24 CRC **disabled**: the remote uses its own CRC (below).
- **Power: −18 dBm** with a PA+LNA module. At 0 dBm the receivers saturate at short range.

## Frame

After the address: `34 CA T 92 D6 7E 6A B S D1 D2` then a **CRC16**.

| Byte | Meaning |
|---|---|
| `T` | `42` / `46`: bit 2 toggles on every frame |
| `B` | `ED` at rest; bit 2 = left orange button (stop A), bit 3 = right orange button (stop B) — **0 when pressed** |
| `S` | selector position (table below) |
| `D1` | wheels, **0 when active**: b7 = left wheel dir 1, b6 = left dir 2, b5 = right dir 1, b4 = right dir 2 |
| `D2` | check byte: rest value for the position (table), XORed per active command: left 1 `^80`, left 2 `^40`, right 1 `^60`, right 2 `^10`, stop left `^0C`, stop right `^08` |

CRC16-CCITT (poly `0x1021`, not reflected) over **address + data**, initial value **`0x8ABC`**.

## Selector positions

| Position | S | D2 at rest | Channels |
|---|---|---|---|
| 1 | 29 | 1A | 12, 13, 57, 58 |
| 2 | 28 | 19 | 9, 10, 52, 53 |
| 3 | 2B | 18 | 7, 8, 41, 42 |
| 4 | 2E | 1B | 19, 20, 67, 68 |

Every frame is sent on the 4 channels of the position. Channels must be exact.

## Timing and behaviour

- One frame every **8 ms**; nothing is sent at rest.
- The wheels have notches: **one step = 5 active frames, then ≥ 6 neutral frames**. After the last step,
  19 neutral frames, then silence.
- The receiver **remembers the speed**: each step adds one; a few steps before the motor starts,
  ~20–25 steps for full speed. The orange button stops the output.
- Receiver light: **blinking = receives nothing**, steady = receives. It needs ~2–3 s of neutral frames
  to lock before taking commands.
- The selector is on the **receiver**: it only listens to its position. One transmitter can imitate the
  4 remotes at once (4 receivers × 2 outputs = 8 motors).
