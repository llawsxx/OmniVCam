# OmniVCam Virtual Cam

## 可播放任意内容的虚拟摄像头

如果想要在直播软件里使用，并且设置的帧率与直播软件相同，建议用控制器里的 `Shift us` 调整输出帧时间偏移，不然有几率一直丢帧，下面有详细说明。
#### 安装
- 压缩包解压到适当位置，如D:\OmniVCam，需将所有dll解压出来

- 管理员身份打开cmd，输入以下命令：
- ```
  cd /d D:\OmniVCam
  regsvr32 OmniVCam.dll
  ```

#### 卸载
- 管理员身份打开cmd，输入以下命令：
- ```
  cd /d D:\OmniVCam
  regsvr32 /u OmniVCam.dll
  ```
  
或者可解压后运行当中对应的bat

#### 使用方法
- 运行 `OmniVCamController.exe` 可以通过界面控制虚拟摄像头，默认连接 `127.0.0.1:16999`。
- 关闭控制器时会自动保存当前界面配置和播放列表，启动时会自动加载。自动配置文件为程序目录下的 `OmniVCamController.xml`。
- `Input` 填入要播放的文件、采集设备或特殊输入，如 `D:\example.mp4`、`<TESTCARD>`、`<TESTCARD2>`、`<OBSVCAM>`。右侧下拉框可快速选择 `<TESTCARD>`、`<TESTCARD2>`、`<OBSVCAM>`，也可以点 `Browse` 选择本地媒体文件。
- `Options` 填入本次播放的附加参数，例如 `seek_time=14`、`video_filter='bwdif=1',audio_filter='loudnorm'`、`format=dshow,rtbufsize=1G`。
- 点 `Play` 开始播放当前 `Input`，点 `Stop` 停止，点 `Reopen` 重新打开当前输入。
- `Video filter`、`Audio filter` 修改后需要点右侧 `Set` 才会发送到虚拟摄像头；点 `Cancel` 会清空输入框并发送空白滤镜，用于取消当前滤镜。
- `HW decode` 修改后需要点右侧 `Set` 才会发送到虚拟摄像头。可选值包括 `none`、`dxva2`、`d3d11va`、`cuda`、`qsv`。
- `Seek seconds`、`Video index`、`Audio index` 改值后会立即发送到虚拟摄像头。`Set video index`、`Set audio index` 可用于重新发送当前轨道设置。
- `Shift us` 用来调整输出帧时间偏移，单位是微秒，数值变化会立即生效。右侧 `-` / `+` 按钮每次按当前步进值调整，适合在 OBS、直播伴侣等软件里观察丢帧情况时微调。60 帧输出时建议在 `0` 到 `16667` 之间调整，大于单帧间隔通常没有作用。
- 进度条可拖动定位。普通模式按播放时长 Seek；勾选 `Byte seek` 后按文件位置百分比 Seek，适合时长识别不准或不可精确按时间跳转的输入。
- 下半部分是播放列表。`Add files` / `Add folder` 添加节目，`Add bumper` 添加垫片，`Start playout` 开始按列表播出，`Next` 播放下一条，`Stop playout` 停止列表播出。`Mode` 可选择顺序、随机或定时模式。
- `Set options` 会把当前 `Options` 写到选中的播放列表条目；`Refresh durations` 重新读取时长；`Load` / `Save` 用于加载和保存播放列表。
- 常用 `Input` 和 `Options` 填法示例：

  | 用途 | Input | Options |
  | --- | --- | --- |
  | 直读 OBS 虚拟摄像头 | `<OBSVCAM>` | `queue_left=1,queue_right=5` |
  | 测试卡 1 | `<TESTCARD>` | 留空 |
  | 测试卡 2 | `<TESTCARD2>` | 留空 |
  | 播放文件并跳到 14 秒 | `D:\example.mp4` | `seek_time=14` |
  | 播放文件并指定滤镜、轨道 | `D:\tv.ts` | `video_filter='bwdif=1',audio_filter='loudnorm',video_index=0,audio_index=1` |
  | 播放 OBS Virtual Camera 设备 | `video=OBS Virtual Camera` | `format=dshow,rtbufsize=1G,queue_left=5,queue_right=20` |
  | 播放 U4 4K60 采集卡 1080p60 YUY2 | `video=U4 4K60` | `format=dshow,rtbufsize=1G,queue_left=1,queue_right=5,video_size=1920x1080,framerate=60,pixel_format=yuyv422` |
  | 播放 U4 4K60 采集卡 1080p120 MJPEG | `video=U4 4K60` | `format=dshow,rtbufsize=1G,queue_left=1,queue_right=10,video_size=1920x1080,framerate=120,vcodec=mjpeg` |
  | 播放文件且不设置参数 | `D:\abc.ts` | 留空 |

- 对于采集卡，可使用以下ffmpeg命令查看采集卡支持的格式，然后再填入以上Options
  ```
  ffmpeg -list_options true -f dshow -i video="U4 4K60"
  ```
- ![TESTCARD](/assets/images/ffmpeg列出采集卡格式.png)

可以用**测试卡**辅助调整 `Shift us`，测试卡目前有两种样式：
- 样式1，名称 `<TESTCARD>`，一个正方形和若干个圆形快速移动，观察移动的连续性判断丢帧情况：
![TESTCARD](/assets/images/TESTCARD.png)
- 样式2，名称 `<TESTCARD2>`，一个正方形和八条转动的线，观察正方形移动的连续性和旋转的线是否缺失判断丢帧情况（注意转动的线可能会造成显示器临时“烧屏”，不用担心，留下的印会在十分钟左右消失）：
![TESTCARD](/assets/images/TESTCARD2.png)
