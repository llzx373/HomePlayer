# 参与开发

欢迎 Issue 和 Pull Request。改动请尽量小、说明原因，并保持现有中文提交说明风格。

## 环境

- Windows 10 1809 或更高版本，64 位
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 编安装包还需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)

## 构建

```powershell
pwsh -File scripts/Restore-Ffmpeg.ps1
dotnet test HomePlayer.slnx -c Release
dotnet build src/HomePlayer.App/HomePlayer.App.csproj -c Release
```

`scripts/Restore-Ffmpeg.ps1` 会按固定版本和 SHA256 下载 FFmpeg LGPL 共享库到 `third_party/ffmpeg`。这些 DLL 不进 Git。直接编译应用时，如果 DLL 不存在，工程也会自动调用该脚本。

## 版本号

版本写在 `Directory.Build.props` 的 `<Version>`。

- **正式版**：`主.次.修订`，例如 `0.1.0`。GitHub 上打对应的 `v0.1.0` 标签后，Release 流水线会测试、编安装包并发布。
- **开发版**：下一个正式版号加上序号 `n`，例如 `0.1.1-1`、`0.1.1-2`。开发版不打正式标签、不发 GitHub Release。

## 安装包

```powershell
pwsh -File installer/build.ps1
```

不传 `-Version` 时用当前开发版本。生成文件：`artifacts/HomePlayer-Setup-<版本>.exe`。

## 许可

贡献默认按 [MIT License](LICENSE) 授权。涉及 FFmpeg 的改动请继续遵守 LGPL，见 [NOTICE.md](NOTICE.md)。
