# M0 技术验证与格式基线

> 2026-10-05。本轮按仓库 M0 理解用户所称 N0，起点为 `58e3d7b`。当前 0.5.0 为未发布开发代码，公开下载仍为 v0.4.0，不触发 Release。M0 逐项记录证据，不把本轮部分验证当作整个里程碑关闭。

## 实现和验证范围

| 链路 | 本轮结果 | 剩余边界 |
|---|---|---|
| WPF / Skia / 共享像素 | 延续既有绘制和会话方向；不新增整图绘制副本 | 首帧、真实混合 DPI、长期回收和全管线预算仍待固定机验收 |
| JPEG / PNG / WebP | 原有有界预览/细化保留；补 WebP EXIF 1–8 原地纠正与 sRGB 输出 | WebP 区域、跨格式元数据、显示器 profile/HDR 未交付 |
| JPEG XR | WIC 实测主图、预览、缩略图及 ROI 通过；接入 `.jxr/.wdp/.hdp` | 输出仍为预乘 BGRA8，不保证 HDR/高位深准确输出 |
| 受限 SVG | 固定 Svg.Skia 5.2.3；形状/路径/变换/本地渐变直接目标栅格化，区域细化因曲线裁切AA边缘差异暂不开放 | 非完整 SVG；文字、图片、use、clip/mask/filter、动画和外部资源不开放 |
| 100MP 输出/进程观察 | 流式生成 10000×10000 RGB PNG，生成与解码分进程；新增可复现观察工具 | 单样本不是 P95；进程峰值不是 native 专用分配，更不是全管线硬上限 |
| AVIF / HEIF | 随包 Skia 的真实样本返回 Unimplemented；独立 codec 继续验证 | 本轮未启用对应扩展名，不依赖用户装有 WIC 扩展宣称支持 |
| RAW 内嵌预览 | 已核对 LibRaw thumbnail API、独立方向和预算要求 | 尚无随包 native / 机型样本验证，不注册 RAW 扩展名，不实现完整显影 |
| ZIP / EXE / 依赖声明 | 沿用现有 Windows CI，补 SVG 完整许可及实际包/native 文件清单 | MSIX、可信签名、完整 SBOM/固定机验收仍待完成 |

## 方向与 ICC 的真实回归

SkiaSharp 4.153.1 的 `EncodedOrigin` 可读 WebP EXIF，`GetPixels` 本身不会纠正像素。测试自生成六色无损 WebP，再加入 TIFF EXIF 1–8，验证主图、预览、缩略图的方向和纠正后 SourceSize。90°/270° 在解码目标盒之前交换宽高，避免旋转后超过预览或缩略图边界。

纠正仅操作未发布的私有 BGRA 数组：镜像/180°直接交换，转置/90°使用原地循环置换与每像素 1 bit 标记。100MP 标记最多 12,500,000 字节；最大 2560×1600 预览最多 512,000 字节。不是无分配，也不把 128MiB 输出预算当全管线内存上限；取消后私有数组可能部分变换，丢弃结果后仍保留旧显示。

ICC 回归自行生成 ICC v2 矩阵/TRC 线性 RGB profile，注入 4×2 灰 128 无损 WebP。未加 profile 输出约128，指定 sRGB 输出的实际解码结果约188，且源文件字节不变；不复制系统/vendor profile。转换发生在同一次 `GetPixels` 输出中，不先生成另一份完整 BGRA。

`Info.ColorSpace != null` 不是嵌入 ICC 存在的证明；未带 ICC 的 WebP 同样有解码解释空间。本轮只验证此 codec 的嵌入 RGB→sRGB 路径，不宣称所有 ICC 编码、WIC、显示器 profile、跨屏或 HDR 已完善。[SKCodec 官方接口](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcodec?view=skiasharp)。

## 受限 SVG 边界

接受标准 SVG 命名空间中的 svg/g/defs、基础形状、path、线性/径向渐变和 stop、内联画笔样式与变换。没有指定视口时明确使用300×150；单位和 viewBox 按实际库输出验证。弧命令旗标只接受分开的0/1，不接受紧凑合并旗标形式。

禁止 DTD/实体、脚本及事件、所有 href、外部或嵌入图片/文件/网络、text/use/clip/mask/filter/style 节点、嵌套 SVG、CSS 导入/转义/变量。paint只允许指向已存在本地渐变，拒绝缺失/递归目标。不自行实现 SVG 渲染器，由固定成熟库绘制。

资源限制：输入最多1MiB、2048元素、结构深度32、每元素48属性、65536个几何数值/路径命令、path/points单属性256KiB；坏几何语法不静默丢弃，NaN、非法单位、截断path/transform反馈坏文件。原始名义尺寸继续受32768边长/100MP限制。

最多同时2个非完全不透明层；属性、内联样式、根与形状均计费。在分配前按当前实际输出字节×层数校验32MiB透明层像素预算，不能将该数表述为所有 native 内存硬上限。默认预览/缩略图直接向pinned输出数组的目标SKSurface绘制，不先栅格化完整名义尺寸。统一入口测试实测曲线跨ROI边界时有8个像素、最大通道差22；有限guard/matrix/clip未证明通用一致性，因此本轮不开放SVG区域细化，大图超完整预算保留预览。

## 可复现的 Windows 观察

```powershell
pwsh ./scripts/measure-codec.ps1
# 已完成Release解决方案构建时复用输出
pwsh ./scripts/measure-codec.ps1 -NoBuild
# 同一机器/样本比较多个格式；每模式单独新进程
pwsh ./scripts/measure-codec.ps1 -NoBuild -ImagePaths ./sample.jpg,./sample.webp -OutputPath ./artifacts/codec-observation/custom.json
```

工具在独立进程逐行生成 RGB PNG，仅30,001字节行缓冲与64KiB IDAT缓冲，压缩数据临时文件；测量进程不会继承400MB的样本数组。每输入/模式分别启动框架依赖 Release进程，记录来源/输出尺寸、输出字节、elapsed、当前/峰值进程内存、OS/运行时/CPU、源文件哈希、提交与工作区状态。CPU型号/RAM读取失败保留null和原因，不编造条件。

本机开发观察：Windows10.0.19045 x64，.NET10.0.11，16逻辑CPU，CPU型号/RAM因CIM权限不可读，工作区包含未提交本轮修改。样本PNG为6,518,547字节，SHA256 `F58375F91A1315E77339E9F19A4BC8E34BBDB1400914DE8A584B5F01341428E6`。

| 模式 | 实际输出 | 耗时（单次） | 进程峰值工作集 | 进程峰值页内存 |
|---|---|---:|---:|---:|
| preview | 1600×1600，10,240,000B | 251.36ms | 54,530,048B | 28,835,840B |
| thumbnail | 140×140，78,400B | 198.09ms | 36,249,600B | 9,965,568B |

这是一个可重现生成样本的开发观察，包含运行时/codec初始化与输出保留，没有WPF surface、完整浏览缓存、强制GC、冷启动控制或取消回收长测。不能由峰值减托管堆推导native专用分配，不能推广为所有PNG/相机文件的规律；也不宣称固定机P95或速度提升。现有Windows CI保留同样的一次观察JSON，安装/激活另行验收。

## 现代 codec 与 RAW 风险

固定随包 SkiaSharp4.153.1 win-x64 native SHA256为 `935EF4A00462E6B0C4DADB870F734FA43679B4F561287FE0D28CBE2BA147E832`，源码提交声明为 `4783f51448f9b070dda4f87b83e941c9599e466e`。同一probe的16×12 PNG Create/GetPixels成功；两个有效官方样本 AVIF/HEIC 的 Create均为Unimplemented，因此不凭枚举或扩展名启用。

| 样本 | 官方固定blob | 文件SHA256 |
|---|---|---|
| example.avif | [libheif blob](https://api.github.com/repos/strukturag/libheif/git/blobs/c2decefeedd53b204967ec3496f109f1a2115b2c) | 54A0DC31D02B6F5D9D4B66027D4787861B7AF15FFD8FAB8EAB963D10C5411469 |
| example.heic | [libheif blob](https://api.github.com/repos/strukturag/libheif/git/blobs/829384037820e545467a4af49aa6414c2b0f2885) | 7F8B363E4936C0666A25F64F3A92FDA10BD8E5453BE4592530B65A55DD98F3F2 |

候选 Magick.NET-Q8-x64 14.17.2 已核对 nuspec/API/NOTICE：托管声明Apache-2.0，NOTICE另含ImageMagick、libheif/libde265及LGPL文本，不能概括全部组件为Apache。Windows native项原始24,742,576字节，完整nupkg57,608,509字节；本机未下载完整native尾部/Core，尚无实际运行验证。其 Memory/Area 不代表所有 libheif native分配或拒绝阈值，须实测读入/缩放顺序与缓存限制再决定接入。[官方候选包](https://www.nuget.org/packages/Magick.NET-Q8-x64/14.17.2)、[官方API源](https://github.com/dlemstra/Magick.NET/blob/main/src/Magick.NET/ResourceLimits.cs)。

RAW最低路线为固定LibRaw native → open_file/unpack_thumb，不调用unpack/dcraw_process → JPEG缩略图接既有有界JPEG路径；位图预览另检查tlength/尺寸/预算。缩略图tflip可不同于RAW主图flip，未知方向不能擅自套用；还需真实机型、有/无预览、损坏/取消、原文件hash与随包许可验证。[LibRaw官方数据结构](https://www.libraw.org/docs/API-datastruct-eng.html)。

## 分发与实际验证状态

固定SVG依赖包括MS-PL与MIT；部分NuGet只有SPDX声明没有完整文本，分发必须另带[固定源码许可证和署名](../third_party/licenses/README.md)。安装器复制实际还原包的LICENSE/NOTICE，并强制复制SVG源许可，生成 `dependencies.json` 的解析包/源提交/许可/包SHA-512与实际图像native文件SHA-256；它不是完整OS/.NET或标准格式认证SBOM。

本地已完成JPEG XR/WIC路径、WebP EXIF、ICC与同源SVG验证；完整正式项目恢复仍受HarfBuzz Linux/Win32包下载故障影响，不能把手工官方DLL同源验证表述为完整发布依赖通过。正式Windows CI继续执行原有格式、构建、UI/codec、启动、激活和安装/升级/卸载检查；最终结果在本轮PR与进度日志补充。
