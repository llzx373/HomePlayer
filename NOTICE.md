# 第三方组件说明

HomePlayer 本身使用 [MIT License](LICENSE)。安装包和运行目录里还带有其他组件，协议与 HomePlayer 不同。使用、修改或再分发时请同时遵守这些协议。

## FFmpeg

播放回退、缩略图、字幕旁路读取使用 FFmpeg 共享库（LGPL 构建，动态加载）。

- 来源：[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 的 `n9.0.2-28-g669f5ab083` win64 LGPL shared
- 协议：[GNU Lesser General Public License v3.0](https://www.gnu.org/licenses/lgpl-3.0.html)
- 完整文本见 `third_party/ffmpeg/LICENSE.txt`，安装后也在程序目录的 `ffmpeg\LICENSE.txt`
- 源码：https://ffmpeg.org/ 以及上述构建仓库
- HomePlayer 以动态库方式加载这些 DLL，你可以替换 `ffmpeg` 目录中对应版本的库

## libass

ASS/SSA 排版由随附 FFmpeg `avfilter` 中的 [libass](https://github.com/libass/libass) 提供。libass 使用 ISC License；完整文本也保存在 `third_party/ffmpeg/libass-LICENSE.txt`，安装后位于 `ffmpeg\libass-LICENSE.txt`。

```
ISC License

Copyright (C) 2006-2016 libass contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## 主要 NuGet 依赖

| 组件 | 用途 | 协议 |
| --- | --- | --- |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | FFmpeg C API 绑定 | LGPL-3.0 |
| [NAudio](https://github.com/naudio/NAudio) | 音频输出 | MIT |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 系统托盘 | MIT |
| [Vortice.Direct3D11](https://github.com/amerkoleci/Vortice.Windows) | Direct3D 11 呈现 | MIT |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 应用框架 | MIT |
| [Microsoft.Data.Sqlite](https://docs.microsoft.com/dotnet/standard/data/sqlite/) | 媒体库数据库 | MIT |
| [System.Drawing.Common](https://github.com/dotnet/runtime) | 图像处理 | MIT |

可选的系统滤镜（例如 [LAV Filters](https://github.com/Nevcairiel/LAVFilters)）由用户自行安装，不包含在本安装包中。
