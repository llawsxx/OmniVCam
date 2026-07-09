#pragma once
#ifdef __cplusplus
extern "C" {
#endif
	#include <libavutil/time.h>
	#include <libswscale/swscale.h>
	void* test_card_alloc(int width, int height, enum AVPixelFormat outPixelFormat, AVRational fps,int style);
	void test_card_free(void* p);
	AVFrame* test_card_draw(void* p, char* infoText);
	AVFrame* test_card_draw_text(void* p, char* text);
#ifdef __cplusplus
}
#endif

// œ‘ Ω¡¥Ω” GDI32 ø‚
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")