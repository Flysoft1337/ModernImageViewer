# LibRaw 内嵌预览分发

固定来源：[官方 LibRaw 0.22.2 Windows x64 源码与二进制包](https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip)，完整 ZIP SHA-256：`AC64FA12BB00A7581332D4C6AB918C0533FB3F119D6B668D47A6875410DCA948`。

LibRaw 部分选择 **CDDL 1.0** 分发，保留原始 `COPYRIGHT`、`LICENSE.CDDL` 和 LGPL 2.1 备选许可 `LICENSE.LGPL`。版权包括 LibRaw LLC、Dave Coffin、Jacek Gozdz、Roland Karlsson 和 Adobe；完整原始源码包含于上述固定官方 ZIP，并随包保存在 `licenses/raw-native/LibRaw-0.22.2-source.zip`，不依赖下载站长期可用。桥接源码 `src/ModernImageViewer.Codecs/Raw/Native/raw-preview.cpp` 采用仓库 MIT 许可，不修改供应商源码文件。

`scripts/build-raw-native.ps1` 校验完整 archive 后，以实际 MSVC x64 编译器 `/MT /O2 /utf-8` 重新构建库与桥接，首次强制 `/A`，不复用官方 `/MD` 静态库。构建配置、来源/桥接哈希、实际 DLL 哈希和 imports 记录在 `raw-native.json`。仅导出内嵌预览接口，不导出完整 RAW 解包或显影入口；未启用 RawSpeed、Adobe DNG SDK、libjpeg、LCMS、OpenMP。

本轮配置限制：LibRaw 单次分配默认 64MiB、缩略图 32MiB、profile 8MiB、X3F 32MiB、输入文件 256MiB；桥接还检查选中预览及实际输出。它们不是全进程或全部原生内存硬上限。`SourceSize` 是方向纠正后的内嵌预览尺寸，不是传感器尺寸。

生成 DNG 测试素材由本项目自行创建，可用于 CI/发布包激活检查。真实 Canon CR2 样本来自 rawpy 测试库，其来源 rawsamples.ch 声明 CC BY-NC-SA 4.0，只用于本机验证，不进入仓库、安装器或 CI 分发素材；真实 Nikon NEF 同样只保留本机验证哈希与来源证据。
