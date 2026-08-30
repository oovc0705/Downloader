# MayBe Downloader

一个 Windows 桌面端的多平台视频 / 图集下载器：粘贴链接，选清晰度（图集则勾选要哪几张），坐等文件落到本地。核心下载能力由内置的 **yt-dlp + ffmpeg** 提供，界面为 WinUI 3 原生实现。

## 支持平台

抖音 · 小红书 · 哔哩哔哩 · YouTube · X · Instagram

（六个平台各自独立分区，粘贴什么链接自动路由到对应分区；快手/微博等链接能识别但暂不支持下载）

## 主要功能

- **智能解析**：粘贴链接即自动解析，视频弹出清晰度选择（按分辨率去重、标注帧率与预估大小）；图集 / 图文帖弹出缩略图网格，默认全选、可反选，只下勾选的几张
- **下载队列**：多任务并发（1–4 可调）、进度 / 速度 / 剩余时间实时显示、彩色状态胶囊；失败或取消的任务一键重试，已完成任务批量清理
- **下载历史**：完成即入档，支持标题搜索、单条删除、按平台清空，点击记录直接打开文件所在位置，重启不丢
- **免登录下载**：抖音内置 ttwid Cookie 免登录解析；需要登录的内容自动回退读取 Edge / Chrome / Firefox 的浏览器 Cookie
- **网络代理**：内置代理设置（对 YouTube / X / Instagram 等直连不畅的平台友好），对新任务即时生效
- **音频提取**：可选下载完成后自动提取为 MP3
- **外观**：深色 / 浅色 / 跟随系统 + 五套配色方案（琥珀暖纸、玫瑰暖粉、青空海洋、森野绿意、经典蓝灰），全部即时切换
- **剪贴板自动填充**：切回窗口时自动识别剪贴板里的平台链接（含分享口令里混排的文字），跳到对应分区并填好
- 友好的中文错误提示（Cookie / 风控 / 网络 / 磁盘等常见失败场景）

## 安装

到 `AppPackages\` 目录（或 Releases）：

1. 双击 `.cer` 证书，安装到「当前用户 → 受信任的人」；
2. 双击 `.msix` 安装即可。

安装包自包含 .NET 与 Windows App SDK 运行时，目标机器无需任何额外依赖；Windows 10 1809 及以上可运行。

## 开发

```bash
# 构建 / 运行（调试身份由 winapp 注册）
dotnet build -p:Platform=x64 -c Debug
dotnet run -p:Platform=x64

# 打包 MSIX
dotnet build -c Release -p:Platform=x64 \
  -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=SideloadOnly \
  -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:RuntimeIdentifier=win-x64 \
  -p:PublishTrimmed=false
```

数据（设置、历史）保存在 `%LOCALAPPDATA%\MyApp\`。项目结构与开发约定见 [agent.md](agent.md)。
