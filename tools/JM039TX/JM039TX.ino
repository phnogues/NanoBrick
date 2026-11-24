// Emetteur qui imite la telecommande grise JM039TX (molettes) avec un ESP32 + nRF24L01+.
// Protocole decode au renifleur (voir ../../CLAUDE.md). Meme cablage que les autres sketches :
// CE->4, CSN->5, SCK->18, MISO->19, MOSI->23 (adaptateur alimente en 5V).
//
// Il imite jusqu'a 4 telecommandes a la fois (une par position du selecteur des recepteurs) :
// 4 recepteurs x 2 sorties = 8 moteurs. Toutes les 8 ms, chaque position active recoit sa trame.
//
// Commandes (moniteur serie 115200, fin de ligne LF). Le chiffre en tete (1 a 4) choisit le recepteur ;
// sans chiffre, c'est la position courante (commande c) :
//   [p]a+ [n]  n crans de la molette gauche (sortie A), sens 1 (defaut 1)   [p]a- [n]  sens 2
//   [p]b+ [n]  n crans de la molette droite (sortie B), sens 1              [p]b- [n]  sens 2
//   [p]sa [n]  bouton orange gauche (stop A) maintenu n x 100 ms (defaut 3)  [p]sb [n]  stop B
//   [p]w <n>   trames neutres pendant n x 100 ms
//   x          stop de toutes les sorties de toutes les positions servies
//   k 1|0      maintien : trames neutres en continu sur les positions servies (recepteurs toujours
//              « accroches ») pendant 15 s, prolonge par chaque commande ; k 0 l'arrete
//   m <1234>   positions servies par le maintien et par x (defaut 1234)
//   c <n>      position courante : 1 a 4 (defaut 1)
//   p 0..3     puissance d'emission (-18 / -12 / -6 / 0 dBm, defaut -18 : plus fort, le recepteur sature)
//   l <c...>   canaux de la position courante (essais)     f  diagnostic
// Boitier 4 sorties (telecommande blanche, puce XN297, canal 2) - ordres prefixes par « h » :
//   ha+ [n] ha- [n] hb+ hb- hc+ hc- hd+ hd- [n]   direction maintenue n x 100 ms (defaut 3)
//                                                 (a+ = A haut, a- = A bas, b+ = B droite, b- = B gauche,
//                                                  c+ = C haut, c- = C bas, d+ = D droite, d- = D gauche)
//   hs1 [n] hs2 [n]   bouton carre gauche / droit     hx   relache tout
//   Le boitier s'appaire a la premiere telecommande qu'il entend (aucun identifiant dans la trame).
// Une file de commandes par moteur : A et B d'un meme recepteur bougent ensemble. Apres un silence,
// une position recoit d'abord 2 s de trames neutres pour que son recepteur accroche.

#include <SPI.h>
#include "xn297.h"

constexpr int PIN_CE = 4, PIN_CSN = 5, PIN_SCK = 18, PIN_MISO = 19, PIN_MOSI = 23;

// ---------- Protocole JM039TX ----------
namespace jm {
// Adresse de la telecommande, dans l'ordre d'emission (88 B4 87 2E CB)
constexpr uint8_t kAddr[5] = {0x88, 0xB4, 0x87, 0x2E, 0xCB};
constexpr uint32_t kPeriodMs = 8;   // une trame toutes les 8 ms
constexpr uint8_t kClickFrames = 5; // trames actives par cran de molette
constexpr uint8_t kGapFrames = 6;   // trames neutres entre deux crans
constexpr uint8_t kTailFrames = 19; // trames neutres avant de se taire

// Commandes : bits a 0 quand actifs, et bits inverses dans l'octet de controle d2
// (relation lineaire relevee au renifleur, pas de formule plus simple trouvee).
enum Cmd : uint8_t { A1 = 1, A2 = 2, B1 = 4, B2 = 8, STOP_A = 16, STOP_B = 32 };

struct Channels { uint8_t list[4]; };
// Canaux vus au renifleur pour chaque position du selecteur (le boitier cherche lui-meme le canal,
// d'ou le temps d'accroche)
// (modifiable a la volee avec la commande « l » ; 255 = case vide)
Channels kChannels[4] = {{{12, 13, 57, 58}}, {{9, 10, 52, 53}}, {{7, 8, 41, 42}}, {{19, 20, 67, 68}}};
// Octet de selection S (0x29 en position 1) et octet de controle d2 : XOR selon la position
constexpr uint8_t kSelXor[4] = {0x00, 0x01, 0x02, 0x07};
constexpr uint8_t kSelD2Xor[4] = {0x00, 0x03, 0x02, 0x01};

uint16_t crc16(const uint8_t* d, uint8_t n, uint16_t crc) {
  for (uint8_t i = 0; i < n; i++) {
    crc ^= (uint16_t)d[i] << 8;
    for (uint8_t b = 0; b < 8; b++) crc = (crc & 0x8000) ? (crc << 1) ^ 0x1021 : crc << 1;
  }
  return crc;
}

// Construit les 13 octets qui suivent l'adresse (11 de donnees + CRC).
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
  uint16_t crc = crc16(kAddr, 5, 0x8ABC);  // le CRC couvre l'adresse puis les donnees
  crc = crc16(p, 11, crc);
  p[11] = crc >> 8;
  p[12] = crc & 0xFF;
}
}  // namespace jm

// ---------- nRF24L01+ ----------
namespace nrf {
const SPISettings spiCfg(8000000, MSBFIRST, SPI_MODE0);  // le nRF24 accepte jusqu'a 10 MHz
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

bool begin() {
  pinMode(PIN_CE, OUTPUT); pinMode(PIN_CSN, OUTPUT);
  digitalWrite(PIN_CE, LOW); digitalWrite(PIN_CSN, HIGH);
  SPI.begin(PIN_SCK, PIN_MISO, PIN_MOSI, PIN_CSN);
  delay(5);
  writeReg(0x03, 0x03);  // SETUP_AW : adresse de 5 octets
  if (readReg(0x03) != 0x03) return false;
  writeReg(0x01, 0x00);  // EN_AA : pas d'acquittement
  writeReg(0x04, 0x00);  // SETUP_RETR : pas de retransmission
  writeReg(0x1C, 0x00);  // DYNPD
  writeReg(0x1D, 0x00);  // FEATURE : trame statique, sans champ de controle ESB
  writeReg(0x06, 0x01);  // RF_SETUP : 1 Mbit/s, -18 dBm (a 0 dBm le module PA+LNA sature le recepteur)
  uint8_t reg[5];
  for (uint8_t i = 0; i < 5; i++) reg[i] = jm::kAddr[4 - i];  // le registre s'ecrit a l'envers
  transfer(0x20 | 0x10, reg, nullptr, 5);  // TX_ADDR
  writeReg(0x00, 0x02);  // CONFIG : PWR_UP, emission, CRC du nRF desactive (on fait le notre)
  delay(5);
  return true;
}

// Lit STATUS en un seul octet (commande NOP : le nRF24 renvoie STATUS a chaque commande)
uint8_t status() {
  SPI.beginTransaction(spiCfg);
  digitalWrite(PIN_CSN, LOW);
  uint8_t s = SPI.transfer(0xFF);
  digitalWrite(PIN_CSN, HIGH);
  SPI.endTransaction();
  return s;
}

// Adresse d'emission (registre TX_ADDR, 5 octets, ordre du registre). On ne la reecrit que si elle change.
const uint8_t* currentTxAddr = nullptr;
void setTxAddr(const uint8_t* reg5) {
  if (reg5 == currentTxAddr) return;
  transfer(0x20 | 0x10, reg5, nullptr, 5);
  currentTxAddr = reg5;
}

uint32_t sentOk = 0, sentFail = 0;  // diagnostic (commande f)
// Une trame sur un canal : ~0,3 ms (130 us de calage + 150 us d'emission). 16 envois par tour de 8 ms
// quand les 4 positions sont actives : chaque echange SPI economise compte.
void send(uint8_t ch, const uint8_t* p, uint8_t n) {
  writeReg(0x05, ch);    // RF_CH
  writeReg(0x07, 0x70);  // efface les drapeaux (la FIFO est vide : la trame precedente est partie)
  transfer(0xA0, p, nullptr, n);        // W_TX_PAYLOAD
  digitalWrite(PIN_CE, HIGH);
  delayMicroseconds(12);
  digitalWrite(PIN_CE, LOW);
  uint32_t t0 = micros();
  while (micros() - t0 < 1000) {
    if (status() & 0x20) { sentOk++; return; }  // TX_DS : partie
  }
  sentFail++;
  transfer(0xE1, nullptr, nullptr, 0);  // FLUSH_TX : on vide la trame restee coincee
}
}  // namespace nrf

// ---------- Emission : 4 positions, 8 moteurs, une trame par position toutes les 8 ms ----------
constexpr uint8_t kPositions = 4;
constexpr uint8_t kQueue = 32;
constexpr uint32_t kKeepMs = 15000;
constexpr uint16_t kLockFrames = 2000 / jm::kPeriodMs;

struct Job { uint8_t cmd; uint16_t frames; uint8_t gap; };

// Un moteur = une sortie (A ou B) d'un recepteur, avec sa propre file de travaux
struct Motor {
  Job queue[kQueue];
  uint8_t head = 0, tail = 0;
  Job cur = {0, 0, 0};
  bool busy() const { return cur.frames || cur.gap || head != tail; }
  bool push(uint8_t cmd, uint16_t frames, uint8_t gap) {
    uint8_t next = (tail + 1) % kQueue;
    if (next == head) return false;  // file pleine
    queue[tail] = {cmd, frames, gap};
    tail = next;
    return true;
  }
  void clear() { head = tail = 0; cur = {0, 0, 0}; }
  // Commande de ce moteur pour la trame en cours (0 = neutre)
  uint8_t step() {
    if (!cur.frames && !cur.gap && head != tail) { cur = queue[head]; head = (head + 1) % kQueue; }
    if (cur.frames) { cur.frames--; return cur.cmd; }
    if (cur.gap) cur.gap--;
    return 0;
  }
};

struct Position {
  Motor motor[2];          // 0 = sortie A, 1 = sortie B
  bool toggle = false;
  bool silent = true;
  uint16_t lockFrames = 0;
  uint16_t tailFrames = 0;
};

Position pos[kPositions];
uint8_t selector = 1;            // position courante (commandes sans chiffre)
uint8_t servedMask = 0x0F;       // positions servies par le maintien (bit 0 = position 1)
uint32_t keepUntil = 0;
uint32_t nextFrame = 0;
uint32_t tickMaxUs = 0;          // diagnostic : duree max d'un tour d'emission

bool keepActive() { return (int32_t)(keepUntil - millis()) > 0; }

const uint8_t kJmTxReg[5] = {0xCB, 0x2E, 0x87, 0xB4, 0x88};  // 88 B4 87 2E CB, ecrit a l'envers

void sendFrame(uint8_t p, uint8_t cmds) {
  nrf::setTxAddr(kJmTxReg);
  uint8_t f[13];
  jm::buildFrame(cmds, p + 1, pos[p].toggle, f);
  pos[p].toggle = !pos[p].toggle;
  for (uint8_t ch : jm::kChannels[p].list) if (ch < 126) nrf::send(ch, f, sizeof f);
}

// Decide et emet la trame d'une position. Renvoie true si quelque chose a ete emis.
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
  P.silent = true;  // plus rien a dire : on se tait, comme la vraie telecommande
  return false;
}

// ---------- Boitier 4 sorties (telecommande blanche XN297) ----------
namespace hub {
constexpr uint8_t kAddr[5] = {0xC1, 0xC2, 0xC3, 0xC4, 0xC5};
constexpr uint8_t kChannel = 2;
constexpr uint16_t kTailFrames = 20;  // trames « rien d'appuye » apres un relachement, comme l'original
// Bits de l'octet 7 (directions) : a+ = A haut ... ; carres dans l'octet 8 (et 6 pour le droit)
enum : uint8_t { kAUp = 0x02, kADown = 0x01, kBLeft = 0x10, kBRight = 0x20, kCUp = 0x04, kCDown = 0x08, kDLeft = 0x40, kDRight = 0x80 };

uint8_t bits = 0, squares = 0;      // ce qui est « appuye » en ce moment
uint16_t holdFrames = 0, tailFrames = 0;
uint8_t extra[6] = {0, 0, 0, 0, 1, 1};  // octets 0 a 5, toujours 00 00 00 00 01 01 (« hv » pour essayer ; 02 a la place de 01 = rien ne tourne)

// Trame de 10 octets : 00 00 00 00 01 01 S6 DIR SQ SOMME (somme des octets 0 a 8 + 0x14)
uint8_t build(uint8_t dir, uint8_t sq, uint8_t* out) {
  uint8_t m[10] = {extra[0], extra[1], extra[2], extra[3], extra[4], extra[5], (uint8_t)((sq & 2) ? 0x01 : 0x00), dir, sq, 0};
  uint8_t sum = 0x14;
  for (uint8_t i = 0; i < 9; i++) sum += m[i];
  m[9] = sum;
  return xn297::encode(kAddr, 5, m, 10, out);
}

void hold(uint8_t dir, uint8_t sq, int tenths) {
  bits = dir; squares = sq;
  holdFrames = tenths * 100 / jm::kPeriodMs;
}

bool tick() {
  uint8_t dir, sq;
  if (holdFrames) {
    holdFrames--; dir = bits; sq = squares;
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
}  // namespace hub

// Appelee en boucle : un tour d'emission toutes les 8 ms.
void tick() {
  if ((int32_t)(millis() - nextFrame) < 0) return;
  nextFrame = millis() + jm::kPeriodMs;
  uint32_t t0 = micros();
  for (uint8_t p = 0; p < kPositions; p++) tickPosition(p);
  hub::tick();
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

// Ordres du boitier 4 sorties : « ha+ 5 », « hs1 », « hx »
bool handleHub(char* line) {
  if (line[0] != 'h') return false;
  char* c = strtok(line + 1, " ");
  if (!c) return true;
  char* arg = strtok(nullptr, " ");
  int n = arg ? atoi(arg) : 3;
  if (n < 1) n = 1;
  String s(c);
  uint8_t dir = 0, sq = 0;
  if (s == "a+") dir = hub::kAUp; else if (s == "a-") dir = hub::kADown;
  else if (s == "b+") dir = hub::kBRight; else if (s == "b-") dir = hub::kBLeft;
  else if (s == "c+") dir = hub::kCUp; else if (s == "c-") dir = hub::kCDown;
  else if (s == "d+") dir = hub::kDRight; else if (s == "d-") dir = hub::kDLeft;
  else if (s == "s1") sq = 0x01; else if (s == "s2") sq = 0x02;
  else if (s == "v") {  // hv b0 .. b5 (hexa) : octets 0 a 5 de la trame ; non donnes = valeur d'origine
    hub::extra[0] = arg ? strtoul(arg, nullptr, 16) : 0;
    const uint8_t def[6] = {0, 0, 0, 0, 1, 1};
    for (uint8_t i = 1; i < 6; i++) { char* t = strtok(nullptr, " "); hub::extra[i] = t ? strtoul(t, nullptr, 16) : def[i]; }
    Serial.printf("OK hv %02X %02X %02X %02X %02X %02X\n", hub::extra[0], hub::extra[1], hub::extra[2], hub::extra[3], hub::extra[4], hub::extra[5]);
    return true;
  }
  else if (s == "x") { hub::holdFrames = 0; hub::tailFrames = hub::kTailFrames; Serial.println("OK hx"); return true; }
  else { Serial.println("? boitier : ha+ ha- hb+ hb- hc+ hc- hd+ hd- [n] | hs1 hs2 [n] | hx | hv b0..b5"); return true; }
  hub::hold(dir, sq, n);
  Serial.printf("OK h%s %d\n", c, n);
  return true;
}

void handleLine(char* line) {
  if (handleHub(line)) return;
  // Chiffre en tete = position du recepteur vise
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
  auto clicks = [&](uint8_t side, uint8_t cmd) {
    for (int i = 0; i < n && ok; i++) ok = P.motor[side].push(cmd, jm::kClickFrames, jm::kGapFrames);
  };
  if (s == "a+") clicks(0, jm::A1);
  else if (s == "a-") clicks(0, jm::A2);
  else if (s == "b+") clicks(1, jm::B1);
  else if (s == "b-") clicks(1, jm::B2);
  else if (s == "sa") { P.motor[0].clear(); ok = P.motor[0].push(jm::STOP_A, n * 100 / jm::kPeriodMs, 0); }
  else if (s == "sb") { P.motor[1].clear(); ok = P.motor[1].push(jm::STOP_B, n * 100 / jm::kPeriodMs, 0); }
  else if (s == "w") ok = P.motor[0].push(0, 0, min(n * 100 / (int)jm::kPeriodMs, 255));
  else if (s == "x") stopAll();
  else if (s == "k") { keepUntil = n ? millis() + kKeepMs : millis(); }
  else if (s == "m") {  // m 13 : positions 1 et 3
    servedMask = 0;
    for (const char* d = arg ? arg : ""; *d; d++) if (*d >= '1' && *d <= '4') servedMask |= 1 << (*d - '1');
  }
  else if (s == "c") selector = (n >= 1 && n <= 4) ? n : 1;
  else if (s == "l") {  // l c1 [c2 c3 c4] : canaux de la position courante (essais)
    uint8_t* L = jm::kChannels[selector - 1].list;
    L[0] = n; L[1] = L[2] = L[3] = 255;
    for (uint8_t i = 1; i < 4 && (arg = strtok(nullptr, " ")); i++) L[i] = atoi(arg);
  }
  else if (s == "p") {  // p 0..3 : puissance -18 / -12 / -6 / 0 dBm (avant l'ampli du module PA+LNA)
    nrf::writeReg(0x06, 0x01 | ((n > 3 ? 3 : n) << 1));
  }
  else if (s == "f") {  // diagnostic
    uint8_t f[13];
    jm::buildFrame(0, selector, false, f);
    Serial.printf("TRAME pos %u :", selector);
    for (uint8_t b : f) Serial.printf(" %02X", b);
    Serial.print(" | canaux");
    for (uint8_t ch : jm::kChannels[selector - 1].list) if (ch < 126) Serial.printf(" %u", ch);
    Serial.printf(" | servies %s%s%s%s | tour max %lu us | envois ok %lu, rates %lu | RF_SETUP %02X\n",
                  servedMask & 1 ? "1" : "", servedMask & 2 ? "2" : "", servedMask & 4 ? "3" : "",
                  servedMask & 8 ? "4" : "", (unsigned long)tickMaxUs, (unsigned long)nrf::sentOk,
                  (unsigned long)nrf::sentFail, nrf::readReg(0x06));
    tickMaxUs = 0;
    return;
  }
  else {
    Serial.println("? commandes : [1-4]a+ a- b+ b- [n] | [1-4]sa sb [n] | [1-4]w <n> | x | k 1|0 | m <1234> | c 1-4 | p 0-3 | f");
    return;
  }
  if (s != "k" || n) {
    if (keepActive()) keepUntil = millis() + kKeepMs;  // une commande prolonge le maintien
  }
  Serial.printf(ok ? "OK %u%s %d\n" : "PLEIN %u%s %d\n", target, c, n);
}

void setup() {
  Serial.begin(115200);
  delay(200);
  if (!nrf::begin()) {
    Serial.println("nRF24L01 non detecte : verifie le cablage et l'alimentation");
    while (true) delay(1000);
  }
  Serial.println("Emetteur JM039TX pret (4 positions). Commandes : [1-4]a+ a- b+ b- [n] | [1-4]sa sb | x | k 1|0 | c 1-4 | p 0-3 | f");
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
