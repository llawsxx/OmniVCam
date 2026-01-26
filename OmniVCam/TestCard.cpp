#include <windows.h>
#include <string>
#include <sstream>
#include <iomanip>
#include <vector>
#include"TestCard.h"
extern "C" {
    #include"global.h"
    #include"video_frame.h"
}

struct Line {
	int framesPerCycle;
	int length;
	int currentFrame;
	int center_x;
	int center_y;
	void update() {
		currentFrame = (currentFrame + 1) % framesPerCycle;
	}

	double getAngle() const {
		return (360.0 * currentFrame) / framesPerCycle;
	}
};

struct Circle {
    float x, y;           // 圆心位置
    float vx, vy;         // 速度向量
    int radius;           // 半径
    COLORREF color;       // 颜色
};

// 全局变量
const int NUM_CIRCLES = 24;  // 圆形数量
const int MIN_RADIUS = 20;
const int MAX_RADIUS = 50;

class TestCard {
public:
	TestCard(int width, int height, AVPixelFormat outPixelFormat, AVRational fps,int style) {
        rowSize = 0;
        frameBufferAligned = NULL;
		this->width = width;
		this->height = height;
		barHeight = height / 5;
		blockSize = width / 12;
		this->outPixelFormat = outPixelFormat;
		this->fps = fps;
        this->hOldBitmap = 0;
        this->style = style;
        HDC hScreenDC = GetDC(NULL);
        hMemDC = CreateCompatibleDC(hScreenDC);


        BITMAPINFO bmi = { 0 };
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; 
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 24;
        bmi.bmiHeader.biCompression = BI_RGB;

        frameBuffer = NULL;
        hBitmap = CreateDIBSection(hMemDC, &bmi, DIB_RGB_COLORS, (void**)&frameBuffer, NULL, 0);
        if (hBitmap) {
            BITMAP bm;
            GetObject(hBitmap, sizeof(BITMAP), &bm);
            rowSize = bm.bmWidthBytes;
            frameBufferAligned = (uint8_t*)av_malloc(rowSize * height); //画好的图像暂时只能先复制到frameBufferAligned再sws_scale，不然sws_scale会崩，内存对齐问题？
            hOldBitmap = (HBITMAP)SelectObject(hMemDC, hBitmap);
        }

        ReleaseDC(NULL, hScreenDC);
        swsCtx = sws_getContext(width, height, AV_PIX_FMT_BGR24,
            width, height, outPixelFormat,
            SWS_POINT, NULL, NULL, NULL);

        lines[0] = { 24, width / 6, 0 ,width / 8 * 1 ,barHeight + (height - barHeight) / 4 * 1 };
        lines[1] = { 16,  width / 6,  0 ,width / 8 * 3 ,barHeight + (height - barHeight) / 4 * 1 };
        lines[2] = { 12,  width / 6,  0 ,width / 8 * 5 ,barHeight + (height - barHeight) / 4 * 1 };
        lines[3] = { 8,  width / 6,  0 ,width / 8 * 7 ,barHeight + (height - barHeight) / 4 * 1 };
        lines[4] = { 6,  width / 6,  0 ,width / 8 * 1 ,barHeight + (height - barHeight) / 4 * 3 };
        lines[5] = { 5,  width / 6,  0 ,width / 8 * 3 ,barHeight + (height - barHeight) / 4 * 3 };
        lines[6] = { 4,  width / 6,  0 ,width / 8 * 5 ,barHeight + (height - barHeight) / 4 * 3 };
        lines[7] = { 3,  width / 6,  0 ,width / 8 * 7 ,barHeight + (height - barHeight) / 4 * 3 };

        
        float blockMovMul = speedMul();
        blockVelX = (int)(blockVelX * blockMovMul);
        blockVelY = (int)(blockVelY * blockMovMul);

        InitializeCircles();
     }

    ~TestCard() {
        if (hMemDC && hOldBitmap) {
            SelectObject(hMemDC, hOldBitmap);
        }
        if (hBitmap) {
            DeleteObject(hBitmap);
        }
        if (hMemDC) {
            DeleteDC(hMemDC);
        }
        if (swsCtx) {
            sws_freeContext(swsCtx);
        }
        if (frameBufferAligned) {
            av_free(frameBufferAligned);
        }
    }

    float speedMul() {
        return 60.0 / (fps.num / (float)fps.den);
    }
    // 初始化圆形
    void InitializeCircles() {
        circles.clear();
        srand(time(NULL));
        // 创建预定义颜色数组
        COLORREF colors[] = {
            // 基本颜色
            RGB(255, 0, 0),       // 红色
            RGB(0, 255, 0),       // 绿色
            RGB(0, 0, 255),       // 蓝色
            RGB(255, 255, 0),     // 黄色
            RGB(255, 0, 255),     // 紫色
            RGB(0, 255, 255),     // 青色
            RGB(255, 128, 0),     // 橙色
            RGB(128, 0, 255),     // 靛蓝色

            // 更多鲜艳颜色
            RGB(255, 105, 180),   // 热粉色
            RGB(50, 205, 50),     // 酸橙绿
            RGB(0, 191, 255),     // 深天蓝
            RGB(255, 20, 147),    // 深粉色
            RGB(138, 43, 226),    // 蓝紫色
            RGB(60, 179, 113),    // 中海绿
            RGB(255, 140, 0),     // 深橙色
            RGB(123, 104, 238),   // 中石板蓝

            // 柔和颜色
            RGB(240, 128, 128),   // 浅珊瑚色
            RGB(152, 251, 152),   // 浅绿色
            RGB(175, 238, 238),   // 浅蓝色
            RGB(255, 182, 193),   // 浅粉色
            RGB(221, 160, 221),   // 浅紫色
            RGB(255, 228, 181),   // 浅黄色
            RGB(176, 224, 230),   // 浅青色
            RGB(245, 222, 179),   // 小麦色
        };

        for (int i = 0; i < NUM_CIRCLES; i++) {
            Circle c;

            // 随机半径
            c.radius = MIN_RADIUS + rand() % (MAX_RADIUS - MIN_RADIUS + 1);

            // 确保圆形在窗口内生成
            c.x = c.radius + rand() % (width - 2 * c.radius);
            c.y = c.radius + rand() % (height - barHeight - 2 * c.radius) + barHeight;

            // 随机速度 (速度与半径成反比，避免大圆太快)
            float speed = 5.0f + (rand() % 100) / 50.0f;
            speed = speed * speedMul();
            float angle = (rand() % 360) * 3.1415926535f / 180.0f;
            c.vx = speed * cos(angle);
            c.vy = speed * sin(angle);

            // 分配颜色
            c.color = colors[i % (sizeof(colors) / sizeof(colors[0]))];

            circles.push_back(c);
        }
    }

    // 更新圆形位置和碰撞检测
    void UpdateCircles() {
        // 更新每个圆形的位置
        for (auto& circle : circles) {
            circle.x += circle.vx;
            circle.y += circle.vy;

            // 边界碰撞检测
            // 左边界
            if (circle.x - circle.radius < 0) {
                circle.x = circle.radius;
                circle.vx = -circle.vx;
            }
            // 右边界
            if (circle.x + circle.radius > width) {
                circle.x = width - circle.radius;
                circle.vx = -circle.vx;
            }
            // 上边界
            if (circle.y - circle.radius < barHeight) {
                circle.y = circle.radius + barHeight;
                circle.vy = -circle.vy;
            }
            // 下边界
            if (circle.y + circle.radius > height) {
                circle.y = height - circle.radius;
                circle.vy = -circle.vy;
            }
        }

        // 圆形之间的碰撞检测
        for (size_t i = 0; i < circles.size(); i++) {
            for (size_t j = i + 1; j < circles.size(); j++) {
                if (CheckCollision(circles[i], circles[j])) {
                    ResolveCollision(circles[i], circles[j]);
                }
            }
        }
    }

    // 绘制所有圆形
    void DrawCircles(HDC hdc) {
        // 绘制每个圆形
        for (const auto& circle : circles) {
            // 创建实心刷子
            HBRUSH hBrush = CreateSolidBrush(circle.color);
            HBRUSH hOldBrush = (HBRUSH)SelectObject(hdc, hBrush);

            // 创建空心笔用于边框
            HPEN hPen = CreatePen(PS_SOLID, 2, RGB(255, 255, 255));
            HPEN hOldPen = (HPEN)SelectObject(hdc, hPen);

            // 绘制圆形
            Ellipse(hdc,
                static_cast<int>(circle.x - circle.radius),
                static_cast<int>(circle.y - circle.radius),
                static_cast<int>(circle.x + circle.radius),
                static_cast<int>(circle.y + circle.radius));

            // 恢复原有对象并删除创建的GDI对象
            SelectObject(hdc, hOldBrush);
            SelectObject(hdc, hOldPen);
            DeleteObject(hBrush);
            DeleteObject(hPen);
        }

    }

    // 检查两个圆形是否碰撞
    bool CheckCollision(const Circle& c1, const Circle& c2) {
        float dx = c2.x - c1.x;
        float dy = c2.y - c1.y;
        float distance = sqrt(dx * dx + dy * dy);

        return distance < (c1.radius + c2.radius);
    }

    // 处理两个圆形的碰撞反应
    void ResolveCollision(Circle& c1, Circle& c2) {
        // 计算碰撞法向量
        float dx = c2.x - c1.x;
        float dy = c2.y - c1.y;
        float distance = sqrt(dx * dx + dy * dy);

        // 避免除以零
        if (distance == 0) return;

        // 归一化碰撞法向量
        float nx = dx / distance;
        float ny = dy / distance;

        // 计算相对速度
        float dvx = c2.vx - c1.vx;
        float dvy = c2.vy - c1.vy;

        // 计算相对速度在法向量上的投影
        float speed = dvx * nx + dvy * ny;

        // 如果圆形正在分离，不处理碰撞
        if (speed > 0) return;

        // 计算碰撞冲量
        float impulse = 2.0f * speed / (1.0f / c1.radius + 1.0f / c2.radius);

        // 应用冲量
        c1.vx += (impulse / c1.radius) * nx;
        c1.vy += (impulse / c1.radius) * ny;
        c2.vx -= (impulse / c2.radius) * nx;
        c2.vy -= (impulse / c2.radius) * ny;

        // 分离圆形，避免粘在一起
        float overlap = (c1.radius + c2.radius) - distance;
        float separate = overlap / 2.0f;

        c1.x -= separate * nx;
        c1.y -= separate * ny;
        c2.x += separate * nx;
        c2.y += separate * ny;
    }


    // 改变色块颜色（碰撞时调用）
    void ChangeBlockColor() {
        int oldIndex = colorIndex;

        // 确保选择不同的颜色
        do {
            colorIndex = rand() % dvdColors.size();
        } while (colorIndex == oldIndex && dvdColors.size() > 1);

        blockColor = dvdColors[colorIndex];
    }

    // 更新色块位置和检测碰撞
    void UpdateMovingBlock() {
        // 更新位置

        blockX += blockVelX;
        blockY += blockVelY;

        bool bounced = false;

        // 左右边界碰撞检测
        if (blockX <= 0) {
            blockX = 0;
            blockVelX = -blockVelX;
            bounced = true;
        }
        else if (blockX + blockSize >= width) {
            blockX = width - blockSize;
            blockVelX = -blockVelX;
            bounced = true;
        }

        // 上下边界碰撞检测（避开SMPTE彩条区域）
        if (blockY <= barHeight) {
            blockY = barHeight;
            blockVelY = -blockVelY;
            bounced = true;
        }
        else if (blockY + blockSize >= height) {
            blockY = height - blockSize;
            blockVelY = -blockVelY;
            bounced = true;
        }

        // 如果碰撞到边界，改变颜色
        if (bounced) {
            ChangeBlockColor();
        }
    }

    // 绘制SMPTE彩条
    void DrawSMPTEBars(HDC hdc) {
        // 绘制背景
        RECT bgRect = { 0, 0, width, barHeight };
        HBRUSH hBgBrush = CreateSolidBrush(RGB(0, 0, 0));
        FillRect(hdc, &bgRect, hBgBrush);
        DeleteObject(hBgBrush);

        // 绘制7个主要彩条
        int barWidth = width / 7;
        for (int i = 0; i < 7; i++) {
            RECT barRect = {
                i * barWidth,
                0,
                (i + 1) * barWidth,
                barHeight * 2 / 3
            };
            HBRUSH hBrush = CreateSolidBrush(smpteColors[i]);
            FillRect(hdc, &barRect, hBrush);
            DeleteObject(hBrush);
        }

        // 绘制底部的三个小色块
        int smallWidth = barWidth;

        // 黄色块 (-Y)
        RECT yRect = { smallWidth, barHeight * 2 / 3,
                      smallWidth * 2, barHeight };
        HBRUSH hYBrush = CreateSolidBrush(RGB(191, 191, 0));
        FillRect(hdc, &yRect, hYBrush);
        DeleteObject(hYBrush);

        // 青色块 (C)
        RECT cRect = { smallWidth * 3, barHeight * 2 / 3,
                      smallWidth * 4, barHeight };
        HBRUSH hCBrush = CreateSolidBrush(RGB(0, 191, 191));
        FillRect(hdc, &cRect, hCBrush);
        DeleteObject(hCBrush);

        // 绿色块 (+Y)
        RECT gRect = { smallWidth * 5, barHeight * 2 / 3,
                      smallWidth * 6, barHeight };
        HBRUSH hGBrush = CreateSolidBrush(RGB(0, 191, 0));
        FillRect(hdc, &gRect, hGBrush);
        DeleteObject(hGBrush);
    }

    // 绘制信息文本（帧数）
    void DrawInfoText(HDC hdc) {
        // 创建字体
        HFONT hFont = CreateFont(
            28, 0, 0, 0, FW_NORMAL,
            FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE,
            L"Arial"
        );
        HFONT hOldFont = (HFONT)SelectObject(hdc, hFont);

        // 设置文本颜色为白色，透明背景
        SetTextColor(hdc, RGB(255, 255, 255));
        SetBkMode(hdc, TRANSPARENT);

        // 绘制帧数
        std::wstring frameText = std::to_wstring(frameCount);
        TextOut(hdc, 10, barHeight, frameText.c_str(), (int)frameText.length());

        // 恢复旧字体
        SelectObject(hdc, hOldFont);
        DeleteObject(hFont);
    }

    // 绘制移动的DVD风格色块
    void DrawDVDBlock(HDC hdc) {
        // 更新色块位置
        UpdateMovingBlock();

        // 绘制实心色块
        HBRUSH hBrush = CreateSolidBrush(blockColor);
        RECT blockRect = {
            blockX,
            blockY,
            blockX + blockSize,
            blockY + blockSize
        };
        FillRect(hdc, &blockRect, hBrush);
        DeleteObject(hBrush);

        // 绘制白色边框
        HPEN hBorderPen = CreatePen(PS_SOLID, 2, RGB(255, 255, 255));
        HPEN hOldPen = (HPEN)SelectObject(hdc, hBorderPen);
        HBRUSH hNullBrush = (HBRUSH)GetStockObject(NULL_BRUSH);
        HBRUSH hOldBrush = (HBRUSH)SelectObject(hdc, hNullBrush);

        // 绘制矩形边框
        Rectangle(hdc, blockX, blockY, blockX + blockSize, blockY + blockSize);

        // 恢复GDI对象
        SelectObject(hdc, hOldPen);
        SelectObject(hdc, hOldBrush);
        DeleteObject(hBorderPen);
    }

    // 绘制旋转的横线
    void DrawRotatingLine(HDC hdc, const Line& line) {
        double angle = line.getAngle() * 3.1415926535 / 180.0;
        int halfLen = line.length / 2;

        // 计算端点
        int x1 = line.center_x;
        int y1 = line.center_y;
        int x2 = line.center_x + (int)((halfLen - 6) * cos(angle));
        int y2 = line.center_y + (int)((halfLen - 6) * sin(angle));

        // 创建画笔
        HPEN hPen = CreatePen(PS_SOLID, 10, RGB(255, 255, 255));
        HPEN hPenEnd = CreatePen(PS_SOLID, 10, RGB(255, 165, 0));
        HPEN hOldPen = (HPEN)SelectObject(hdc, hPen);

        // 绘制线
        MoveToEx(hdc, x1, y1, NULL);
        LineTo(hdc, x2, y2);

        SelectObject(hdc, hPenEnd);
        for (int i = 0; i < line.framesPerCycle; i++) {
            angle = (360.0 / line.framesPerCycle * i) * 3.1415926535 / 180.0;
            int x1 = line.center_x + (int)((halfLen - 6) * cos(angle));
            int y1 = line.center_y + (int)((halfLen - 6) * sin(angle));
            int x2 = line.center_x + (int)(halfLen * cos(angle));
            int y2 = line.center_y + (int)(halfLen * sin(angle));
            MoveToEx(hdc, x1, y1, NULL);
            LineTo(hdc, x2, y2);
        }

        SelectObject(hdc, hOldPen);
        // 绘制端点
        HBRUSH hBrush = CreateSolidBrush(RGB(255, 0, 0));
        HBRUSH hOldBrush = (HBRUSH)SelectObject(hdc, hBrush);
        Ellipse(hdc, x1 - 10, y1 - 10, x1 + 10, y1 + 10);

        // 恢复并删除对象
        SelectObject(hdc, hOldBrush);
        DeleteObject(hPen);
        DeleteObject(hPenEnd);
        DeleteObject(hBrush);
    }

    AVFrame* Draw() {
        if (!frameBuffer || !frameBufferAligned || !swsCtx) return NULL;
        HDC hdc = hMemDC;

        // 1. 清空背景为黑色
        memset(frameBuffer + rowSize * barHeight, 0, rowSize * (height - barHeight));

        // 2. 绘制SMPTE彩条
        if (drawSMPTE) {
            DrawSMPTEBars(hdc);
            memcpy(frameBufferAligned, frameBuffer, rowSize * barHeight);
            drawSMPTE = false;
        }

        // 3. 绘制DVD风格移动色块
        DrawDVDBlock(hdc);

        if (style == 0) {
            UpdateCircles();
            DrawCircles(hdc);
        }
        else if (style == 1) {
            for (int i = 0; i < 8; i++) {
                DrawRotatingLine(hdc, lines[i]);
                lines[i].update();
            }
        }

        // 4. 绘制帧数和时间戳信息
        DrawInfoText(hdc);


        memcpy(frameBufferAligned + rowSize * barHeight, frameBuffer + rowSize * barHeight, rowSize * (height - barHeight));


        const uint8_t* srcSlice[4] = { frameBufferAligned };
        int srcStride[4] = { rowSize };

        if (outPixelFormat == AV_PIX_FMT_BGR24 || outPixelFormat == AV_PIX_FMT_0RGB32) {
            srcSlice[0] += rowSize * (height - 1);
            srcStride[0] *= -1;
        }

        AVFrame* outFrame = av_frame_alloc();
        if (outFrame) {
            outFrame->width = width;
            outFrame->height = height;
            outFrame->format = outPixelFormat;
            if (get_video_buffer(outFrame) < 0) {
                av_frame_free(&outFrame);
            }
            else {
                sws_scale(swsCtx, srcSlice, srcStride, 0, height,
                    outFrame->data, outFrame->linesize);
                outFrame->pts = av_rescale_q(frameCount,
                    { fps.den, fps.num }, UNIVERSAL_TB);
                frameCount++;
                return outFrame;
            }
        }

        return NULL;
    }

private:
	int width;
	int height;
	int barHeight;
	int blockSize;
    int style;
	AVPixelFormat outPixelFormat;
	AVRational fps;
    bool drawSMPTE = true;
	int blockX = 100, blockY = 100;
	int blockVelX = 8, blockVelY = 8;
	COLORREF blockColor = RGB(255, 0, 0);  // 起始颜色：红色
	DWORD startTime = 0;
	int frameCount = 0;

	int colorIndex = 0;
	Line lines[8];
    int rowSize;
    SwsContext* swsCtx;
    HDC hMemDC;
    HBITMAP hBitmap;
    HBITMAP hOldBitmap;
    uint8_t* frameBuffer;
    uint8_t* frameBufferAligned;

	// DVD风格的颜色集合
	std::vector<COLORREF> dvdColors = {
		RGB(255, 0, 0),     // 红色
		RGB(0, 255, 0),     // 绿色
		RGB(0, 0, 255),     // 蓝色
		RGB(255, 255, 0),   // 黄色
		RGB(255, 0, 255),   // 品红色
		RGB(0, 255, 255)    // 青色
	};

    std::vector<Circle> circles;


	// SMPTE彩条颜色
	COLORREF smpteColors[7] = {
		RGB(191, 191, 191),  // 75% 灰
		RGB(191, 191, 0),    // 黄色
		RGB(0, 191, 191),    // 青色
		RGB(0, 191, 0),      // 绿色
		RGB(191, 0, 191),    // 品红
		RGB(191, 0, 0),      // 红色
		RGB(0, 0, 191)       // 蓝色
	};



};

void* test_card_alloc(int width, int height, AVPixelFormat outPixelFormat, AVRational fps,int style) {
    return new TestCard(width, height, outPixelFormat, fps, style);
}
void test_card_free(void* p) {
    TestCard* card = (TestCard*)p;
    delete card;
}
AVFrame* test_card_draw(void* p) {
    TestCard* card = (TestCard*)p;
    return card->Draw();
}

int main20() {
    void* p = test_card_alloc(1920, 1080, AV_PIX_FMT_YUV420P, { 50,1 },0);
    int count = 0;
    while (1) {
        printf("%d\n", count++);
        AVFrame *f = test_card_draw(p);
        av_frame_free(&f);
    }
    test_card_free(p);

    return 0;
}