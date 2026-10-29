# HomePlayer

[![CI](https://github.com/llzx373/HomePlayer/actions/workflows/ci.yml/badge.svg)](https://github.com/llzx373/HomePlayer/actions/workflows/ci.yml)
[![发布](https://img.shields.io/github/v/release/llzx373/HomePlayer)](https://github.com/llzx373/HomePlayer/releases/latest)
[![许可](https://img.shields.io/github/license/llzx373/HomePlayer)](LICENSE)

Windows 上的本地视频播放器。面向自己的片库：扫描文件夹、记住进度、用系统滤镜或自带 FFmpeg 播放，而不是做在线流媒体客户端。

## 安装

1. 打开 [Releases](https://github.com/llzx373/HomePlayer/releases/latest)，下载 `HomePlayer-Setup-x.y.z.exe`
2. 按向导安装（需要 64 位 Windows 10 1809 或更高版本，安装时会显示许可协议）
3. 如需作为默认播放器，在安装结束页或稍后到 **Windows 设置 → 应用 → 默认应用** 中确认。Windows 11 不会让安装程序悄悄改掉默认应用
4. 建议另外安装 64 位 [LAV Filters](https://github.com/Nevcairiel/LAVFilters/releases)，硬解和常见封装会更省心。没装也能用，程序会改用自带的 FFmpeg

## 能做什么

- **媒体库**：添加本地文件夹，卡片/紧凑卡片/列表浏览，跨库搜索与组合筛选、多选批量处理、收藏、标签、手动与智能自定义合集；文件变化自动增量入库，离线恢复不会误删记录
- **剧集与合集**：按季集自然排序、上一集/下一集与自动连播、整剧进度和下一集未看、系列电影合集；支持手改元数据、本地 NFO 和海报
- **续播**：跨重启记住进度、播放列表和当前条目，可标记已看/未看、从头播放、暂停历史记录或清空历史；恢复列表不自动播放
- **两条引擎**：优先 DirectShow / LAV；分离器和解码都失败时回退 FFmpeg。设置里可以调整滤镜顺序
- **字幕**：外挂和内嵌文本字幕、libass ASS/SSA 排版、PGS/VobSub 位图字幕；记住每文件选择，支持外观、延迟、编码、字体附件和截图包含字幕
- **播放**：记住每文件音轨和音频延迟，支持首选语言、多声道、保音调倍速、归一化、夜间模式、章节、书签、A-B 循环和悬停预览
- **备份与诊断**：自动保留最近两代数据，可导入导出完整备份、迁移媒体库位置、清理孤立记录并导出脱敏诊断包
- **画面**：适应、填充、拉伸、16:9 / 4:3；只改铺屏，不擅自改窗口（可另开「自适应窗口」）
- **窗口**：经典标题栏或无边框播放、全屏、置顶、关闭后留在托盘
- **系统**：安装时登记常见视频扩展名，并加入右键「用 HomePlayer 播放」
- **崩溃日志**：未处理异常、后台任务异常、原生崩溃和异常退出都会记到 `%LocalAppData%\HomePlayer\crashes`。从源码运行和安装后的软件共用这一份，设置里或开始菜单「崩溃日志」随时可看
- **可选 TMDB**：在设置里填本机保存的 API Key，用来匹配标题和海报

快捷键可在设置里改。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。FFmpeg 动态库不在 Git 里，先还原再编译：

```powershell
pwsh -File scripts/Restore-Ffmpeg.ps1
dotnet test HomePlayer.slnx -c Release
dotnet build src/HomePlayer.App/HomePlayer.App.csproj -c Release
```

本地打安装包（需 [Inno Setup 6](https://jrsoftware.org/isdl.php)）：

```powershell
pwsh -File installer/build.ps1
```

不传 `-Version` 时用 `Directory.Build.props` 里的当前版本。更完整的说明见 [CONTRIBUTING.md](CONTRIBUTING.md)。公开发布由 GitHub Actions 在 `v主.次.修订` 标签上构建并上传安装包。

## 协议

- HomePlayer 源码与安装包主体：[MIT License](LICENSE)
- 自带的 FFmpeg 共享库：[LGPL-3.0](NOTICE.md)，安装后位于 `ffmpeg` 目录，可按 LGPL 自行替换
- 其他依赖见 [NOTICE.md](NOTICE.md)

软件按原样提供，没有担保。第三方片源、在线版权内容请自行遵守相应法律。

## 安全

漏洞请按 [SECURITY.md](SECURITY.md) 私下报告，不要公开提 Issue。
