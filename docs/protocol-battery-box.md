# White remote → battery box with 4 outputs (A–D)

USB-C battery box with an orange button and a built-in receiver, sold with a white remote
(PCB `AW2101048`, 8 arrow buttons + 2 square buttons). Decoded with [`tools/Sniffer`](../tools/Sniffer).
Implemented in [`BatteryBoxProtocol.cs`](../src/NanoBrick/Protocols/BatteryBoxProtocol.cs) and checked against 10 real frames
in [`src/NanoBrick.Tests`](../src/NanoBrick.Tests).

## Pairing

Switch the box on without its remote: it pairs with the **first remote it hears** (any button).
Blue LEDs turn steady on both sides. **There is no ID in the frame**: the ESP32 pairs the same way.

## Radio

- **XN297** chip (scrambled, emulated on the nRF24: `TX_ADDR` = `55 0F 71 0C 00`, nRF24 CRC disabled),
  address **`C1 C2 C3 C4 C5`**, **fixed channel 2**, 1 Mbit/s.
- One frame every ~8 ms **while a button is held**, about 20 "nothing pressed" frames after release,
  then silence.
- XN297 coding: whitening LFSR (x⁷ + x³ + 1, seed `0x71`), CRC16-CCITT init `FFFF` over the clear address
  and data, data bytes bit-reversed — see `BatteryBoxProtocol.Encode`.

## Payload (10 bytes)

`00 00 00 00 01 01 S6 DIR SQ SUM`

| Byte | Meaning |
|---|---|
| 0–3 | always `00` (other values: no effect) |
| 4–5 | always `01 01` (`02`: the box ignores the frame) |
| 6 (`S6`) | `01` with the right square button |
| 7 (`DIR`) | one bit per arrow: A▲ `02`, A▼ `01`, B◀ `10`, B▶ `20`, C▲ `04`, C▼ `08`, D◀ `40`, D▶ `80` |
| 8 (`SQ`) | left square `01`, right square `02` |
| 9 (`SUM`) | (sum of bytes 0–8 + `0x14`) & `0xFF` |

## Behaviour

**On / off only, full speed**: the output runs while frames with its bit arrive and stops at the first
frame without it. No speed control was found (bytes 0–5, interleaving pressed / released frames and the
square buttons were all tried). Good for a gate, a crane, anything that runs at a single speed.
