// Renifleur 2,4 GHz : ESP32 + nRF24L01+ en ecoute, pour capturer ce qu'envoie une telecommande.
// Meme cablage que JM039TX.ino (CE->4, CSN->5, SCK->18, MISO->19, MOSI->23).
//
// Principe : le nRF24 n'accepte que les paquets dont l'adresse correspond a la sienne. On lui donne comme
// adresse le preambule d'une puce XN297 (71 0F 55) : il recoit alors tout ce qu'emet une XN297 (ou un clone),
// quel que soit l'appareil. CRC du nRF desactive, 32 octets bruts par paquet, analyse faite sur PC.
//
// Commandes (moniteur serie 115200, fin de ligne LF) :
//   s            scan : tous les canaux 0..125 aux 3 debits, affiche le nombre de paquets par canal
//   t <sec>      balayages repetes a 1 Mbit/s pendant <sec> s, nombre de paquets par canal a la fin
//   d <r> <ch>   dump : affiche les paquets du canal ch au debit r (1 = 1 Mbit/s, 2 = 2 Mbit/s, 0 = 250 kbit/s)
//   h <r> c1 c2 ...  dump en sautant de canal en canal (jusqu'a 8 canaux, 20 ms chacun)
//   r <sec>      energie : balaye les canaux pendant <sec> secondes et compte, par canal, les fois ou le nRF24
//                detecte un signal fort (> -64 dBm, registre RPD), quel que soit le protocole
//   a <v>        adresse d'ecoute : 0 = preambule XN297 (defaut) ; 1..4 = astuce « promiscuous » nRF24
//                (adresse de 2 octets 00AA / AA00 / 0055 / 5500 : capte le preambule de toute puce nRF24-compatible,
//                avec beaucoup de bruit, les vraies trames se reperent parce qu'elles se repetent)
//                5 = adresse exacte de la telecommande grise JM039TX (88 B4 87 2E CB) : plus de bruit
//   x            stop

#include <SPI.h>

constexpr int PIN_CE = 4, PIN_CSN = 5, PIN_SCK = 18, PIN_MISO = 19, PIN_MOSI = 23;
constexpr uint8_t PAYLOAD = 32;
constexpr uint8_t DEDUP = 12;  // les octets au-dela de la vraie trame sont du bruit : on compare le debut

namespace nrf {
enum : uint8_t { CONFIG = 0x00, EN_AA = 0x01, EN_RXADDR = 0x02, SETUP_AW = 0x03, SETUP_RETR = 0x04,
                 RF_CH = 0x05, RF_SETUP = 0x06, STATUS = 0x07, RX_ADDR_P0 = 0x0A, RX_PW_P0 = 0x11,
                 FIFO_STATUS = 0x17, DYNPD = 0x1C, FEATURE = 0x1D };
const SPISettings spiCfg(4000000, MSBFIRST, SPI_MODE0);

void transfer(uint8_t cmd, const uint8_t* tx, uint8_t* rx, uint8_t n) {
  SPI.beginTransaction(spiCfg);
  digitalWrite(PIN_CSN, LOW);
  SPI.transfer(cmd);
  for (uint8_t i = 0; i < n; i++) {
    uint8_t v = SPI.transfer(tx ? tx[i] : 0xFF);
    if (rx) rx[i] = v;
  }
  digitalWrite(PIN_CSN, HIGH);
  SPI.endTransaction();
}
void cmd(uint8_t c) { transfer(c, nullptr, nullptr, 0); }
void writeReg(uint8_t r, uint8_t v) { transfer(0x20 | r, &v, nullptr, 1); }
void writeRegs(uint8_t r, const uint8_t* d, uint8_t n) { transfer(0x20 | r, d, nullptr, n); }
uint8_t readReg(uint8_t r) { uint8_t v; transfer(r, nullptr, &v, 1); return v; }

// 0 = 250 kbit/s, 1 = 1 Mbit/s, 2 = 2 Mbit/s (LNA active)
void setRate(uint8_t r) { writeReg(RF_SETUP, r == 2 ? 0x0F : (r == 0 ? 0x27 : 0x07)); }
void setChannel(uint8_t ch) {
  digitalWrite(PIN_CE, LOW);
  writeReg(RF_CH, ch);
  cmd(0xE2);               // FLUSH_RX
  writeReg(STATUS, 0x70);
  digitalWrite(PIN_CE, HIGH);
  delayMicroseconds(150);  // temps de stabilisation du synthetiseur
}

bool begin() {
  pinMode(PIN_CE, OUTPUT); pinMode(PIN_CSN, OUTPUT);
  digitalWrite(PIN_CE, LOW); digitalWrite(PIN_CSN, HIGH);
  SPI.begin(PIN_SCK, PIN_MISO, PIN_MOSI, PIN_CSN);
  delay(5);
  writeReg(SETUP_AW, 0x01);  // adresse de 3 octets
  if (readReg(SETUP_AW) != 0x01) return false;
  writeReg(EN_AA, 0x00);
  writeReg(EN_RXADDR, 0x01);
  writeReg(SETUP_RETR, 0x00);
  writeReg(DYNPD, 0x00);
  writeReg(FEATURE, 0x00);
  static const uint8_t xn297Preamble[3] = {0x55, 0x0F, 0x71};  // 71 0F 55 sur les ondes
  writeRegs(RX_ADDR_P0, xn297Preamble, 3);
  writeReg(RX_PW_P0, PAYLOAD);
  writeReg(CONFIG, 0x03);  // PWR_UP | PRIM_RX, CRC desactive
  delay(5);
  return true;
}

void setAddress(uint8_t v) {
  static const uint8_t xn297Preamble[3] = {0x55, 0x0F, 0x71};
  static const uint8_t promisc[4][2] = {{0x00, 0xAA}, {0xAA, 0x00}, {0x00, 0x55}, {0x55, 0x00}};
  // 5 : adresse de la telecommande grise JM039TX (88 B4 87 2E CB sur les ondes, ecrite a l'envers)
  static const uint8_t jm039[5] = {0xCB, 0x2E, 0x87, 0xB4, 0x88};
  digitalWrite(PIN_CE, LOW);
  if (v == 5) { writeReg(SETUP_AW, 0x03); writeRegs(RX_ADDR_P0, jm039, 5); }
  else if (v == 0 || v > 4) { writeReg(SETUP_AW, 0x01); writeRegs(RX_ADDR_P0, xn297Preamble, 3); }
  else { writeReg(SETUP_AW, 0x00); writeRegs(RX_ADDR_P0, promisc[v - 1], 2); }  // AW=0 : 2 octets (non documente)
  digitalWrite(PIN_CE, HIGH);
}

// true si un paquet a ete lu
bool read(uint8_t* buf) {
  if (readReg(FIFO_STATUS) & 0x01) return false;  // RX_EMPTY
  transfer(0x61, nullptr, buf, PAYLOAD);           // R_RX_PAYLOAD
  writeReg(STATUS, 0x40);
  return true;
}
}  // namespace nrf

enum class Mode { Idle, Dump, Hop };
Mode mode = Mode::Idle;
uint8_t rate = 1, hopList[8], hopCount = 0, hopIdx = 0;
uint32_t hopT = 0;
uint8_t last[PAYLOAD];
uint32_t repeats = 0;

const char* rateName(uint8_t r) { return r == 2 ? "2M" : (r == 0 ? "250k" : "1M"); }

void printPacket(uint8_t ch, const uint8_t* b) {
  if (memcmp(b, last, DEDUP) == 0) { repeats++; return; }
  if (repeats) Serial.printf("  (x%lu)\n", (unsigned long)repeats);
  repeats = 0;
  memcpy(last, b, PAYLOAD);
  Serial.printf("P %lu %s %3u ", (unsigned long)millis(), rateName(rate), ch);
  for (uint8_t i = 0; i < PAYLOAD; i++) Serial.printf("%02X", b[i]);
  Serial.println();
}

void scan() {
  Serial.println("SCAN debut");
  const uint8_t rates[3] = {1, 2, 0};
  uint8_t buf[PAYLOAD];
  for (uint8_t r : rates) {
    nrf::setRate(r);
    for (uint8_t ch = 0; ch < 126; ch++) {
      nrf::setChannel(ch);
      uint16_t n = 0;
      uint32_t t0 = millis();
      while (millis() - t0 < 40) if (nrf::read(buf)) n++;
      if (n) Serial.printf("S %s %3u %u\n", rateName(r), ch, n);
    }
  }
  Serial.println("SCAN fin");
}

// Balayages repetes a 1 Mbit/s (20 ms par canal) pendant <seconds> s, totaux par canal a la fin.
void scanTotals(uint32_t seconds) {
  static uint16_t hits[126];
  memset(hits, 0, sizeof hits);
  uint8_t buf[PAYLOAD];
  uint32_t sweeps = 0, t0 = millis();
  nrf::setRate(1);
  while (millis() - t0 < seconds * 1000) {
    for (uint8_t ch = 0; ch < 126; ch++) {
      nrf::setChannel(ch);
      uint32_t t1 = millis();
      while (millis() - t1 < 20) if (nrf::read(buf)) hits[ch]++;
    }
    sweeps++;
  }
  Serial.printf("TOTAUX %lu balayages\n", (unsigned long)sweeps);
  for (uint8_t ch = 0; ch < 126; ch++)
    if (hits[ch]) Serial.printf("T %3u %u\n", ch, hits[ch]);
  Serial.println("TOTAUX fin");
}

void energy(uint32_t seconds) {
  static uint16_t hits[126];
  memset(hits, 0, sizeof hits);
  uint32_t sweeps = 0, t0 = millis();
  while (millis() - t0 < seconds * 1000) {
    for (uint8_t ch = 0; ch < 126; ch++) {
      nrf::setChannel(ch);
      delayMicroseconds(100);
      if (nrf::readReg(0x09) & 1) hits[ch]++;  // RPD
    }
    sweeps++;
  }
  Serial.printf("ENERGIE %lu balayages\n", (unsigned long)sweeps);
  for (uint8_t ch = 0; ch < 126; ch++)
    if (hits[ch]) Serial.printf("E %3u %u\n", ch, hits[ch]);
  Serial.println("ENERGIE fin");
}

void handleLine(char* line) {
  char* tok = strtok(line, " ");
  if (!tok) return;
  mode = Mode::Idle;
  repeats = 0;
  memset(last, 0, sizeof last);
  switch (tok[0]) {
    case 'a': { uint8_t v = atoi(strtok(nullptr, " ")); nrf::setAddress(v); Serial.printf("ADRESSE %u\n", v); break; }
    case 's': scan(); break;
    case 't': scanTotals(atoi(strtok(nullptr, " "))); break;
    case 'r': nrf::setRate(1); energy(atoi(strtok(nullptr, " "))); break;
    case 'd': {
      rate = atoi(strtok(nullptr, " "));
      uint8_t ch = atoi(strtok(nullptr, " "));
      nrf::setRate(rate);
      nrf::setChannel(ch);
      hopList[0] = ch; hopCount = 1; hopIdx = 0;
      mode = Mode::Dump;
      Serial.printf("DUMP %s canal %u\n", rateName(rate), ch);
      break;
    }
    case 'h': {
      rate = atoi(strtok(nullptr, " "));
      hopCount = 0;
      while ((tok = strtok(nullptr, " ")) && hopCount < 8) hopList[hopCount++] = atoi(tok);
      if (!hopCount) break;
      nrf::setRate(rate);
      hopIdx = 0; hopT = millis();
      nrf::setChannel(hopList[0]);
      mode = Mode::Hop;
      Serial.printf("HOP %s sur %u canaux\n", rateName(rate), hopCount);
      break;
    }
    default: Serial.println("STOP"); break;
  }
}

void setup() {
  Serial.begin(115200);
  delay(200);
  if (!nrf::begin()) {
    Serial.println("nRF24L01 non detecte : verifie le cablage et l'alimentation");
    while (true) delay(1000);
  }
  Serial.println("Renifleur pret. Commandes : s | d <r> <ch> | h <r> <ch...> | x");
}

void loop() {
  static char line[64];
  static uint8_t len = 0;
  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (len) { line[len] = 0; handleLine(line); len = 0; }
    } else if (len < sizeof line - 1) {
      line[len++] = c;
    }
  }
  if (mode == Mode::Idle) return;
  if (mode == Mode::Hop && millis() - hopT >= 20) {
    hopT = millis();
    hopIdx = (hopIdx + 1) % hopCount;
    nrf::setChannel(hopList[hopIdx]);
  }
  uint8_t buf[PAYLOAD];
  while (nrf::read(buf)) printPacket(hopList[hopIdx], buf);
}
