// ESP32 を Bluetooth (BLE) マウスにして、PC から USB シリアル経由で操作する。
// シリアル (115200bps) に 1 行ずつコマンドを送る:
//   m <dx> <dy>   相対移動 (小さな刻みに分けて送る)
//   M <dx> <dy>   相対移動を溜めて接続間隔ごとに送る (返事なし。PC のマウスを中継するとき用)
//   h             ポインタを左上の角へ寄せる
//   g <x> <y>     角へ寄せてから x、y の順に移動 (絶対位置指定の代わり)
//   c / r         左クリック / 右クリック
//   d / u         左ボタンを押す / 離す (ドラッグ用)
//   s <n>         スクロール
//   p <step> <ms> 移動の刻み幅と間隔を設定 (加速を抑える調整用)
//   ?             接続状態を表示
#include <NimBLEDevice.h>
#include <NimBLEHIDDevice.h>

static const uint8_t kReportMap[] = {
  0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, 0x01,
  0x09, 0x01, 0xA1, 0x00,
  0x05, 0x09, 0x19, 0x01, 0x29, 0x03, 0x15, 0x00, 0x25, 0x01,
  0x95, 0x03, 0x75, 0x01, 0x81, 0x02,
  0x95, 0x01, 0x75, 0x05, 0x81, 0x03,
  0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38,
  0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x03, 0x81, 0x06,
  0xC0, 0xC0
};

NimBLEHIDDevice* hid;
NimBLECharacteristic* input;
volatile bool connected = false;
uint8_t buttons = 0;
int stepSize = 10;
int stepDelayMs = 15;  // BLE の接続間隔より短いと報告が落ちて移動量がずれる

volatile uint16_t connIntervalUnits = 0;  // 1.25ms 単位

// iPhone が接続間隔を長くすると、送った報告が溜まって遅れて届く。
// 短い間隔 (11.25〜15ms) を要求し、実際の間隔より速くは送らないようにする。
class ServerCallbacks : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer* s, NimBLEConnInfo& info) override {
    connected = true;
    connIntervalUnits = info.getConnInterval();
    Serial.printf("event connected interval=%.2fms\n", connIntervalUnits * 1.25f);
    s->updateConnParams(info.getConnHandle(), 9, 12, 0, 400);  // HID は 11.25ms まで許される
  }
  void onConnParamsUpdate(NimBLEConnInfo& info) override {
    connIntervalUnits = info.getConnInterval();
    Serial.printf("event interval=%.2fms\n", connIntervalUnits * 1.25f);
  }
  void onDisconnect(NimBLEServer* s, NimBLEConnInfo& info, int reason) override {
    connected = false;
    Serial.printf("event disconnected %d\n", reason);
    NimBLEDevice::startAdvertising();
  }
};

void sendReport(int8_t dx, int8_t dy, int8_t wheel) {
  if (!connected) return;
  uint8_t r[4] = { buttons, (uint8_t)dx, (uint8_t)dy, (uint8_t)wheel };
  input->setValue(r, sizeof(r));
  input->notify();
}

int pacingMs() {
  int interval = (connIntervalUnits * 5 + 3) / 4;  // 切り上げ
  return max(stepDelayMs, interval + 1);
}

void moveBy(long dx, long dy) {
  while (dx != 0 || dy != 0) {
    int sx = constrain(dx, -stepSize, stepSize);
    int sy = constrain(dy, -stepSize, stepSize);
    sendReport(sx, sy, 0);
    dx -= sx; dy -= sy;
    delay(pacingMs());
  }
}

// 大きな刻みで左上の角へ。行き過ぎは画面端で止まるので精度は不要。
void goHome() {
  for (int i = 0; i < 40; i++) { sendReport(-127, -127, 0); delay(pacingMs()); }
  delay(50);
}

void setup() {
  Serial.begin(115200);
  NimBLEDevice::init("ESP32 Mouse");
  NimBLEDevice::setSecurityAuth(true, false, true);

  NimBLEServer* server = NimBLEDevice::createServer();
  server->setCallbacks(new ServerCallbacks());

  hid = new NimBLEHIDDevice(server);
  input = hid->getInputReport(1);
  hid->setManufacturer("OperateiPhones");
  hid->setPnp(0x02, 0xe502, 0xa111, 0x0210);
  hid->setHidInfo(0x00, 0x02);
  hid->setReportMap((uint8_t*)kReportMap, sizeof(kReportMap));
  hid->startServices();
  hid->setBatteryLevel(100);

  NimBLEAdvertising* adv = NimBLEDevice::getAdvertising();
  adv->setAppearance(0x03C2);  // マウス
  adv->addServiceUUID(hid->getHidService()->getUUID());
  adv->setName("ESP32 Mouse");
  adv->enableScanResponse(true);
  adv->start();
  Serial.println("ready");
}

// 'M' (返事なし) で届いた移動量を溜め、接続間隔ごとに 1 回の報告にまとめて送る。
// PC とのやり取りを待たないので、ポインタが滑らかに動く。
long accumX = 0, accumY = 0;
unsigned long lastReportMs = 0;
String lineBuf;

void flushAccum(bool all) {
  do {
    if (accumX == 0 && accumY == 0) return;
    if (millis() - lastReportMs < (unsigned long)pacingMs()) { if (!all) return; delay(1); continue; }
    int sx = constrain(accumX, -127, 127), sy = constrain(accumY, -127, 127);
    sendReport(sx, sy, 0);
    accumX -= sx; accumY -= sy;
    lastReportMs = millis();
  } while (all);
}

void loop() {
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n') { handleLine(lineBuf); lineBuf = ""; }
    else if (ch != '\r') lineBuf += ch;
  }
  flushAccum(false);
  delay(1);
}

void handleLine(String line) {
  line.trim();
  if (line.length() == 0) return;
  char cmd = line.charAt(0);
  long a = 0, b = 0;
  sscanf(line.c_str() + 1, "%ld %ld", &a, &b);

  if (cmd == 'M') { accumX += a; accumY += b; return; }
  flushAccum(true);  // ボタン操作などの前に、溜まった移動を送り切る

  switch (cmd) {
    case 'm': moveBy(a, b); break;
    case 'h': goHome(); break;
    case 'g': goHome(); moveBy(a, 0); moveBy(0, b); break;
    case 'c': buttons = 1; sendReport(0, 0, 0); delay(30); buttons = 0; sendReport(0, 0, 0); break;
    case 'r': buttons = 2; sendReport(0, 0, 0); delay(30); buttons = 0; sendReport(0, 0, 0); break;
    case 'd': buttons = 1; sendReport(0, 0, 0); break;
    case 'u': buttons = 0; sendReport(0, 0, 0); break;
    case 's': sendReport(0, 0, constrain(a, -127, 127)); break;
    case 'p': if (a > 0) stepSize = constrain(a, 1, 127); if (b >= 0) stepDelayMs = b; break;
    case '?': break;
    default: Serial.println("err unknown"); return;
  }
  Serial.printf("ok %s step=%d delay=%d interval=%.2f\n", connected ? "connected" : "waiting", stepSize, stepDelayMs, connIntervalUnits * 1.25f);
}
