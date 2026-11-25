/* ============================================================
 *  tuya_crypto.h — primitivas usadas pelo protocolo Tuya LAN
 *
 *  Wrappers finos sobre o mbedTLS do core ESP32. Todas operam no
 *  lugar (`data` entra em claro e sai cifrado, ou vice-versa).
 * ============================================================ */
#pragma once
#include <stddef.h>
#include <stdint.h>

namespace tuya {

constexpr size_t kKeyLen    = 16;
constexpr size_t kAesBlock  = 16;
constexpr size_t kHmacLen   = 32;
constexpr size_t kGcmTagLen = 16;

// AES-128-ECB. `len` precisa ser multiplo de 16.
bool aesEcb(const uint8_t key[kKeyLen], bool encrypt, uint8_t *data, size_t len);

// AES-128-GCM. Na decifragem a tag e conferida: frame adulterado ou
// cifrado com outra chave devolve false em vez de lixo silencioso.
bool aesGcm(const uint8_t key[kKeyLen], bool encrypt,
            const uint8_t *nonce, size_t nonceLen,
            const uint8_t *aad, size_t aadLen,
            uint8_t *data, size_t len, uint8_t tag[kGcmTagLen]);

void hmacSha256(const uint8_t *key, size_t keyLen,
                const uint8_t *msg, size_t msgLen, uint8_t out[kHmacLen]);

// CRC32 padrao (zlib), usado como integridade no protocolo 3.3.
uint32_t crc32(const uint8_t *data, size_t len);

// PKCS#7. pad devolve o novo tamanho (0 se nao couber em `cap`);
// unpad devolve o tamanho sem padding (0 se o padding for invalido).
size_t pkcs7Pad(uint8_t *buf, size_t len, size_t cap);
size_t pkcs7Unpad(const uint8_t *buf, size_t len);

// Comparacao em tempo constante, para conferir MACs.
bool equalsConstTime(const uint8_t *a, const uint8_t *b, size_t len);

}  // namespace tuya
