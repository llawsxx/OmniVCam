# OmniVCam Virtual Cam

## 可播放任意内容的虚拟摄像头

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
- 压缩包中有配置文件示例，将示例配置文件解压到适当的文件夹中，如D:\OmniVCam\Config
- 设置环境变量OMNI_VCAM_CONFIG到含有config.ini等文件的文件夹，比如D:\OmniVCam\Config，（OmniVCam Virtual Camera 2环境变量为OMNI_VCAM_CONFIG2，以此类推）
- 编辑config.ini设置通用参数
- 将需要播放的文件一行一个放到input.txt，每个文件可设置独立的参数
  例子：
- ```
  <TESTCARD>   #测试卡
  D:\example.mp4	seek_time=14	#播放D:\example.mp4文件，跳转到14秒
  D:\tv.ts	video_filter='bwdif=1',audio_filter='loudnorm',video_index=0,audio_index=1	#播放D:\example.mp4文件，设置视频滤镜为bwdif=1，音频滤镜loudnorm，设置视频轨道0，音频轨道1
  video=OBS Virtual Camera	format=dshow,rtbufsize=1G,queue_left=5,queue_right=20	#设置播放OBS Virtual Camera，协议为dshow，缓存1G，缓冲帧数最低5最高20（帧数量达到20时会自动清除多余的帧，直到帧数量等于(20-5)/2+5=12帧，低于5帧时则会缓存，直到帧数量够12帧再继续播放）
  D:\abc.ts	#不设置参数
  ```
- 使用control.txt控制播放（具体控制方法请查看control.txt）
- ```
  goto D:\example.mp4	seek_time=14 #播放D:\example.mp4文件
  ```
- filters.txt、audio_filters.txt 设置视频音频滤镜（即时生效）
  
  - filters.txt：
  - ```
    bwdif=1	#设置使用bwdif滤镜
    ```
  - audio_filters.txt
  - ```
    loudnorm	#设置使用loudnorm滤镜
    ```
- 使用setIndex.txt设置视频音频轨道（即时生效）
  
  - setIndex.txt
  - ```
    video_index = 0
    audio_index = 0
    ```
- 使用shift.txt设置输出帧的时间偏移（即时生效），单位微秒，调整以对齐OBS、直播伴侣等软件的获取帧的时间，避免临界情况导致丢帧（虚拟摄像头与直播软件输出帧率一致时会发生该情况），需要在每次打开直播软件时调整一次，或者看着有丢帧再调也可以，（建议还是打开直播软件时专门调一次），如果是60帧输出的话，调整范围0-16667，即16ms，大于单帧间隔时间的话没作用
  
  - shift.txt
  - ```
    0
    ```
