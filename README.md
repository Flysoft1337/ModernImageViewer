# Modern Image Viewer

面向 Windows 10/11 的本地图片查看器，目标是快速打开、流畅浏览、完成常用编辑，并确保原图安全。

当前正在推进 **M1 图片浏览体验**，M0 的扩展解码器与性能验证仍待完成。已接入 WIC 解码和 SkiaSharp 画布，支持 JPEG/PNG 打开、拖放、同目录自然排序浏览、邻近图片缩略图、适应窗口、实际像素大小、缩放、平移和全屏。采用深色画廊界面，支持浅色与跟随系统主题，文件信息面板默认收起。libvips、LibRaw、编辑、EXIF 和完整相邻图片预取尚未实现。

## 语言

目前支持简体中文和英文。首次启动跟随系统显示语言，未支持的语言回退到英文；也可在应用内即时切换，选择会保存在当前用户的本地设置中。

## 浏览与交互

- `Ctrl+O` 打开图片，`← / →` 切换，`Home / End` 跳到目录首尾。
- 滚轮围绕鼠标缩放，拖动平移，双击切换适应窗口与实际大小。
- `0` 适应窗口，`1` 实际像素大小，`+ / -` 缩放。
- `F11` 全屏，`Esc` 退出全屏或收起信息面板；全屏会恢复此前窗口位置与状态。
- `Ctrl+I` 切换信息面板，`Ctrl+T` 切换缩略图带；点击缩略图打开对应图片。
- 文件夹列表在浏览期间复用；新增、删除或修改图片后，按 `F5` 刷新列表、缩略图与文件信息。
- 右上角菜单切换主题和语言。主题选择当前为会话设置；语言选择会持久保存。

缩略图带显示当前位置附近最多 9 张图片，后台同时最多解码 2 张，缓存上限为 24 张降采样图片。文件信息包括尺寸、格式、大小、修改时间和路径。

## 环境要求

- Windows 10 22H2 或 Windows 11
- .NET 10 SDK

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

完整范围与架构见 [项目计划](docs/project-plan.md)。

## License

[MIT](LICENSE)
