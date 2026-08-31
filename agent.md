# agent.md — 项目状态档案（LLM 必读）

> **维护约定（最高优先级）**：任何 LLM 在本仓库完成一项任务（新功能、Bug 修复、重构、依赖升级等）后，
> **必须更新本文件**：修改「当前状态」「已实现功能」「已知痛点」中对应的小节，并在文末「变更日志」追加一行
> （格式：`- YYYY-MM-DD 简述变更与原因`）。新对话开始时先通读本文件，避免重复探索或破坏既有设计。

---

## 一、项目概述

**MyApp** 是一个 Windows 桌面视频/图片下载器（**对外显示名与安装包名为 MayBe Downloader / MayBeDownloader**：显示名 2026-08-29 起，包 Identity Name 与 csproj 2026-08-31 起一并改名；C# 命名空间、
数据目录仍用 MyApp，勿混淆）：用户在对应平台分区粘贴内容链接，程序自动解析并列出可选清晰度
（图集内容则让用户勾选要下载哪几张），下载原视频/图片到指定目录。核心能力由打包内置的 **yt-dlp.exe + ffmpeg.exe**
提供，C# 层负责 UI、任务队列、进度解析、Cookie 回退与平台特判。

- 技术栈：**WinUI 3**（Windows App SDK 2.4.0）+ **.NET 10**（`net10.0-windows10.0.26100.0`）
- 目标平台：x64（也配置了 x86 / ARM64）
- 不是 git 仓库（截至 2026-08-27）
- 解决方案即项目：仓库根目录就是唯一 csproj（`MayBeDownloader.csproj`，2026-08-31 前为 MyApp.csproj），无 .sln
- **UI 形态（2026-08-27 大改 + 2026-08-28 视觉/交互优化后）**：`MainWindow` → `Views/ShellPage`（NavigationView 顶部导航，平台项带图标）→
  `Views/PlatformPage`（按平台参数化的单页面类，六个平台共用；输入卡内含 InfoBar 通知与路由提示，任务卡带状态胶囊与重试/清理）。已删除 MainPage。
- **应用内支持平台（导航分区 + 入队白名单）**：抖音、小红书、哔哩哔哩、YouTube、X、Instagram
  （`DownloadService.SupportedPlatforms`）。快手/微博/TikTok 有 URL 识别但**刻意不接收入队**（未实测，用户决定搁置）。

## 二、构建 / 运行（重要）

```bash
# 构建（必须显式指定平台，否则 WinUI 默认平台会报错）
dotnet build -p:Platform=x64 -c Debug

# 运行（必须用 dotnet run，winapp 会注册调试用包标识）
dotnet run -p:Platform=x64
```

⚠️ **直接双击 `bin\...\MayBeDownloader.exe` 会崩溃**：
`COMException 0x80040154 (REGDB_E_CLASSNOTREG)` —— 未注册包身份的 WinUI 3 应用需要包身份（winapp 注册后由
`dotnet run` 启动）。调试期崩溃先检查是不是这个原因。

### 打包为可安装的 MSIX（2026-08-30 验证通过）

```bash
# 一条命令产出可安装包（自包含 .NET + WinAppSDK，目标机器零依赖）
dotnet build -c Release -p:Platform=x64 \
  -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=SideloadOnly \
  -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:RuntimeIdentifier=win-x64 \
  -p:PublishTrimmed=false
```

- 产出：`AppPackages\MayBeDownloader_<版本>_x64_Test\`，内含 `.msix`（约 227MB）、`.cer`、`Install.ps1`/
  `Add-AppDevPackage.ps1` 助手脚本。
- 安装：①双击包内 `.cer` → 安装到「当前用户 → 受信任的人」；②双击 `.msix`（应用安装程序）或
  `Add-AppxPackage .\MayBeDownloader_*.msix`。换机器安装只需这两个文件。
- `PublishTrimmed` 必须显式关：图集勾选器走 `XamlReader.Load` 运行时模板 + 经典 Binding（反射），
  裁剪会破坏 XamlTypeInfo（csproj 里 Release 默认 true 是模板遗留）。
- 签名：`AppSigning.pfx`（**无密码** pfx，csproj `PackageCertificateKeyFile` 引用；带密码的 pfx 会被
  MSIX targets 拒绝并退化为临时证书）。`.pfx/.cer` 已 gitignore。证书丢失/换机重生成（CN 必须等于
  manifest 的 `CN=AppPublisher`，密码随后用 openssl 去掉）：

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=AppPublisher" -KeyUsage DigitalSignature `
  -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
$pwd = ConvertTo-SecureString -String "临时密码" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath AppSigning_raw.pfx -Password $pwd
# Git Bash: openssl pkcs12 -in AppSigning_raw.pfx -passin pass:临时密码 -nodes -out t.pem
#           openssl pkcs12 -export -in t.pem -out AppSigning.pfx -passout pass: && rm t.pem AppSigning_raw.pfx
Export-Certificate -Cert $cert -FilePath AppSigning.cer
```

- 升版本：改 `Package.appxmanifest` 的 `Identity Version`（如 1.0.1.0）后重跑打包命令，覆盖安装即可。


内置工具位于 `tools/`（yt-dlp.exe、ffmpeg.exe、ffprobe.exe），csproj 以 `Content` + `PreserveNewest`
复制到输出目录；`YtDlpRunner` 按 `AppContext.BaseDirectory/tools/` 定位。yt-dlp 当前版本 **2026.08.19**。

## 三、代码结构与职责

| 文件 | 职责 |
|---|---|
| `MainWindow.xaml / .cs` | 窗口外壳（**不透明方案底色** + TitleBar；Mica/半透明纱罩已移除，见陷阱 14），RootFrame 导航到 ShellPage |
| `App.xaml / .cs` | 应用入口 + **组合根**：创建全局唯一 `App.Downloads`（DownloadService），接上 `Ui.Dialogs` 的清晰度/图集选择回调，注册 `UnhandledException` 落盘 crash.log |
| `Views/ShellPage.xaml / .cs` | 顶部导航（NavigationView PaneDisplayMode=Top），六个平台 Tag 项 + 底部「设置」入口；选中即 `new PlatformPage(tag)` 塞进 Frame 并把 `LastPlatform` 写入设置（启动恢复）；**剪贴板自动填充**：窗口激活时读剪贴板，若为支持平台链接自动跳到对应分区并填入输入框（可在设置关闭） |
| `Views/PlatformPage.xaml / .cs` | **单类复用的平台分区页**：URL 输入（跨平台粘贴自动路由，输入时实时显示归队提示 `RouteHint`，操作反馈走 `InputNotice` InfoBar，提示类 6 秒自动收起）、工具行（保存目录 Chip + 询问清晰度）、下载任务列表（状态胶囊按主题取色、进度百分比、失败/取消可重试、「清空已完成」批量清理）、历史记录列表（**主列表 + 标题搜索过滤视图**，订阅 `HistoryStore` 事件增量更新，点击行直接打开文件位置）；含全部 x:Bind 静态转换函数；`SetUrlText` 供外壳剪贴板填充调用 |
| `Views/SettingsPage.xaml / .cs` | 设置页（导航底部齿轮进入）：代理开关+地址校验提示（新任务立即生效）、最大并发数 NumberBox（1-4，重启生效）、完成后提取 MP3、主题 RadioButtons（即时切换）、剪贴板自动填充开关；全部改动即时落盘 |
| `Ui/Dialogs.cs` | 服务层回调的统一弹窗入口：后台线程封送回 UI + 全局信号量排队（ContentDialog 单例限制）；清晰度选择对话框（RadioButtons + ScrollViewer 防溢出）、图集缩略图勾选器（GridView 多选、缩略图内存缓存、视频角标、顶部显示作品标题、全选/反选/已选计数）、`LogDiagnostic` 诊断落盘 |
| `Ui/ThemeHelper.cs` | 主题切换助手：`Application.RequestedTheme` 加载后只读，运行时切换挂窗口根元素的 `RequestedTheme`（级联全子树）；MainWindow 启动时按设置恢复 |
| `Styles/Common.xaml` | 全局样式字典（合入 App.xaml）：卡片 Border、区块标题、行内图标按钮、Chip 胶囊按钮、列表容器行（去默认最小高度 + 行距）、页面内边距规格，以及**平铺的状态色板**（`Status*Fg/Dark/Bg` 15 键，供代码按明暗主题索引，见陷阱 13） |
| `Models/DownloadItem.cs` | 任务条目（INPC）：状态机 `Pending→Resolving→Downloading→Processing→Completed/Failed/Cancelled`；Url/Platform 用普通可变属性（见第四节陷阱 3） |
| `Models/HistoryRecord.cs` | 历史索引条目（JSON 持久化用，plain get/set） |
| `Models/QualityOption.cs` | 清晰度选项（Label + yt-dlp 选择器）；已从 record 改为普通类（陷阱 3） |
| `Services/DownloadService.cs` | 核心：**按平台的任务集合字典**（新任务置顶 Insert(0)）、入队白名单路由、并发（信号量启动时按 `MaxConcurrency` 创建，**默认 2、上限 4，改后重启生效**）、取消、`RemoveFinished`（批量清理终态任务并清 `_cancellations`）、`EffectiveProxy()`（实时读代理设置）、Cookie 回退链、清晰度解析、图集选择回调接入、终态写历史（错误经 `ErrorMessages.Friendly` 转中文）、播放列表(playlist)链接直接报错 |
| `Services/YtDlpRunner.cs` | yt-dlp 进程封装：`--dump-single-json` 解析、下载（进度模板 `@@PROGRESS@@|percent|speed|eta`、`--print after_move:filepath` 取最终路径、可选 `--proxy` 透传、可选 `-x --audio-format mp3` 音频提取）、取消时 `taskkill /T /F` 杀进程树、错误提取 |
| `Services/PlatformDetector.cs` | URL → 平台中文名（B站/抖音/小红书/快手/微博/YouTube/X/Instagram/TikTok/其他） |
| `Services/ErrorMessages.cs` | yt-dlp/网络英文错误 → 中文可读提示的包含匹配映射（Cookie/风控、登录验证、链接失效、403/429、网络、磁盘/权限、ffmpeg 等），未命中原样返回 |
| `Services/AppSettings.cs` | 设置持久化（`settings.json`）：输出目录、下载前询问清晰度、上次停留分区、代理（`ProxyEnabled`/`ProxyUrl`）、最大并发数、提取 MP3、主题（`ThemeMode`: system/light/dark）、剪贴板自动填充；`AppFolders.DataDir` 定义数据目录；新增字段对旧文件向后兼容 |
| `Services/HistoryStore.cs` | 历史记录存储（`history.json`）：内存倒序列表 + `Added/Removed/Cleared` 事件驱动 UI 增量更新；后台落盘（临时文件 + 原子替换）；按 Id 去重 |
| `Services/FileLocations.cs` | 资源管理器定位/打开（任务行与历史行共用） |
| `Services/TtwidCookieProvider.cs` | 调字节跳动 `ttwid/union/register` 接口换取 ttwid，生成 Netscape 格式 Cookie 文件到 `%TEMP%\MyApp_douyin_cookies.txt`（20h 缓存），供抖音免登录解析 |
| `Services/XhsNoteService.cs` | 小红书**图文**笔记：抓页面正则提取 `window.__INITIAL_STATE__`（bare `undefined` 需替换为 `null`），取 `noteDetailMap.*.note.imageList` 图片直链 → 包装为 `GalleryItem` |
| `Services/DouyinGalleryService.cs` | 抖音**图集/拼贴帖**：iPhone UA + ttwid Cookie 请求 `iesdouyin.com/share/video/{id}/`（SSR），从 `window._ROUTER_DATA` 取 `item_list[*].images` 直链；**并尝试收集每个图片卡片上的 `videos` 嵌套分段（拼贴类作品，字段结构未经真实验证，取不到自动降级为纯图集）**；短链逐跳解析 id；`filter_reason` 转可读错误 |
| `Services/ImageNote.cs` | 图集共用基础设施：`GalleryItem`（Url/ThumbUrl/IsVideo）、`ImageNote`（标题+条目列表）、`JsonFragmentExtractor`（大括号配平）、`GalleryImageDownloader`（逐条下载、`标题 (n).ext`、扩展名按 Content-Type→URL 后缀→类型默认值） |

## 四、关键实现细节与陷阱（改代码前必读）

1. **弹窗必须封送回 UI 线程**：解析在 `Task.Run` 后台线程，`QualityPrompt`/`GalleryPrompt` 回调统一经
   `Ui.Dialogs.MarshalAsync` 封送（`DispatcherQueue.TryEnqueue` + `TaskCompletionSource`），并用全局信号量
   `Gate` 排队——WinUI 同一时刻只允许一个 ContentDialog。清晰度列表/图集条目都**作为参数传入**回调，
   不要读 `item.Qualities`（跨线程填充竞态）。
2. **XamlReader 运行时模板的坑**：图集勾选器的 DataTemplate 是运行时 `XamlReader.Load` 的字符串，
   只能用经典 `{Binding}`（不支持 x:Bind）；**Border 没有 `ClipToBounds` 属性（那是 WPF 的）**；
   颜色字符串必须是 8 位 `#AARRGGBB`（写成 10 位会在运行时抛 XamlParseException 直接杀进程）。
   这类错误会被 `App.UnhandledException` 记到 crash.log（遇 stowed exception 0xc000027b 先看那里）。
3. **XAML 类型信息导出不支持 init-only 属性**：任何被 `x:DataType`/x:Bind 摸到的模型（DownloadItem、
   QualityOption、HistoryRecord 等）属性必须用普通 `get; set;`，用 `record` 位置参数或 `{ get; init; }`
   会让 XamlTypeInfo.g.cs 生成非法赋值导致 CS8852 构建失败。
4. **抖音必须有"新鲜 Cookie"**：无 Cookie 时 yt-dlp 报 `Fresh cookies ... are needed`。
   `WithCookieFallbackAsync` 捕获消息含 `ookie` 的异常后依次试 edge → chrome → firefox（`--cookies-from-browser`），
   抖音额外在链首插入 ttwid Cookie 文件。成功后记住 `_workingCookieSource` 不再重试。
   ⚠️ ttwid 属未公开接口，失效时优先怀疑这里（视频与图集两条链路都依赖）。
5. **抖音格式没有独立音频流**：所有 format 均为合流 mp4，同清晰度有 4 个重复 id（后缀 -0..-3）。
   选择器 `{id}+ba/{id}/bv*+ba/b` 的 `+ba` 分支实际不命中，属兼容写法，勿"简化"掉 `bv*+ba/b` 兜底。
6. **小红书**：视频 → yt-dlp 正常；图文 → yt-dlp 报 `No video formats found` 后走 `XhsNoteService`。
   分享链接里的 `xsec_token` 有时效，风控页/失效链接会解析不出 `__INITIAL_STATE__`，此时报
   「请使用完整分享链接重试」。
7. **抖音图集**：与视频两套通道。yt-dlp 报 `Unsupported URL`（note 路由）时由 `DouyinGalleryService` 接管。
   已知行为：图文帖短链也 302 到 `share/video/{id}`，id 正则 `(?:video|note)/(\d+)` 都匹配；
   **必须带 ttwid Cookie** 否则 `item_list` 为空；图文特征 `aweme_type: 2`、`images` 非空；
   图片 CDN（douyinpic.com）带 iPhone UA 即可下载，扩展名按响应 Content-Type（常见 `image/webp`）。
8. **进度与编码**：yt-dlp 一律 `--encoding utf-8` + 进程 UTF8 编码；进度行 `@@PROGRESS@@|…` 标记解析；
   最终路径靠 `--print after_move:filepath` 且校验 `File.Exists`。
9. **输出命名**：视频 `%(title).100s [%(id)s].%(ext)s` + `--windows-filenames`；标题含 `#` 已被
   `ArgumentList` 正确转义，不要改成字符串拼接。图集文件名经 `GalleryImageDownloader.SanitizeFileName`
   （去非法字符、限 80 字符）。
10. **UI 绑定**：DataTemplate 用 `x:Bind` + 页面静态转换函数（`views:PlatformPage.*`）；`FolderDisplay` 是
    `OneTime`，改目录后需手动 `Bindings.Update()`。任务列表 ItemsSource 也是 OneTime（页面实例按导航重建）。
11. **数据目录与 AppData 虚拟化**：设置/历史写在 `%LOCALAPPDATA%\MyApp\`（`AppFolders.DataDir`）。
    ⚠️ 应用经 winapp 注册了包身份运行，**AppData 写入被 MSIX 虚拟化重定向**到
    `%LOCALAPPDATA%\Packages\2F12C0BC-..._1z32rh13vfry6\LocalCache\Local\MyApp\`——排查数据文件去那里找。
    换身份/免打包运行会导致数据"看起来丢了"。
12. **保存目录**：默认 `~/Downloads/MediaDownloader`，运行期 FolderPicker 修改，**现已持久化**
    （settings.json），重启恢复；导航分区选中项同样恢复。
13. **状态色板必须平铺在 Common.xaml**：任务/历史状态胶囊的颜色键（`Status*Fg/Dark/Bg`）故意**不放
    ThemeDictionaries**，因为代码侧（`PlatformPage.StatusBrush`）用 `Application.Current.Resources` 的
    索引/TryGetValue 按明暗主题挑键——放 ThemeDictionaries 里代码索引可能查不到。改色板时保持平铺结构，
    Fg/Light 与 Fg/Dark 成对、Bg 用低透明度 tint（`#AARRGGBB`）不分主题。
14. **配色设计规则（改 Palettes/*.xaml 前必读）**：
    - **窗口底不透明**：不要恢复 Mica/Acrylic/半透明纱罩——壁纸色透入导致"发透"，且半透明叠层会把方案底色
      冲淡到肉眼不可辨（两次翻车的根因）。MainWindow 根 Grid 直接铺 `ApplicationPageBackgroundThemeBrush`。
    - **底色必须带明确色相**：浅色 L≈91-95%、深色 L≈9-11% 且饱和度足以一眼区分五套方案；同时**色相只上底色
      与描边**，卡片/输入保持近中性白/深灰并拉开明度差（浅色卡近纯白、深色卡比底亮一档）——色相刷满所有
      表面会整体发灰、层次塌掉。
    - **NavigationView 内容区/顶栏透明**：`NavigationViewContentBackground`/`NavigationViewTopPaneBackground`
      在各方案 ThemeDictionaries 里显式设为 Transparent——默认值是半透明 Layer 色，会把底色盖成近白。
    - **五套方案键集必须一致**（含 Light/Default 成对）；`LayerFillColorDefaultBrush` 为不透明卡片色
      （ElevatedCardStyle 与 NavView 内层复用）；`ThemePalette.Presets.BackgroundHex` 与浅色底色保持同步
      （设置页色板预览用）。
    - **切换必须"原地逐键覆盖 + 主题翻转"，勿换字典实例**：实测替换 `MergedDictionaries` 里的字典实例后，
      部分元素（尤其窗口根 Grid 的底色）的 ThemeResource 绑定**不会重新求值**——首帧正确、运行时切换不刷新。
      `ThemePalette.Apply` 现为：定位常驻工作字典（`_installed`，即 App.xaml 合并项）→ `OverwriteValues`
      逐键写入 → `ThemeHelper.ForceResourceRefresh()`（MainWindow 内同帧把根元素与 RootFrame 的
      RequestedTheme 翻对侧再复原，同步赋值无中间渲染帧、无闪烁）。WarmUp 缓存的模板字典**只读**，
      工作字典独立于缓存，勿再让两者共享可变实例。
    - 历史遗留：`WarmVeilBrush` 键已随纱罩移除，不要再引用。
15. **图标资产**：所有图标/Logo/启动画面由根目录 **icon.png**（1024×1024 橙色下载图标，源文件勿删）经
    `tools/ffmpeg.exe` 生成，换图标时重跑即可：
    - `Assets/AppIcon.ico` = 多尺寸 ico（16/24/32/48/64/128/256，PNG 条目需 `format=rgba`），窗口
      `AppWindow.SetIcon`、TitleBar、csproj `<ApplicationIcon>`（内嵌 exe）三处引用同一文件；
    - Logo 资源按原文件名覆盖：StoreLogo=50、Square44x44 scale-200=88 / targetsize-24 / targetsize-48、
      Square150x150 scale-200=300、Wide310x150 scale-200=620×300（280 图标透明垫居中）、LockScreenLogo=48；
      启动画面 SplashScreen scale-200=1240×600（琥珀底 `#F5E9D5` + 居中 240px 图标）。
    - ⚠️ 根目录 `icon.ico` 是**改扩展名的 PNG**（与 icon.png 字节相同），LoadImage 吃不下，勿直接给
      SetIcon 用；要新图标请从 icon.png 重生成多尺寸 ico。

## 五、已实现功能

- 顶部导航按平台分区（抖音/小红书/哔哩哔哩/YouTube/X/Instagram，各项带图标），每区独立输入框、任务列表、历史记录
- 跨平台粘贴自动路由到正确分区；未收录平台给出明确提示（不入队）；**输入过程实时提示**链接归属（归队去向或暂不支持），归队成功后 InfoBar 提示去向（6 秒自动收起）
- 解析后弹出清晰度选择（RadioButtons 样式，按分辨率去重，标注帧率与预估大小；可关「下载前询问清晰度」）
- 图集/图文解析后弹出**缩略图网格勾选器**：顶部显示作品标题，默认全选、可反选、视频角标、按钮实时显示"下载所选 N 项"，只下载勾选项；单张内容不弹窗直接下
- 抖音拼贴类作品（图片卡片挂视频分段）尝试作为可选条目收集（字段结构待真实链接验证，见痛点）
- 任务卡视觉分级：彩色**状态胶囊**（排队/解析/下载/合并/完成/失败/取消，明暗主题各自取色）、进度条+百分比、速度与剩余时间一行元信息；合并阶段转圈
- **失败/取消任务一键重试**（原链接重新解析入队）；「清空已完成」批量清理终态任务；新任务置顶显示
- 历史记录索引：完成/失败自动入档，支持单条删除、在文件夹中显示、**点击行直接打开文件位置**、按平台清空（带确认）；重启不丢
- 设置持久化：输出目录（输入卡内 Chip 形态，点击直接换目录）、清晰度偏好、上次停留分区，重启恢复
- 抖音免登录（ttwid Cookie）；浏览器 Cookie 多级回退（edge/chrome/firefox）
- 播放列表/合辑链接（json 根 `_type=playlist`）直接给出「暂不支持，请粘贴单条视频」的明确报错
- **常见 yt-dlp/网络错误映射为中文提示**（`ErrorMessages`，未命中原样展示）
- **设置页**（导航底部齿轮）：代理透传（`--proxy`，对 YouTube/X/Instagram 等直连不通平台的解决入口，新任务立即生效、带地址格式校验提示）、最大并发数 1-4（重启生效）、完成后提取 MP3 音频（`-x --audio-format mp3`）、主题跟随系统/浅色/深色与五套配色方案（琥珀/玫瑰/海洋/森野/经典，均即时切换、重启保持，见陷阱 14）、剪贴板自动填充开关
- **剪贴板自动填充**：切回窗口自动识别剪贴板中的支持平台链接（含分享口令里混排的文字），跳到对应分区并填入输入框
- 历史记录**按标题搜索**（分区页内过滤，含"没有匹配"空态文案）
- 全局圆角体系：`ControlCornerRadius`/`OverlayCornerRadius` 主题资源覆盖，输入框/按钮/下拉整体 8px 圆角；主按钮/搜索框/浮起卡片样式（`Common.xaml`）
- 完成后「打开所在文件夹」（资源管理器定位文件）
- 窗口默认 1120×780，最小 720×560；页面内容上限 1080 宽居中，超宽屏不散架

## 六、已知痛点 / 待办

- [ ] **历史删除按钮的真实鼠标点击未回归**：应用内验证时发现 UIA Invoke（自动化按压）对历史行的
      Subtle 小图标按钮只给焦点不触发 Click（下载/Tab/超链接按钮正常）；存储层 Remove/Clear 已用控制台
      harness 验证通过，处理器是标准 Click 接线，真人鼠标点击大概率正常——**需要人工点一次确认**。
      若真人点击也无效，删除处理器对 DataContext 异常情况留有诊断日志（crash.log）。
- [ ] **抖音拼贴/实况类作品的分段字段未适配**：用户实测一条拼接内容能解析出 ID 与图片，但勾选器里只有
  图片条目、没有视频段（用户怀疑其实拼的是动图）。诊断转储已就位：重试一次该链接后读
  `debug\douyin_last.json`，核对每个 image 卡片上的真实视频字段名（猜测的 `videos` 数组未命中），
  再适配 `DouyinGalleryService.ParseNote`；当前按 `images[*].videos[*].play_addr` 的猜测实现，降级安全。
- [ ] **YouTube / X / Instagram 网络层问题**（无代理直连超时）：`--proxy` 设置入口**已实现**
      （设置页 → 网络，写进 `AppSettings`，`DownloadService.EffectiveProxy()` 每任务实时读取传给 yt-dlp），
      但**尚未在真实代理环境验证**；代理可用后仍待验证：YouTube community post、X 纯图帖、Instagram 多图帖。
- [ ] crash.log 诊断机制（App.UnhandledException + Dialogs 失败落盘，位于虚拟化数据目录）是临时设施，
      稳定后可去掉或改为正式日志。
- [ ] ttwid 未公开接口，抖音风控收紧时可能失效（视频与图集两条链路都依赖它）。
- [ ] 抖音图集图片保存为 webp（源站直链即 webp），未转 jpg/png；如需通用格式可加 ffmpeg 转换。
- [ ] yt-dlp 为手动更新的静态二进制，无自动升级（可考虑做「检查更新」）。
- [ ] 抖音下载速度偶尔显示 `Unknown B/s`（已过滤为空串，上游问题）。
- [ ] 无正式单测；`ParseQualities`、两个 ParseNote、HistoryStore 适合抽纯函数补测试
      （HistoryStore 已用临时控制台 harness 验证过持久化往返，方法可复用：csproj 直接 Compile Include 源文件）。
- [ ] 快手/微博/TikTok 平台识别存在但刻意不接收入队（用户决定搁置）；如要开放，加进
      `DownloadService.SupportedPlatforms` 并给 ShellPage 加导航项即可。

## 七、变更日志

- 2026-08-31 **项目名统一为 MayBe Downloader（用户要求）**：包 Identity Name（GUID→`MayBeDownloader`，
  包文件名随之变为 `MayBeDownloader_<版本>_<架构>.msix`）、csproj 文件名（`MyApp.csproj`→`MayBeDownloader.csproj`，
  AssemblyName 随之，exe 为 `MayBeDownloader.exe`；RootNamespace 显式保持 MyApp 故命名空间不动）、
  launchSettings 配置名同步。显示名/窗口标题/README 标题此前（08-29）已是 MayBe Downloader。
  注意：包族（PackageFamilyName）变化 → 旧 GUID 开发包成为独立应用需手动卸载，其虚拟化数据
  （`%LOCALAPPDATA%\Packages\<旧族>\LocalCache\...`）不自动迁移；C# 命名空间与 `%LOCALAPPDATA%\MyApp`
  数据目录按既定约定保持 MyApp。发布者显示名仍为 AppPublisher（CN=AppPublisher 证书匹配，未动）。

- 2026-08-30 新增 README.md（对外简介：功能一览、安装两步、开发命令；从简不重复 agent.md 细节）。

- 2026-08-30 **.gitignore 完善与 MSIX 打包链路打通**：
  - .gitignore：补签名密钥（`*.pfx`/`*.cer`，绝不入库）、`.zcode/`、`.vscode/`、根目录杂散 exe
    （`/*.exe` 仅根级，tools/ 引擎 exe 必须提交）；修复 `*.pubxml` 全局规则误排除
    Properties/PublishProfiles 三个在用发布配置（加 `!Properties/PublishProfiles/*.pubxml` 反选）。
  - 打包签名：生成自签名证书 CN=AppPublisher（与 manifest Publisher 一致，指纹 B443E3F0…，5 年期，
    Code Signing EKU），导出 `AppSigning.pfx`（**无密码**——带密码 pfx 会被 MSIX targets 拒绝，
    APPX0105/0107 退化为临时证书签名）+ `AppSigning.cer`；csproj 增加
    `PackageCertificateKeyFile=AppSigning.pfx`，Debug 构建回归 0 警告。
  - 打包命令验证通过（自包含 .NET + WinAppSDK、SideloadOnly、显式关 PublishTrimmed 保护 XamlReader
    反射）：产出 `AppPackages\MyApp_1.0.0.0_x64_Test\MyApp_1.0.0.0_x64.msix`（227MB，含
    AppxSignature.p7x 与 tools/ 三件套，包内 .cer 与签名证书一致）。流程与证书再生成命令沉淀到第二节。

- 2026-08-29 **修复"切换配色方案底色不变"（用户反馈：底色不随方案改变、不好看）**：
  - 根因：`ThemePalette.Apply` 此前替换 `MergedDictionaries` 中的字典实例，替换后部分元素
    （尤其窗口根 Grid）的 ThemeResource 绑定不重新求值——启动首帧正确（窗口创建前已加载方案），
    运行时切换只有部分控件刷新，底色纹丝不动（即 2026-08-29 早前记录的"个别控件未刷新"残留，
    实为主症）。
  - 修复：Apply 重写为「定位常驻工作字典（App.xaml 合并项，找不到则自建追加）→ 把目标方案全部键
    **原位覆盖**进工作字典（逐键资源变更通知；缓存模板字典只读不写）→ `ThemeHelper.ForceResourceRefresh()`
    兜底：MainWindow 同帧把根元素与 RootFrame 的 RequestedTheme 翻对侧再复原，强制全树重算，
    同步两次赋值不产生中间渲染帧、无闪烁」；启动时主题偏好从 RootFrame 统一挂到窗口根元素。
  - 顺带核实 `NavigationViewContentBackground`/`NavigationViewTopPaneBackground` 为官方覆盖键名
    （Microsoft Learn Mica 文档同款写法），此前的透明覆盖有效。构建通过 0 警告 0 错误。

- 2026-08-29 **更名 + 换图标（用户要求）**：应用对外名称改为 **MayBe Downloader**（manifest 两处
  DisplayName、Description 改「多平台视频与图集下载器」、VisualElements BackgroundColor `transparent`→
  `#F5E9D5`；MainWindow 的窗口 Title 与 TitleBar Title）。图标整体换成根目录 icon.png：用 tools/ffmpeg
  从它生成多尺寸 `Assets/AppIcon.ico`（16–256 七档；原文件是模板占位灰图标）并覆盖全部 Logo 资源与启动
  画面（琥珀底居中图标）；csproj 新增 `<ApplicationIcon>` 让 exe 本身带图标。内部标识（命名空间/程序集/
  数据目录 `MyApp`）未动。构建通过 0 警告 0 错误。要点沉淀为陷阱 15（含「根目录 icon.ico 实为 PNG」）。

- 2026-08-29 **配色三次重设计（用户反馈：浅/深主题都"太透明"、切换配色方案底色几乎不变）**：
  - 根因有二：①窗口 = Mica + 96% 半透明纱罩 + 半透明 Layer/Control 填充多层叠加，壁纸色透入且底色被稀释；
    ②底色本身全是 L≈95%+ 的近白色（五套肉眼几乎无差），且 NavigationView 内容区默认的半透明 Layer 色
    又把底色盖掉一层。
  - 修复：**移除 MicaBackdrop 与 WarmVeilBrush 纱罩**，MainWindow 根 Grid 直接铺不透明的
    `ApplicationPageBackgroundThemeBrush`；五套方案底色改为**带明确色相的不透明色**（浅色：琥珀 `#F5E9D5` /
    玫瑰 `#F8E7EB` / 海洋 `#E4EEF1` / 森野 `#E9F0DE` / 经典 `#EFF1F5`，深色为对应色相的近黑
    `#211A0F`/`#241519`/`#131F24`/`#1A2312`/`#1B1D23`）；`NavigationViewContentBackground`/
    `NavigationViewTopPaneBackground` 显式透明露出底色；卡片仍近中性白/深灰保持明度层次（吸取上一版
    "色相刷满表面发灰"的教训）；新增各方案 `CardStrokeColorDefaultBrush`（带色相的描边 tint），
    `LayerFillColorDefaultBrush` 改为不透明卡片色；删除 `WarmVeilBrush` 键。完整规则沉淀为**陷阱 14**。
  - `ThemePalette.Presets.BackgroundHex` 同步新浅色底色（设置页色板预览）。构建通过 0 警告 0 错误。

- 2026-08-29 **配色方案二次重设计（用户反馈：组件与背景颜色不协调、不好看）**：
  - 上一版失败原因：把色相涂到所有表面（卡片/输入框/分层全带色），导致整体发灰、明度层次塌掉。
  - 新设计规则（60-30-10）：**底色只带低语级色相**（L≈96.5%，S 极低，如琥珀 `#F9F4EA` / 玫瑰 `#FAF1F3` /
    海洋 `#EFF5F6` / 森野 `#F0F5EE` / 经典 `#F6F6F8`）；**表面接近纯白/纯深灰并明确亮于（暗则暗于）底色**，
    靠描边+明度差立层次（浅色卡 `#FFFDF7` 级，深色卡 `#232019` 级）；**强调色只在交互元素**，不下渗表面；
    输入框统一近纯白/深。新增键集不变，仅重定值——后续调色必须遵守此规则，勿再把色相刷上大面积表面。
  - 纱罩保持 95% 不透明（`#F5…`）；ThemePalette 色板预览 hex 同步新底色。
- 2026-08-29 **配色方案增强（用户反馈：切换整体感弱、组件变化不明显、响应慢）**：
  - 慢：`Apply` 原来每次 `new ResourceDictionary{Source=…}` 走惰性磁盘解析，UI 更新滞后；改为
    `WarmUp()` 启动时把五套方案全部预解析进静态缓存，`Apply` 只做内存合并字典原位替换，
    且选中当前方案时直接 return 避免无谓重算。
  - 整体感弱：根因是窗口底层是 Mica（按壁纸取色，不吃配色覆盖），纱罩 82% 透明度让壁纸色大量透入，
    卡片又接近中性白。纱罩提到 95%（`#F2…`），五套方案的页面底色改为**带明显色调倾向**（琥珀米色
    `#F8F1E4` / 玫瑰粉 `#F9EEF1` / 海洋青 `#EAF2F4` / 森野绿 `#EDF3EA` / 经典中性 `#F7F7F7`），卡片、
    分层、输入框填充、Subtle 悬停层也按方案着色——五套深浅模式共 10 组全部重写。
  - 已知残留：若个别控件在换字典后颜色未刷新，兜底手段是根元素 ActualTheme 翻转两次强制重算
    （有闪烁风险，未默认启用，等用户实测反馈）。
- 2026-08-29 **平台品牌图标**：导航栏六个平台项的 Fluent 字形图标替换为真实品牌 SVG——
  `Assets/PlatformIcons/`（douyin/xiaohongshu/bilibili/youtube/instagram + x 黑白两版），数据来自
  Simple Icons CDN（CC0；douyin 无 slug，用 TikTok 音符路径手工合成抖音标志性青 `#25F4EE` 红 `#FE2C55`
  双色错位 + 深色主体三层 SVG）。csproj 以 Content 部署；`Styles/PlatformIcons.xaml` 定义
  `SvgImageSource` 资源（48px 光栅化），X 用 ThemeDictionaries 随主题切黑白；ShellPage 用
  `NavigationViewItem.Icon` + `ImageIcon` 引用（⚠️ 本版 NavigationViewItem **没有** IconSource 属性，
  WMC0011；ImageIcon : IconElement 可进 Icon）。其余 UI 图标仍是 Fluent 字形，如需替换走同一模式。
- 2026-08-28 **外观系统扩展：配色方案热切换**：
  - 强调色+底色覆盖从 `Common.xaml` 拆到 `Styles/Palettes/` 五套独立字典（Amber 琥珀暖纸 / Rose 玫瑰暖粉 /
    Ocean 青空海洋 / Forest 森野绿意 / Classic 经典蓝灰=Windows 原生观感），每套含 Light/Default 两主题
    完整键集（SystemAccentColor 7 色 + 8 强调画刷 + 10 底色画刷含 WarmVeilBrush），键集必须与其他方案一致。
  - `App.xaml` 在 Common 之后合并 Amber；`Ui/ThemePalette.Apply(id)` 按 Source 路径识别并原位替换该合并项
    （后合并者优先，替换触发全树 ThemeResource 重算，即时生效）；`App.OnLaunched` 在创建窗口前 Apply 恢复。
  - `AppSettings.ThemePreset` 持久化；设置页外观区新增「配色方案」RadioButtons（ItemsSource=PaletteOption，
    DataTemplate 内色板圆点渐变 + 名称），与深浅模式（ThemeHelper）独立组合。
  - 已知联动：`PlatformPage.StatusBrush` 按钮的明暗判断读的是 `Application.Current.RequestedTheme`，
    若用户用 ThemeHelper 强制了浅/深而系统主题相反，胶囊前景色取的是系统侧键——色板 Bg 是主题中性 tint
    不至于不可读，后续可改成读根元素 ActualTheme。
  - 历史搜索框改回胶囊：直接在元素上写本地 `CornerRadius="18,18,18,18"` + `MinHeight=36`
    （SearchBoxStyle 的 999 setter 会被全局 ControlCornerRadius=8 的模板 ThemeResource 引用干扰，
    本地值优先级最高）。
- 2026-08-28 **品牌配色**：`Common.xaml` 新增 ThemeDictionaries（Light/Default 成对）——覆盖
  `SystemAccentColor` 及 Light1-3/Dark1-3 变体 + 8 个强调色派生画刷（琥珀橙 `#C96A28`，深色提亮为
  `#E08A4C`）；暖底覆盖 `SolidBackgroundFillColor*`、`CardBackgroundFillColor*`、`LayerFillColorDefaultBrush`、
  `ControlFillColorDefaultBrush`、`SubtleFillColor*`、`ApplicationPageBackgroundThemeBrush`
  （浅色米色纸感 `#FBF6ED` 系，深色暖棕 `#221D16` 系）；`MainWindow` 根层加 `WarmVeilBrush`
  半透明暖纱盖在 Mica 上稳定整体色调。**已被 Palettes 方案字典取代，色值现维护在 Styles/Palettes/*.xaml**。
  调色改方案文件里对应 hex 即可，浅深两套必须成对改。
- 2026-08-28 **功能扩展一轮**（用户要求只写代码，已构建通过 0 警告 0 错误）：
  - 新增 `Views/SettingsPage`（导航底部齿轮入口）：代理开关+地址（实时读，新任务立即生效，含格式校验提示）、
    最大并发数 NumberBox（`DownloadService` 信号量启动时按设置创建，改后重启生效）、完成后提取 MP3
    （`-x --audio-format mp3 --audio-quality 0`，不保留视频文件）、主题跟随系统/浅色/深色（即时切换）、
    剪贴板自动填充开关。
  - `AppSettings` 新增 `ProxyEnabled/ProxyUrl/MaxConcurrency/ExtractAudio/ThemeMode/AutoFillClipboard`
    （对旧 settings.json 向后兼容）；`DownloadService.EffectiveProxy()` 每任务实时读取传
    `YtDlpRunner`（GetInfoJsonAsync/DownloadAsync 新增可选参 proxy/extractAudio）。
  - 新增 `Ui/ThemeHelper`：运行时主题切换挂窗口根元素 `RequestedTheme`；`MainWindow` 启动时按设置恢复。
  - `ShellPage`：导航底部「设置」项 + 路由；**剪贴板自动填充**（窗口激活时读剪贴板文本，正则提取第一段
    http(s) 链接，属支持平台则跳分区并 `PlatformPage.SetUrlText` 填入；口令混排文字可解析）。
  - `PlatformPage`：历史记录改**主列表 + 搜索过滤视图**（标题忽略大小写包含匹配，含"没有匹配"动态空态）；
    下载按钮换 `PrimaryActionButtonStyle`；新增 `SetUrlText`。
  - `Styles/Common.xaml` 追加：全局 `ControlCornerRadius`/`OverlayCornerRadius` 覆盖（默认控件 8px 圆角）、
    `PageTitleStyle`/`ItemTitleStyle`/`ElevatedCardStyle`/`PrimaryActionButtonStyle`/`SearchBoxStyle`。
  - 同日另有一轮外部改动（状态胶囊色板、InfoBar、重试、清空已完成、ErrorMessages、窗口尺寸等）已先落地，
    本轮在其之上叠加，见下一条。
  - 构建修复：`ThemeHelper.Apply` 里 `Window.Content` 静态类型是 `UIElement`，`RequestedTheme` 在其子类
    `FrameworkElement` 上，需 `is FrameworkElement root` 模式匹配（CS1061 → 已修复，x64 Debug 构建通过
    0 警告 0 错误；WMC9999 为 C# 错误的 XAML 编译连锁反应，见陷阱 2）。
- 2026-08-28 **UI 现代化与交互优化**（本次改动，**尚未构建验证**，构建命令见第二节）：
  - 视觉：导航平台项加图标；任务卡改为「状态胶囊（主题感知色板）+ 进度百分比 + 元信息行」结构；历史行加
    状态圆点；空状态改为图标+标题+提示三行；`Styles/Common.xaml` 新增状态色板（平铺，陷阱 13）、Chip、
    RowItemStyle；页面内容限宽 1080 居中；`MainWindow` 默认 1120×780、最小 720×560。
  - 交互：输入卡内加 InfoBar 反馈（校验失败/跨平台归队提示，提示类 6s 自动收起）；输入时实时路由提示；
    失败/取消任务加重试按钮；任务区加「清空已完成」（`DownloadService.RemoveFinished`）；历史行点击直接
    打开文件位置；新任务置顶 Insert(0)；入队成功后焦点回输入框便于连续粘贴；保存目录改为 Chip 点击即换。
  - 弹窗：清晰度选择从 ListView 改 RadioButtons + ScrollViewer 防溢出；图集勾选器顶部显示作品标题、格子
    圆角 8。
  - 报错：新增 `Services/ErrorMessages.cs`，失败任务错误映射为中文（两处 catch 接线）。
  - 配套：`DownloadItem.QualityLabel` 补 INPC 通知（供 MetaText 多路径 OneWay 绑定刷新）。
  - 首次构建曾报 CS0234（`Windows.UI.Colors` → 已改 `Microsoft.UI.Colors`），WMC9999 为其连锁反应。
- 2026-08-27 **链接 ID 解析加固**（用户实测拼接视频报「无法解析出抖音内容 ID」后）：ItemIdPattern 增加
  `/slides/` 路由；新增 `ModalIdPattern` 识别 `?modal_id=`/`item_id=`（用户主页弹窗播放形态，浏览器地址栏
  复制常见）；短链逐跳解析补 ttwid Cookie + Referer 降低被风控拦成 200 的概率；报错文案改为列出支持的
  链接形态。
- 2026-08-27 **抖音分享页诊断转储**：`DouyinGalleryService.FetchNoteAsync` 每次解析把内嵌 JSON 写到
  数据目录 `debug\douyin_last.json`（覆盖式），用于核对拼贴/实况类作品的真实字段结构后适配解析器；
  字段确认后可移除该转储。
- 2026-08-27 **UI 大改造**：单页平铺改为「顶部导航平台分区」——`Views/ShellPage`（NavigationView Top）
  + 参数化 `Views/PlatformPage`（输入/工具行/任务列表/历史记录四段布局），退役 MainPage；新增
  `Styles/Common.xaml`（卡片/规格统一样式）。
- 2026-08-27 **图集自选下载**：`ImageNote` 升级为 `GalleryItem`（Url/ThumbUrl/IsVideo）列表，
  `DownloadService` 新增 `GalleryPrompt` 回调，`Ui/Dialogs` 实现缩略图网格勾选器（多选/全选/反选/计数/
  视频角标/缩略图缓存），只下载勾选项；单条目不弹窗。
- 2026-08-27 **历史记录持久化**：新增 `HistoryRecord` + `HistoryStore`（history.json，事件驱动增量更新），
  任务终态自动入档；平台页支持单条删除、打开位置、按平台清空（确认框）。
- 2026-08-27 **设置持久化**：新增 `AppSettings`（settings.json）：输出目录、询问清晰度、上次分区，重启恢复。
- 2026-08-27 **多平台队列**：`DownloadService` 单集合改为按平台字典 + 入队白名单路由；播放列表链接直接报
  「暂不支持」防多文件互相覆盖。
- 2026-08-27 **抖音拼贴多段视频（实验性）**：`DouyinGalleryService` 尝试收集 `images[*].videos` 嵌套分段
  作为可选视频条目，字段未经验证、降级安全；`GalleryImageDownloader` 支持视频扩展名推断（Content-Type →
  URL 后缀 → 类型默认）。
- 2026-08-27 修复 XamlTypeInfo 对 init-only/record 模型生成非法赋值的构建失败（DownloadItem.Url/Platform、
  QualityOption 改普通可变属性）。
- 2026-08-27 修复图集勾选器两个运行时模板错误：Border 无 ClipToBounds、颜色串 `#B300000000` 非法
  （10 位 hex），已修正并新增 App 级 UnhandledException → crash.log 诊断。
- 2026-08-27 应用内实测（真实链接）：Tab 切换、未收录平台警告（快手）、抖音图集勾选下载（15 张选 6 张只落
  6 文件）、抖音视频清晰度弹窗（720P）下载、历史跨重启恢复、目录改 E:\VideoDownloadTest 并持久化恢复；
  HistoryStore Remove/Clear 控制台 harness 通过。历史行小图标按钮的 UIA 按压只聚焦不触发 Click（真人鼠标待确认）。
- 2026-08-27 修复 `JsonFragmentExtractor` 严重 bug：bare `undefined` 的替换目标误写为 `"$1"`，应为 `"$1null"`。
- 2026-08-27 五平台 9 链接全量实测（控制台 harness）：抖音/小红书四条全过，YouTube/X/Instagram 因本机无代理
  直连不通（应用层无能为力，已列入待办加 --proxy 支持）。
- 2026-08-27 新增抖音图集（图文帖）下载：`DouyinGalleryService`（iesdouyin SSR + ttwid + `_ROUTER_DATA`）；
  抽出共用 `ImageNote.cs`；`XhsNoteService` 重构复用。
- 2026-08-27 修复抖音「发生错误：」空白报错：清晰度对话框后台线程创建 UI 的 COMException，改封送 + 排队。
- 2026-08-27 新增小红书图文笔记下载（捕获 yt-dlp "No video formats found" 后回退）。
- 2026-08-27 错误信息兜底：Exception Message 为空时展示 `[类型名] 完整异常`。
