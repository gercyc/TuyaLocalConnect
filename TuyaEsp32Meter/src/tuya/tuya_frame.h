/* ============================================================
 *  tuya_frame.h — montagem e leitura dos frames Tuya LAN
 *
 *  Dois envelopes convivem na porta 6668:
 *
 *  55AA (3.3 / 3.4)
 *    00 00 55 AA | seq(4) | cmd(4) | len(4) | payload | crc-ou-hmac | 00 00 AA 55
 *    Payload em AES-128-ECB + PKCS#7. Integridade CRC32 (3.3) ou
 *    HMAC-SHA256 (3.4). `len` conta payload + integridade + sufixo.
 *
 *  6699 (3.5)
 *    00 00 66 99 | flags(2) | seq(4) | cmd(4) | len(4) |
 *                | nonce(12) | ciphertext | tag(16) | 00 00 99 66
 *    AES-128-GCM. AAD = os 14 bytes entre o magic e o nonce. `len`
 *    conta nonce + ciphertext + tag (o sufixo fica de fora).
 * ============================================================ */
#pragma once
#include <stddef.h>
#include <stdint.h>

#include "tuya_crypto.h"

namespace tuya {

// Teto de frame aceito. O campo de tamanho vem do header, controlado
// pelo outro lado: rejeitar antes de ler protege a RAM. O DP_QUERY do
// medidor dual (~50 DPs) responde perto de 1KB.
constexpr size_t kMaxFrame = 4096;

enum Cmd : uint32_t {
  kCmdSessStart  = 0x03,
  kCmdSessResp   = 0x04,
  kCmdSessFinish = 0x05,
  kCmdStatus     = 0x08,
  kCmdDpQuery    = 0x0A,
  kCmdDpQueryNew = 0x10,
};

// Versao do protocolo em decimos: 33, 34 ou 35.
using Proto = uint8_t;

// Monta um frame em `out`. Devolve o tamanho total ou 0 se nao couber.
size_t buildFrame(Proto proto, uint32_t seq, uint32_t cmd,
                  const uint8_t *payload, size_t payloadLen,
                  const uint8_t key[kKeyLen], uint8_t *out, size_t cap);

// Quantos bytes o frame que comeca em `buf` ocupa no total, olhando so
// o header. 0 = header ainda incompleto; -1 = frame invalido ou grande
// demais para kMaxFrame.
long frameTotalLength(const uint8_t *buf, size_t got);

struct ParsedFrame {
  uint32_t cmd = 0;
  size_t   payloadLen = 0;   // bytes validos em `plain`
};

// Confere a integridade, decifra e tira os cabecalhos internos (retcode,
// "3.x" + 12 zeros). Devolve false se o frame nao bater com a chave.
bool parseFrame(Proto proto, const uint8_t *frame, size_t frameLen,
                const uint8_t key[kKeyLen], uint8_t *plain, size_t cap,
                ParsedFrame &out);

}  // namespace tuya
