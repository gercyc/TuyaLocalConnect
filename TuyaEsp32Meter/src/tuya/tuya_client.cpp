#include "tuya_client.h"

#include <WiFi.h>
#include <esp_random.h>

namespace tuya {
namespace {

constexpr uint32_t kConnectTimeoutMs = 5000;
constexpr uint32_t kIoTimeoutMs      = 5000;
constexpr uint32_t kQueryDeadlineMs  = 10000;  // handshake + resposta, apos o connect
constexpr uint16_t kTcpPort          = 6668;
constexpr size_t   kNonceLen         = 16;
constexpr uint8_t  kMaxFramesPerQuery = 4;   // pula STATUS que chegue antes
constexpr size_t   kHeaderProbe      = 18;  // maior header (6699)

// Nonce local do handshake. ASCII imprimivel, como os clientes oficiais:
// alguns firmwares rejeitam bytes de controle aqui.
void makeLocalNonce(uint8_t out[kNonceLen]) {
  esp_fill_random(out, kNonceLen);
  for (size_t i = 0; i < kNonceLen; i++) out[i] = '0' + (out[i] % 74);
}

String legacyQueryPayload(const String &id) {
  JsonDocument req;
  req["gwId"]  = id;
  req["devId"] = id;
  req["uid"]   = id;
  req["t"]     = String(millis() / 1000);
  String out;
  serializeJson(req, out);
  return out;
}

}  // namespace

Client::Client()
    : tx_(new uint8_t[kMaxFrame]),
      rx_(new uint8_t[kMaxFrame]),
      plain_(new uint8_t[kMaxFrame]) {}

bool Client::send(WiFiClient &cli, uint32_t cmd, const uint8_t *payload,
                  size_t len, const uint8_t *key, Proto proto, String &err) {
  const size_t n = buildFrame(proto, seq_++, cmd, payload, len, key,
                              tx_.get(), kMaxFrame);
  if (n == 0) { err = "falha ao montar frame"; return false; }
  if (cli.write(tx_.get(), n) != n) { err = "falha ao enviar"; return false; }
  return true;
}

bool Client::receive(WiFiClient &cli, const uint8_t *key, Proto proto,
                     ParsedFrame &frame, String &err) {
  uint8_t *buf = rx_.get();
  size_t got = 0;
  long total = 0;
  const uint32_t start = millis();

  // Cada leitura tem kIoTimeoutMs, mas nunca passa do prazo da consulta
  // inteira: varios frames lentos somados nao seguram quem chamou.
  while (millis() - start < kIoTimeoutMs && int32_t(deadline_ - millis()) > 0) {
    const int avail = cli.available();
    if (avail > 0) {
      // Sem o tamanho ainda, le so o header: assim nunca consome bytes
      // do frame seguinte (todo frame tem mais que kHeaderProbe bytes).
      const size_t want = total > 0 ? size_t(total) - got : kHeaderProbe - got;
      const int r = cli.read(buf + got, min(size_t(avail), want));
      if (r > 0) got += size_t(r);
      total = frameTotalLength(buf, got);
      if (total < 0) { err = "frame invalido ou grande demais"; return false; }
      if (total > 0 && got >= size_t(total)) break;
    } else if (!cli.connected()) {
      err = "device fechou a conexao";
      return false;
    } else {
      delay(5);
    }
  }
  if (total <= 0 || got < size_t(total)) { err = "sem resposta do device"; return false; }

  if (!parseFrame(proto, buf, got, key, plain_.get(), kMaxFrame, frame)) {
    err = "resposta nao decifrada (local_key ou versao incorreta?)";
    return false;
  }
  return true;
}

// Handshake de 3 passos do 3.4/3.5. Deriva a chave de sessao a partir
// dos dois nonces e confere o HMAC do device: chave errada aparece aqui
// com mensagem clara, em vez de um "nao decifrado" generico depois.
bool Client::negotiate(WiFiClient &cli, const Endpoint &ep, String &err) {
  uint8_t localNonce[kNonceLen];
  makeLocalNonce(localNonce);

  if (!send(cli, kCmdSessStart, localNonce, kNonceLen, ep.localKey, ep.proto, err))
    return false;

  ParsedFrame resp;
  if (!receive(cli, ep.localKey, ep.proto, resp, err)) {
    // Com chave ou versao erradas o device nao responde: so fecha a
    // conexao. O motivo real quase sempre e um dos dois.
    err = "handshake recusado (" + err + ") - confira a local_key e o protocolo";
    return false;
  }
  if (resp.cmd != kCmdSessResp || resp.payloadLen < kNonceLen + kHmacLen) {
    err = "handshake: resposta inesperada (cmd " + String(resp.cmd) + ")";
    return false;
  }

  uint8_t remoteNonce[kNonceLen];
  memcpy(remoteNonce, plain_.get(), kNonceLen);

  uint8_t expected[kHmacLen];
  hmacSha256(ep.localKey, kKeyLen, localNonce, kNonceLen, expected);
  if (!equalsConstTime(expected, plain_.get() + kNonceLen, kHmacLen)) {
    err = "local_key incorreta (HMAC do handshake nao confere)";
    return false;
  }

  uint8_t mac[kHmacLen];
  hmacSha256(ep.localKey, kKeyLen, remoteNonce, kNonceLen, mac);
  if (!send(cli, kCmdSessFinish, mac, kHmacLen, ep.localKey, ep.proto, err))
    return false;

  for (size_t i = 0; i < kKeyLen; i++) sessionKey_[i] = localNonce[i] ^ remoteNonce[i];

  // 3.4 cifra o XOR em ECB; 3.5 usa GCM com localNonce[:12], sem AAD.
  if (ep.proto >= 35) {
    uint8_t tag[kGcmTagLen];   // descartada: so o keystream importa
    if (!aesGcm(ep.localKey, true, localNonce, 12, nullptr, 0, sessionKey_, kKeyLen, tag)) {
      err = "handshake: falha ao derivar chave";
      return false;
    }
  } else if (!aesEcb(ep.localKey, true, sessionKey_, kKeyLen)) {
    err = "handshake: falha ao derivar chave";
    return false;
  }
  return true;
}

bool Client::queryDps(const Endpoint &ep, JsonDocument &dps, String &err) {
  deadline_ = millis() + kQueryDeadlineMs;
  WiFiClient cli;
  if (!cli.connect(ep.ip.c_str(), kTcpPort, kConnectTimeoutMs)) {
    err = "sem conexao TCP com " + ep.ip + ":6668";
    return false;
  }
  cli.setNoDelay(true);

  bool ok = false;
  do {
    const uint8_t *key = ep.localKey;
    if (ep.proto >= 34) {
      if (!negotiate(cli, ep, err)) break;
      key = sessionKey_;
    }

    // 3.4/3.5 usam DP_QUERY_NEW com "{}"; o 3.3 manda os ids no corpo.
    const bool isNew = ep.proto >= 34;
    const String payload = isNew ? String("{}") : legacyQueryPayload(ep.id);
    const uint32_t cmd = isNew ? kCmdDpQueryNew : kCmdDpQuery;
    if (!send(cli, cmd, reinterpret_cast<const uint8_t *>(payload.c_str()),
              payload.length(), key, ep.proto, err))
      break;

    // O device pode empurrar um STATUS antes da resposta: aceita o
    // primeiro frame que trouxer "dps".
    err = "device respondeu sem dps";   // sobrescrito por erro mais especifico
    for (uint8_t i = 0; i < kMaxFramesPerQuery && !ok; i++) {
      ParsedFrame frame;
      if (!receive(cli, key, ep.proto, frame, err)) break;
      if (frame.payloadLen == 0) continue;

      JsonDocument doc;
      if (deserializeJson(doc, plain_.get(), frame.payloadLen)) {
        err = "JSON invalido na resposta";
        continue;
      }
      JsonVariant found = doc["dps"];
      if (found.isNull()) found = doc["data"]["dps"];
      if (found.isNull()) { err = "resposta sem dps"; continue; }

      dps.clear();
      dps.set(found);
      ok = true;
    }
  } while (false);

  cli.stop();
  memset(sessionKey_, 0, sizeof(sessionKey_));   // sessao vale por conexao
  return ok;
}

}  // namespace tuya
