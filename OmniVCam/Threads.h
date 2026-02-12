#pragma once
#include <Windows.h>
#ifdef __cplusplus
extern "C" {
#endif
	int open_thread(HANDLE* thread, LPTHREAD_START_ROUTINE start, LPVOID arg);
	void free_thread(HANDLE* thread);
#ifdef __cplusplus
}
#endif
