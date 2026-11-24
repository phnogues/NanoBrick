// XN297 radio chip emulation on an nRF24L01+ (XN297-specific whitening + CRC).
// Ported from DIY-Multiprotocol-TX-Module (XN297_EMU.ino, GPLv3), so this file is GPLv3 as well.
// https://github.com/pascallanger/DIY-Multiprotocol-TX-Module
// Coding checked on the PC against a reference implementation and against frames of the white remote.
#pragma once
#include <stdint.h>
#include <string.h>

namespace xn297 {

// XN297 whitening: x^7 + x^3 + 1, seed 0x71 (E3 B1 4B EA 85 ...)
constexpr uint32_t kScrambleSeed = 0xE3B14BE0;

inline uint8_t bitReverse(uint8_t b) {
  uint8_t r = 0;
  for (uint8_t i = 0; i < 8; ++i) { r = (r << 1) | (b & 1); b >>= 1; }
  return r;
}

struct Codec {
  uint32_t lfsr = kScrambleSeed;
  uint16_t crc = 0xFFFF;

  uint8_t nextScramble() {
    uint8_t s = lfsr >> 24;
    lfsr = (lfsr << 8) | ((uint32_t)(uint8_t)(s ^ (lfsr >> 8)) << 4);
    return s;
  }
  void crcUpdate(uint8_t a) {
    crc ^= (uint16_t)a << 8;
    for (uint8_t i = 0; i < 8; ++i)
      crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
  }
  uint8_t crcThenScramble(uint8_t b) { crcUpdate(b); return b ^ nextScramble(); }
  uint8_t unscrambleThenCrc(uint8_t b) { b ^= nextScramble(); crcUpdate(b); return b; }
  uint16_t finalCrc() const { return (uint16_t)(~crc ^ (lfsr >> 16)); }
};

// nRF24 TX_ADDR register content (XN297 preamble 0xC710F55). 6 bytes, 5 are written.
inline const uint8_t* nrfTxAddress(uint8_t addrLen) {
  static const uint8_t pre[6] = {0x55, 0x0F, 0x71, 0x0C, 0x00, 0x00};
  return addrLen == 3 ? pre + 1 : pre;
}

// Whitened address to write to RX_ADDR_P0 (register write order).
inline void nrfRxAddress(const uint8_t* addr, uint8_t addrLen, uint8_t* out) {
  Codec c;
  for (uint8_t i = addrLen; i--;) out[i] = addr[i] ^ c.nextScramble();
}

// Builds the frame to push into the nRF24 TX FIFO (nRF CRC disabled). Returns its length.
inline uint8_t encode(const uint8_t* addr, uint8_t addrLen, const uint8_t* msg, uint8_t len, uint8_t* out) {
  uint8_t n = 0;
  if (addrLen < 4) out[n++] = 0x55;  // end of the preamble that does not fit in TX_ADDR
  Codec c;
  for (uint8_t i = 0; i < addrLen; ++i) out[n++] = c.crcThenScramble(addr[addrLen - 1 - i]);
  for (uint8_t i = 0; i < len; ++i) out[n++] = c.crcThenScramble(bitReverse(msg[i]));
  uint16_t crc = c.finalCrc();
  out[n++] = crc >> 8;
  out[n++] = crc & 0xFF;
  return n;
}

// Decodes the bytes received after the address (len payload bytes + 2 CRC bytes). True if the CRC matches.
inline bool decode(const uint8_t* addr, uint8_t addrLen, const uint8_t* in, uint8_t len, uint8_t* msg) {
  Codec c;
  uint8_t scrambled[5];
  nrfRxAddress(addr, addrLen, scrambled);
  for (uint8_t i = 0; i < addrLen; ++i) c.unscrambleThenCrc(scrambled[addrLen - 1 - i]);
  for (uint8_t i = 0; i < len; ++i) msg[i] = bitReverse(c.unscrambleThenCrc(in[i]));
  uint16_t crc = c.finalCrc();
  return (crc >> 8) == in[len] && (crc & 0xFF) == in[len + 1];
}

}  // namespace xn297
