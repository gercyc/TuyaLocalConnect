#include "tuya_frame.h"

#include <esp_random.h>
#include <string.h>

namespace tuya {
namespace {

constexpr uint8_t kPrefix55[4] = {0x00, 0x00, 0x55, 0xAA};
constexpr uint8_t kSuffix55[4] = {0x00, 0x00, 0xAA, 0x55};
constexpr uint8_t kPrefix66[4] = {0x00, 0x00, 0x66, 0x99};
constexpr uint8_t kSuffix66[4] = {0x00, 0x00, 0x99, 0x66};

constexpr size_t kHeader55   = 16;   // prefix + seq + cmd + len
constexpr size_t kHeader66   = 18;   // prefix + flags + seq + cmd + len
constexpr size_t kAad66      = 14;   // flags + seq + cmd + len
constexpr size_t kNonceLen   = 12;
constexpr size_t kSuffixLen  = 4;
constexpr size_t kRetcodeLen = 4;
constexpr size_t kVerHdrLen  = 15;   // "3.x" + 12 bytes de reserva

void put32(uint8_t *p, uint32_t v) {
  p[0] = v >> 24; p[1] = v >> 16; p[2] = v >> 8; p[3] = v;
}

uint32_t get32(const uint8_t *p) {
  return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) |
         (uint32_t(p[2]) << 8) | uint32_t(p[3]);
}

bool isVersionHeader(const uint8_t *p, size_t len) {
  return len >= kVerHdrLen && p[0] == '3' && p[1] == '.' &&
         p[2] >= '1' && p[2] <= '9';
}

// Tira um retcode de 4 bytes zerados e/ou o cabecalho de versao que
// algumas respostas trazem antes do JSON. O corpo util comeca em '{' ou
// e o nonce ASCII do handshake, entao nenhum dos dois comeca com 0x00.
size_t stripInnerHeaders(uint8_t *plain, size_t len) {
  if (len > kRetcodeLen && plain[0] == 0 && plain[1] == 0 &&
      plain[2] == 0 && plain[3] == 0) {
    memmove(plain, plain + kRetcodeLen, len - kRetcodeLen);
    len -= kRetcodeLen;
  }
  if (isVersionHeader(plain, len)) {
    memmove(plain, plain + kVerHdrLen, len - kVerHdrLen);
    len -= kVerHdrLen;
  }
  return len;
}

size_t build66(uint32_t seq, uint32_t cmd, const uint8_t *payload, size_t len,
               const uint8_t key[kKeyLen], uint8_t *out, size_t cap) {
  const size_t bodyLen = kNonceLen + len + kGcmTagLen;
  const size_t total = kHeader66 + bodyLen + kSuffixLen;
  if (total > cap) return 0;

  memcpy(out, kPrefix66, 4);
  out[4] = 0; out[5] = 0;   // flags
  put32(out + 6, seq);
  put32(out + 10, cmd);
  put32(out + 14, bodyLen);

  // Nonce do RNG de hardware: em GCM, repetir nonce com a mesma chave
  // quebra a autenticacao.
  uint8_t *nonce = out + kHeader66;
  esp_fill_random(nonce, kNonceLen);

  uint8_t *ct = nonce + kNonceLen;
  memcpy(ct, payload, len);
  if (!aesGcm(key, true, nonce, kNonceLen, out + 4, kAad66, ct, len, ct + len))
    return 0;

  memcpy(out + kHeader66 + bodyLen, kSuffix66, kSuffixLen);
  return total;
}

size_t build55(Proto proto, uint32_t seq, uint32_t cmd, const uint8_t *payload,
               size_t len, const uint8_t key[kKeyLen], uint8_t *out, size_t cap) {
  const size_t macLen = (proto >= 34) ? kHmacLen : 4;
  uint8_t *enc = out + kHeader55;
  const size_t encCap = cap > kHeader55 + macLen + kSuffixLen
                            ? cap - kHeader55 - macLen - kSuffixLen : 0;
  if (len >= encCap) return 0;

  memcpy(enc, payload, len);
  const size_t encLen = pkcs7Pad(enc, len, encCap);
  if (encLen == 0 || !aesEcb(key, true, enc, encLen)) return 0;

  const size_t bodyLen = encLen + macLen + kSuffixLen;
  memcpy(out, kPrefix55, 4);
  put32(out + 4, seq);
  put32(out + 8, cmd);
  put32(out + 12, bodyLen);

  uint8_t *tail = enc + encLen;
  if (proto >= 34) {
    hmacSha256(key, kKeyLen, out, kHeader55 + encLen, tail);
  } else {
    put32(tail, crc32(out, kHeader55 + encLen));
  }
  memcpy(tail + macLen, kSuffix55, kSuffixLen);
  return kHeader55 + bodyLen;
}

bool parse66(const uint8_t *frame, size_t frameLen, const uint8_t key[kKeyLen],
             uint8_t *plain, size_t cap, ParsedFrame &out) {
  if (frameLen < kHeader66 + kNonceLen + kGcmTagLen) return false;
  const uint32_t bodyLen = get32(frame + 14);
  if (bodyLen < kNonceLen + kGcmTagLen || bodyLen > frameLen - kHeader66)
    return false;

  const size_t ctLen = bodyLen - kNonceLen - kGcmTagLen;
  if (ctLen > cap) return false;

  const uint8_t *nonce = frame + kHeader66;
  const uint8_t *ct = nonce + kNonceLen;
  uint8_t tag[kGcmTagLen];
  memcpy(tag, ct + ctLen, kGcmTagLen);
  memcpy(plain, ct, ctLen);

  if (!aesGcm(key, false, nonce, kNonceLen, frame + 4, kAad66, plain, ctLen, tag))
    return false;

  out.cmd = get32(frame + 10);
  out.payloadLen = stripInnerHeaders(plain, ctLen);
  return true;
}

bool parse55(Proto proto, const uint8_t *frame, size_t frameLen,
             const uint8_t key[kKeyLen], uint8_t *plain, size_t cap,
             ParsedFrame &out) {
  const size_t macLen = (proto >= 34) ? kHmacLen : 4;
  if (frameLen < kHeader55 + macLen + kSuffixLen) return false;
  const uint32_t bodyLen = get32(frame + 12);
  if (bodyLen < macLen + kSuffixLen || bodyLen > frameLen - kHeader55)
    return false;

  // Integridade sobre header + payload (retcode incluso, se houver).
  const size_t signedLen = kHeader55 + bodyLen - macLen - kSuffixLen;
  const uint8_t *mac = frame + signedLen;
  if (proto >= 34) {
    uint8_t expected[kHmacLen];
    hmacSha256(key, kKeyLen, frame, signedLen, expected);
    if (!equalsConstTime(expected, mac, kHmacLen)) return false;
  } else if (crc32(frame, signedLen) != get32(mac)) {
    return false;
  }

  const uint8_t *enc = frame + kHeader55;
  size_t encLen = signedLen - kHeader55;
  // Retcode de 4 bytes fora da cifra: o corpo cifrado e sempre multiplo
  // de 16, entao o resto da divisao denuncia o prefixo.
  if (encLen % kAesBlock == kRetcodeLen) { enc += kRetcodeLen; encLen -= kRetcodeLen; }
  // 3.3: alguns frames (STATUS) trazem "3.3" + 12 bytes em claro.
  if (isVersionHeader(enc, encLen) && (encLen - kVerHdrLen) % kAesBlock == 0) {
    enc += kVerHdrLen; encLen -= kVerHdrLen;
  }
  if (encLen == 0) {   // ACK sem corpo
    out.cmd = get32(frame + 8);
    out.payloadLen = 0;
    return true;
  }
  if (encLen > cap) return false;

  memcpy(plain, enc, encLen);
  if (!aesEcb(key, false, plain, encLen)) return false;
  const size_t len = pkcs7Unpad(plain, encLen);
  if (len == 0) return false;

  out.cmd = get32(frame + 8);
  out.payloadLen = stripInnerHeaders(plain, len);
  return true;
}

}  // namespace

size_t buildFrame(Proto proto, uint32_t seq, uint32_t cmd,
                  const uint8_t *payload, size_t payloadLen,
                  const uint8_t key[kKeyLen], uint8_t *out, size_t cap) {
  return proto >= 35 ? build66(seq, cmd, payload, payloadLen, key, out, cap)
                     : build55(proto, seq, cmd, payload, payloadLen, key, out, cap);
}

long frameTotalLength(const uint8_t *buf, size_t got) {
  if (got < 4) return 0;
  // O tamanho vem do outro lado: compara antes de somar, porque com
  // size_t de 32 bits um valor perto de 2^32 daria a volta na soma.
  if (memcmp(buf, kPrefix66, 4) == 0) {
    if (got < kHeader66) return 0;
    const uint32_t body = get32(buf + 14);
    if (body > kMaxFrame) return -1;
    const size_t total = kHeader66 + body + kSuffixLen;
    return total > kMaxFrame ? -1 : long(total);
  }
  if (memcmp(buf, kPrefix55, 4) == 0) {
    if (got < kHeader55) return 0;
    const uint32_t body = get32(buf + 12);
    if (body > kMaxFrame) return -1;
    const size_t total = kHeader55 + body;
    return total > kMaxFrame ? -1 : long(total);
  }
  return -1;
}

bool parseFrame(Proto proto, const uint8_t *frame, size_t frameLen,
                const uint8_t key[kKeyLen], uint8_t *plain, size_t cap,
                ParsedFrame &out) {
  // O envelope se identifica pelo magic: um device configurado como 3.4
  // que responda em 6699 ainda e entendido.
  if (frameLen >= 4 && memcmp(frame, kPrefix66, 4) == 0)
    return parse66(frame, frameLen, key, plain, cap, out);
  if (frameLen >= 4 && memcmp(frame, kPrefix55, 4) == 0)
    return parse55(proto, frame, frameLen, key, plain, cap, out);
  return false;
}

}  // namespace tuya
