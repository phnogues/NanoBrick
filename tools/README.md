# Tools (Arduino, ESP32 + nRF24)

Same wiring as the C# firmware ([docs/wiring.md](../docs/wiring.md)). Build and flash with the Arduino IDE
or `arduino-cli compile --fqbn esp32:esp32:esp32 -u -p COM5 <folder>` (ESP32 core 3.x).
Serial monitor at 115200 baud. Comments and messages are in French.

## `Sniffer/` — decode a 2.4 GHz remote
The tool used to reverse-engineer both remotes. Commands:
- `s`: scan channels 0–125 at the 3 data rates, packets per channel
- `t <sec>`: repeated 1 Mbit/s scans, to find which channels a remote hops on
- `d <rate> <ch>`: dump packets on a channel (rate 1 = 1 Mbit/s, 2 = 2 Mbit/s, 0 = 250 kbit/s)
- `h <rate> c1 c2 …`: dump while hopping over up to 8 channels
- `r <sec>`: energy per channel (RPD, > −64 dBm), whatever the protocol
- `a 0..5`: listening address — 0 = XN297 preamble (default), 1–4 = "promiscuous" trick (2-byte address,
  catches any nRF24-compatible frame, noisy: real frames stand out because they repeat), 5 = grey remote address
- `x`: stop

## `JM039TX/` — Arduino transmitter (alternative, not maintained)
The first working transmitter, before the C# port: same text commands, but over the **USB serial port**.
It is faster than the C# firmware (all 4 receivers locked without losing commands).
`remote.html` is a Web Serial page (Chrome / Edge) to drive it from the browser; the Windows app
`src/Remote` can also use it over USB.
