/* RAII para mutex do FreeRTOS: libera no fim do escopo, inclusive em return. */
#pragma once
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>

class LockGuard {
 public:
  explicit LockGuard(SemaphoreHandle_t m) : m_(m) { xSemaphoreTake(m_, portMAX_DELAY); }
  ~LockGuard() { xSemaphoreGive(m_); }
  LockGuard(const LockGuard &) = delete;
  LockGuard &operator=(const LockGuard &) = delete;

 private:
  SemaphoreHandle_t m_;
};
