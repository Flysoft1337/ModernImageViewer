# 解码器与图片格式支持

> 最新状态（2026-10-06）：GIF/WebP动画与TIFF分页已通过固定像素样本和Windows CI，569通过/1本机RAW样本跳过，实际边界见[验收记录](animation-multipage-validation.md)。当前源码0.6.0未发布，最新公开下载仍v0.4.0，本轮不触发Release。固定机静态性能、混合DPI/跨设备长测与WebP背景提示兼容仍待补，不宣称全部变体或性能无退化。

## 实际解码与绘制路径

JPEG、PNG、BMP、单页 TIFF、ICO 和 JPEG XR 沿用 WPF/WIC 静态路径；GIF 代表帧和 GIF/WebP 动画使用随包 SkiaSharp 4.153.1 的 `SKCodec`。WebP 静态路径也使用 Skia；多页 TIFF 使用最小系统 WIC COM adapter，在专有串行线程持有 decoder 和只读流，按页取得 frame/scaler/converter 并显式释放，不靠 WPF finalizer，也不每页重开容器。无需新增 native 包或系统 WebP 扩展。所有显示输出均为预乘 Alpha 的 8-bit BGRA；受限 SVG 按目标栅格化，所有路径不修改原文件。

普通静态图片、单帧 GIF、静态 WebP 和单页 TIFF 不建立活动帧会话、不显示帧控件、不启动动画时钟。缩略图和邻图缓存只保存代表帧/首页。动画时钟与 `F6` 文件幻灯片独立；内部帧/页变化不改变目录位置或重置文件停留时间，也不等待动画完整循环。GIF/WebP 支持播放、暂停、重播、定位与有限/无限循环，用户定位暂停播放；不可见、失活、最小化与卸载暂停呈现，恢复不解除用户暂停。TIFF 页切换重置页面视口身份并清除旧页区域，同页升级保留视口。

开发版主图按可见画布DIP乘各轴DPI生成有界预览，4096单边/32MiB BGRA，未布局回退2560×1600，不放大原图；窗口350ms稳定后升级，15%变化阈值复用旧像素，邻图首次显示按实际目标补足。旧图在新预览或细节完成前继续显示。主图/完整/区域共用一个主槽，打开优先于排队细化；前台忙时不新启低优先任务，运行native必须真实返回才归还槽。Fit只补首屏，不自动读取完整像素；实际大小或放大按需细化。源坐标、DIP和物理像素明确区分，细化保留视口，新来源重置。F5开始即失效各请求代次，解码前后和细化提交核对文件戳，过期结果释放。缩略图仍224×140降采样/最多两槽。RAW已接固定LibRaw内嵌预览；libvips和独立MetadataExtractor尚未接入。详见[浏览收口记录](browsing-core-closeout.md)。

## 未发布开发基线的区域细化与相邻预取

- 静态 WIC 支持格式可按原图坐标请求区域；多页 TIFF 区域入口必须携带当前页索引，按该页尺寸和 Orientation 1–8 处理，不能回读首页。每次最多2048×2048/16MiB，约160ms去抖，保留一个当前区域，取消旧请求并丢弃过期结果。单帧 GIF 沿用静态 WIC 区域入口；活动 GIF/WebP 动画禁用区域覆盖，WebP 静态路径也没有区域入口。
- WIC 只向应用复制请求 ROI 的 BGRA，不先分配整幅托管 BGRA；部分 codec 仍可能在内部解码整图，不能由 ROI 输出大小推断 native 峰值已降低。这是单区域细化，不是完整分块缓存；超出该区域的可见部分继续显示预览，不承诺 4K 或高 DPI 视区一次全部清晰。
- WebP 暂无区域解码；输出预算内的完整细化仍可用，超预算保持预览。基础源尺寸限制继续适用，本轮未扩大源尺寸支持范围。
- 按导航方向在后台最多预取一张邻图的 1280×800 预览，像素缓存上限 4MiB，不缓存邻图的完整分辨率。使用前复核路径、修改时间与长度；F5 清理缓存，取消和过期结果释放其像素所有权。主图解码槽忙时跳过预取，不排队或自动重试；已经开始的原生解码不保证立即中断。属性相同但内容改变仍需 F5 兜底。

## 当前能力表

当前开发代码支持会话查看方向：90° 旋转、水平/垂直翻转与恢复，作用于 EXIF 校正后的原图，预览和细节区域使用同一绘制矩阵。可见视区四角逆变换回原图后生成有界 ROI；旋转/镜像不新增整图像素副本，不改文件。90°/270° 交换显示尺寸，实际像素保持 DPI 密度，缩放锚点和平移使用显示坐标。会话查看方向切图重置，缩略图继续显示源方向；文件编辑窗口复用方向矩阵，提供自由/比例裁剪、尺寸/撤销与PNG/JPEG/WebP新文件导出，见[编辑补全](editing-improvements.md)；首批验收见[格式与编辑记录](raw-modern-editing.md)。

| 格式 | 扩展名 | 主图 / 缩略图 / 浏览 / 候选关联 | 实际解码器 | 能力边界 |
|---|---|---|---|---|
| JPEG | `.jpg`、`.jpeg` | 支持 | WIC | 静态；可用的常见 EXIF 与 8 种 Orientation；不承诺 CMYK/广色域色彩准确性 |
| PNG | `.png` | 支持 | WIC | 静态首帧、透明像素；16-bit 输入转为 8-bit 通道；不提供 APNG 播放 |
| BMP | `.bmp` | 支持 | WIC | 静态；编码变体能否读取取决于 WIC，不承诺所有历史变体 |
| GIF | `.gif` | 静态/动画固定样本与CI通过 | SkiaSharp SKCodec；单帧区域用WIC | 代表帧及动画；透明/局部/disposal依赖合成；播放、暂停、重播、定位和循环 |
| TIFF | `.tif`、`.tiff` | 静态/分页固定样本与CI通过 | 单页 WPF/WIC；多页系统 WIC COM | 页数/定位、异尺寸和逐页方向；当前页预览/完整/区域读取；高位深转为8-bit |
| ICO | `.ico` | 支持 | WIC | 仅第一个图标帧；没有多尺寸/多帧选择，不承诺总能自动选最大尺寸 |
| WebP | `.webp` | 静态/动画固定样本与CI通过 | SkiaSharp SKCodec | 静态/动画、Alpha、Source/Over 与依赖帧合成；播放、暂停、重播、定位和循环；无ROI；ANIM背景颜色未独立修正 |
| JPEG XR / HD Photo | `.jxr`、`.wdp`、`.hdp` | 开发代码支持 | WIC | 主图、预览、缩略图与ROI；输出BGRA8，不保证HDR/高位深准确输出 |
| 受限 SVG | `.svg` | 开发代码支持 | Svg.Skia 5.2.3 + Skia | 基础形状/路径/变换/本地渐变，直接完整目标栅格化；文字/图片/脚本/外部引用/use/clip/mask/filter/动画不支持 |
| HEIF/HEIC、AVIF | `.heif`、`.heic`、`.avif` | 开发代码支持 | Magick.NET-Q8-x64 14.17.2 | 静态首图；容器方向一次纠正，RGB ICC→sRGB；源32MP/32768边/文件128MiB，序列与ROI不支持，HDR不保证 |
| RAW 内嵌预览 | `.dng/.cr2/.cr3/.nef/.arw/.raf/.rw2/.orf/.pef` | 开发代码支持 | 固定LibRaw0.22.2 /MT桥接 | JPEG/RGB8/RGB16预览，独立tflip/EXIF一次纠正；尺寸是预览，输入256MiB/预览32MiB；无可读预览明确反馈，不显影，无ROI |

统一格式目录驱动文件选择器、目录/多选过滤、关联注册和安装器的扩展名清单，扩展名不区分大小写。解码器另外核对真实容器：把未支持的格式改名为 `.jpg` 不会使其获得支持。已有支持容器使用其他扩展名也不属于完整浏览/关联流程的支持范围，建议保留正确扩展名。

## 当前能力矩阵

此表描述已验证的当前代码，支持不代表所有变体都可打开。WebP当前Skia对ANIM背景提示按透明背景处理，固定样本已记录该语义，不承诺全部背景颜色变体完整兼容。ICC指嵌入profile处理，未实现显示器profile切换或HDR管线。EXIF字段展示与方向纠正是不同能力。Edit指进入已有静态编辑器并安全导出PNG/JPEG/WebP，不代表原格式回写。

| 格式/路径 | Static 静态 | Animation 动画 | Pages 多页 | Transparency 透明 | ICC | EXIF | Region 区域 | Edit 编辑 |
|---|---|---|---|---|---|---|---|---|
| JPEG | 支持 | 无 | 无 | 无 | 浏览未显式转换；编辑可用RGB→sRGB | 可读常见字段，方向1–8 | WIC ROI | 既有静态编辑 |
| PNG | 支持 | 无；不播放APNG | 无 | Alpha | 浏览未显式转换；编辑可用RGB→sRGB | WIC可读字段/方向，非全部元数据 | WIC ROI | 既有静态编辑 |
| BMP | 支持的WIC变体 | 无 | 无 | 依变体 | 浏览未显式转换 | 仅WIC可读元数据 | WIC ROI | 既有静态编辑 |
| 单帧GIF/代表帧 | 支持 | 代表帧不播放 | 无 | 支持 | SKCodec输出sRGB；嵌入ICC不承诺 | 不读取摄影字段 | 单帧静态WIC入口 | 单帧可编辑 |
| GIF动画 | 代表帧用于缓存/超限反馈 | 播放/暂停/重播/定位/循环，codec合成 | 无 | 帧透明、局部与disposal合成 | SKCodec输出sRGB；嵌入ICC不承诺 | codec方向；无摄影字段 | 无 | 禁用 |
| 单页TIFF | 支持 | 无 | 单页不建立会话 | 依WIC变体 | 浏览未显式转换；编辑可用RGB→sRGB | WIC可读字段、方向1–8 | WIC ROI | 单页可编辑 |
| 多页TIFF | 代表首页用于缓存 | 无 | 随机按页读取，页尺寸独立 | 依WIC变体 | 未接ICC色彩转换 | 每页Orientation 1–8；不读取摄影字段 | 当前页ROI | 禁用 |
| ICO | 第一个图标 | 无 | 无尺寸选择 | 依首个图标变体 | 未显式转换 | 仅WIC可读元数据 | 首图标WIC ROI | 既有静态编辑 |
| 静态WebP | 支持 | 无 | 无 | Alpha | RGB ICC→sRGB | 方向1–8；无摄影字段 | 无 | 既有静态编辑 |
| WebP动画 | 代表帧用于缓存/超限反馈 | 播放/暂停/重播/定位/循环，codec合成 | 无 | Alpha、Source/Over合成 | RGB ICC→sRGB | codec方向1–8；无摄影字段 | 无 | 禁用 |
| JPEG XR | WIC支持的变体 | 无 | 无 | 依变体 | 浏览未显式转换 | WIC可读字段/方向 | WIC ROI | 既有静态编辑；输出8-bit |
| 受限SVG | 目标栅格化 | 无 | 无 | 支持 | 无嵌入profile管线 | 无 | 无 | 既有栅格编辑；非矢量回写 |
| AVIF/HEIF | 首图 | 无 | 无 | 依Magick支持的变体 | RGB ICC→sRGB | 容器方向；非完整摄影字段 | 无 | 既有静态编辑；非HDR |
| RAW内嵌预览 | JPEG/RGB预览 | 无 | 无 | 不承诺 | 预览profile未统一 | 内嵌预览方向；非传感器元数据全集 | 无 | 仅预览编辑，不显影 |

多帧 GIF/WebP 与多页 TIFF 禁止进入编辑窗口，本批不扩大编辑或完整 RAW 能力。剪贴板只复制当前已显示的不可变帧/页快照，保留查看方向；不复制动画容器、不按原路径重读首页、不自动解码未显示页。原尺寸复制仍要求当前显示的是完整像素且不超过64MiB，不包含ROI覆盖。

可读首页加坏后页时，坏页读取错误保留最后有效画面；TIFF 不截断 `Info` 页数来隐藏坏页。损坏 strip 的后页可在读取时报错；如果后页 IFD 连尺寸都不可读，则不能建立完整页面会话。切图/F5/关闭失效旧代次并释放资源，native必须真正返回后才完成取消、丢弃晚结果和归还槽。`Dispose` 不等待UI，`ReleaseCompletion` 在资源真实退出后完成；不强制GC证明释放。

支持容器不代表所有损坏文件、编码变体或超限尺寸都可打开。损坏、不支持、权限拒绝、文件消失与尺寸超限由打开管线反馈；新请求全部失败时保留当前图片和浏览会话。静态首帧、动画、多页、导出、色彩管理分别是独立能力。

## 像素、尺寸与内存边界

- 现代格式在Ping后、Read前验证源32MP，native全图后缩放；现代单槽/192MiB像素cache/禁disk和外部delegate，逐行导出。RAW单槽且只open/unpack_thumb，输入256MiB/预览32MiB/默认单次分配64MiB/profile8MiB。均不是进程硬上限，现代源限内也可能资源拒绝。完整边界见[本批验证](raw-modern-editing.md)。
- 主图输出为四个 8-bit 通道的预乘 BGRA，每像素 4 字节；不是保留 16-bit 原始精度的处理管线。
- 原始图片单边不超过 32,768 像素、总像素不超过 100,000,000（100MP）；基础解码器的单图输出上限仍为 400,000,000 字节（约 381.5MiB）。预览入口也先检查原始尺寸，不能通过降采样绕过源尺寸限制。
- 首屏预览根据窗口/DPI计算，单边4096、BGRA32MiB，保持原宽高比且不超过源图。3840×2160目标约31.6MiB；普通窗口使用更小目标。这是输出数据量上限，不是实测工作集；公开v0.4.0仍固定2560×1600。
- 开发版主图输出预算160MiB（167,772,160字节），预留两份32MiB预览及4MiB邻图后，完整输出上限92MiB。取消交接仍有保守余量，细化分配前重新读取头/检查预算，提交复核原文件戳。超预算时WIC可用有界中心区域，WebP保留预览；不无条件生成完整像素。
- 160MiB不是整个进程或全管线预算：不包含所有native工作区、surface、缩略图、方向临时缓冲或其他应用资源。完整分块、WebP区域和超出原始尺寸限制的路径仍未实现。
- 动画每个输出最多8MiB，应用掌控的帧/参考/待交接像素按32MiB边界处理，纳入既有主图交接范围，不展开全部帧；TIFF页面沿用静态32MiB预览/92MiB完整细节预算，完整读取不强制4096单边，区域仍2048×2048/16MiB。
- GIF/WebP动画source的BGRA等价数据量 `宽×高×4` 最多92MiB；这是源尺寸native工作区的准入检查，不是native实际分配或进程cap，也不能由此宣称内存改善。超限会反馈并尝试静态代表预览；代表帧可能仍使用源尺寸native工作区。输入最多256MiB、帧/页数最多10,000、元信息政策上限2MiB，32768单边/100MP源限制继续适用。动画有效帧延时小于20ms回退100ms；总播放轮数统一为首次加重复次数，无限单独表示。
- 缩略图目标是 224×140 包围盒，不放大原图，最多并发 2 个任务；也先验证原始尺寸安全，不用缩略图入口绕过单图尺寸/像素限制。解码器按可用的原生缩放能力生成较小像素缓冲，但原生 codec 的内部工作内存不计入缩略图缓存上限。
- 缩略图缓存按实际保留的像素字节计费，2MiB 与 24 项双重上限，采用 LRU 逐出。224×140 的 32-bit 缩略图最多 125,440 字节，预算可容纳最多 16 张满尺寸缩略图；较窄图片可缓存更多，但仍不超过 24 项。键包含路径、修改时间和文件长度；属性读取与解码均在后台进行。该上限不包含已显示或在途缩略图、WIC/Skia native 分配及其他应用资源。
- F5 清理缓存并增加代次，使刷新前的在途结果不能重新进入新缓存；本轮没有持续文件监听。同路径内容变化但修改时间与长度均不变时，仍需 F5 强制失效。
- WIC 路径可读的 EXIF Orientation 1–8 用于主图和缩略图方向；WebP已应用方向，拍摄信息未统一。缺失或不适用元数据不阻止打开。

浏览管线补WebP嵌入RGB ICC→sRGB输出；编辑窗口新增WIC RGB ICC→sRGB预览/导出和明确输出sRGB声明。可选择保留安全摄影信息，GPS/缩略图/厂商/XMP移除；非RGB设备profile、原profile保留、显示器profile切换、HDR/高位深编辑仍不保证。窗口/Skia格式转换不等于完整色彩管理。常见 JPEG EXIF 展示包括相机、镜头、拍摄时间、ISO、快门、光圈和焦距；不保证各格式的所有元数据都能读取。PNG/JPEG/有损和无损WebP安全新文件导出见[编辑能力](editing-improvements.md)。

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

本批0.6自动化验收已通过，启动/激活/动画资源与真实0.5→0.6升级/重装/卸载结果见[记录](animation-multipage-validation.md)；开发artifacts不代表GitHub已发布v0.5.0或v0.6.0。最新公开下载仍为v0.4.0，本轮不发布。

代码核对入口：

- [`DependencyInjection.cs`](../src/ModernImageViewer.Codecs/DependencyInjection.cs) 与 [`ImageDecoder.cs`](../src/ModernImageViewer.Codecs/ImageDecoder.cs)：组合解码器与 WIC/WebP 分派。
- [`SupportedImageFormats.cs`](../src/ModernImageViewer.Application/Images/SupportedImageFormats.cs)：统一格式和扩展名目录。
- [`WicImageDecoder.cs`](../src/ModernImageViewer.Codecs/Wic/WicImageDecoder.cs)：WIC 容器、首帧、格式转换与尺寸检查。
- [`ImageDecoder.Frames.cs`](../src/ModernImageViewer.Codecs/ImageDecoder.Frames.cs)、[`SkiaImageFrameSession.cs`](../src/ModernImageViewer.Codecs/Frames/SkiaImageFrameSession.cs)：帧会话派发、GIF代表帧及GIF/WebP动画合成与准入。
- [`TiffImageFrameSession.cs`](../src/ModernImageViewer.Codecs/Frames/TiffImageFrameSession.cs)、[`TiffWicContext.cs`](../src/ModernImageViewer.Codecs/Frames/TiffWicContext.cs)：持久WIC分页、逐页方向、预算、文件戳与确定释放。
- [`ImageFrameInfo.cs`](../src/ModernImageViewer.Imaging/ImageFrameInfo.cs)、[`ImageOpenCoordinator.Frames.cs`](../src/ModernImageViewer.Application/Images/ImageOpenCoordinator.Frames.cs)：帧政策、当前页细化和释放完成观测。
- [`ImageDecodeLimits.cs`](../src/ModernImageViewer.Imaging/ImageDecodeLimits.cs)：主图单图默认限制。
- [`NeighborPreviewCache.cs`](../src/ModernImageViewer.Application/Images/NeighborPreviewCache.cs)：单张邻图预览、文件属性复核与所有权转移。
- [`RegionOrientation.cs`](../src/ModernImageViewer.Codecs/Wic/RegionOrientation.cs)：WIC 区域坐标与 EXIF 方向变换。
- [`ThumbnailImage.cs`](../src/ModernImageViewer.UI/Controls/ThumbnailImage.cs)：尺寸目标、后台加载、并发与缓存。
- [`ImageBrowseSession.cs`](../src/ModernImageViewer.Application/Browsing/ImageBrowseSession.cs) 与 [`ImageOpenCoordinator.cs`](../src/ModernImageViewer.Application/Images/ImageOpenCoordinator.cs)：目录索引、打开提交与取消。
