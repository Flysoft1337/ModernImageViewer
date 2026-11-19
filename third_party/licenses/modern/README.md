# Magick.NET 15.1.0 分发来源

固定 NuGet 包：Magick.NET-Q8-x64 与 Magick.NET.Core 15.1.0；两包 nuspec 均指向仓库提交 `80d8318173db37bd2ee9f3b53c4c736a1267e689`，声明 Apache-2.0。

- `Apache-2.0.txt` 保留 [该源码提交的 License.txt](https://github.com/dlemstra/Magick.NET/blob/80d8318173db37bd2ee9f3b53c4c736a1267e689/License.txt) 完整许可正文；与此前版本正文一致，保留现有文本。
- `Copyright.txt` 与实际 Magick.NET.Core 15.1.0 包逐字节一致，保留 Dirk Lemstra 署名。
- **必须同时分发实际 Magick.NET-Q8-x64 15.1.0 包中的 `Notice.txt`**。其中记录 ImageMagick 和随包第三方 native 的许可/版权，包含 libheif/libde265 的 LGPL 完整文本与来源，不能将整个解码栈概括为纯 Apache-2.0。15.1.0 的 Notice 新增各组件固定源码提交链接，须保留，不能沿用旧包 Notice；其中 libheif 1.23.7 指向 `498042a72e805c50e5e84b57e15327f3cfdf6a63`，libde265 1.1.3 指向 `5a0bf9f624d810de6af20e41ac3cfd92bf7de508`（均为 ImageMagick 官方依赖仓库）。

Windows x64 官方 native 为 `Magick.Native-Q8-x64.dll`，实际还原包文件为 24,928,432 字节，SHA-256 `D0A95D90AED4B7A0012C15363012639DB7F5F3099412027890E2C4305FAF63AA`。Notice 记录 ImageMagick 7.1.2-35（2026-10-31），源码提交 `5d1bcc55f2c83bc2ff802118b7369756610f3ad4`。依赖清单仍应取实际发布文件 SHA-256 和解析包 SHA-512，不凭本目录代替发布核对。

此项目仅调用随包静态 AVIF/HEIF 解码；不使用外部命令委托，不调用 Magick RAW 显影。测试 fixture 为本项目自行生成的色条/透明 AVIF、线性 RGB ICC 灰 AVIF 与 FFmpeg/libx265 lossless 64×32 红色 HEIC，源码中保留固定数据。测试/验收用的官方 libheif example 样本仅在 ignored artifacts 中，不进入分发。
