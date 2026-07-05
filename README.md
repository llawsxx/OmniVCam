# OmniVCam Virtual Cam

OmniVCam 是一个 Windows DirectShow 虚拟摄像头。它可以把本地媒体文件、DirectShow 采集设备、OBS Virtual Camera 共享内存画面或内置测试卡输出成摄像头设备，并通过配套的 `OmniVCamController.exe` 进行播放控制。

当前项目包含两部分：

- `OmniVCam`：DirectShow 虚拟摄像头 DLL，负责解码、滤镜、音视频同步和输出。
- `OmniVCamController`：WinForms 控制器，默认连接 `127.0.0.1:16999`，通过 TCP 控制虚拟摄像头。

## 主要功能

- 播放本地视频/音频文件，支持常见封装和编码格式，实际能力取决于随程序一起分发的 FFmpeg DLL。
- 播放 DirectShow 采集设备，例如采集卡、OBS Virtual Camera 设备等。
- 读取 OBS Virtual Camera 共享内存输入：`<OBSVCAM>`。
- 内置两种测试卡：`<TESTCARD>`、`<TESTCARD2>`，用于观察掉帧和调整输出时序`Shift us`。
- 支持视频滤镜、音频滤镜、硬件解码、音视频轨道选择、按秒 seek、按文件位置百分比 seek。
- 支持控制器播放列表、垫片、顺序/随机播出、定时开始、自动切下一条。
- 控制器会自动保存界面设置、播放列表和常用输入到 `OmniVCamController.xml`。

## 安装

1. 将发布包解压到固定目录，例如 `D:\OmniVCam`。需要保留 `OmniVCam.dll`、控制器、FFmpeg 相关 DLL 和配置文件在同一套发布目录中。
2. 以管理员身份打开 `cmd`，注册虚拟摄像头：

   ```bat
   cd /d D:\OmniVCam
   regsvr32 OmniVCam.dll
   ```

3. 在需要摄像头输入的软件里选择 `OmniVCam Virtual Camera`。

卸载时以管理员身份执行：

```bat
cd /d D:\OmniVCam
regsvr32 /u OmniVCam.dll
```

如果发布包中包含对应的注册/卸载 bat，也可以直接以管理员身份运行。

## 使用控制器

运行 `OmniVCamController.exe` 后，默认连接本机 `127.0.0.1:16999`。端口需要与 `config.ini` 中的 `tcp_port` 一致。

常用控件说明：

- `Host` / `Port`：控制器连接的虚拟摄像头 TCP 地址。
- `Input`：要播放的输入，可以是本地文件、DirectShow 输入或特殊输入，例如 `<TESTCARD>`、`<TESTCARD2>`、`<OBSVCAM>`。
- `Input` 右侧下拉框：快速选择三种特殊输入。
- `Browse`：选择本地媒体文件。
- `Options`：本次播放的附加参数，多个参数用逗号分隔，例如 `seek_time=14,video_filter='bwdif=1'`。
- `Play`：播放当前 `Input`。
- `Stop`：停止播放，同时停止控制器播放列表播出。
- `Reopen`：重新打开当前输入。
- `Ping`：测试 TCP 控制连接。
- `Video filter` / `Audio filter`：填写 FFmpeg 滤镜表达式，点击右侧 `Set` 后生效；`Cancel` 会取消对应滤镜。
- `HW decode`：设置硬解方式，点击 `Set` 后生效。可选值包括 `none`、`dxva2`、`d3d11va`、`cuda`、`qsv`。
- `Video index` / `Audio index`：选择视频/音频流索引，修改后会立即发送；也可以用 `Set video index` / `Set audio index` 重发当前值。`-1` 表示自动选择。
- `Seek seconds`：按秒跳转。
- `Progress`：拖动进度条跳转；`-5s` / `+5s` 用于相对跳转。
- `Byte seek`：勾选后，拖动进度条会按文件位置百分比跳转，适合时长识别不准或无法精确按时间 seek 的输入。
- `Shift us`：调整输出帧时间偏移，单位是微秒，改值立即生效。旁边 `-` / `+` 按当前步进微调。

在 OBS、直播伴侣等软件中如果出现周期性丢帧，可以用测试卡观察运动连续性，并尝试调整 `Shift us`。60 fps 输出时通常可在 `0` 到 `16667` 之间微调；超过单帧间隔通常意义不大。

## 常用输入和参数

`Options` 会作为 FFmpeg 打开输入时的参数和 OmniVCam 自定义参数一起解析。常用项如下：

| 用途 | Input | Options |
| --- | --- | --- |
| OBS 共享内存输入 | `<OBSVCAM>` | `queue_left=1,queue_right=5` |
| 测试卡 1 | `<TESTCARD>` | 留空 |
| 测试卡 2 | `<TESTCARD2>` | 留空 |
| 播放文件并跳到 14 秒 | `D:\example.mp4` | `seek_time=14` |
| 播放文件并指定滤镜/轨道 | `D:\tv.ts` | `video_filter='bwdif=1',audio_filter='loudnorm',video_index=0,audio_index=1` |
| 播放 OBS Virtual Camera 设备 | `video=OBS Virtual Camera` | `format=dshow,rtbufsize=1G,queue_left=5,queue_right=20` |
| 播放采集卡 1080p60 YUY2 | `video=U4 4K60` | `format=dshow,rtbufsize=1G,queue_left=1,queue_right=5,video_size=1920x1080,framerate=60,pixel_format=yuyv422` |
| 播放采集卡 1080p120 MJPEG | `video=U4 4K60` | `format=dshow,rtbufsize=1G,queue_left=1,queue_right=10,video_size=1920x1080,framerate=120,vcodec=mjpeg` |

OmniVCam 当前会特殊处理这些参数：

- `video_filter`：本次播放使用的视频滤镜。
- `audio_filter`：本次播放使用的音频滤镜。
- `video_index` / `audio_index`：指定视频/音频流索引。
- `seek_time`：打开后跳转到指定秒数。
- `queue_left` / `queue_right` / `queue_center`：设置帧队列参数，常用于采集卡输入或 OBS 共享内存输入。通过将缓存帧数维持在一定范围内，有效防止因队列接近空置或填满而导致的丢帧问题。
- `probesize` / `analyzeduration`：传给 FFmpeg 的探测参数。
- `format`：指定输入格式，例如 `dshow`。
- `vcodec` / `acodec` / `scodec` / `dcodec`：指定解码器。

查看采集卡支持格式可使用 FFmpeg：

```bat
ffmpeg -list_options true -f dshow -i video="U4 4K60"
```

示例输出截图：

![FFmpeg 列出采集卡格式](assets/images/ffmpeg列出采集卡格式.png)

## 播放列表和常用输入

控制器下半部分是播放列表，上半部分右侧是常用输入列表。

播放列表功能：

- `Add files`：添加一个或多个媒体文件。
- `Add folder`：递归添加文件夹内的媒体文件。
- `Add bumper`：添加垫片，列表中类型显示为 `Bumper`。
- `Remove` / `Up` / `Down`：删除或调整顺序。
- `Set options`：把当前 `Options` 写入选中的播放列表条目，并重新读取时长。
- `Refresh durations`：重新读取列表条目的时长。读取失败时使用默认时长 1800 秒。
- `Load` / `Save`：加载或保存 `.ovcpl` 播放列表文件。文件为 UTF-8、制表符分隔格式。
- `Mode`：选择 `Sequential` 顺序播出或 `Random` 随机播出。
- `Start at`：勾选后按设定时间开始播出。
- `Start playout`：开始播出。
- `Next`：手动播放下一条；随机模式下会随机选择下一条。
- `Stop playout`：停止列表播出并停止当前播放。

常用输入功能：

- `Add current`：把当前 `Input` 和 `Options` 加入常用输入。
- `Remove` / `Up` / `Down`：删除或调整顺序。
- 单击条目会把对应 `Input` 和 `Options` 填入输入框。
- 双击条目会直接播放。

关闭控制器时会自动保存当前设置、播放列表和常用输入；下次启动时自动恢复。自动配置文件位于控制器程序目录下的 `OmniVCamController.xml`。

## 测试卡

可以使用测试卡辅助观察掉帧并调整 `Shift us`。

`<TESTCARD>`：一个正方形和多个圆形快速移动，适合观察移动连续性。

![TESTCARD](assets/images/TESTCARD.png)

`<TESTCARD2>`：一个正方形和八条旋转线，适合观察移动连续性和旋转线是否缺失。

![TESTCARD2](assets/images/TESTCARD2.png)

## 配置文件

`OmniVCam/config.ini` 会在虚拟摄像头启动时读取，常用配置包括：

- `hw_decode`：默认硬解方式，可选 `none`、`dxva2`、`d3d11va`、`cuda`、`qsv`。
- `tcp_port`：TCP 控制端口，默认 `16999`。
- `log_level`：FFmpeg 日志级别。
- `video_frame_buffer` / `audio_frame_buffer`：视频/音频帧缓存数量。
- `packet_queue_size`：包队列大小。
- `timeout`：输入超时时间，单位为微秒。
- `use_fixed_frame_interval`：使用固定帧间隔计算输出时间，通常建议为 `1` 以适配 OBS。
- `ajust_start_time_if_delay_over`：输出延迟超过指定帧数后校正时间。
- `av_max_offset_time`：输入音视频时间戳与输出时间戳的最大允许偏移，单位为微秒。

修改 `config.ini` 后需要重新打开使用 OmniVCam 的宿主软件或重新加载虚拟摄像头实例。

## TCP 控制协议

控制器使用 TCP 文本命令控制虚拟摄像头，每条命令以换行结束。默认端口为 `16999`。

当前支持的命令：

- `PING`：返回 `OK PONG`。
- `STATUS`：返回当前秒数、时长、文件大小、状态和输入。
- `DURATION <input>[\toptions]`：探测输入时长。
- `PLAY <input>[\toptions]`：播放输入。
- `STOP`：停止播放。
- `REOPEN`：重新打开当前输入。
- `SET_FILTER <filter>`：设置全局视频滤镜，空值表示取消。
- `SET_AUDIO_FILTER <filter>`：设置全局音频滤镜，空值表示取消。
- `SET_HW_DECODE <none|dxva2|d3d11va|cuda|qsv>`：设置硬解方式。
- `SET_INDEX video=<n> audio=<n>`：设置音视频轨道索引。
- `SET_SHIFT <microseconds>`：设置输出帧时间偏移。
- `SEEK <seconds>`：按秒跳转。
- `SEEK_BYTE_PERCENT <0-10000>`：按文件位置百分比跳转，`10000` 表示 100%。

## 构建

当前解决方案为 `OmniVCam.sln`：

- `OmniVCam`：C/C++ DirectShow DLL，Visual Studio 2022 / v143 工具集，支持 `Win32` 和 `x64`，依赖 DirectShow Base Classes 和 FFmpeg 开发库。
- `OmniVCamController`：C# WinForms 程序，目标框架为 .NET Framework 4.8，平台为 `AnyCPU`。

依赖目录约定：

- `deps/include`：DirectShow Base Classes 头文件。
- `deps/lib/x86`、`deps/lib/x64`：DirectShow Base Classes 静态库。
- `ffmpeg_deps/include`、`ffmpeg_deps/lib`：x64 FFmpeg 开发文件。

Release 包需要同时带上运行时所需的 FFmpeg DLL、`config.ini` 和控制器程序。
