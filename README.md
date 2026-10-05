# Modern Image Viewer

面向 Windows 10/11 的本地图片查看器，目标是快速打开、流畅浏览、完成常用编辑，并确保原图安全。

当前推进 **M1 浏览体验与 M2 文件编辑首批**。已有常见静态格式、文件夹/多选浏览、缩放/平移/全屏、幻灯片、缩略图、排序和文件关联；支持中英文、深浅/系统主题、常见 EXIF 和方向纠正。开发代码已补 RAW 内嵌预览、静态 AVIF/HEIF，以及非破坏性自由/固定比例裁剪、尺寸调整、撤销/重做和 PNG/JPEG/WebP 安全另存为。剪贴板文件/位图打开、复制预览/受限原尺寸及内存图片编辑已接入开发代码。完整 RAW 显影、动画/多页与完整色彩管理仍待实现。比例裁剪和有损 WebP 品质导出见[编辑补全](docs/editing-improvements.md)。

**当前开发格式：12 类、25 个扩展名。** 常见静态格式、JPEG XR、受限 SVG、AVIF、HEIF，以及 RAW 内嵌预览（DNG/CR2/CR3/NEF/ARW/RAF/RW2/ORF/PEF）。AVIF/HEIF 使用随包 Magick.NET，静态首图、最大32MP/128MiB输入；RAW使用固定LibRaw0.22.2桥接，最大256MiB输入/32MiB预览，尺寸代表内嵌预览，不是传感器；没有可读预览时反馈，不进行完整显影。GIF/WebP仅首帧、TIFF仅首页、ICO仅首个图标；受限SVG不支持文字、外部资源、复杂合成和动画。详细边界见[格式支持](docs/decoder-support.md)与[本批验证](docs/raw-modern-editing.md)。公开v0.4.0仍只有原9扩展名。

当前使用 **0.5.0 未发布开发基线**，N1/N2已合并，本批接入N5现代格式/RAW预览及N6文件编辑首批。本轮不发布新版本；最新公开下载仍为[v0.4.0](https://github.com/Flysoft1337/ModernImageViewer/releases/tag/v0.4.0)。实际验证见[进度记录](docs/planning/progress.md)及交付PR/CI。

## 安装与分发

0.4.0 提供 Windows x64 EXE 安装器与便携 ZIP，面向 Windows 10 22H2 或 Windows 11。两种分发均包含 .NET 10 和实际 native 运行依赖，用户无需另装 .NET。

- 安装器：`ModernImageViewer-0.4.0-win-x64-Setup.exe`。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\ModernImageViewer`，无需管理员权限；提供开始菜单入口、可选桌面快捷方式、升级和卸载。
- 安装时可选注册上述 9 个扩展名的打开方式；安装后在 Windows 默认应用中选择 `Modern Image Viewer`，按需要选择上述扩展名，随后资源管理器双击即可打开。安装器不会自动修改默认应用。
- 便携 ZIP 解压后运行 `ModernImageViewer.App.exe`，需要时在应用设置里注册 `Modern Image Viewer (Portable)`。安装版与便携版使用独立关联身份，可共存；卸载安装版不撤销便携版候选。
- 当前 EXE **未签名**；可信签名发布与 MSIX 继续规划。构建产物和校验清单由 Windows CI 上传，实际安装/卸载验证结果以对应运行记录为准；系统默认选择和 Shell 双击仍需人工验收。

从 Windows 开发环境打包（PowerShell 7）：

```powershell
# 源码构建需 MSVC x64 Build Tools；发布包运行不需要它
pwsh .\scripts\build-raw-native.ps1
pwsh .\scripts\build-installer.ps1
# 已完成 win-x64 self-contained publish 时复用发布输出
pwsh .\scripts\build-installer.ps1 -SkipPublish
```

脚本使用固定版本、校验 SHA-256 的 Inno Setup 编译器。发布输入位于 `artifacts/publish/win-x64`，安装器输出位于 `artifacts/installer/ModernImageViewer-<Version>-win-x64-Setup.exe`（当前源码构建为 0.5.0）。安装、修复、升级与卸载说明见 [Windows 文件关联方案](docs/windows-file-association.md)。

需要安装时，从 [GitHub Releases](https://github.com/Flysoft1337/ModernImageViewer/releases) 下载对应版本的安装器或便携包。需要发布新版本时，在 **Actions → CI → Run workflow** 选择 **master**，勾选 **publish_release**；构建和安装检查通过后，自动创建 `v<Version>` Release，上传 EXE、ZIP 与两份 SHA256。版本来自 `Directory.Build.props`，已发布版本不会覆盖；普通 CI 只上传 artifacts。完整操作与失败恢复见 [GitHub 手动发版](docs/github-releases.md)。

## 语言

目前支持简体中文和英文。首次启动跟随系统显示语言，未支持的语言回退到英文；也可在应用内即时切换，选择会保存在当前用户的本地设置中。

## 浏览与交互

- 安装版与便携版均可在右上角“设置 → 用此应用打开图片”中注册或修复上述格式的打开方式，再进入 Windows 默认应用选择此应用；随后双击图片即可打开。注册可撤销，便携版移动后需重新注册。请使用 self-contained 发布包，开发运行不注册。详见 [双击图片打开应用](docs/windows-file-association.md)。
- 重复启动会把图片交给同一用户、同一会话的已有窗口；无参数启动恢复窗口。请求受理后后续进程退出，不等待图片完整解码。
- 命令行和拖放支持多张图片，按选择顺序浏览、缩略图与播放；去重并略过无效项，每次最多 128 项。混合选择中的文件夹不会递归展开；单个文件夹按保存的排序偏好打开，默认自然名称升序。
- `Ctrl+O` 打开图片，`Ctrl+Shift+O` 打开文件夹；支持拖入文件夹或通过命令行指定文件夹。
- 在资源管理器中复制图片文件后，`Ctrl+V` 或“更多 → 从剪贴板打开”打开，最多128项；多文件保持复制列表顺序，沿用去重/不支持或缺失文件提示。单个文件按同目录浏览，单个文件夹沿用打开文件夹行为；混合列表不展开目录。读取失败保留当前图片，文本框仍使用自身粘贴。
- `Ctrl+V`也支持剪贴板PNG/兼容位图，文件列表优先、PNG透明数据优先于位图。内存来源没有文件路径，不提供目录导航、排序、定位或复制路径；可用`Ctrl+E`编辑并安全另存为PNG/JPEG/WebP。
- `Ctrl+C`复制整图预览（最大2560×1600），包含会话旋转/翻转与PNG透明，不包含界面和局部细化区域。“更多 → 文件 → 复制原尺寸图片”仅在完整像素已加载且不超过64MiB时可用。源单边32768、BGRA等价64MiB、PNG编码32MiB；暂不接受高位深剪贴板输入。[边界与验证](docs/clipboard-images.md)。
- `F6` 开始/暂停循环幻灯片，菜单可选 2/5/10 秒；加载期间和最小化时暂停计时，解码失败时停止播放。`Esc` 停止播放；画布聚焦时也支持空格。
- `← / →` 切换，`Home / End` 跳到目录首尾。更多菜单支持名称、修改时间、文件大小排序及升降序；排序保留当前图与视口，多选图片保持选择顺序，排序偏好跨启动保存。
- “更多 → 查看方向”支持向左/右旋转 90°、水平/垂直翻转与恢复方向；`Ctrl+R` 向右旋转，`Ctrl+Shift+R` 向左旋转。作用于 EXIF 校正后的图像，只改变绘制矩阵，不复制整图像素、不修改原文件；细化保留方向，切换图片重置方向。
- “更多→编辑图片”或`Ctrl+E`打开编辑窗口：拖动/数值裁剪、按比例缩放或解锁拉伸、方向操作，`Ctrl+Z`撤销、`Ctrl+Shift+Z`/`Ctrl+Y`重做、`Ctrl+S`另存为。PNG/WebP保留透明，JPEG白底；JPEG/WebP品质可调，WebP品质100仍是有损；只创建新文件，不覆盖原图或已有目标。关闭丢弃未导出历史，保存后仍可撤销；RAW编辑的是内嵌预览。[预算与边界](docs/raw-modern-editing.md)。
- 滚轮围绕鼠标缩放，拖动平移，双击切换适应窗口与实际大小。
- `0` 适应窗口，`1` 实际像素大小，`+ / -` 缩放。
- `F11` 全屏，`Esc` 退出全屏或收起信息面板；全屏会恢复此前窗口位置与状态及面板选择。闲置 2.5 秒隐藏浮层与光标，鼠标移动或键盘操作唤回；菜单、控件焦点与拖拽期间保持可见。
- `Ctrl+I` 切换信息面板，`Ctrl+T` 切换缩略图带；点击缩略图打开对应图片。
- `Ctrl+Shift+E` 在资源管理器中定位当前文件，也可从“更多 → 文件”进入；文件失踪或 Shell 失败时保留当前图片。定位按需在后台运行，已进入的原生调用可能在取消后完成。
- `F1` 打开快捷键帮助，按打开、导航、画布、播放、窗口和文件分组；帮助窗口支持主题/语言切换，关闭后恢复主窗口焦点。
- 文件夹列表在浏览期间复用；新增、删除或修改图片后，按 `F5` 刷新列表、缩略图、邻图缓存和当前主图。
- 最小窗口为 720×480，窄窗口使用紧凑工具条；信息面板覆盖画布，避免挤窄主图。“更多”菜单保留信息面板、缩略图带、播放与全屏入口。右上角菜单切换主题和语言。“浏览偏好”提供面板与幻灯片间隔选择；正常窗口位置/大小、最大化状态、面板、排序、播放间隔与语言/主题合并保存，兼容旧设置。全屏临时边界不写入配置，恢复按当前显示器工作区和 DPI 校正；启动不会自动打开历史图片或扫描目录。

缩略图带显示当前位置附近最多9张，后台最多2个解码，2MiB/24项LRU；缓存键包括路径、修改时间/长度，属性读取在后台，F5取消旧代次并清理缓存。文件信息包括尺寸、格式、大小、时间和路径；有常见EXIF时显示拍摄参数。WIC与WebP主图/缩略图都纠正EXIF方向，不改源文件；WebP原地置换不另建完整BGRA，RGB ICC转换直接输出sRGB，拍摄参数跨格式读取和完整显示器色彩管理仍待完成。

## 启动与内存

启动只创建必要的依赖与窗口，首次打开图片时才初始化 Skia 画布；不加载后台服务宿主、配置文件监听或命令行配置解析。命令行图片/文件夹在首轮界面布局之后处理。单实例检查先于窗口创建；仅主实例启动后台请求监听，文件关联服务和注册状态读取延迟到打开设置时。

主画布直接使用解码缓冲，不再保留另一份完整 Skia 像素副本。按 `宽 × 高 × 4` 计算，24MP（6000×4000）图片可少一份 96MB（约 91.6MiB）像素副本；这不是进程整体工作集的测量结果。WIC 按需解码，先验证尺寸再分配，全尺寸解码最多并发 1 个；过期排队请求会取消。目录枚举、排序与刷新在后台执行。名称/时间/大小排序在后台建立快照，属性和名称预读后再比较，快速切换只提交最新代次；排序不重新解码当前图或复制像素，仅更新导航与邻图预取。直接打开单张图片时，先显示已解码主图，再后台建立同目录导航，首图不等待完整目录索引；索引期间不提供依赖目录的跳转。打开文件夹仍需先枚举候选文件，多选序列不展开目录。

主图先按最大 2560×1600 包围盒解码，不放大原图；预览像素最多 16,384,000 字节（约 15.6MiB）。小图直接显示完整细节，大图先显示预览，放大超过预览可提供的分辨率或切到实际像素时才请求原尺寸细节。浮层提供“加载细节”和“取消”，细化完成保持缩放和平移；取消、失败或超出预算时保留预览。尺寸信息与 100% 缩放始终按原图尺寸计算，不改写原文件。

主图输出128MiB预算保守预留预览/取消交接、邻图和区域空间；codec在分配前复查真实尺寸/预算，完整细节超过约92.75MiB时保留预览。WIC提供最多2048×2048/16MiB中心区域，160ms去抖、取消/过期释放、保留视口；WebP暂无区域。SVG直接绘制完整目标，暂不开放区域细化，另有1MiB/2048元素/深度32及最多2个透明层、32MiB透明层像素预算，不先栅格化整幅源图。源边长32768/100MP限制仍适用。WIC/native内部工作、绘制surface、临时方向标记和其它资源不全计入输出预算，不能将它当整个进程硬上限。

同目录索引完成后按导航方向预取一张邻图，最多 1280×800、4MiB 像素缓存；延迟 250ms，解码槽忙则跳过。缓存命中直接转移像素所有权，消费前核对路径、修改时间与长度；F5 清空缓存并重载当前图。已开始的 native 解码不保证立即中断，仍需固定 Windows 样本比较切图耗时。

缩略图先按 224×140 目标降采样，不走完整主图像素缓冲。2MiB 限制仅针对缓存中的缩略图像素，不包含正在解码或显示的缩略图、原生 codec 与主画布。

可在同一台 Windows 机器上比较前后两个 Release self-contained 包（PowerShell 7）：

```powershell
pwsh .\scripts\measure-startup.ps1 -AppPath .\artifacts\publish\win-x64\ModernImageViewer.App.exe
# 可选：带一张固定样本图片观察内存
pwsh .\scripts\measure-startup.ps1 -AppPath .\artifacts\publish\win-x64\ModernImageViewer.App.exe -ImagePath .\sample.jpg -OutputPath image-results.json
```

脚本默认记录 3 次可见窗口/输入空闲耗时及观察后的工作集、峰值工作集，不记录图片路径。它用于快速比较，不替代项目计划的冷启动、首帧或 P95 验收；大图在观察结束时可能仍在解码。Windows CI 会对发布包执行一次真实启动检查并上传 `startup-observation`；该单样本反映 CI 机器状态，不代表冷启动或性能提升比例。Linux 云端不报告 Windows 启动速度或实际工作集的百分比提升。

## 环境要求

- 运行安装器/便携版：Windows 10 22H2 或 Windows 11，x64；无需另装 .NET。
- 开发构建：.NET 10 SDK；安装器编译需要 Windows 与 PowerShell 7。

## 构建

```powershell
dotnet restore .\ModernImageViewer.slnx
dotnet build .\ModernImageViewer.slnx --configuration Release --no-restore
```

## 运行

```powershell
dotnet run --project .\src\ModernImageViewer.App\ModernImageViewer.App.csproj
```

## 测试

```powershell
dotnet test .\ModernImageViewer.slnx --configuration Release
```

## 基准

```powershell
dotnet run --project .\benchmarks\ModernImageViewer.Benchmarks\ModernImageViewer.Benchmarks.csproj --configuration Release
```

## 后续开发

本轮M0验证与未关闭风险见[M0技术验证与格式基线](docs/m0-validation.md)：记录100MP单进程观察、实际现代codec样本、ICC原型、固定许可证与native清单；AVIF/HEIF、RAW、显示器profile和MSIX仍待完成，不将局部证据冒充整个M0退出。

应用端外部打开、窗口复用和便携关联已实现，新增当前用户 EXE 安装路径；可信签名、MSIX 和 Windows Shell 人工验收继续推进。0.3.0 增加静态格式与缩略图字节缓存、单图先显示后索引；0.4.0 增加主图预览、按需细化和手动 Release 发布流程。PR #21 已合并区域细节、邻图预取与紧凑/全屏交互。N1 排序、文件定位和快捷键帮助已合并；本轮开发代码补齐 N2 会话旋转/翻转、区域逆变换和偏好保存，N3 文件/位图打开与复制图片已接入，N4 帧能力留后续，详细完成条件见 [下一批功能实施方案](docs/feature-expansion-plan.md)。仍需补齐以下能力：

- **近期 P1：** 窗口/DPI 自适应解码、完整分块缓存、WebP 区域与全管线字节预算；剪贴板外部应用互通实机验收；真实混合 DPI 多屏、高 DPI 人工验收。
- **随后 P2：** GIF/WebP 动画与 TIFF 多页；HEIF/AVIF、RAW、完整SVG；非破坏性裁剪/尺寸调整、撤销/重做与另存为；ICC 色彩管理、高位深与签名发布。

性能目标仍需固定 Windows 机器实测，当前不承诺整进程内存上限或速度提升百分比。

- [下一批功能实施方案](docs/feature-expansion-plan.md)：N1–N6 的交付顺序、代码接入点、UI 与资源边界。
- [后续迭代路线图](docs/next-iteration-roadmap.md)：当前功能缺口、优先级、UI 和性能优化、完成标准。
- [Windows 文件关联方案](docs/windows-file-association.md)：安装版/便携版、用户默认应用选择、重复激活和验收步骤。
- [解码器与格式支持](docs/decoder-support.md)：实际启用的 codec、扩展名、像素与尺寸限制、未支持能力。
- [项目计划](docs/project-plan.md)：完整产品范围、架构、里程碑和性能目标。
- [进度日志](docs/planning/progress.md)：已完成的迭代记录。

路线图中的待办与目标不代表当前版本已支持或达标。

## License

[MIT](LICENSE)
