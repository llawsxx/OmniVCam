#include "Threads.h"
int open_thread(HANDLE* thread, LPTHREAD_START_ROUTINE start, LPVOID arg)
{
	if (!thread) return -1;

	*thread = CreateThread(NULL, 0, start, arg, 0, NULL);

	if (*thread != NULL) return 0;
	return -1;
}

void free_thread(HANDLE* thread)
{
	if (!thread || !(*thread)) return;
	WaitForSingleObject(*thread, INFINITE);
	CloseHandle(*thread);
	*thread = NULL;
}