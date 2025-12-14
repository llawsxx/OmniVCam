# OmniVCam Virtual Cam 
## 可播放任意内容的虚拟摄像头
#### 使用方法
 - 设置环境变量OMNI_VCAM_CONFIG到含有config.ini等文件的文件夹
 - 编辑config.ini设置通用参数
 - 将需要播放的文件一行一个放到input.txt，每个文件可设置独立的参数
 	例子：
 - ```
    D:\example.mp4	seek_time=14	#播放D:\example.mp4文件，跳转到14秒
    D:\tv.ts	video_filter='bwdif=1',audio_filter='loudnorm',video_index=0,audio_index=1	#播放D:\example.mp4文件，设置视频滤镜为bwdif=1，音频滤镜loudnorm，设置视频轨道0，音频轨道1
    video=OBS Virtual Camera	format=dshow,rtbufsize=100M,queue_left=15,queue_right=50	#设置播放OBS Virtual Camera，协议为dshow，缓存100M，缓冲帧数最低15最高50
    D:\abc.ts	#不设置参数
    ```
 - 使用control.txt控制播放（具体控制方法请查看control.txt）
 - ```
	goto D:\example.mp4	seek_time=14 #播放D:\example.mp4文件
  
 - filters.txt、audio_filters.txt 设置视频音频滤镜（即时生效）
	- filters.txt：
	- ```
        bwdif=1	#设置使用bwdif滤镜
	
	- audio_filters.txt
	- ```
        loudnorm	#设置使用loudnorm滤镜
 - 使用setIndex.txt设置视频音频轨道（即时生效）
 
	- setIndex.txt
	- ```
      video_index = 0
      audio_index = 0