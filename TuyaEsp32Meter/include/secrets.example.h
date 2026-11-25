/* ============================================================
 *  secrets.example.h — copie para secrets.h (gitignored).
 *
 *  Valores usados SO no primeiro boot, para semear a NVS quando ela
 *  esta vazia. Depois disso tudo e editado pela pagina web, e mudar
 *  este arquivo nao tem efeito (use "Apagar configuracoes" na pagina
 *  para voltar a eles).
 *
 *  Sem secrets.h o ESP32 sobe em modo AP ("TuyaMeter-XXXX"; a senha,
 *  aleatoria por placa, sai na serial) e a configuracao e feita em
 *  http://192.168.4.1/.
 *
 *  Chaves de device (local_key) NAO entram aqui: sao cadastradas pela
 *  pagina e ficam so na NVS do ESP32.
 * ============================================================ */
#pragma once

#define SEED_WIFI_SSID  "minha-rede"
#define SEED_WIFI_PASS  "minha-senha"
#define SEED_MQTT_HOST  "192.168.0.10"
#define SEED_MQTT_PORT  1883
