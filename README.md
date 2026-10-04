# Modern Image Viewer

面向 Windows 10/11 的本地图片查看器，目标是快速打开、流畅浏览、完成常用编辑，并确保原图安全。

当前正在推进 **M1 图片浏览体验**，M0 的扩展解码器与性能验证仍待完成。已接入 WIC 解码和 SkiaSharp 画布，支持 JPEG/PNG 打开、拖放、同目录自然排序浏览、文件夹打开、循环幻灯片、邻近图片缩略图、适应窗口、实际像素大小、缩放、平移和全屏。采用深色画廊界面，支持浅色与跟随系统主题，文件信息面板默认收起，支持常见 JPEG EXIF 信息和拍摄方向自动纠正。libvips、LibRaw、编辑和完整相邻图片预取尚未实现。

## 语言

目前支持简体中文和英文。首次启动跟随系统显示语言，未支持的语言回退到英文；也可在应用内即时切换，选择会保存在当前用户的本地设置中。

## 浏览与交互

- 便携版可在右上角“设置 → 用此应用打开图片”中注册 JPEG/PNG 打开方式，再进入 Windows 默认应用选择此应用；随后双击图片即可打开。注册可撤销，移动应用后需重新注册。请使用 self-contained 发布包，开发运行不注册。详见 [双击图片打开应用](docs/windows-file-association.md)。
- 重复启动会把图片交给同一用户、同一会话的已有窗口；无参数启动恢复窗口。请求受理后后续进程退出，不等待图片完整解码。
- 命令行和拖放支持多张图片，按选择顺序浏览、缩略图与播放；去重并略过无效项，每次最多 128 项。混合选择中的文件夹不会递归展开；单个文件夹仍按自然名称排序打开。
- `Ctrl+O` 打开图片，`Ctrl+Shift+O` 打开文件夹；支持拖入文件夹或通过命令行指定文件夹。
- `F6` 开始/暂停循环幻灯片，菜单可选 2/5/10 秒；加载期间和最小化时暂停计时，解码失败时停止播放。`Esc` 停止播放；画布聚焦时也支持空格。
- `← / →` 切换，`Home / End` 跳到目录首尾。
- 滚轮围绕鼠标缩放，拖动平移，双击切换适应窗口与实际大小。
- `0` 适应窗口，`1` 实际像素大小，`+ / -` 缩放。
- `F11` 全屏，`Esc` 退出全屏或收起信息面板；全屏会恢复此前窗口位置与状态。
- `Ctrl+I` 切换信息面板，`Ctrl+T` 切换缩略图带；点击缩略图打开对应图片。
- 文件夹列表在浏览期间复用；新增、删除或修改图片后，按 `F5` 刷新列表、缩略图与文件信息。
- 右上角菜单切换主题和语言。主题和语言选择均会持久保存。

缩略图带显示当前位置附近最多 9 张图片，后台同时最多解码 2 张，缓存上限为 24 张降采样图片。文件信息包括尺寸、格式、大小、修改时间和路径；存在 EXIF 时还会显示相机、镜头、拍摄时间、ISO、快门、光圈和焦距。主图及缩略图会遵循 EXIF Orientation（包含旋转和镜像），不改写原文件。

## 启动与内存

启动只创建必要的依赖与窗口，首次打开图片时才初始化 Skia 画布；不加载后台服务宿主、配置文件监听或命令行配置解析。命令行图片/文件夹在首轮界面布局之后处理。单实例检查先于窗口创建；仅主实例启动后台请求监听，文件关联服务和注册状态读取延迟到打开设置时。

主画布直接使用解码缓冲，不再保留另一份完整 Skia 像素副本。按 `宽 × 高 × 4` 计算，24MP（6000×4000）图片可少一份 96MB（约 91.6MiB）像素副本；这不是进程整体工作集的测量结果。WIC 按需解码，先验证尺寸再分配，全尺寸解码最多并发 1 个；过期排队请求会取消。目录枚举、排序与刷新在后台执行。

可在同一台 Windows 机器上比较前后两个 Release self-contained 包（PowerShell 7）：

```powershell
pwsh .\scripts\measure-startup.ps1 -AppPath .\artifacts\publish\win-x64\ModernImageViewer.App.exe
# 可选：带一张固定样本图片观察内存
pwsh .\scripts\measure-startup.ps1 -AppPath .\artifacts\publish\win-x64\ModernImageViewer.App.exe -ImagePath .\sample.jpg -OutputPath image-results.json
```

脚本默认记录 3 次可见窗口/输入空闲耗时及观察后的工作集、峰值工作集，不记录图片路径。它用于快速比较，不替代项目计划的冷启动、首帧或 P95 验收；大图在观察结束时可能仍在解码。Windows CI 会对发布包执行一次真实启动检查并上传 `startup-observation`；该单样本反映 CI 机器状态，不代表冷启动或性能提升比例。Linux 云端不报告 Windows 启动速度或实际工作集的百分比提升。

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

## 后续开发

应用端外部打开、窗口复用和便携关联已实现；MSIX 打包、签名和 Windows Shell 人工验收继续推进。随后依次完善：渐进预览和大图内存预算、紧凑布局与沉浸全屏、预算内相邻预取，再扩展格式、剪贴板和安全编辑/导出。

- [后续迭代路线图](docs/next-iteration-roadmap.md)：当前功能缺口、优先级、UI 和性能优化、完成标准。
- [Windows 文件关联方案](docs/windows-file-association.md)：安装版/便携版、用户默认应用选择、重复激活和验收步骤。
- [项目计划](docs/project-plan.md)：完整产品范围、架构、里程碑和性能目标。
- [进度日志](docs/planning/progress.md)：已完成的迭代记录。

路线图中的待办与目标不代表当前版本已支持或达标。

## License

[MIT](LICENSE)
