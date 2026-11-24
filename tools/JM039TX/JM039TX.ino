// Arduino transmitter for ESP32 + nRF24L01+: imitates the grey JM039TX remote (thumb wheels) and the
// white remote of the 4-output battery box. Alternative to the C# firmware in ../../src/NanoBrick.
// Protocols: ../../docs. Wiring: CE->4, CSN->5, SCK->18, MISO->19, MOSI->23 (adapter powered with 5 V).
//
// Imitates up to 4 grey remotes at once (one per receiver selector position):
// 4 receivers x 2 outputs = 8 motors. Every 8 ms, each active position gets its frame.
//
// Commands (serial monitor 115200 baud, LF line ending). A leading digit (1 to 4) selects the receiver;
// without it, the current position (command c):
//   [p]a+ [n]  n notches of the left wheel (output A), direction 1 (default 1)   [p]a- [n]  direction 2
//   [p]b+ [n]  n notches of the right wheel (output B), direction 1              [p]b- [n]  direction 2
//   [p]sa [n]  left orange button (stop A) held n x 100 ms (default 3)           [p]sb [n]  stop B
//   [p]w <n>   neutral frames for n x 100 ms
//   x          stop every output of every served position
//   k 1|0      keep-alive: neutral frames on the served positions (receivers stay locked) for 15 s,
//              extended by every command; k 0 stops it
//   m <1234>   positions served by the keep-alive and by x (default 1234)
//   c <n>      current position: 1 to 4 (default 1)
//   p 0..3     radio power (-18 / -12 / -6 / 0 dBm, default -18: louder saturates the receivers)
//   l <c...>   channels of the current position (experiments)     f  diagnostics
// 4-output battery box (white remote, XN297 chip, channel 2), commands starting with "h":
//   ha+ [n] ha- [n] hb+ hb- hc+ hc- hd+ hd- [n]   arrow held n x 100 ms (default 3)
//                                                 (a+ = A up, a- = A down, b+ = B right, b- = B left,
//                                                  c+ = C up, c- = C down, d+ = D right, d- = D left)
//   hs1 [n] hs2 [n]   left / right square button     hx   release everything
//   hv b0..b5         set data bytes 0 to 5 (hex) for experiments
//   The box pairs with the first remote it hears (frames carry no ID).
// One job queue per motor: outputs A and B of a receiver can move at the same time. After a silence,
// a position first gets 2 s of neutral frames so that its receiver locks on.

#include <SPI.h>
#include "xn297.h"

constexpr int PIN_CE = 4, PIN_CSN = 5, PIN_SCK = 18, PIN_MISO = 19, PIN_MOSI = 23;

// ---------- JM039TX protocol ----------
namespace jm {
// Remote address, in on-air order
constexpr uint8_t kAddr[5] = {0x88, 0xB4, 0x87, 0x2E, 0xCB};
// Same address in TX_ADDR register order (reversed)
const uint8_t kAddrRegister[5] = {0xCB, 0x2E, 0x87, 0xB4, 0x88};
constexpr uint32_t kPeriodMs = 8;    // one frame every 8 ms
constexpr uint8_t kStepFrames = 5;   // active frames per wheel notch
constexpr uint8_t kGapFrames = 6;    // neutral frames between two notches
constexpr uint8_t kTailFrames = 19;  // neutral frames before going silent

// Commands: active-low bits, mirrored in the check byte d2
// (linear relation measured with the sniffer, no simpler formula found).
enum Cmd : uint8_t { A1 = 1, A2 = 2, B1 = 4, B2 = 8, STOP_A = 16, STOP_B = 32 };

struct Channels { uint8_t list[4]; };
// Channels of each selector position, as seen with the sniffer
// (can be changed at run time with command "l"; 255 = unused slot)
Channels kChannels[4] = {{{12, 13, 57, 58}}, {{9, 10, 52, 53}}, {{7, 8, 41, 42}}, {{19, 20, 67, 68}}};
// Selector byte S (0x29 for position 1) and check byte d2: XOR depending on the position
constexpr uint8_t kSelXor[4] = {0x00, 0x01, 0x02, 0x07};
constexpr uint8_t kSelD2Xor[4] = {0x00, 0x03, 0x02, 0x01};

uint16_t crc16(const uint8_t* d, uint8_t n, uint16_t crc) {
  for (uint8_t i = 0; i < n; i++) {
    crc ^= (uint16_t)d[i] << 8;
    for (uint8_t b = 0; b < 8; b++) crc = (crc & 0x8000) ? (crc << 1) ^ 0x1021 : crc << 1;
  }
  return crc;
}

// Builds the 13 bytes following the address (11 data bytes + CRC).
void buildFrame(uint8_t cmds, uint8_t selector, bool toggle, uint8_t* p) {
  static const uint8_t base[11] = {0x34, 0xCA, 0x42, 0x92, 0xD6, 0x7E, 0x6A, 0xED, 0x29, 0xF9, 0x1A};
  memcpy(p, base, 11);
  if (toggle) p[2] |= 0x04;
  p[8] ^= kSelXor[selector - 1];
  p[10] ^= kSelD2Xor[selector - 1];
  if (cmds & A1) { p[9] ^= 0x80; p[10] ^= 0x80; }
  if (cmds & A2) { p[9] ^= 0x40; p[10] ^= 0x40; }
  if (cmds & B1) { p[9] ^= 0x20; p[10] ^= 0x60; }
  if (cmds & B2) { p[9] ^= 0x10; p[10] ^= 0x10; }
  if (cmds & STOP_A) { p[7] ^= 0x04; p[10] ^= 0x0C; }
  if (cmds & STOP_B) { p[7] ^= 0x08; p[10] ^= 0x08; }
  uint16_t crc = crc16(kAddr, 5, 0x8ABC);  // the CRC covers the address, then the data
  crc = crc16(p, 11, crc);
  p[11] = crc >> 8;
  p[12] = crc & 0xFF;
}
}  // namespace jm

// ---------- nRF24L01+ ----------
namespace nrf {
const SPISettings spiCfg(8000000, MSBFIRST, SPI_MODE0);  // the nRF24 accepts up to 10 MHz
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
void writeReg(uint8_t r, uint8_t v) { transfer(0x20 | r, &v, nullptr, 1); }
uint8_t readReg(uint8_t r) { uint8_t v; transfer(r, nullptr, &v, 1); return v; }

// TX address (TX_ADDR register, 5 bytes, register order). Only rewritten when it changes.
const uint8_t* currentTxAddr = nullptr;
void setTxAddr(const uint8_t* reg5) {
  if (reg5 == currentTxAddr) return;
  transfer(0x20 | 0x10, reg5, nullptr, 5);
  currentTxAddr = reg5;
}

bool begin() {
  pinMode(PIN_CE, OUTPUT); pinMode(PIN_CSN, OUTPUT);
  digitalWrite(PIN_CE, LOW); digitalWrite(PIN_CSN, HIGH);
  SPI.begin(PIN_SCK, PIN_MISO, PIN_MOSI, PIN_CSN);
  delay(5);
  writeReg(0x03, 0x03);  // SETUP_AW: 5-byte addresses
  if (readReg(0x03) != 0x03) return false;
  writeReg(0x01, 0x00);  // EN_AA: no auto-ack
  writeReg(0x04, 0x00);  // SETUP_RETR: no retransmission
  writeReg(0x1C, 0x00);  // DYNPD
  writeReg(0x1D, 0x00);  // FEATURE: static payload, no ESB control field
  writeReg(0x06, 0x01);  // RF_SETUP: 1 Mbit/s, -18 dBm (at 0 dBm the PA+LNA module saturates the receiver)
  setTxAddr(jm::kAddrRegister);
  writeReg(0x00, 0x02);  // CONFIG: PWR_UP, TX mode, nRF CRC disabled (the protocols compute their own)
  delay(5);
  return true;
}

// Reads STATUS in a single byte (NOP command: the nRF24 returns STATUS with every command)
uint8_t status() {
  SPI.beginTransaction(spiCfg);
  digitalWrite(PIN_CSN, LOW);
  uint8_t s = SPI.transfer(0xFF);
  digitalWrite(PIN_CSN, HIGH);
  SPI.endTransaction();
  return s;
}

uint32_t sentOk = 0, sentFail = 0;  // diagnostics (command f)
// One frame on one channel: ~0.3 ms (130 us settling + 150 us on air). Up to 16 sends per 8 ms round
// when the 4 positions are active, so every SPI exchange saved counts.
void send(uint8_t ch, const uint8_t* p, uint8_t n) {
  writeReg(0x05, ch);    // RF_CH
  writeReg(0x07, 0x70);  // clear the flags (the FIFO is empty: the previous frame has left)
  transfer(0xA0, p, nullptr, n);        // W_TX_PAYLOAD
  digitalWrite(PIN_CE, HIGH);
  delayMicroseconds(12);
  digitalWrite(PIN_CE, LOW);
  uint32_t t0 = micros();
  while (micros() - t0 < 1000) {
    if (status() & 0x20) { sentOk++; return; }  // TX_DS: sent
  }
  sentFail++;
  transfer(0xE1, nullptr, nullptr, 0);  // FLUSH_TX: drop the stuck frame
}
}  // namespace nrf

// ---------- Transmission: 4 positions, 8 motors, one frame per position every 8 ms ----------
constexpr uint8_t kPositions = 4;
constexpr uint8_t kQueue = 32;
constexpr uint32_t kKeepMs = 15000;
constexpr uint16_t kLockFrames = 2000 / jm::kPeriodMs;

struct Job { uint8_t cmd; uint16_t frames; uint8_t gap; };

// A motor = one output (A or B) of a receiver, with its own job queue
struct Motor {
  Job queue[kQueue];
  uint8_t head = 0, tail = 0;
  Job cur = {0, 0, 0};
  bool busy() const { return cur.frames || cur.gap || head != tail; }
  bool push(uint8_t cmd, uint16_t frames, uint8_t gap) {
    uint8_t next = (tail + 1) % kQueue;
    if (next == head) return false;  // queue full
    queue[tail] = {cmd, frames, gap};
    tail = next;
    return true;
  }
  void clear() { head = tail = 0; cur = {0, 0, 0}; }
  // Command of this motor for the current frame (0 = neutral)
  uint8_t step() {
    if (!cur.frames && !cur.gap && head != tail) { cur = queue[head]; head = (head + 1) % kQueue; }
    if (cur.frames) { cur.frames--; return cur.cmd; }
    if (cur.gap) cur.gap--;
    return 0;
  }
};

struct Position {
  Motor motor[2];          // 0 = output A, 1 = output B
  bool toggle = false;
  bool silent = true;
  uint16_t lockFrames = 0;
  uint16_t tailFrames = 0;
};

Position pos[kPositions];
uint8_t selector = 1;            // current position (commands without a digit)
uint8_t servedMask = 0x0F;       // positions served by the keep-alive (bit 0 = position 1)
uint32_t keepUntil = 0;
uint32_t nextFrame = 0;
uint32_t tickMaxUs = 0;          // diagnostics: longest transmission round

bool keepActive() { return (int32_t)(keepUntil - millis()) > 0; }

void sendFrame(uint8_t p, uint8_t cmds) {
  nrf::setTxAddr(jm::kAddrRegister);
  uint8_t f[13];
  jm::buildFrame(cmds, p + 1, pos[p].toggle, f);
  pos[p].toggle = !pos[p].toggle;
  for (uint8_t ch : jm::kChannels[p].list) if (ch < 126) nrf::send(ch, f, sizeof f);
}

// Decides and sends the frame of one position. Returns true if something was sent.
bool tickPosition(uint8_t p) {
  Position& P = pos[p];
  bool pending = P.motor[0].busy() || P.motor[1].busy();
  bool keep = keepActive() && (servedMask & (1 << p));

  if (pending && P.silent) { P.lockFrames = kLockFrames; P.silent = false; }
  if (P.lockFrames) { P.lockFrames--; sendFrame(p, 0); return true; }
  if (pending) {
    uint8_t cmds = P.motor[0].step() | P.motor[1].step();
    if (cmds) P.tailFrames = jm::kTailFrames;
    sendFrame(p, cmds);
    return true;
  }
  if (keep) { P.silent = false; sendFrame(p, 0); return true; }
  if (P.tailFrames) { P.tailFrames--; sendFrame(p, 0); return true; }
  P.silent = true;  // nothing left to say: go silent, like the original remote
  return false;
}

// ---------- 4-output battery box (white XN297 remote) ----------
namespace box {
constexpr uint8_t kAddr[5] = {0xC1, 0xC2, 0xC3, 0xC4, 0xC5};
constexpr uint8_t kChannel = 2;
constexpr uint16_t kTailFrames = 20;  // "nothing pressed" frames after a release, like the original
// Arrow bits (data byte 7); squares in byte 8 (and byte 6 for the right one)
enum : uint8_t { kAUp = 0x02, kADown = 0x01, kBLeft = 0x10, kBRight = 0x20, kCUp = 0x04, kCDown = 0x08, kDLeft = 0x40, kDRight = 0x80 };

uint8_t arrows = 0, squares = 0;    // what is "pressed" right now
uint16_t holdFrames = 0, tailFrames = 0;
uint8_t extra[6] = {0, 0, 0, 0, 1, 1};  // bytes 0 to 5, always 00 00 00 00 01 01 ("hv" to experiment; 02 instead of 01 = nothing runs)

// 10-byte frame: 00 00 00 00 01 01 S6 ARROWS SQUARES SUM (sum of bytes 0 to 8 + 0x14)
uint8_t build(uint8_t dir, uint8_t sq, uint8_t* out) {
  uint8_t m[10] = {extra[0], extra[1], extra[2], extra[3], extra[4], extra[5], (uint8_t)((sq & 2) ? 0x01 : 0x00), dir, sq, 0};
  uint8_t sum = 0x14;
  for (uint8_t i = 0; i < 9; i++) sum += m[i];
  m[9] = sum;
  return xn297::encode(kAddr, 5, m, 10, out);
}

void hold(uint8_t dir, uint8_t sq, int tenths) {
  arrows = dir; squares = sq;
  holdFrames = tenths * 100 / jm::kPeriodMs;
}

bool tick() {
  uint8_t dir, sq;
  if (holdFrames) {
    holdFrames--; dir = arrows; sq = squares;
    if (!holdFrames) tailFrames = kTailFrames;
  }
  else if (tailFrames) { tailFrames--; dir = 0; sq = 0; }
  else return false;
  uint8_t f[32];
  uint8_t n = build(dir, sq, f);
  nrf::setTxAddr(xn297::nrfTxAddress(5));
  nrf::send(kChannel, f, n);
  return true;
}
}  // namespace box

// Called from loop(): one transmission round every 8 ms.
void tick() {
  if ((int32_t)(millis() - nextFrame) < 0) return;
  nextFrame = millis() + jm::kPeriodMs;
  uint32_t t0 = micros();
  for (uint8_t p = 0; p < kPositions; p++) tickPosition(p);
  box::tick();
  uint32_t dt = micros() - t0;
  if (dt > tickMaxUs) tickMaxUs = dt;
}

void stopAll() {
  for (uint8_t p = 0; p < kPositions; p++) {
    if (!(servedMask & (1 << p))) continue;
    pos[p].motor[0].clear(); pos[p].motor[0].push(jm::STOP_A, 3 * 100 / jm::kPeriodMs, 0);
    pos[p].motor[1].clear(); pos[p].motor[1].push(jm::STOP_B, 3 * 100 / jm::kPeriodMs, 0);
  }
}

// Battery box commands: "ha+ 5", "hs1", "hx"
bool handleBox(char* line) {
  if (line[0] != 'h') return false;
  char* c = strtok(line + 1, " ");
  if (!c) return true;
  char* arg = strtok(nullptr, " ");
  int n = arg ? atoi(arg) : 3;
  if (n < 1) n = 1;
  String s(c);
  uint8_t dir = 0, sq = 0;
  if (s == "a+") dir = box::kAUp; else if (s == "a-") dir = box::kADown;
  else if (s == "b+") dir = box::kBRight; else if (s == "b-") dir = box::kBLeft;
  else if (s == "c+") dir = box::kCUp; else if (s == "c-") dir = box::kCDown;
  else if (s == "d+") dir = box::kDRight; else if (s == "d-") dir = box::kDLeft;
  else if (s == "s1") sq = 0x01; else if (s == "s2") sq = 0x02;
  else if (s == "v") {  // hv b0 .. b5 (hex): data bytes 0 to 5; missing ones get their original value
    box::extra[0] = arg ? strtoul(arg, nullptr, 16) : 0;
    const uint8_t def[6] = {0, 0, 0, 0, 1, 1};
    for (uint8_t i = 1; i < 6; i++) { char* t = strtok(nullptr, " "); box::extra[i] = t ? strtoul(t, nullptr, 16) : def[i]; }
    Serial.printf("OK hv %02X %02X %02X %02X %02X %02X\n", box::extra[0], box::extra[1], box::extra[2], box::extra[3], box::extra[4], box::extra[5]);
    return true;
  }
  else if (s == "x") { box::holdFrames = 0; box::tailFrames = box::kTailFrames; Serial.println("OK hx"); return true; }
  else { Serial.println("? battery box: ha+ ha- hb+ hb- hc+ hc- hd+ hd- [n] | hs1 hs2 [n] | hx | hv b0..b5"); return true; }
  box::hold(dir, sq, n);
  Serial.printf("OK h%s %d\n", c, n);
  return true;
}

void handleLine(char* line) {
  if (handleBox(line)) return;
  // Leading digit = target receiver position
  uint8_t target = selector;
  if (line[0] >= '1' && line[0] <= '4' && line[1] >= 'a' && line[1] <= 'z') { target = line[0] - '0'; line++; }
  char* c = strtok(line, " ");
  if (!c) return;
  char* arg = strtok(nullptr, " ");
  String s(c);
  int n = arg ? atoi(arg) : (s[0] == 's' ? 3 : 1);
  if (n < 0) n = 0;
  Position& P = pos[target - 1];
  bool ok = true;
  auto steps = [&](uint8_t side, uint8_t cmd) {
    for (int i = 0; i < n && ok; i++) ok = P.motor[side].push(cmd, jm::kStepFrames, jm::kGapFrames);
  };
  if (s == "a+") steps(0, jm::A1);
  else if (s == "a-") steps(0, jm::A2);
  else if (s == "b+") steps(1, jm::B1);
  else if (s == "b-") steps(1, jm::B2);
  else if (s == "sa") { P.motor[0].clear(); ok = P.motor[0].push(jm::STOP_A, n * 100 / jm::kPeriodMs, 0); }
  else if (s == "sb") { P.motor[1].clear(); ok = P.motor[1].push(jm::STOP_B, n * 100 / jm::kPeriodMs, 0); }
  else if (s == "w") ok = P.motor[0].push(0, 0, min(n * 100 / (int)jm::kPeriodMs, 255));
  else if (s == "x") stopAll();
  else if (s == "k") { keepUntil = n ? millis() + kKeepMs : millis(); }
  else if (s == "m") {  // m 13: positions 1 and 3
    servedMask = 0;
    for (const char* d = arg ? arg : ""; *d; d++) if (*d >= '1' && *d <= '4') servedMask |= 1 << (*d - '1');
  }
  else if (s == "c") selector = (n >= 1 && n <= 4) ? n : 1;
  else if (s == "l") {  // l c1 [c2 c3 c4]: channels of the current position (experiments)
    uint8_t* L = jm::kChannels[selector - 1].list;
    L[0] = n; L[1] = L[2] = L[3] = 255;
    for (uint8_t i = 1; i < 4 && (arg = strtok(nullptr, " ")); i++) L[i] = atoi(arg);
  }
  else if (s == "p") {  // p 0..3: power -18 / -12 / -6 / 0 dBm (before the PA+LNA module's amplifier)
    nrf::writeReg(0x06, 0x01 | ((n > 3 ? 3 : n) << 1));
  }
  else if (s == "f") {  // diagnostics
    uint8_t f[13];
    jm::buildFrame(0, selector, false, f);
    Serial.printf("FRAME pos %u:", selector);
    for (uint8_t b : f) Serial.printf(" %02X", b);
    Serial.print(" | channels");
    for (uint8_t ch : jm::kChannels[selector - 1].list) if (ch < 126) Serial.printf(" %u", ch);
    Serial.printf(" | served %s%s%s%s | max round %lu us | sent %lu, failed %lu | RF_SETUP %02X\n",
                  servedMask & 1 ? "1" : "", servedMask & 2 ? "2" : "", servedMask & 4 ? "3" : "",
                  servedMask & 8 ? "4" : "", (unsigned long)tickMaxUs, (unsigned long)nrf::sentOk,
                  (unsigned long)nrf::sentFail, nrf::readReg(0x06));
    tickMaxUs = 0;
    return;
  }
  else {
    Serial.println("? commands: [1-4]a+ a- b+ b- [n] | [1-4]sa sb [n] | [1-4]w <n> | x | k 1|0 | m <1234> | c 1-4 | p 0-3 | f | h... (battery box)");
    return;
  }
  if (s != "k" || n) {
    if (keepActive()) keepUntil = millis() + kKeepMs;  // any command extends the keep-alive
  }
  Serial.printf(ok ? "OK %u%s %d\n" : "FULL %u%s %d\n", target, c, n);
}

void setup() {
  Serial.begin(115200);
  delay(200);
  if (!nrf::begin()) {
    Serial.println("nRF24L01 not found: check the wiring and the power supply");
    while (true) delay(1000);
  }
  Serial.println("JM039TX transmitter ready (4 positions + battery box). Commands: [1-4]a+ a- b+ b- [n] | [1-4]sa sb | x | k 1|0 | c 1-4 | p 0-3 | f | h...");
}

void loop() {
  static char line[32];
  static uint8_t len = 0;
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n' || ch == '\r') {
      if (len) { line[len] = 0; handleLine(line); len = 0; }
    } else if (len < sizeof line - 1) {
      line[len++] = ch;
    }
  }
  tick();
}
