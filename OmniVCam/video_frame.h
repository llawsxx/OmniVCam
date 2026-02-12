#ifdef __cplusplus
extern "C" {
#endif
#include <libavutil/imgutils.h>
#include <libavutil/frame.h>
int get_video_buffer(AVFrame* frame);
#ifdef __cplusplus
}
#endif