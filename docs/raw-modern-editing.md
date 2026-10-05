# RAW、现代格式与文件编辑首批

> 2026-10-05，起点 master `6738cf4`。0.5.0 为未发布开发代码，公开 Release 仍为 v0.4.0，不触发 Release。正式 Windows CI、分发与安装结果在[进度](planning/progress.md)补充，不提前记为通过。

## 格式范围

当前12类、25扩展名统一用于选择器、导航、多选、缩略图、关联与安装器。新增 `.avif/.heif/.heic` 静态首图；RAW `.dng/.cr2/.cr3/.nef/.arw/.raf/.rw2/.orf/.pef` 只读取可用内嵌预览，不保证所有机型/编码。N3剪贴板/内存来源、N4动画/多页、完整RAW显影、完整SVG与完整M2仍未交付。

### AVIF / HEIF

固定 Magick.NET-Q8-x64/Core14.17.2，实际native ImageMagick7.1.2-32 Q8 x64，24,742,576B，SHA256 `E73B69325859131F8AE0F909533210932D05AD8CB931A3DDA92EE56F1233654A`。按需初始化，JPEG/PNG启动不加载它；ftyp最多4KiB探测。静态主图/预览/缩略图/预算内细化已接入，序列(avis/msf1)与ROI未开放。

源32MP、边长32768、输入文件128MiB。固定[HEIC源码](https://github.com/ImageMagick/ImageMagick/blob/ad98b244c/coders/heic.c)与64KiB cache实验确认Ping先读尺寸、在像素Read前返回；检查后native整图读取再缩放，不能称原生目标降采样。现代单槽、192MiB像素cache、禁map/disk/外部delegate/filter、profile4MiB与box/item/tile限制；这些不是libheif或进程硬上限，源限内也可能资源拒绝。逐行导出到一份pinned BGRA，export临时像素最多一行。

libheif处理irot/imir，禁止EXIF再次同步旋转，输出Orientation1；RGB ICC转换至sRGB，Alpha原地预乘。输出BGRA8，不保证HDR、所有NCLX变体或显示器profile。分发保留Apache全文/署名及实际native Notice（含LGPL依赖），见[许可](../third_party/licenses/modern/README.md)。

官方libheif AVIF/HEIC样本沿用[M0记录](m0-validation.md)的固定blob/hash，原字节不变；自有色条、alpha128、irot+EXIF6、线性RGB ICC灰128→约188和红色HEIC回归通过。新进程thumbnail单次：AVIF800×533→210×140/117600B，145.73ms/峰值WS41,443,328B；HEIC1280×854→209×139/116204B，173.99ms/峰值WS39,706,624B（Windows10.0.19045/.NET10.0.11/16逻辑CPU）。不是P95、提升百分比或native专用内存测量。

### RAW 内嵌预览

固定[官方LibRaw0.22.2 Win64源码归档](https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip)，3,495,702B，SHA256 `AC64FA12BB00A7581332D4C6AB918C0533FB3F119D6B668D47A6875410DCA948`。MSVC x64 `/MT /utf-8` 强制重建，不复用/MD预编译库；实际imports仅KERNEL32/WS2_32，无额外VC运行库前置。每个包携带编译器/配置/桥接源/实际DLL哈希的 `raw-native.json`。选择CDDL分发，完整固定源码ZIP、原始COPYRIGHT/CDDL与LGPL备选许可随包保留，见[源码与许可](../third_party/licenses/raw/README.md)。本机DLL1,170,432B，SHA256 `F45B574CFD7F12CFE3CBA3A3FDB93040C6E6D89F0E4650DE38D7A72C8A4B3D48`；CI编译器不同，必须以实际包manifest核对，不要求相同二进制哈希。

只open_file/unpack_thumb[_ex]，不调用sensor unpack/dcraw_process；ABI检查未分配sensor raw_alloc/image。JPEG通过只读native预览流进入WIC目标解码；RGB8/RGB16/灰度bitmap以两行scratch缩放后原地方向纠正。跨主图/缩略图选择同一预览，已知缩略图tflip覆盖EXIF且只处理一次；未知JPEG tflip保留EXIF。RGB16的LibRaw分支不填tcolors，桥接使用选中预览tmisc的真实通道数。

SourceSize/界面尺寸与RAW编辑均代表内嵌预览，不是sensor。无可读预览反馈RawNoPreview；损坏/取消分开，失败保留已有图片。单native槽、输入256MiB、预览32MiB、原库默认单次分配64MiB/profile8MiB/X3F32MiB，检查后输出；它们不是整个进程硬内存上限。所有context/流/callback在取消或失败后释放。

真实相机验证（本机输入，不分发）：

| 样本 | 文件SHA256 | 纠正后的预览 / 目标输出 |
|---|---|---|
| Canon EOS40D CR2，6,800,865B | `152382CE4DBF644899D12B41B4C577F07638AA3EC5745AC344C36BAD93826125` | 1936×1288 → 240×160/153600B |
| Nikon D3S NEF，10,656,312B | `5922721D13F11795557D97FDEB0A60B900086C402BC82A848FF280D15B99FFD4` | 4256×2832 → 240×160/153600B |

来源为rawpy测试库，CR2来源rawsamples声明CC BY-NC-SA，只在ignored artifacts作验证；源哈希均不变。CI/激活使用本项目自生成DNG（JPEG EXIF6/独立sensorflip、RGB8、Imacon RGB16、无预览/损坏），不下载或分发真实摄影素材。其他RAW家族复用固定LibRaw识别，不声称全机型语料库已验收；HEVC/JXL预览及不支持的旧bitmap反馈无可读预览。

## 文件编辑与导出

更多→编辑图片或Ctrl+E打开独立窗口：拖动/数值裁剪、旋转/翻转、按比例Resize或解锁Stretch、最多64步不可变历史。Ctrl+Z撤销、Ctrl+Shift+Z或Ctrl+Y重做、Ctrl+S另存为。关闭丢弃会话，保存后可继续撤销；浏览窗口继续显示原文件。

预览/区域共用EXIF纠正后的原图矩阵，不复制显示像素。解码handle锁内记录文件长度/修改时间，编辑与导出比对；版本匹配才复用已有完整像素。stamp不是内容hash，属性相同的外部内容改动仍需F5。源文件FileShare.Read锁到提交，目标只允许新文件，CreateNew临时→flush→Move不覆盖；取消/失败清临时，已有或竞态出现的目标保留原字节。

PNG保留透明；JPEG先白底合成，质量1–100。输出BGRA8，不复制源EXIF/ICC等元数据，也不保留动画、多页或sensor RAW。输出最多64MiB（16,777,216px），完整源读取最多64MiB；共享SKBitmap视图不另复制完整源。WIC大图裁剪逐块读取最多2048²/16MiB，1px边界供双线性采样；无ROI且超源预算的裁剪拒绝。整图缩小可按输出密度目标解码，只用于整图，避免先缩全图再裁剪混入外部颜色。完整源裁剪共享subset并clamp边界，内红/外蓝的旋转缩小回归通过。限制不包含全部native/surface/已有主图等进程资源。

## 构建与验证

```powershell
pwsh ./scripts/build-raw-native.ps1
# 源码构建需要 MSVC x64 Build Tools；发布包运行不需要编译器
dotnet restore ./ModernImageViewer.slnx
dotnet build ./ModernImageViewer.slnx --configuration Release --no-restore
```

沿用现有Windows CI，仅增加固定RAW源码构建一步；复用格式/构建/测试、100MP观察、便携/安装激活与安装生命周期，不发布Release。打包校验实际native、完整许可、固定源码ZIP哈希，dependencies.json记录解析包/native/源码manifest，不是完整OS/.NET SBOM。

本地Release全方案0警告0错误；核心61项、UI/codec146项通过（含真实RAW），另3项旧文件关联因沙箱拒绝隔离HKCU写入，提升权限仅补跑这3项全部通过，合计210项验证通过。210个中英文资源键一致，已查看720×480深中文/浅英文编辑截图。正式CI/真实包/安装结果补在进度及PR，不用本地测试代替分发验收，不宣称整个M0/M2退出。


## 正式 Windows CI 与实际分发核对

功能提交 `2af705e` 的[Windows CI 37320482281](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37320482281)成功：Release构建0警告0错误；核心61+UI/codec148=209项通过，另1项真实摄影样本仅本机运行而在CI明确跳过，自有DNG JPEG/RGB8/RGB16均执行。固定native构建、格式、依赖审计、100MP观察、便携/安装版全部12类格式单窗口激活、安装/重装旧关联迁移/卸载主要断言成功；Release步骤跳过。正常卸载后重复清理warning已消除。仍有dotnet format workspace加载warning；Inno旧函数名hint在本批最终文档/小修提交改用WizardIsTaskSelected，最终检查见PR #26，不称零告警验收。

取回实际Windows包核对完整许可、Magick Notice、LibRaw源码ZIP及dependencies.json全部native哈希。CI的RAW桥接以MSVC19.51.36260.0构建，1,178,624B，SHA256 `0F8F296A678B026D070239115E95B40363F7B6D1924B37DCB4C807F637739809`，imports仅KERNEL32/WS2_32。Magick/Skia/HarfBuzz实际SHA与记录匹配；31个解析包、1条固定原生源码manifest。源码ZIP哈希与固定官方归档一致，深中文/浅英文编辑截图已查看。

额外RAW一次新进程观察（Release框架依赖、提交2af705e/工作区干净，Windows10.0.19045/.NET10.0.11/16逻辑CPU，CIM拒绝故CPU型号/RAM未知）：

| 来源 / 模式 | 输出 / BGRA字节 | 单次耗时 | 进程峰值WS |
|---|---|---:|---:|
| Canon CR2 / preview | 1936×1288 / 9,974,272B | 105.10ms | 57,282,560B |
| Canon CR2 / thumbnail | 210×140 / 117,600B | 98.42ms | 40,275,968B |
| Nikon NEF / preview | 2404×1600 / 15,385,600B | 157.63ms | 68,476,928B |
| Nikon NEF / thumbnail | 210×140 / 117,600B | 105.66ms | 41,451,520B |

这是现有codec probe的四个独立进程观察，不含WPF首帧、长期回收/固定机P95，不推导native专用分配或速度提升。原图hash不变。修正M0文档多文件调用示例：PowerShell内用脚本调用操作符与string数组，避免将逗号路径作为原生pwsh单个参数。
