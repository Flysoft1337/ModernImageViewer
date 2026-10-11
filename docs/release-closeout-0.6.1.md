# 0.6.1 补丁收口验收

日期：2026-10-11。固定基线`320886eff025e2f6e87d75966c89600ec5b17ab1`，开发分支`release/0.6.1`。只收口既有能力，不新增格式或编辑功能，不创建Tag/Release，不修改操作者真实安装、关联、配置和图片。

## 起点与决定

重新查询GitHub确认PR #39/#40已合并，公开v0.6.0为Pre-release，最新稳定版仍为v0.4.0。master [CI #113](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37932248817)的604通过/1 RAW跳过不能代替完整CI，其最后性能观察失焦失败。PR #41最终[CI #115](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37940365262)被取消；Dependabot的绿色检查不是产品CI。

[PR #41](https://github.com/Flysoft1337/ModernImageViewer/pull/41)不纳入本补丁：额外减少约1–2MiB，缺少最终完整CI，收益不足以扩大本补丁风险。保持OpenTK/GLFW依赖声明及随包资源；不声称移除方案已验收。

## CI修复

原调用方try/catch不能捕获子脚本`exit 1`。新入口以隐藏pwsh子进程捕获退出码、并发排空输出，执行上限600秒，输出排空上限5秒。#115停滞原因没有完整日志证明，不把执行上限描述为已定位其根因。

先验证completion marker及实际浏览报告，再分类失焦，避免用失焦掩盖细节未完成/坏报告。仅`ForegroundWindowLost`/`ForegroundNotObserved`、退出1且所有已执行driver原始报告完整时输出Inconclusive；不计有效性能，不写成验收通过。缺报告、超时、结构错误、产品异常和有效配对中位数超过现有10%规则仍失败，不自动批准性能豁免。

复用现有Windows job：固定pre-patch master与0.6.1均在同机同SDK/runtime构建，3对AB/BA加1对预热；另以同一候选源码比较散装/压缩包。严格保留前台、窗口/DPI一致和二进制身份检查。原始报告及独立disposition上传，不用小样本声称P95。

## 本地证据

- Windows 10.0.19045 x64，SDK10.0.303。restore、format verify、Release构建0警告0错误、依赖审计通过；完整604通过/1真实RAW样本跳过。首次完整运行发现本地化测试的List在后台偏好通知期间被枚举，改线程安全通知队列并释放其VM/coordinator后复跑通过；产品核心代码未改变。
- 4944×6456、16位RGB PNG只读探针：预览1次、完整细节1次，随后20次请求复用同一PixelBuffer；完整127,673,856字节，关闭后像素数/字节均为0，无强制GC。SHA256前后均为`78a95dd5aebcee4d407f2a15c3b236b74d04ca929cd6d16c0c0b687e04953fd2`，大小/修改时间不变。预览1484.04ms、细节1266.93ms仅一次coordinator观察，未测鼠标或WPF绘制，不是性能对照。
- 100MP观察：预览1600×1600/10,240,000字节，缩略图140×140/78,400字节；311.50/455.57ms仅一次本机codec观察。最终CI重新执行既有100MP脚本。
- 复用既有菜单真实WPF模板/主题/图标/勾选/快捷键和四档模拟DPI、viewport、取消/过期结果、缓存/资源、GIF/WebP合成/TIFF、编辑导出/原图保护和剪贴板测试。未重复增加xUnit数量；脚本边界补39项断言及原有9项发布脚本测试。
- 本地0.6.1包：Setup76,746,480字节，便携ZIP82,541,204字节，载荷116,946,112字节/49文件/根2/app13；PE版本0.6.1.0及4份图像native哈希通过。ZIP SHA256为`8d58622b68433e458cc8dc578c9d08d7d6a2aa3419931e373d245a9e0e89d671`，Setup为`002150fe7d37658133644be5c298bbb6513f152f534ac74b349e867d5c7af843`。这是未提交源码的本地构建，最终master产物哈希以该提交CI artifact为准；载荷不等于安装后占用。

## 隔离安装验证

仅GitHub-hosted disposable Windows可执行安装smoke，本机执行直接拒绝。三个路径：公开0.4→0.6.1、公开0.6→0.6.1、0.6.1全新安装；均检查重装、激活、快捷方式目标、入口/程序集/注册版本、卸载、便携/外来关联保留、未知用户文件及修改过的旧payload保护。升级刻意不选fileassoc以检查已有身份迁移。目标安装器与便携包名称/PE版本/SHA256必须一致。实际三路结果及落盘占用由候选CI补录，不能引用0.6.0布局smoke冒充版本升级。

## 独立真实桌面记录

本机使用真实MainWindow、资源和DI创建720×480隔离WPF窗口，配置写入专用artifacts目录，绕过单实例，不碰用户profile；该harness不用于性能测量。Computer Use实际识别窗口和中文空状态可访问性树；截图两次分别失败`FrameArrived timed out`、`window capture timed out`，鼠标点击失败`coordinate input geometry is unavailable`。不能把RenderTarget截图或可访问性树作为真实视觉/鼠标验收。

| 人工项目 | 状态 |
|---|---|
| 菜单视觉、屏幕边缘子菜单、鼠标悬停/点击 | 未验证：截图/输入几何失败 |
| 真实100/125/150/200% DPI、混合屏 | 未验证；已有自动模拟覆盖 |
| 普通浏览、滚轮/拖拽、大PNG反复缩放 | 未完成真实鼠标验收；探针/自动回归另记 |
| GIF/WebP动画视觉、TIFF真实翻页 | 未完成视觉验收；固定像素/生命周期自动检查另记 |
| 编辑窗口真实PNG/JPEG/WebP导出 | 未完成人工；自动像素/安全提交回归另记 |
| Shell双击、公开升级、便携共存 | 未完成人工；隔离安装/激活smoke另记 |
| 跨应用透明剪贴板 | 未完成人工；注入IDataObject覆盖另记 |

## 发布判断

完整最终master CI尚待执行时为NOT READY FOR RELEASE。全部核心/打包CI通过且无P0、但真实视觉/鼠标与有效压缩性能仍缺证据时，最多READY FOR PRE-RELEASE ONLY，不能升级为稳定验收。最终提交/CI链接、产物哈希与实际三路结果在本轮交付中给出，公开资产保持不变。
