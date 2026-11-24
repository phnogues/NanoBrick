// Emulation de la puce radio XN297 sur un nRF24L01+ (brouillage + CRC propres a la XN297).
// Porte de DIY-Multiprotocol-TX-Module (XN297_EMU.ino, GPLv3) : ce fichier est donc sous GPLv3.
// https://github.com/pascallanger/DIY-Multiprotocol-TX-Module
// Codage verifie sur PC contre une implementation de reference et contre les trames de la telecommande blanche.
#pragma once
#include <stdint.h>
#include <string.h>

namespace xn297 {

// Blanchiment XN297 : x^7 + x^3 + 1, graine 0x71 (E3 B1 4B EA 85 ...)
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

// Contenu du registre TX_ADDR du nRF24 (preambule XN297 0xC710F55). 6 octets : on en ecrit 5.
inline const uint8_t* nrfTxAddress(uint8_t addrLen) {
  static const uint8_t pre[6] = {0x55, 0x0F, 0x71, 0x0C, 0x00, 0x00};
  return addrLen == 3 ? pre + 1 : pre;
}

// Adresse brouillee a mettre dans RX_ADDR_P0 (ordre d'ecriture du registre).
inline void nrfRxAddress(const uint8_t* addr, uint8_t addrLen, uint8_t* out) {
  Codec c;
  for (uint8_t i = addrLen; i--;) out[i] = addr[i] ^ c.nextScramble();
}

// Construit la trame a pousser dans la FIFO TX du nRF24 (CRC du nRF desactive). Retourne sa longueur.
inline uint8_t encode(const uint8_t* addr, uint8_t addrLen, const uint8_t* msg, uint8_t len, uint8_t* out) {
  uint8_t n = 0;
  if (addrLen < 4) out[n++] = 0x55;  // fin du preambule qui ne tient pas dans TX_ADDR
  Codec c;
  for (uint8_t i = 0; i < addrLen; ++i) out[n++] = c.crcThenScramble(addr[addrLen - 1 - i]);
  for (uint8_t i = 0; i < len; ++i) out[n++] = c.crcThenScramble(bitReverse(msg[i]));
  uint16_t crc = c.finalCrc();
  out[n++] = crc >> 8;
  out[n++] = crc & 0xFF;
  return n;
}

// Decode les octets recus apres l'adresse (len octets utiles + 2 de CRC). true si CRC correct.
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
