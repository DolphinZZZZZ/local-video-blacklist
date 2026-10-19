# FFmpeg / ffprobe

本目录的依赖来自 FFmpeg 官方 `7.1.3` 未修改源码。项目只将 `ffprobe.exe` 作为独立进程调用，读取所选本地视频的时长、分辨率和编码信息。

- 上游：https://ffmpeg.org/
- 源码包：https://ffmpeg.org/releases/ffmpeg-7.1.3.tar.xz
- 源码副本：`source/ffmpeg-7.1.3.tar.xz`
- 构建脚本：`../scripts/build-ffprobe.sh`
- 许可证：`COPYING.LGPLv2.1`，LGPL 2.1 或更新版本。
- 发布构建的源码及二进制 SHA256：`PROVENANCE.json`。

构建禁用 GPL/nonfree 组件、外部库自动探测和网络协议，只启用本地文件访问；未修改 FFmpeg 源码。编译所需 MinGW-w64 和 GCC 的运行库遵循其各自许可证及运行库例外。

源码包和构建脚本会与二进制一起放进发布 ZIP 的 `app/project`，可据此重新构建或替换 `app/ffprobe.exe`。主程序的 MIT 许可证不覆盖 FFmpeg。
