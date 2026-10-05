# 第三方许可证来源

本目录补充 NuGet 包未携带的完整许可证文本。许可证文件保留固定源码版本的原文与版权署名；NuGet 的 SPDX 表达式只用于记录包声明，不替代完整许可。

| 实际依赖 | 包声明 | 完整文本 | 固定来源 |
| --- | --- | --- | --- |
| Svg.Skia、Svg.Model、Svg.SceneGraph、Svg.Animation、ShimSkiaSharp 5.2.3 | MIT；`Copyright © Wiesław Šoltés 2026` | `Svg.Skia-MIT.txt`，原文 `Copyright (c) 2020 Wiesław Šoltés` | [Svg.Skia/LICENSE.TXT](https://github.com/wieslawsoltes/Svg.Skia/blob/7910666415a96a09643d1729eb5da5d115d75748/LICENSE.TXT) |
| Svg.Custom 5.2.3 | MS-PL；`Copyright © Wiesław Šoltés 2026` | `Svg.Custom-MS-PL.txt` | [SVG/license.txt](https://github.com/wieslawsoltes/SVG/blob/7a418e7953cd6542b18214d7b37e6f4accc5c2ff/license.txt) |
| ExCSS 4.3.1 | MIT；作者 Tyler Brinks，nuspec 未提供 copyright 字段 | `ExCSS-MIT.txt`，原文 `Copyright (c) 2024 Tyler Brinks` | [ExCSS/license.txt](https://github.com/TylerBrinks/ExCSS/blob/c97e84d6126bb2e42658cf5af627d52e697c8779/license.txt) |

以上 SVG 5.2.3 包的 nuspec 指向源码提交 `7910666415a96a09643d1729eb5da5d115d75748`。Svg.Custom 使用其 [SVG 子模块](https://github.com/wieslawsoltes/Svg.Skia/tree/7910666415a96a09643d1729eb5da5d115d75748/externals/SVG)，固定提交为 `7a418e7953cd6542b18214d7b37e6f4accc5c2ff`。该版本 [Source/Properties/AssemblyInfo.cs](https://github.com/wieslawsoltes/SVG/blob/7a418e7953cd6542b18214d7b37e6f4accc5c2ff/Source/Properties/AssemblyInfo.cs) 另有原作者声明 `Copyright © Microsoft 2006`；分发时一并保留在 `THIRD-PARTY-NOTICES.txt`。

HarfBuzzSharp 14.2.0 的官方包包含 `LICENSE.txt`；已读取的 HarfBuzzSharp.NativeAssets.macOS 14.2.0 包另包含 `THIRD-PARTY-NOTICES.txt`。直接复制实际还原包内的完整文本，不用本目录的 SVG/MIT 文件替换。其 nuspec 指向 SkiaSharp 源码提交 `4e4ce7af7ea8702593af5aeb25d05c65ffb74e90`，包版权为 `© Microsoft Corporation. All rights reserved.`。Windows 发布应核对实际选中的 `HarfBuzzSharp.NativeAssets.Win32/14.2.0`，保留它自己的原生组件声明。

官方包地址为 `https://www.nuget.org/api/v2/package/<包名>/<版本>`，也可从对应 [Svg.Skia 5.2.3](https://www.nuget.org/packages/Svg.Skia/5.2.3)、[Svg.Custom 5.2.3](https://www.nuget.org/packages/Svg.Custom/5.2.3)、[ExCSS 4.3.1](https://www.nuget.org/packages/ExCSS/4.3.1)、[HarfBuzzSharp 14.2.0](https://www.nuget.org/packages/HarfBuzzSharp/14.2.0) 页面下载。

分发清单按实际 `project.assets.json` 记录包名、解析版本、包内容 SHA-512、nuspec 声明的源码 URL/提交和许可；对发布目录中的实际原生文件额外记录 SHA-256。只有通过完整 ZIP/CRC 校验的官方包进入本地源，不把部分下载文件标成完整依赖。
