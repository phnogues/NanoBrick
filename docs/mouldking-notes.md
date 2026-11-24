# Mould King 2.4 GHz protocol — notes for a future integration

This was the project's first attempt (code removed on 2026-11-24). **It did not work with either of
our systems** (selector receivers / grey remote, 4-output battery box / white remote), but it may be
useful for genuine **Mould King** boxes driven by a Mould King 2.4 GHz remote.

## Source
- Ported from **DIY-Multiprotocol-TX-Module** (GPLv3): https://github.com/pascallanger/DIY-Multiprotocol-TX-Module
  (`MouldKg_nrf24l01.ino` for the protocol, `XN297_EMU.ino` for the XN297 emulated on the nRF24).
- The XN297 coding is **already in this repository** (`tools/JM039TX/xn297.h`, `src/NanoBrick/Protocols/BatteryBoxProtocol.cs`):
  only the part below is missing.

## What the port did
- XN297, 1 Mbit/s, **3-byte** address. Binding on address `'K' 'D' 'H'`: the transmitter sends on
  **channel 11**, the box answers on **channel 76**, every 12 ms.
- Bind frame (7 bytes): `C0 ID0 ID1 ID2 00 00 00` (ID = 3 bytes chosen by the transmitter).
  Box answer: `xx ID0 ID1 ID2 RX0 RX1 RX2` → RX = box ID.
- Then one frame every **5 ms**, **frequency hopping** on 4 channels derived from the box ID:
  ```
  n = 0; ch = 0x0C
  if (count & 4) { n = 1; ch += 0x20 }
  if (count & 2) ch += 0x10 + (RX[n] >> 4)
  else           ch += RX[n] & 0x0F
  ```
- **Digital** mode (on/off, 5 bytes): even frames `30 ID0 ID1 ID2 V`, odd frames `31 ID0 ID1 ID2 V`
  - even V = 0x60 | A | D; odd V = B | C | A | D
  - A fwd 01 / rev 02, D 04 / 08, B 40 / 80, C 10 / 20
- **Analog** mode (proportional speed, 10 bytes): `36 ID0 ID1 ID2 B A C D 80 80`,
  each output 0x00–0xFF, **0x80 = stop**.

## To integrate
1. Binding: send on 11, listen for the answer on 76 (needs nRF24 RX mode and `xn297::decode`, already written).
2. Frequency hopping and both frame formats (`MouldKingProtocol.cs` next to `BatteryBoxProtocol.cs`).
3. Check with the sniffer against a real Mould King remote before trusting the port.
