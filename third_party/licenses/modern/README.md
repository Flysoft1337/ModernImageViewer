# Magick.NET 14.17.2 分发来源

固定 NuGet 包：Magick.NET-Q8-x64 与 Magick.NET.Core 14.17.2，仓库提交 `aa58615154d7895c800fe756c695a55e4ee5d4ee`。

- `Apache-2.0.txt` 保留 [该源码提交的 License.txt](https://github.com/dlemstra/Magick.NET/blob/aa58615154d7895c800fe756c695a55e4ee5d4ee/License.txt) 完整原文。
- `Copyright.txt` 原样来自实际 Magick.NET.Core 14.17.2 包，保留 Dirk Lemstra 署名。
- **必须同时分发实际 Magick.NET-Q8-x64 包中的 `Notice.txt`**。其中记录 ImageMagick 和随包第三方 native 的许可/版权，包含 libheif/libde265 的 LGPL 完整文本与来源，不能将整个解码栈概括为纯 Apache-2.0。

Windows x64 官方 native 为 `Magick.Native-Q8-x64.dll`，24,742,576 字节；依赖清单应取实际发布文件 SHA-256 和解析包 SHA-512，不凭本目录代替发布核对。运行版本为 ImageMagick 7.1.2-32 Q8 x64、源码标识 `ad98b244c:20260927`。

此项目仅调用随包静态 AVIF/HEIF 解码；不使用外部命令委托，不调用 Magick RAW 显影。测试 fixture 为本项目自行生成的色条/透明 AVIF、线性 RGB ICC 灰 AVIF 与 FFmpeg/libx265 lossless 64×32 红色 HEIC，源码中保留固定数据。测试/验收用的官方 libheif example 样本仅在 ignored artifacts 中，不进入分发。
