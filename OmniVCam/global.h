#pragma once
#include <libavutil/rational.h>
static AVRational UNIVERSAL_TB = { 1,1000000 };
static AVRational NS_TB = { 1,1000000000 };
static AVRational DSHOW_TB = { 1,10000000 };
#define COND_TIMEOUT 100
