#include "tuya_crypto.h"

#include <mbedtls/aes.h>
#include <mbedtls/gcm.h>
#include <mbedtls/md.h>

namespace tuya {

bool aesEcb(const uint8_t key[kKeyLen], bool encrypt, uint8_t *data, size_t len) {
  if (len == 0 || (len % kAesBlock) != 0) return false;

  mbedtls_aes_context ctx;
  mbedtls_aes_init(&ctx);
  const int keyRc = encrypt ? mbedtls_aes_setkey_enc(&ctx, key, 128)
                            : mbedtls_aes_setkey_dec(&ctx, key, 128);
  bool ok = (keyRc == 0);
  const int mode = encrypt ? MBEDTLS_AES_ENCRYPT : MBEDTLS_AES_DECRYPT;
  for (size_t off = 0; ok && off < len; off += kAesBlock)
    ok = mbedtls_aes_crypt_ecb(&ctx, mode, data + off, data + off) == 0;
  mbedtls_aes_free(&ctx);
  return ok;
}

bool aesGcm(const uint8_t key[kKeyLen], bool encrypt,
            const uint8_t *nonce, size_t nonceLen,
            const uint8_t *aad, size_t aadLen,
            uint8_t *data, size_t len, uint8_t tag[kGcmTagLen]) {
  mbedtls_gcm_context ctx;
  mbedtls_gcm_init(&ctx);
  bool ok = false;

  if (mbedtls_gcm_setkey(&ctx, MBEDTLS_CIPHER_ID_AES, key, 128) == 0) {
    if (encrypt) {
      ok = mbedtls_gcm_crypt_and_tag(&ctx, MBEDTLS_GCM_ENCRYPT, len, nonce,
                                     nonceLen, aad, aadLen, data, data,
                                     kGcmTagLen, tag) == 0;
    } else {
      ok = mbedtls_gcm_auth_decrypt(&ctx, len, nonce, nonceLen, aad, aadLen,
                                    tag, kGcmTagLen, data, data) == 0;
    }
  }
  mbedtls_gcm_free(&ctx);
  return ok;
}

void hmacSha256(const uint8_t *key, size_t keyLen,
                const uint8_t *msg, size_t msgLen, uint8_t out[kHmacLen]) {
  const mbedtls_md_info_t *info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  mbedtls_md_hmac(info, key, keyLen, msg, msgLen, out);
}

uint32_t crc32(const uint8_t *data, size_t len) {
  uint32_t crc = 0xFFFFFFFFUL;
  for (size_t i = 0; i < len; i++) {
    crc ^= data[i];
    for (uint8_t b = 0; b < 8; b++)
      crc = (crc >> 1) ^ (0xEDB88320UL & (0U - (crc & 1U)));
  }
  return ~crc;
}

size_t pkcs7Pad(uint8_t *buf, size_t len, size_t cap) {
  const size_t pad = kAesBlock - (len % kAesBlock);
  if (len + pad > cap) return 0;
  for (size_t i = 0; i < pad; i++) buf[len + i] = static_cast<uint8_t>(pad);
  return len + pad;
}

size_t pkcs7Unpad(const uint8_t *buf, size_t len) {
  if (len == 0 || (len % kAesBlock) != 0) return 0;
  const uint8_t pad = buf[len - 1];
  if (pad == 0 || pad > kAesBlock) return 0;
  for (size_t i = len - pad; i < len; i++)
    if (buf[i] != pad) return 0;
  return len - pad;
}

bool equalsConstTime(const uint8_t *a, const uint8_t *b, size_t len) {
  uint8_t diff = 0;
  for (size_t i = 0; i < len; i++) diff |= a[i] ^ b[i];
  return diff == 0;
}

}  // namespace tuya
