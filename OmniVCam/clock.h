#include <windows.h>
#include <stdint.h>

uint64_t util_mul_div64(uint64_t num, uint64_t mul, uint64_t div);
uint64_t os_gettime_ns(void);
BOOL os_sleepto_ns(uint64_t time_target);