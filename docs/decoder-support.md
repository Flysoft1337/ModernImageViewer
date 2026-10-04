# 解码器与图片格式支持

> 核对日期：2026-10-04；分发版本：0.2.0。以下支持范围来自当前代码，不将项目计划或 Windows 已安装的额外 codec 当成应用能力。

## 实际解码与绘制路径

主图唯一启用的 `IImageDecoder` 是 `WicImageDecoder`，通过 WPF `BitmapDecoder` 使用 Windows Imaging Component（WIC）。读取实际容器 GUID 后，只接受 JPEG 和 PNG，再取第一帧、读取可用 EXIF、纠正方向并转换为 `Pbgra32` 像素。主图在后台解码，同一时间最多一个全尺寸解码；排队和结果提交支持取消，已经执行的原生 WIC 调用不保证立即中断。

缩略图使用 WPF/WIC 的 JPEG/PNG 路径，先降采样，再应用 EXIF 方向。SkiaSharp 4.153.1 负责画布绘制和共享解码像素，不负责本应用的文件解码。libvips、LibRaw 和 MetadataExtractor 尚未接入；当前元数据读取来自 WIC，`Metadata` 工程的存在不代表已有独立元数据引擎。

## 当前能力表

| 格式 | 文件扩展名 | 主图 | 缩略图 / 目录浏览 | 安装版与便携版打开方式 | 边界 |
|---|---|---|---|---|---|
| JPEG | `.jpg`、`.jpeg` | 支持 | 支持 | 支持候选注册 | 静态首帧；可用的常见 EXIF 字段与 8 种 Orientation；不承诺 CMYK/广色域的色彩准确性 |
| PNG | `.png` | 支持 | 支持 | 支持候选注册 | 静态首帧、透明像素；16-bit 输入最终转换为 8-bit 通道；不提供 APNG 动画播放 |
| BMP、GIF、TIFF、WebP、ICO | 相应扩展名 | 未启用 | 未启用 | 不注册 | 即使 Windows WIC 能解码，也会被当前应用容器白名单拒绝；无动画或多页浏览 |
| HEIF/HEIC、AVIF | 相应扩展名 | 未实现 | 未实现 | 不注册 | 不依赖用户安装系统扩展来宣称支持；后续需验证 codec 与分发方式 |
| RAW、SVG | 相应扩展名 | 未实现 | 未实现 | 不注册 | LibRaw 与受限 SVG 渲染仍属后续计划 |

文件选择器、目录/多选过滤与关联注册均限定 `.jpg`、`.jpeg`、`.png`，扩展名比较不区分大小写。主解码器另外核对容器，不会因为把 BMP 改名为 `.jpg` 就接受它。反过来，即使内容是 JPEG/PNG，使用其他扩展名也不属于完整浏览流程的支持范围；建议保留正确扩展名。

支持某种容器不代表任意损坏文件、编码变体或超限尺寸都可打开。损坏、不支持、无权限、文件消失和尺寸超限会通过打开管线反馈；查看图片和自动方向纠正不改写原文件。

## 像素、尺寸与内存边界

- 主图输出为预乘 Alpha 的 BGRA，四个 8-bit 通道，每像素 4 字节；不是保留 16-bit 原始精度的处理管线。
- 默认单边不超过 32,768 像素、总像素不超过 100,000,000（100MP）、输出像素字节不超过 400,000,000（约 381.5MiB）。解码器在分配主图缓冲前检查，并在方向变换后再次检查。
- 这些限制只约束单张输出图，不是整个进程的内存预算。当前旧图交接、WIC 原生分配、缩略图和绘制 surface 可能同时占用内存；主图尚无超限降采样回退、渐进预览或分块/区域解码。
- 缩略图以不超过原始尺寸的 224×140 显示包围盒为降采样目标，后台最多 2 个任务、缓存最多 24 张，按路径和数量做 FIFO；并非按字节做 LRU。超出主图尺寸限制的输入也不会先生成缩略图绕过检查。
- 缩略图读取可用的 EXIF Orientation 1–8，与主图方向保持一致；缓存可由 F5 刷新清理，尚无基于文件修改时间的自动失效。

当前没有应用级 ICC 转换、显示器 profile 切换、HDR/广色域输出或高位深编辑保证。Windows codec 可能完成自身的格式转换，但不能据此宣称已经实现完整色彩管理。常见 JPEG EXIF 展示包括相机、镜头、拍摄时间、ISO、快门、光圈和焦距；缺失或无法读取的可选元数据不会阻止图片打开。

## 安装包的依赖与格式声明

0.2.0 EXE 安装器面向 Windows 10 22H2 / Windows 11 x64，包含 self-contained .NET 10 应用、WPF 运行依赖和发布输出中的 SkiaSharp native 依赖；用户无需另装 .NET 或 libvips/LibRaw。WIC 来自 Windows。安装器只提供 JPEG/PNG 候选注册，不设置系统默认应用，也不添加尚未启用的扩展名。

安装器构建和使用见 [README](../README.md#安装与分发)，文件关联、升级与卸载边界见 [Windows 文件关联方案](windows-file-association.md)。Windows CI 的安装/激活检查与真实 Windows 默认应用/Shell 人工验收分别记录，不能互相代替。

## 后续格式交付规则

每增加一种格式，先确认 codec 实际接入、native 依赖、许可证、损坏输入和内存边界，再同步主图、缩略图、选择器、导航、关联声明和本表。静态首帧、完整动画、多页、高位深与色彩管理分别验收，不把其中一项实现当成其他项已支持。

代码核对入口：

- [`DependencyInjection.cs`](../src/ModernImageViewer.Codecs/DependencyInjection.cs)：实际注册的解码器。
- [`WicImageDecoder.cs`](../src/ModernImageViewer.Codecs/Wic/WicImageDecoder.cs)：容器白名单、首帧、格式转换和主图并发。
- [`ImageDecodeLimits.cs`](../src/ModernImageViewer.Imaging/ImageDecodeLimits.cs)：单图默认限制。
- [`ThumbnailImage.cs`](../src/ModernImageViewer.UI/Controls/ThumbnailImage.cs)：缩略图解码、尺寸目标、并发与缓存。
- [`ImageBrowseSession.cs`](../src/ModernImageViewer.Application/Browsing/ImageBrowseSession.cs) 与 [`WindowsImageFilePicker.cs`](../src/ModernImageViewer.Platform/Files/WindowsImageFilePicker.cs)：浏览和选择扩展名。
