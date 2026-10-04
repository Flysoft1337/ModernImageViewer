# Modern Image Viewer

面向 Windows 10/11 的本地图片查看器，目标是快速打开、流畅浏览、完成常用编辑，并确保原图安全。

当前处于 **M0 技术验证阶段**。已接入 WIC 解码和 SkiaSharp 画布，支持 JPEG/PNG 打开、拖放、同目录上一张/下一张、适应窗口、实际大小、缩放和平移。libvips、LibRaw、编辑和元数据功能尚未实现。

## 语言

目前支持简体中文和英文。首次启动跟随系统显示语言，未支持的语言回退到英文；也可在应用内即时切换，选择会保存在当前用户的本地设置中。

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
