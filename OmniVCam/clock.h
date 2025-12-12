#include <windows.h>
#include <stdint.h>

uint64_t get_current_time(uint64_t start_time);
BOOL sleepto(uint64_t time_target, uint64_t start_time);