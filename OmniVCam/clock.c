#include "clock.h"

static BOOL have_clockfreq = FALSE;
static LARGE_INTEGER clock_freq;

//directshow use 100ns as time unit
//OBS虚拟摄像头特意把clock分一个文件出来，有特别原因？可能会影响时钟准确性？

uint64_t get_current_time(uint64_t start_time)
{
	LARGE_INTEGER current_time;
	double time_val;

	if (!have_clockfreq) {
		QueryPerformanceFrequency(&clock_freq);
		have_clockfreq = TRUE;
	}

	QueryPerformanceCounter(&current_time);
	time_val = (double)current_time.QuadPart;
	time_val *= 1000000.0;
	time_val /= (double)clock_freq.QuadPart;

	return (uint64_t)time_val - start_time;
}

BOOL sleepto(uint64_t time_target, uint64_t start_time)
{
	uint64_t t = get_current_time(start_time);
	uint32_t milliseconds;

	if (t >= time_target)
		return FALSE;

	milliseconds = (uint32_t)((time_target - t) / 1000);
	if (milliseconds > 1)
		Sleep(milliseconds - 1);

	return TRUE;
}

