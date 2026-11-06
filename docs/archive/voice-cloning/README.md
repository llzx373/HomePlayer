# 归档：实时/批量配音与音色克隆

归档日期：2026-11-06

## 为什么归档

实时配音、批量中文配音、音色克隆和说话人区分在当前阶段效果不稳定：原声与译文的混音、音轨对齐、角色音色准备时间和克隆质量都还达不到可用标准。决定先把整套实现从产品中移除，代码和设计留在这里，等技术和模型成熟后再评估恢复。

## 从产品中删除了什么

- 实时配音（边播边生成）与“音色模仿”
- 批量生成中文配音、配音混音与“中文配音”音轨菜单
- CosyVoice3、ZipVoice、TTS Worker 及对应模型清单
- 说话人分析、声纹跟踪、实时说话人时间线
- 播放器的 `IAudioMixSource` / `IPlaybackAudioMix` 混音接口和 FFmpeg 混音插入点
- 相关设置字段、每文件配音偏好、安装包与 SBOM 中的 CosyVoice/CUDA 13.1 条目
- 相关单元测试

AI 字幕（Whisper/SenseVoice 识别、Hy-MT2 翻译、实时字幕、导出）不受影响。

## 文件说明

- `source/`：删除时的完整独立源文件和测试，按原路径存放。
- `dubbing-feature.patch`：以 `3be5cf2` 为基线、包含当前工作区改动的完整功能补丁，覆盖独立文件和 `PlayerPage`、`AppSettings`、`MediaPreference`、`LibraryRepository`、`FfmpegPlayer`、安装脚本等集成点。

## 关键实现摘要

- 实时调度：`LiveDubbingScheduler` 按识别定稿、译文和说话人就绪三个条件排队；`LiveDubbingTrack` 用写时复制保存合成片段和人声区间。
- 混音：`LiveDubbingMixSource` 按人声区间压低原声，把 TTS 以媒体时间混入 48 kHz PCM。
- 音色：`CosyVoiceService` 启动常驻 OpenAI 兼容服务，`PrepareVoiceAsync` 用 ONNX 前端把参考音频编码成 prompt；`LiveSpeakerService` + `LiveDiarizationRunner` 流式分析说话人。
- 批量：`DubbingService` 逐句合成、压缩到字幕时长并写出 `<视频>.ai.dub.wav`，`DubbingMixSource` 负责播放时混音。
- UI：`PlayerPage.LiveSubtitles.cs` 中的“中文配音（实验）”菜单同时提供实时与批量入口。

## 恢复方式

1. 从 `source/` 把文件复制回原路径。
2. 参考 `dubbing-feature.patch` 恢复集成点；补丁保留的是归档时的代码，可能需要按当前代码结构手工调整。
3. 也可以从 Git 历史恢复，相关提交：`269e97e`、`b09fdef`、`85c8111`、`93d971c`、`c0d0811`、`601afd8`、`76289df`、`a152b51`、`ed9a4e5`。
4. 恢复后需要同步恢复 `third_party/ai/cosyvoice`、CUDA 13.1 运行时、`ai-files.json`、安装脚本和 SBOM 配置。

## 说明

- 旧数据库里的 `MediaPreference.DubbingMode` 列会在升级到 schema 12 时删除。
- 旧 `settings.json` 里的 `Dubbing*` 字段会被忽略，新版本保存设置时会消失。
- 本目录只用于归档，不参与编译。
- CosyVoice、ZipVoice、TTS Worker、说话人模型和 CUDA 13.1 运行时已从本地和安装目录删除；恢复功能时需要重新下载这些资源。
