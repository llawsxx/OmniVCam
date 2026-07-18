# OmniVCam 虚拟摄像机

[English](README.md)

OmniVCam 是一个 Windows DirectShow 虚拟摄像机。它可以把本地媒体文件、DirectShow 采集设备、OBS 虚拟摄像机共享内存输入，以及内置测试卡输出为摄像机设备。`OmniVCamController.exe` 是 WinForms 控制器，用于播放输入、调整播放参数和运行播出播放列表。

项目主要包含两部分：

- `OmniVCam`：DirectShow 虚拟摄像机 DLL，负责解码、滤镜、音视频同步和输出。
- `OmniVCamController`：控制器 UI，通过 TCP 连接虚拟摄像机控制服务，默认 `127.0.0.1:16999`。

## 功能

- 播放本地媒体文件和任意 FFmpeg 输入。
- 播放 DirectShow 设备，例如采集卡或 OBS Virtual Camera；既可使用 FFmpeg，也可使用原生 `<DSHOW>` 采集输入。
- 原生 `<DSHOW>` 采集配置：枚举设备和格式、选择视频/音频 Pin、配置 Crossbar 路由和模拟设备属性页、使用设备时间戳，以及请求音频缓冲大小。
- 通过 `<OBSVCAM>` 读取 OBS 虚拟摄像机共享内存输入。
- 内置测试卡：`<TESTCARD>` 和 `<TESTCARD2>`。
- 支持每个输入单独设置 `seek_time`、流索引、滤镜、队列和输入格式等选项。
- 手动控制：打开/播放、暂停/恢复、停止、重新打开、秒定位、字节定位、拖动进度、滤镜、显示宽高比、缩放模式和输出时间偏移。
- 播放时可修改缩放模式和显示宽高比。
- 常用输入列表，可复用输入、标题和选项。
- 多连接标签页，每个标签页有独立主机、端口、输入设置、播放列表、计划列表和常用输入。
- 独立播出窗口，包含计划播放列表和普通播放列表。
- 一次性和每周计划项，支持标题、选项、开始/结束行为和播放中应用修改。
- 普通播放列表支持垫片项，用于节目间插播。
- 拖放文件到输入框、普通播放列表或计划播放列表。
- 内置日志查看器，显示虚拟摄像机通过 TCP 转发的 FFmpeg 日志。
- 位置区域显示帧投递阻塞时间统计。
- 可选正在播放 XML 输出，供 vMix 等外部工具读取。
- 控制器 UI 使用标准 `.resx` 资源实现多语言，目前包含英文、简体中文和繁体中文。
- 自动保存 UI 设置、播放列表、计划播放列表和常用输入到 `OmniVCamController.xml`。

## 安装

1. 将发布文件复制到固定目录，例如 `D:\OmniVCam`。保持 `OmniVCam.dll`、FFmpeg DLL、配置文件和控制器在同一目录。
2. 在管理员命令提示符中注册虚拟摄像机 DLL：

   ```bat
   cd /d D:\OmniVCam
   regsvr32 OmniVCam.dll
   ```

3. 在 OBS、vMix、会议软件或其他摄像机客户端中选择 `OmniVCam Virtual Camera`。

卸载注册：

```bat
cd /d D:\OmniVCam
regsvr32 /u OmniVCam.dll
```

## 多实例

OmniVCam 支持最多 4 个同时存在的虚拟摄像机实例。每个实例有独立 CLSID，并通过环境变量读取自己的 `config.ini`：

| 实例 | 环境变量 | 配置路径示例 |
| --- | --- | --- |
| OmniVCam | `OMNI_VCAM_CONFIG` | `D:\OmniVCam\config.ini` |
| OmniVCam2 | `OMNI_VCAM_CONFIG2` | `D:\OmniVCam2\config.ini` |
| OmniVCam3 | `OMNI_VCAM_CONFIG3` | `D:\OmniVCam3\config.ini` |
| OmniVCam4 | `OMNI_VCAM_CONFIG4` | `D:\OmniVCam4\config.ini` |

要运行多实例，把 DLL 和配置文件复制到不同目录，设置对应环境变量指向各自的 `config.ini`，再注册各实例 DLL。每个实例会暴露不同摄像机名称，例如 `OmniVCam Virtual Camera`、`OmniVCam2 Virtual Camera`。

## Controller 基本使用

运行 `OmniVCamController.exe`。默认目标是 `127.0.0.1:16999`，端口必须与 `OmniVCam/config.ini` 中的 `tcp_port` 一致。

### 连接标签页

控制器支持多个连接标签页。每个标签页保存独立的主机、端口、输入设置、播放列表、常用输入和日志。使用 `+` 添加标签页，使用 `-` 删除标签页。标签标题会显示连接端点。

主要控件：

- `Host` / `Port`：TCP 控制端点。
- `Input`：要播放的输入，可以是文件路径、URL、DirectShow 输入，或 `<TESTCARD>`、`<TESTCARD2>`、`<OBSVCAM>`、`<DSHOW>` 等特殊输入。
- `Title`：播放列表和正在播放 XML 使用的标题。留空时从输入自动推导。
- `Options`：当前输入的 FFmpeg/open-input 选项，例如 `seek_time=14,video_filter='bwdif=1'`。
- `Browse`：选择本地媒体文件。
- `Open`：使用当前 `Input` 和 `Options` 打开/播放。
- `Play`：播放当前输入；播放中或暂停时切换暂停/播放。
- `Stop`：停止播放并停止播出。
- `Reopen`：重新打开当前输入。
- `Ping`：测试 TCP 连接。
- `Video filter` / `Audio filter`：设置或取消全局滤镜。
- `Auto deinterlace`：自动反交错。开启后，只有在没有设置 `video_filter` 且检测到隔行帧时才自动使用所选反交错滤镜。逐行/隔行混合流会逐帧处理：逐行帧直接输出，隔行帧进入自动反交错滤镜。
- `Deinterlace filter`：自动反交错使用的滤镜，支持 `bwdif`、`yadif`、`w3fdif`。
- `HW decode`：设置硬件解码模式：`none`、`dxva2`、`d3d11va`、`cuda` 或 `qsv`。
- `Video index` / `Audio index`：选择输入流索引，`-1` 表示自动。
- `Seek seconds`：按绝对秒数定位。
- `Progress`：拖动进度条定位。启用 `Byte seek` 时按文件字节百分比定位，旁边有 `-5s` / `+5s` 快捷按钮。
- `Shift us`：输出帧时间偏移，单位微秒。
- `Scale mode`：输出缩放模式，目前控制器提供 `letterbox` 和 `fill`。
- `Display AR`：显示宽高比，支持 `auto`、`16:9`、`4:3`、`1:1`。
- `DirectShow` / `Camera`：打开原生 `<DSHOW>` 采集配置对话框。该功能用于 `<DSHOW>` 输入，并会自动写入所需选项。
- `Open playout`：打开独立播出窗口。
- `Language`：选择自动、英文、简体中文或繁体中文。切换后重启控制器生效。

### 原生 `<DSHOW>` 采集输入

`<DSHOW>` 是原生 DirectShow 采集路径。它不同于使用 `format=dshow` 的 FFmpeg 输入：OmniVCam 会在自身内部创建 DirectShow 图，并将采集帧送入常规的视频/音频滤镜和输出管线。

1. 在 `Input` 中选择 `<DSHOW>`，然后点击 `DirectShow` → `Camera`。
2. 选择视频设备和采集格式。音频为可选项，可来自采集设备或独立音频设备。
3. 可选配置视频/音频 Crossbar 路由、播放时打开 Crossbar/电视调谐器/电视音频属性页、使用视频/音频设备时间戳，以及请求音频缓冲大小。
4. 点击 `OK` 写入 `Input`、`Title` 和 `Options`；点击 `Open` 则会写入并立即播放。

控制器会枚举兼容 Pin 和格式，并以 UTF-8 Base64 选项保存设备名称与 Pin ID，因此通常应保留自动生成的选项。`<DSHOW>` 支持纯视频、纯音频及音视频联合采集；常规单输入选项同样可用，包括 `video_filter`、`audio_filter`、`queue_left`、`queue_right`、`queue_center`。

用于程序控制的原生选项为：`dshow_device_b64`、`dshow_pin_b64`、`dshow_format`、`dshow_audio_device_b64`、`dshow_audio_device_type`（视频采集设备为 `0`，音频设备为 `1`）、`dshow_audio_pin_b64`、`dshow_audio_format`、`dshow_xbar_video_in`、`dshow_xbar_video_out`、`dshow_xbar_audio_in`、`dshow_xbar_audio_out`、`dshow_crossbar_dialog`、`dshow_tv_tuner_dialog`、`dshow_tv_audio_dialog`、`dshow_use_video_device_timestamp`、`dshow_use_audio_device_timestamp` 和 `dshow_audio_buffer_size`（毫秒）。设备和 Pin 选项使用 UTF-8 Base64；省略格式或路由选项时使用设备默认值。

### 日志查看器

主窗口左侧日志面板显示虚拟摄像机通过 TCP 转发的 FFmpeg 日志。日志会自动滚动，并限制最大行数。每个连接标签页独立保存自己的日志文本。

### 拖放

- 拖文件到 `Input` 输入框可加载为当前输入。
- 拖文件到普通播放列表或计划播放列表可添加为列表项。

## 常用输入

常用输入显示在主窗口右半部分。

- `Add current`：添加当前 `Input`、`Title` 和 `Options`。
- `Remove`、`Up`、`Down`：编辑列表顺序。
- 单击：加载到主输入字段。
- 双击：立即播放。

## 播出窗口

点击主窗口中的 `Open playout`。播出窗口包含两个列表：

- `Scheduled playlist`：按时间触发的计划项。
- `Normal playlist`：兜底或连续播放的普通列表。

关闭播出窗口只是隐藏窗口；关闭主控制器会退出程序。

顶部控件：

- `Start playout`：开始播出调度。播出计时器只在播出启动后运行。
- `Next`：手动播放下一条普通列表项。`Random` 模式下随机选择。
- `Stop playout`：停止播出、停止播放并清除计划触发标记。
- `Save XML`：保存控制器设置和两个播放列表到 `OmniVCamController.xml`。
- `Write now playing XML`：启用或禁用正在播放 XML 输出。

## 计划播放列表

计划列表列：

- `Status`：`Waiting`、`In window`、`Blocked`、`Playing`、`Ended`、`Error` 等状态。
- `Title`：项目标题。
- `Duration`：项目时长，未知时显示 `--:--:--`。
- `Schedule`：一次性日期时间或每周星期/时间。
- `End`：显式结束时间，未设置时显示下一计划。
- `Start`：开始行为。
- `End action`：媒体早于结束时间结束时的行为。
- `Last triggered`：上次触发的计划窗口开始时间。停止播出会清除。
- `Path`：输入路径、URL、设备或特殊输入。

计划项控件：

- `Add current`：把主窗口当前输入添加为计划项。
- `Add files`：添加一个或多个媒体文件。
- `Add folder`：递归添加文件夹内媒体文件。
- `Apply`：把当前计划设置写入选中的计划项，播放中也可使用。
- `Remove`、`Up`、`Down`：编辑列表顺序。
- `Refresh durations`：只探测计划列表项时长。

计划设置：

- `Schedule type`：`One-time` 或 `Weekly`。
- `One-time start`：一次性项目的完整日期时间。
- `Weekly time`：每周项目的一天内时间。
- `Days`：每周星期掩码。
- `Input title`：计划项标题。留空时回退到主 `Title`，再回退到输入推导标题。
- `Input options`：计划项选项。留空时回退到主 `Options`。
- `Use end time`：启用显式结束时间。
- `End time`：一次性项目使用日期时间；每周项目只使用一天内时间。
- `If media ends early`：`Replay until end` 重播到结束时间，`Wait until end` 等待到结束时间，`Continue immediately` 立即离开计划项。
- `At scheduled time`：`Start immediately` 到点立即切入，`Wait current item` 等当前播放结束后再检查。

调度行为：

- 调度使用时间窗口，不依赖精确一秒触发。如果电脑短暂停顿，只要仍在窗口内就可启动。
- 启用 `Use end time` 时窗口为 `start -> end`。
- 未启用 `Use end time` 时窗口为 `start -> next scheduled task start`；没有下一计划时窗口保持开放。
- 较晚的 `Start immediately` 计划项可以切入当前正在播放的计划项。
- `Stop playout` 会清除所有 `Last triggered` 标记，因此重新开始播出时仍在窗口内的任务可以再次触发。

## 普通播放列表

普通列表控件：

- `Add current`：把主窗口当前输入添加到普通列表，支持任意输入，不限文件。
- `Add files`：添加媒体文件。
- `Add folder`：递归添加文件夹内媒体文件。
- `Add bumper`：添加标记为 `Bumper` 的文件，用作普通列表项之间的插播内容。
- `Remove`、`Up`、`Down`：编辑列表顺序。
- `Set title`：把主窗口 `Title` 应用到选中的普通列表项。
- `Set options`：把播出窗口的 `Input options` 应用到选中的普通列表项。
- `Load` / `Save file`：加载或保存 `.ovcpl` 播放列表文件。
- `Refresh durations`：只探测普通列表项时长。
- `Mode`：`Sequential` 或 `Random`。
- `Normal auto next`：启用后，普通列表会在结束、错误或达到已知时长时自动下一条。
- `Start at`：普通列表启动的可选时间点。

添加或加载项目时不会自动探测时长。需要时点击 `Refresh durations`。

计划播放结束后，如果普通列表有项目，控制器会返回普通列表；如果没有普通项目，则保持停止或结束状态。

## 正在播放 XML

勾选 `Write now playing XML` 时，控制器会在可执行文件旁写入 XML。单连接标签页时文件名为：

```text
OmniVCamNowPlaying.xml
```

多个连接标签页时，每个标签页按主机和端口写入独立文件：

```text
OmniVCamNowPlaying-{host}-{port}.xml
```

例如 `OmniVCamNowPlaying-127.0.0.1-16999.xml`。

文件由 `STATUS` 轮询和停止动作更新，可被 vMix 等工具导入。

示例：

```xml
<NowPlaying>
  <Title>Program title</Title>
  <Path>D:\media\program.mp4</Path>
  <PositionSeconds>123</PositionSeconds>
  <Position>00:02:03</Position>
  <DurationSeconds>3600</DurationSeconds>
  <Duration>01:00:00</Duration>
  <Status>Playing</Status>
</NowPlaying>
```

取消勾选 `Write now playing XML` 会停止写入。已有 XML 文件不会自动删除。

## 常见输入和选项

`Options` 会作为 FFmpeg open-input 选项和 OmniVCam 专用选项解析，多个选项用逗号分隔。

| 用途 | Input | Options |
| --- | --- | --- |
| OBS 共享内存输入 | `<OBSVCAM>` | `queue_left=1,queue_right=5` |
| 测试卡 1 | `<TESTCARD>` | 留空 |
| 测试卡 2 | `<TESTCARD2>` | 留空 |
| 原生 DirectShow 采集 | `<DSHOW>` | 通过 `DirectShow` → `Camera` 配置；可按需增加 `video_filter='bwdif=1',audio_filter='loudnorm'` |
| 播放文件并跳到 14 秒 | `D:\example.mp4` | `seek_time=14` |
| 播放文件并指定滤镜/索引 | `D:\tv.ts` | `video_filter='bwdif=1',audio_filter='loudnorm',video_index=0,audio_index=1` |
| OBS Virtual Camera 设备 | `video=OBS Virtual Camera` | `format=dshow,rtbufsize=1G,queue_left=5,queue_right=20` |
| 采集卡 1080p60 YUY2 | `video=U4 4K60` | `format=dshow,rtbufsize=1G,queue_left=1,queue_right=5,video_size=1920x1080,framerate=60,pixel_format=yuyv422` |
| 采集卡 1080p120 MJPEG | `video=U4 4K60` | `format=dshow,rtbufsize=1G,queue_left=1,queue_right=10,video_size=1920x1080,framerate=120,vcodec=mjpeg` |

支持的 OmniVCam 专用选项包括：

- `video_filter`：每个输入的视频滤镜。
- `audio_filter`：每个输入的音频滤镜。
- `auto_deinterlace`：可选的单输入自动反交错覆盖项，`1` 或 `0`。
- `auto_deinterlace_filter`：可选的单输入自动反交错滤镜覆盖项，`bwdif`、`yadif` 或 `w3fdif`。
- `video_index` / `audio_index`：输入流索引。
- `seek_time`：打开输入后跳转到指定时间。
- `queue_left` / `queue_right` / `queue_center`：帧队列调节。
- `probesize` / `analyzeduration`：FFmpeg 探测选项。
- `format`：输入格式，例如 `dshow`。
- `vcodec` / `acodec` / `scodec` / `dcodec`：解码器选择。

`format=dshow` 仍可用于 FFmpeg 的 DirectShow 解复用器。需要通过控制器选择原生设备 Pin、格式、音频来源、Crossbar 路由或设备时间戳时，建议使用 `<DSHOW>`。

使用 FFmpeg 列出 DirectShow 采集格式：

```bat
ffmpeg -list_options true -f dshow -i video="U4 4K60"
```

示例截图：

![FFmpeg 列出采集卡格式](assets/images/ffmpeg列出采集卡格式.png)

## 测试卡

`<TESTCARD>` 显示方块和移动圆点，适合检查丢帧和运动连续性。

![TESTCARD](assets/images/TESTCARD.png)

`<TESTCARD2>` 显示方块和旋转线条，适合检查运动连续性和旋转线条丢失。

![TESTCARD2](assets/images/TESTCARD2.png)

## Controller 截图

![Controller 主窗口](assets/images/Controller_main.png)

![Controller 播出窗口](assets/images/Controller_playout.png)

## 配置文件

Controller 状态保存到：

```text
OmniVCamController.xml
```

该文件保存控制器设置、常用输入、普通播放列表、计划播放列表和正在播放 XML 输出偏好。如果显式选择了界面语言，也会在根节点 `uiCulture` 属性中保存。

`OmniVCam/config.ini` 由虚拟摄像机实例读取。常用键包括：

- `hw_decode`：默认硬件解码模式。
- `tcp_port`：TCP 控制端口，默认 `16999`。
- `scale_mode`：输出缩放模式，`letterbox` 或 `fill`。
- `display_aspect`：输出显示宽高比，`auto` 或 `16:9` 等比例。
- `auto_deinterlace`：自动反交错默认开关，`1` 开启，`0` 关闭。只有没有设置全局或单输入视频滤镜时才生效。
- `auto_deinterlace_filter`：自动反交错滤镜，`bwdif`、`yadif` 或 `w3fdif`，默认 `bwdif`。
- `log_level`：FFmpeg 日志级别。
- `video_frame_buffer` / `audio_frame_buffer`：输出帧缓冲。
- `packet_queue_size`：包队列大小。
- `timeout`：输入超时，单位微秒。
- `use_fixed_frame_interval`：通常为 `1`，用于兼容 OBS。
- `ajust_start_time_if_delay_over`：输出时间校正阈值。
- `av_max_offset_time`：最大音视频时间戳偏移，单位微秒。

修改 `config.ini` 后需要重启宿主应用或重新加载虚拟摄像机实例。

## TCP 控制协议

虚拟摄像机接受基于行的 TCP 命令。默认端口：`16999`。

支持命令包括：

- `PING`：返回 `OK PONG`。
- `STATUS`：返回 `seconds`、`duration`、`size`、`state`、`scale_mode`、`display_aspect`、`input`、`deliver_ns`、`deliver_avg_ns` 和 `controller_connected`。
- `DURATION <input>[\toptions]`：探测输入时长。
- `PLAY <input>[\toptions]`：播放输入。
- `PAUSE`：暂停输出但不关闭输入。
- `RESUME`：恢复暂停输出。
- `STOP`：停止播放。
- `REOPEN`：重新打开当前输入。
- `SET_FILTER <filter>`：设置或取消全局视频滤镜。
- `SET_AUDIO_FILTER <filter>`：设置或取消全局音频滤镜。
- `SET_HW_DECODE <none|dxva2|d3d11va|cuda|qsv>`：设置硬件解码。
- `SET_SCALE_MODE <letterbox|fill|stretch|fit|keep_aspect|fullscreen>`：运行时设置输出缩放模式。
- `SET_DISPLAY_ASPECT <auto|num:den>`：运行时设置显示宽高比。
- `SET_AUTO_DEINTERLACE <0|1> [bwdif|yadif|w3fdif]`：运行时设置自动反交错行为。
- `SET_INDEX video=<n> audio=<n>`：设置流索引。
- `SET_SHIFT <microseconds>`：设置输出时间偏移。
- `DSHOW_DEVICES`：为控制器列出原生 DirectShow 视频和音频设备。
- `DSHOW_FORMATS <type> <device-moniker>`：列出设备支持的原生采集格式（`type`：视频采集为 `0`，音频采集为 `1`）。
- `DSHOW_CROSSBAR <device-moniker>`：列出可用 Crossbar 路由，以及电视调谐器/电视音频能力。
- `SEEK <seconds>`：按秒定位。
- `SEEK_BYTE_PERCENT <0-10000>`：按字节百分比定位，`10000` 表示 100%。

## 构建

解决方案：`OmniVCam.sln`。

- `OmniVCam`：C/C++ DirectShow DLL，Visual Studio 2022 / v143，支持 `Win32` 和 `x64`。
- `OmniVCamController`：C# WinForms 应用，目标 .NET Framework 4.8，`AnyCPU`。

依赖约定：

- `deps/include`：DirectShow Base Classes 头文件。
- `deps/lib/x86` 和 `deps/lib/x64`：DirectShow Base Classes 库。
- `ffmpeg_deps/include` 和 `ffmpeg_deps/lib`：构建原生 DLL 时使用的 FFmpeg 开发文件。

发布包需要包含虚拟摄像机 DLL、所需 FFmpeg 运行时 DLL、`config.ini` 和 `OmniVCamController.exe`。简体/繁体资源会作为 `zh-CN`、`zh-TW` 卫星程序集目录输出。
