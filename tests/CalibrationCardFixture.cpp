#include <windows.h>
#include <cstdio>
#include "../OmniVCam/TestCard.h"

// TestCard's only project dependency; FFmpeg performs the same aligned allocation here.
extern "C" int get_video_buffer(AVFrame* frame) { return av_frame_get_buffer(frame, 32); }

static bool SaveBmp(const char* path, AVFrame* frame) {
    FILE* file = nullptr;
    if (fopen_s(&file, path, "wb") || !file) return false;
    int stride = (frame->width * 3 + 3) & ~3;
    BITMAPFILEHEADER header = {};
    header.bfType = 0x4d42;
    header.bfOffBits = sizeof(header) + sizeof(BITMAPINFOHEADER);
    header.bfSize = header.bfOffBits + stride * frame->height;
    BITMAPINFOHEADER info = {};
    info.biSize = sizeof(info);
    info.biWidth = frame->width;
    info.biHeight = frame->height; // TestCard BGR output uses DirectShow's bottom-up layout.
    info.biPlanes = 1;
    info.biBitCount = 24;
    fwrite(&header, sizeof(header), 1, file);
    fwrite(&info, sizeof(info), 1, file);
    for (int y = 0; y < frame->height; y++) fwrite(frame->data[0] + y * frame->linesize[0], stride, 1, file);
    fclose(file);
    return true;
}

int main() {
    const int sizes[][2] = { {1920,1080}, {640,480}, {320,240} };
    for (int size = 0; size < 3; ++size) {
        void* card = test_card_alloc(sizes[size][0], sizes[size][1], AV_PIX_FMT_BGR24, {60000,1001}, 2);
        test_card_set_identity(card, 16999, 123456);
        const int shifts[] = {0, 1042, 8333};
        for (int i = 0; i < 3; ++i) {
            test_card_set_shift(card, shifts[i]);
            AVFrame* frame = test_card_draw(card, nullptr);
            if (!frame) return 1;
            char path[120];
            sprintf_s(path, "tests/out/card-%d-%d.bmp", size, i);
            if (!SaveBmp(path, frame)) return 2;
            av_frame_free(&frame);
        }
        test_card_free(card);
    }
    printf("Generated 9 native calibration frames.\n");
    return 0;
}
