# 解码器与图片格式支持

> 核对日期：2026-10-05；最新公开 Release 为 v0.4.0，当前未发布开发基线为 0.5.0。本轮仅开发、验证和合并，不触发新 Release；下文标注的区域细化、相邻预取和 UI 改进属于开发代码，不包含在现有 v0.4.0 下载包中。以下支持范围按实际接入的 codec 记录，不将项目目标或系统另装的 codec 当成应用能力；构建、固定样本和安装结果见本轮交付 PR/CI。

## 实际解码与绘制路径

JPEG、PNG、BMP、GIF、TIFF、ICO和JPEG XR使用Windows Imaging Component（WIC），通过 WPF `BitmapDecoder` 读取实际容器后取第一帧。WebP 使用已有 SkiaSharp 4.153.1 的 `SKCodec`，核对实际容器后解码首帧；不要求用户另装系统 WebP 扩展。所有格式均输出预乘 Alpha 的 8-bit BGRA 供 SkiaSharp 画布绘制。WIC 路径读取可用 EXIF 后纠正方向；WebP已纠正EXIF方向并直接输出sRGB，拍摄信息和完整显示器色彩管线未统一。受限SVG通过固定Svg.Skia直接完整目标绘制。所有路径不修改原文件。

主图在后台解码，默认先生成最大 2560×1600 包围盒的降采样预览，不放大小图；预览与按需全尺寸细化共用一个主图解码槽，不在每次打开后自动解码完整图片。预览保留经过 EXIF 方向校正的原始尺寸，画布按原图坐标绘制，因此显示尺寸、缩放比例和实际像素模式不以预览尺寸冒充原图。当画布需要的像素密度超过预览分辨率时，实际大小、双击、滚轮/按钮缩放或放大窗口会按需触发细化，也可使用“加载细节”入口；输出预算内允许完整细化，开发基线另外为 WIC 格式提供有界中心视区细化。细化可取消，切图后过期结果不能覆盖新图，失败或超预算仍保留预览与导航。排队和结果提交支持取消，已经执行的原生 codec 调用不保证立即中断。缩略图复用格式识别和方向处理，按 224×140 目标先降采样，不通过主图完整像素缓冲生成。SkiaSharp 本轮既负责 WebP 解码，也负责全部格式的绘制。libvips、LibRaw 和 MetadataExtractor 尚未接入，元数据可用程度取决于对应 codec；`Metadata` 工程存在不代表已有独立元数据引擎。

## 未发布开发基线的区域细化与相邻预取

- WIC 的 JPEG、PNG、BMP、GIF 首帧、TIFF 首页和 ICO 首帧支持按原图坐标请求区域；EXIF Orientation 1–8 对区域坐标与像素方向一致处理。每次仅生成中心视区内最多 2048×2048 的清晰区域，并叠加在现有预览上，输出最多 16MiB。平移或缩放稳定约 160ms 后请求新区域，最多保留一个当前区域，取消旧请求并丢弃过期结果。
- WIC 只向应用复制请求 ROI 的 BGRA，不先分配整幅托管 BGRA；部分 codec 仍可能在内部解码整图，不能由 ROI 输出大小推断 native 峰值已降低。这是单区域细化，不是完整分块缓存；超出该区域的可见部分继续显示预览，不承诺 4K 或高 DPI 视区一次全部清晰。
- WebP 暂无区域解码；输出预算内的完整细化仍可用，超预算保持预览。基础源尺寸限制继续适用，本轮未扩大源尺寸支持范围。
- 按导航方向在后台最多预取一张邻图的 1280×800 预览，像素缓存上限 4MiB，不缓存邻图的完整分辨率。使用前复核路径、修改时间与长度；F5 清理缓存，取消和过期结果释放其像素所有权。主图解码槽忙时跳过预取，不排队或自动重试；已经开始的原生解码不保证立即中断。属性相同但内容改变仍需 F5 兜底。

## 当前能力表

当前开发代码支持会话查看方向：90° 旋转、水平/垂直翻转与恢复，作用于 EXIF 校正后的原图，预览和细节区域使用同一绘制矩阵。可见视区四角逆变换回原图后生成有界 ROI；旋转/镜像不新增整图像素副本，不改文件。90°/270° 交换显示尺寸，实际像素保持 DPI 密度，缩放锚点和平移使用显示坐标。会话查看方向切图重置，缩略图继续显示源方向；文件编辑窗口复用方向矩阵，提供裁剪/尺寸/撤销与PNG/JPEG新文件导出，见[本批验证](raw-modern-editing.md)。

| 格式 | 扩展名 | 主图 / 缩略图 / 浏览 / 候选关联 | 实际解码器 | 能力边界 |
|---|---|---|---|---|
| JPEG | `.jpg`、`.jpeg` | 支持 | WIC | 静态；可用的常见 EXIF 与 8 种 Orientation；不承诺 CMYK/广色域色彩准确性 |
| PNG | `.png` | 支持 | WIC | 静态首帧、透明像素；16-bit 输入转为 8-bit 通道；不提供 APNG 播放 |
| BMP | `.bmp` | 支持 | WIC | 静态；编码变体能否读取取决于 WIC，不承诺所有历史变体 |
| GIF | `.gif` | 支持 | WIC | 仅首帧；没有动画播放、暂停或逐帧查看 |
| TIFF | `.tif`、`.tiff` | 支持 | WIC | 仅第一页；没有多页浏览；高位深转为 8-bit 通道 |
| ICO | `.ico` | 支持 | WIC | 仅第一个图标帧；没有多尺寸/多帧选择，不承诺总能自动选最大尺寸 |
| WebP | `.webp` | 支持 | SkiaSharp | 静态或动画文件的首帧、透明像素；没有动画播放 |
| JPEG XR / HD Photo | `.jxr`、`.wdp`、`.hdp` | 开发代码支持 | WIC | 主图、预览、缩略图与ROI；输出BGRA8，不保证HDR/高位深准确输出 |
| 受限 SVG | `.svg` | 开发代码支持 | Svg.Skia 5.2.3 + Skia | 基础形状/路径/变换/本地渐变，直接完整目标栅格化；文字/图片/脚本/外部引用/use/clip/mask/filter/动画不支持 |
| HEIF/HEIC、AVIF | `.heif`、`.heic`、`.avif` | 开发代码支持 | Magick.NET-Q8-x64 14.17.2 | 静态首图；容器方向一次纠正，RGB ICC→sRGB；源32MP/32768边/文件128MiB，序列与ROI不支持，HDR不保证 |
| RAW 内嵌预览 | `.dng/.cr2/.cr3/.nef/.arw/.raf/.rw2/.orf/.pef` | 开发代码支持 | 固定LibRaw0.22.2 /MT桥接 | JPEG/RGB8/RGB16预览，独立tflip/EXIF一次纠正；尺寸是预览，输入256MiB/预览32MiB；无可读预览明确反馈，不显影，无ROI |

统一格式目录驱动文件选择器、目录/多选过滤、关联注册和安装器的扩展名清单，扩展名不区分大小写。解码器另外核对真实容器：把未支持的格式改名为 `.jpg` 不会使其获得支持。已有支持容器使用其他扩展名也不属于完整浏览/关联流程的支持范围，建议保留正确扩展名。

支持容器不代表所有损坏文件、编码变体或超限尺寸都可打开。损坏、不支持、权限拒绝、文件消失与尺寸超限由打开管线反馈；新请求全部失败时保留当前图片和浏览会话。静态首帧、动画、多页、导出、色彩管理分别是独立能力。

## 像素、尺寸与内存边界

- 现代格式在Ping后、Read前验证源32MP，native全图后缩放；现代单槽/192MiB像素cache/禁disk和外部delegate，逐行导出。RAW单槽且只open/unpack_thumb，输入256MiB/预览32MiB/默认单次分配64MiB/profile8MiB。均不是进程硬上限，现代源限内也可能资源拒绝。完整边界见[本批验证](raw-modern-editing.md)。
- 主图输出为四个 8-bit 通道的预乘 BGRA，每像素 4 字节；不是保留 16-bit 原始精度的处理管线。
- 原始图片单边不超过 32,768 像素、总像素不超过 100,000,000（100MP）；基础解码器的单图输出上限仍为 400,000,000 字节（约 381.5MiB）。预览入口也先检查原始尺寸，不能通过降采样绕过源尺寸限制。
- 默认预览最大 2560×1600，32-bit 像素缓冲最多 16,384,000 字节（约 15.6MiB），实际值按宽高比缩小；这是输出数据量上限，不是实测工作集。原图不大于预览目标时可直接显示完整像素。
- 浏览管线保留 128MiB（134,217,728 字节）主图输出像素预算，在请求全尺寸细化前为当前/下一张预览、晚完成任务、邻图预取和区域像素/方向变换临时缓冲保守预留空间；具体完整输出上限按源码预算公式计算，并受基础解码器单图限制约束。细化重新读取真实文件头，在分配前检查上限，避免同路径文件替换后绕过预算。完整细化超预算时，WIC 可继续请求有界区域，WebP 保持降采样预览，不无条件分配完整像素。
- 128MiB 不是整个进程或全管线的内存预算：不包含 WIC/Skia native 工作区、绘制 surface、缩略图和其他应用资源。当前开发基线仅接入 WIC 单区域输出和一张邻图预览预取；完整分块缓存、WebP 区域解码和超出原始尺寸限制的打开路径仍未实现。
- 缩略图目标是 224×140 包围盒，不放大原图，最多并发 2 个任务；也先验证原始尺寸安全，不用缩略图入口绕过单图尺寸/像素限制。解码器按可用的原生缩放能力生成较小像素缓冲，但原生 codec 的内部工作内存不计入缩略图缓存上限。
- 缩略图缓存按实际保留的像素字节计费，2MiB 与 24 项双重上限，采用 LRU 逐出。224×140 的 32-bit 缩略图最多 125,440 字节，预算可容纳最多 16 张满尺寸缩略图；较窄图片可缓存更多，但仍不超过 24 项。键包含路径、修改时间和文件长度；属性读取与解码均在后台进行。该上限不包含已显示或在途缩略图、WIC/Skia native 分配及其他应用资源。
- F5 清理缓存并增加代次，使刷新前的在途结果不能重新进入新缓存；本轮没有持续文件监听。同路径内容变化但修改时间与长度均不变时，仍需 F5 强制失效。
- WIC 路径可读的 EXIF Orientation 1–8 用于主图和缩略图方向；WebP已应用方向，拍摄信息未统一。缺失或不适用元数据不阻止打开。

当前仅补WebP嵌入RGB ICC→sRGB输出，跨格式应用级ICC管线、显示器profile切换、HDR/广色域输出或高位深编辑仍不保证。Windows/Skia codec 自身的格式转换不等于完整色彩管理。常见 JPEG EXIF 展示包括相机、镜头、拍摄时间、ISO、快门、光圈和焦距；不保证各格式的所有元数据都能读取。

## 首图响应与目录索引

直接打开单张图片，先完成解码并显示，再后台建立同目录自然排序导航。索引期间禁用前后/首尾导航与幻灯片，状态提示正在扫描。第一张可辨认画面不等待完整目录扫描；索引失败或取消仍保留已经显示的图片，索引结果按请求版本提交，过期结果不能覆盖后来打开的会话。多选输入沿用选择顺序，不展开每张图所在目录；打开文件夹仍需先枚举候选文件，因此超大目录或慢网络目录仍可能影响文件夹首图。

0.4.0 将首图改为有界降采样预览，再按需请求完整细节；预览与完整图提交均受请求版本保护。细化时保留当前缩放与平移，取消、失败或超预算不丢弃已显示的预览。固定 Windows 机器上的首帧、工作集和长期浏览数据仍需另行测量，不宣称速度百分比或整进程峰值已达目标。

## 安装包的依赖与格式声明

公开版 0.4.0 EXE 安装器面向 Windows 10 22H2 / Windows 11 x64，包含 self-contained .NET 10、WPF 和固定版本的 SkiaSharp native 依赖。WIC 来自 Windows，不需要另装 .NET、WebP 扩展、libvips 或 LibRaw。

安装版与便携版使用独立身份，开发代码提供25扩展名，v0.4.0下载包仍是原9扩展名；0.4.0 沿用 0.2.0 的安装身份和目录。旧版升级后修复候选注册可补齐新格式，卸载只清理属于当前安装的候选。应用与安装器均不改写系统默认应用；用户按需要在 Windows 中选择各扩展名的默认程序。

安装、修复与卸载详见 [README](../README.md#安装与分发)和 [Windows 文件关联方案](windows-file-association.md)。Windows CI 自动化安装/激活与真实默认选择/Shell 人工验收分别记录，不能互相代替。当前 EXE 未签名，签名与 MSIX 另行推进。按需构建并发布到 GitHub Release 的入口与限制见 [GitHub 发版说明](github-releases.md)；普通 CI 继续只提供 artifacts。

## 后续格式交付规则

本轮M0完整证据与资源/许可边界见[M0验证记录](m0-validation.md)。开发代码由原9扩展名增至25（JPEG XR/SVG、AVIF/HEIF与九种RAW预览扩展）；公开v0.4.0的9扩展名声明保持不变。WebP已纠正EXIF 1–8并用同一次codec输出转换嵌入RGB ICC至sRGB，不能据此声称WIC/显示器profile/HDR全完成。SVG最多1MiB输入/2048元素/深度32/65536几何项，最多2层透明合成与32MiB辅助层像素；弧旗标需独立0/1，坏几何不静默截断。预算不代表全部native或进程上限。

增加格式前确认实际 codec、native 依赖、许可证、损坏输入与内存边界，再同步主图、缩略图、选择器、导航、关联声明和本表。动画、TIFF 多页、ICO 多尺寸、导出、高位深、ICC 分别验收，不把静态首帧标成完整播放或编辑支持。

本轮开发 CI 可生成 0.5.0 artifacts 用于验证，不代表 GitHub 已发布 v0.5.0；最新公开下载仍为 v0.4.0。需要新 Release 时按发版说明显式触发，本轮不触发发布。

代码核对入口：

- [`DependencyInjection.cs`](../src/ModernImageViewer.Codecs/DependencyInjection.cs) 与 [`ImageDecoder.cs`](../src/ModernImageViewer.Codecs/ImageDecoder.cs)：组合解码器与 WIC/WebP 分派。
- [`SupportedImageFormats.cs`](../src/ModernImageViewer.Application/Images/SupportedImageFormats.cs)：统一格式和扩展名目录。
- [`WicImageDecoder.cs`](../src/ModernImageViewer.Codecs/Wic/WicImageDecoder.cs)：WIC 容器、首帧、格式转换与尺寸检查。
- [`ImageDecodeLimits.cs`](../src/ModernImageViewer.Imaging/ImageDecodeLimits.cs)：主图单图默认限制。
- [`NeighborPreviewCache.cs`](../src/ModernImageViewer.Application/Images/NeighborPreviewCache.cs)：单张邻图预览、文件属性复核与所有权转移。
- [`RegionOrientation.cs`](../src/ModernImageViewer.Codecs/Wic/RegionOrientation.cs)：WIC 区域坐标与 EXIF 方向变换。
- [`ThumbnailImage.cs`](../src/ModernImageViewer.UI/Controls/ThumbnailImage.cs)：尺寸目标、后台加载、并发与缓存。
- [`ImageBrowseSession.cs`](../src/ModernImageViewer.Application/Browsing/ImageBrowseSession.cs) 与 [`ImageOpenCoordinator.cs`](../src/ModernImageViewer.Application/Images/ImageOpenCoordinator.cs)：目录索引、打开提交与取消。
