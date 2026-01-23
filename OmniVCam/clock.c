#include "clock.h"

static BOOL have_clockfreq = FALSE;
static LARGE_INTEGER clock_freq;


uint64_t util_mul_div64(uint64_t num, uint64_t mul, uint64_t div)
{
	const uint64_t rem = num % div;
	return (num / div) * mul + (rem * mul) / div;
}

static inline uint64_t get_clockfreq(void)
{
	if (!have_clockfreq) {
		QueryPerformanceFrequency(&clock_freq);
		have_clockfreq = TRUE;
	}

	return clock_freq.QuadPart;
}


uint64_t os_gettime_ns(void)
{
	LARGE_INTEGER current_time;
	QueryPerformanceCounter(&current_time);
	return util_mul_div64(current_time.QuadPart, 1000000000, get_clockfreq());
}

BOOL os_sleepto_ns(uint64_t time_target)
{
	const uint64_t freq = get_clockfreq();
	const LONGLONG count_target = util_mul_div64(time_target, freq, 1000000000);

	LARGE_INTEGER count;
	QueryPerformanceCounter(&count);

	const BOOL stall = count.QuadPart < count_target;
	if (stall) {
		const DWORD milliseconds = (DWORD)(((count_target - count.QuadPart) * 1000.0) / freq);
		if (milliseconds > 1)
			Sleep(milliseconds - 1);

		for (;;) {
			QueryPerformanceCounter(&count);
			if (count.QuadPart >= count_target)
				break;

			YieldProcessor();
		}
	}

	return stall;
}
