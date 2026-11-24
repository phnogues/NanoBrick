# Wiring

| nRF24 adapter (5 V board) | ESP32 |
|---|---|
| VCC | **5V** (the adapter has its own 3.3 V regulator) |
| GND | GND |
| CE | GPIO 4 |
| CSN | GPIO 5 |
| SCK | GPIO 18 |
| MO (MOSI) | GPIO 23 |
| MI (MISO) | GPIO 19 |
| IRQ | not connected |

- Screw the nRF24 antenna on **before** powering up.
- ESP32 pins are labelled with their GPIO number ("4", not "D4"). Don't use D0–D3 / CLK / CMD (flash).
- Same wiring for the C# firmware and for the Arduino tools.
