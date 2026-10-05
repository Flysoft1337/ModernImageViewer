# Modern Image Viewer

面向 Windows 10/11 的本地图片查看器，目标是快速打开、流畅浏览、完成常用编辑，并确保原图安全。

当前正在推进 **M1 图片浏览体验**，M0 的扩展解码器与性能验证仍待完成。已接入 WIC 解码和 SkiaSharp 画布，支持 JPEG、PNG、BMP、GIF、TIFF、ICO 和 WebP 静态图片打开、拖放、同目录自然排序浏览、文件夹打开、循环幻灯片、邻近图片缩略图、适应窗口、实际像素大小、缩放、平移和全屏。采用深色画廊界面，支持浅色与跟随系统主题，文件信息面板默认收起，支持常见 JPEG EXIF 信息和拍摄方向自动纠正。libvips、LibRaw、编辑和完整相邻图片预取尚未实现。

**当前格式：JPEG、PNG、BMP、GIF、TIFF、ICO、WebP，共 9 个扩展名（`.jpg`、`.jpeg`、`.png`、`.bmp`、`.gif`、`.tif`、`.tiff`、`.ico`、`.webp`）。** GIF/WebP 仅显示首帧，TIFF 仅显示第一页，ICO 仅显示第一个图标帧；动画播放、多页/多尺寸选择、HEIF/HEIC、AVIF、RAW 和 SVG 尚未实现。WIC 解码前 6 类容器，SkiaSharp 解码 WebP 并绘制所有格式。详细像素、尺寸和色彩边界见 [解码器与格式支持](docs/decoder-support.md)。

当前 master 使用 **0.5.0 未发布开发基线**，继续完善区域细节、单张邻图预取、紧凑布局与全屏交互。本轮不发布新版本；最新公开下载仍为 [v0.4.0](https://github.com/Flysoft1337/ModernImageViewer/releases/tag/v0.4.0)。下面浏览与性能说明以当前源码为准。

## 安装与分发

0.4.0 提供 Windows x64 EXE 安装器与便携 ZIP，面向 Windows 10 22H2 或 Windows 11。两种分发均包含 .NET 10 和实际 native 运行依赖，用户无需另装 .NET。

- 安装器：`ModernImageViewer-0.4.0-win-x64-Setup.exe`。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\ModernImageViewer`，无需管理员权限；提供开始菜单入口、可选桌面快捷方式、升级和卸载。
- 安装时可选注册上述 9 个扩展名的打开方式；安装后在 Windows 默认应用中选择 `Modern Image Viewer`，按需要选择上述扩展名，随后资源管理器双击即可打开。安装器不会自动修改默认应用。
- 便携 ZIP 解压后运行 `ModernImageViewer.App.exe`，需要时在应用设置里注册 `Modern Image Viewer (Portable)`。安装版与便携版使用独立关联身份，可共存；卸载安装版不撤销便携版候选。
- 当前 EXE **未签名**；可信签名发布与 MSIX 继续规划。构建产物和校验清单由 Windows CI 上传，实际安装/卸载验证结果以对应运行记录为准；系统默认选择和 Shell 双击仍需人工验收。

从 Windows 开发环境打包（PowerShell 7）：

```powershell
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
- 命令行和拖放支持多张图片，按选择顺序浏览、缩略图与播放；去重并略过无效项，每次最多 128 项。混合选择中的文件夹不会递归展开；单个文件夹仍按自然名称排序打开。
- `Ctrl+O` 打开图片，`Ctrl+Shift+O` 打开文件夹；支持拖入文件夹或通过命令行指定文件夹。
- `F6` 开始/暂停循环幻灯片，菜单可选 2/5/10 秒；加载期间和最小化时暂停计时，解码失败时停止播放。`Esc` 停止播放；画布聚焦时也支持空格。
- `← / →` 切换，`Home / End` 跳到目录首尾。
- 滚轮围绕鼠标缩放，拖动平移，双击切换适应窗口与实际大小。
- `0` 适应窗口，`1` 实际像素大小，`+ / -` 缩放。
- `F11` 全屏，`Esc` 退出全屏或收起信息面板；全屏会恢复此前窗口位置与状态及面板选择。闲置 2.5 秒隐藏浮层与光标，鼠标移动或键盘操作唤回；菜单、控件焦点与拖拽期间保持可见。
- `Ctrl+I` 切换信息面板，`Ctrl+T` 切换缩略图带；点击缩略图打开对应图片。
- 文件夹列表在浏览期间复用；新增、删除或修改图片后，按 `F5` 刷新列表、缩略图、邻图缓存和当前主图。
- 最小窗口为 720×480，窄窗口使用紧凑工具条；信息面板覆盖画布，避免挤窄主图。“更多”菜单保留信息面板、缩略图带、播放与全屏入口。右上角菜单切换主题和语言。主题和语言选择均会持久保存。

缩略图带显示当前位置附近最多 9 张图片，后台同时最多解码 2 张，缩略图像素缓存受 2MiB 和 24 项双重上限约束，按最近使用顺序逐出。缓存键包含路径、修改时间和文件长度；文件属性读取也在后台执行。F5 清理缓存并让已在途的旧结果失效。文件信息包括尺寸、格式、大小、修改时间和路径；存在 EXIF 时还会显示相机、镜头、拍摄时间、ISO、快门、光圈和焦距。WIC 格式的主图及缩略图会遵循可读取的 EXIF Orientation（包含旋转和镜像），不改写原文件；WebP 元数据与方向尚未统一读取。

## 启动与内存

启动只创建必要的依赖与窗口，首次打开图片时才初始化 Skia 画布；不加载后台服务宿主、配置文件监听或命令行配置解析。命令行图片/文件夹在首轮界面布局之后处理。单实例检查先于窗口创建；仅主实例启动后台请求监听，文件关联服务和注册状态读取延迟到打开设置时。

主画布直接使用解码缓冲，不再保留另一份完整 Skia 像素副本。按 `宽 × 高 × 4` 计算，24MP（6000×4000）图片可少一份 96MB（约 91.6MiB）像素副本；这不是进程整体工作集的测量结果。WIC 按需解码，先验证尺寸再分配，全尺寸解码最多并发 1 个；过期排队请求会取消。目录枚举、排序与刷新在后台执行。直接打开单张图片时，先显示已解码主图，再后台建立同目录导航，首图不等待完整目录索引；索引期间不提供依赖目录的跳转。打开文件夹仍需先枚举候选文件，多选序列不展开目录。

主图先按最大 2560×1600 包围盒解码，不放大原图；预览像素最多 16,384,000 字节（约 15.6MiB）。小图直接显示完整细节，大图先显示预览，放大超过预览可提供的分辨率或切到实际像素时才请求原尺寸细节。浮层提供“加载细节”和“取消”，细化完成保持缩放和平移；取消、失败或超出预算时保留预览。尺寸信息与 100% 缩放始终按原图尺寸计算，不改写原文件。

主图输出采用 128MiB 预算，原尺寸细化保守预留两份最大预览用于跨图与取消交接，以及 4MiB 邻图预览缓存；真实 codec 在分配输出前重新检查目标尺寸和预算。原尺寸细节超过约 92.75MiB（约 24.31MP）时不分配完整图，WIC 格式可请求当前中心视区最多 2048×2048、16MiB 的原尺寸区域细节；平移/缩放约 160ms 去抖，旧任务取消，过期像素释放，保留当前视口。区域遵循 EXIF 坐标，绘制共享缓冲并避免透明像素重复叠加。最多保留一块区域，不是完整分块系统，不保证整个 4K/高 DPI 视区同时清晰；WebP 暂无区域路径，超预算时保留预览。WIC 内部可能解码或缓存更多像素，方向校正也有有界临时输出。源文件原有的 32,768 单边与 100MP 限制仍有效，预览不会绕过安全尺寸检查。这是输出像素预算，不是整个进程、native codec、渲染 surface 或尚未回收内存的上限。

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

应用端外部打开、窗口复用和便携关联已实现，新增当前用户 EXE 安装路径；可信签名、MSIX 和 Windows Shell 人工验收继续推进。0.3.0 增加静态格式与缩略图字节缓存、单图先显示后索引；0.4.0 增加主图预览、按需细化和手动 Release 发布流程。仍需补齐以下能力：

- **近期 P1：** 窗口/DPI 自适应解码、完整分块缓存、WebP 区域与全管线字节预算；排序、剪贴板打开/复制图片、资源管理器定位、会话旋转/翻转；高 DPI 人工验收及窗口/布局持久化。
- **随后 P2：** GIF/WebP 动画与 TIFF 多页；HEIF/AVIF、RAW、SVG；非破坏性裁剪/尺寸调整、撤销/重做与另存为；ICC 色彩管理、高位深与签名发布。

性能目标仍需固定 Windows 机器实测，当前不承诺整进程内存上限或速度提升百分比。

- [后续迭代路线图](docs/next-iteration-roadmap.md)：当前功能缺口、优先级、UI 和性能优化、完成标准。
- [Windows 文件关联方案](docs/windows-file-association.md)：安装版/便携版、用户默认应用选择、重复激活和验收步骤。
- [解码器与格式支持](docs/decoder-support.md)：实际启用的 codec、扩展名、像素与尺寸限制、未支持能力。
- [项目计划](docs/project-plan.md)：完整产品范围、架构、里程碑和性能目标。
- [进度日志](docs/planning/progress.md)：已完成的迭代记录。

路线图中的待办与目标不代表当前版本已支持或达标。

## License

[MIT](LICENSE)
