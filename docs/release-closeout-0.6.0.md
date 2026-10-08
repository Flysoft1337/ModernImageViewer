# 0.6.0 Release 收口验收

基线为已合并 `master 1f31355`，0.5 对照固定为 `9cdcde9`。本轮仅静态性能复测、WebP ANIM 背景兼容、实际 Windows 动画/DPI/安装验收及发布资料收敛；版本保持 0.6.0，不发布 GitHub Release。历史动画验证见 [上一轮记录](animation-multipage-validation.md)。

## 同机交替静态观察

Windows 10 19045/x64，i7-11800H、34,159,673,344 字节物理内存，真实 150% DPI。两版同机 SDK 10.0.303、self-contained win-x64 .NET 10.0.11。视口 1224×482 DIP / 1836×723 physical pixels，两版全部报告尺寸一致；原始报告保存产品版本和程序集 SHA-256，脚本 checkout 的 Commit 不冒充被测二进制提交。

统一 8 个 PNG（64×64 与 2048×2048 交替）、8 次邻图切换、4 次快速请求、每 4 次 F5、每 3 次 Actual Size 细化（一大一小）及 2 秒空闲；每版每次独立进程，无图片参数启动一次。一次预热对保存但不统计。前台采样严格拒绝测量中失活，显式测量完成信号后才排除正常关闭尾部；没有注入焦点、清 OS 缓存、强制 GC 或修改产品运行路径。

原批 `static-original` 因前台失活中止且缺少时点诊断，全部保留为未完成记录。新协议批 `static-original-complete` 的前七组通过，第八组候选浏览中有一次约 45.7ms 失活，整组排除；`static-original-replacement` 补一组相同 AB 顺序通过。八组有效对照为 AB/BA 各四组，每版 8 次启动/首图、64 次切图及 16 次细化，未挑选快慢样本。

| 阶段（ms） | 0.5 `9cdcde9` | 原 0.6 `1f31355` |
|---|---:|---:|
| 启动：窗口句柄与 input-idle | 736.75 | 732.90 |
| 首次可辨认画面：自收到请求起 | 118.83 | 129.34 |
| 目录导航可用：自收到请求起 | 147.00 | 156.76 |
| 每进程 8 次邻图切换均值的中位数 | 35.86 | 36.60 |
| 两次所需细化需求至绘制均值的中位数 | 101.86 | 32.16 |

阶段独立定义：启动采用进程启动时钟，其余采用既有打开/绘制观察时钟；paint 为 Skia 回调，不是显示扫描。细化包括一大一小，受是否已有可用细节影响，波动很大，不把上述差值称为优化幅度。仅此 PNG 场景未复现上一轮单次 CI 的近两倍切图差距；首图中位数偏慢约 10.5ms，不能宣称所有格式无退化、冷启动达标或 P95。

原始文件均位于 `artifacts/release-closeout` 对应子目录。可复测命令：

```powershell
.\scripts\measure-static-comparison.ps1 -BaselineAppPath <0.5-app.exe> -CandidateAppPath <candidate-app.exe> -Pairs 8 -WarmupPairs 1 -BuildKind SelfContained -OutputDirectory <new-empty-directory>
```

## 修复后候选的静态复测

同一机器、相同协议/视口/运行时再次比较0.5与本轮未提交候选。`static-candidate` 前三组通过，第四组0.5启动中发生约139.4ms前台失活，排除并保留；`static-candidate-continuation` 从相同AB顺序补五组全部通过。共八组有效对照，AB/BA各四组；产品版本仍基线SHA加未提交修改，实际应用程序集集合SHA以报告为准。

| 阶段（ms） | 同批0.5 | 本轮0.6候选 |
|---|---:|---:|
| 启动：窗口句柄与input-idle | 731.30 | 741.35 |
| 首次可辨认画面 | 104.78 | 126.98 |
| 目录导航可用 | 136.24 | 153.49 |
| 每进程8次切图均值的中位数 | 36.47 | 36.78 |
| 两次所需细化均值的中位数 | 33.10 | 33.76 |

切图中位数接近，首图偏慢约22.2ms，阶段耗时仍随进程变化。两批0.5细化中位数差异也说明小样本/cache状态的影响，不能将原候选32.16ms对101.86ms解释为稳定提升。未增加JPG/PNG帧会话、worker或timer；这次没有修改静态产品浏览逻辑，不能把复测写成静态性能优化交付。所有原始成功、失败及补测均保留；不承诺所有静态格式无退化。

## WebP兼容与回归

Skia原WebP动画路径将ANIM背景按透明处理。新实现读取有界RIFF/ANIM/ANMF元信息，不复制整个编码文件；借现有SKCodec读取局部帧位流，在BGRA画布按Source/Over合成，并仅将被disposal的矩形填回ANIM背景。Source透明像素正确清空原区域；透明、半透明及不透明背景均保留预乘alpha。首帧、重播与随机定位同一路径，代表图/缩略图/预取共享首帧逻辑；GIF/TIFF和统一播放控制不增加格式分支播放器。

新增29项固定像素回归（不是仅无异常）：背景三档alpha、局部首帧、Source/Over半透明、disposal、随机定位/重启/发布数组不可变、代表路径一致、EXIF1–8/缩放、线性RGB ICC→sRGB、有损ALPH+VP8、背景alpha无需VP8X Alpha标记、缩成零的帧、截断/尺寸/预算/取消/释放。修改既有WebP背景断言与实际规范一致，历史记录保留。样本说明见[固定样本](../tests/ModernImageViewer.UI.Tests/Fixtures/AnimationFixtures.md)。

单输出仍8MiB，暂态BGRA按当前显示、旧参考、新合成及局部解码各最多8MiB处理；局部方法退出后才创建方向输出副本。方向标记和native内部存储不纳入32MiB。WebP局部codec使用完显式释放，观察计数可暂到2（容器+局部），不把它判为泄漏；retained-byte只统计留下的参考，不等于暂态分配或native峰值。Over采用预乘sRGB，不提供线性光混合。

## 本地构建与开发验证

- Release solution构建0警告/0错误；完整测试核心187、UI/codec411，共598通过，1本机真实RAW样本跳过。29个新增通过案例，不累加代理子集。TRX在`artifacts/release-closeout/test-results`。
- 全solution format verify与diff检查通过；最初新增观察字段有CRLF格式错误，经限定文件format修复后通过。9项发布脚本离线测试、24项比较脚本断言通过。
- 在线NuGet含传递依赖审计完成，当前源未报告已知漏洞，无新增native依赖。100MP输出预览1600×1600/10,240,000B、缩略140×140/78,400B，单次265.92/214.34ms；不是P95或性能提升。
- 本轮生成双语深浅720×480帧/页截图，已核对深中文动画和浅英文分页；它们是测试RenderTarget截图，不冒充150%物理桌面截图。

## 真实Windows动画与DPI

同机self-contained候选使用Google公开动画 `https://www.gstatic.com/webp/animated/1.gif`（890,847B，SHA256 `8d39708c72335539431cb4093f84960ac08d15002a4cab2ae0fe4a5a4c82074e`）与 `1.webp`（380,850B，`8afc28fef9bba14b5eecbbc6bb8b4379dc96b6ebc3e0845607ced41b7dda337c`），加自有多页TIFF/静态JPG。样本仅下载到artifacts，不提交第三方图片；驱动复制到独占临时目录，不改变原样本。

`animation-real.json` 八轮/32次混合输入，再持续WebP180秒，总约234秒通过；没有强杀或丢失采样，没有强制GC。覆盖暂停/恢复、帧定位/重播、最小化冻结与恢复、用户暂停保持、当前页细化、Fit/Actual Size/缩放、全屏进出、F5、快速切图与关闭。WPF实际报告DPI各轴1.5；全屏1280×960 DIP / 1920×1440 physical，普通视口1224×482或614 DIP。四档DPI、鼠标锚点/平移与帧间保持由既有自动化回归覆盖，没有将它们称为真实100/125/200%设备检查。

采样主帧/参考各最多270,000B，native session最多1、decoder最多2（WebP容器加短时局部decoder）、timer/pin各最多1。WS/private峰值221,900,800 / 266,686,464B、handles736；末次采样206,491,648 / 171,474,944B、handles724。切静态后帧资源归零，清来源/空闲/关闭时wrapper、pin、bitmap、timer、session、decoder及retained/snapshot全部0。只证明此样本内项目计数有界并回收，不宣称整个进程不泄漏或跨设备长期达标。

4096×4096固定GIF与真实WebP/TIFF/JPG的两轮混合观察另通过（`animation-large.json`，持续段为WebP60秒）；随后专门将大GIF作为最后动画，`animation-large-gif.json`证实3帧GIF持续60秒（总约68.4秒），无强杀、关闭计数全部0。持续段主帧/参考各2,090,916B，采样峰值WS/private283,484,160 / 358,567,936B、handles735，末次203,284,480 / 162,603,008B、handles720，存在回落。源native工作区未包含在帧输出预算，不用这些数据承诺整个进程上限。

真实窗口Computer Use截图多次返回`FrameArrived/window capture timed out`，重置连接后仍失败；本轮实际设备的视觉截图及鼠标拖拽人工验收未完成，不能以RenderTarget截图替代。真实混合DPI多屏和跨设备低内存长期观察继续保留。

## 安装与最终CI边界

12类真实格式及动画/多页文件激活通过，单实例PNG/WebP重复请求正常。本机使用固定0.5与本轮0.6 self-contained安装器执行`check-installer.ps1`，`installer-smoke/lifecycle.json`确认0.5.0安装→0.6.0升级→0.6.0同版本重装；程序集hash从`d7fcfba3...1284a`变为`9f1de60d...697ef`，重装后不变，卸载identity一致，卸载及独占临时目录清理成功。安装前无真实ModernImageViewer安装，现有默认关联、portable及其他候选、用户文件保护断言通过；没有把同版本重装充作跨版本升级。

本轮通过[PR #34](https://github.com/Flysoft1337/ModernImageViewer/pull/34)复用原Windows CI，最终候选143e1df的[CI 37814873158](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37814873158)完整通过：格式、Release构建、核心187+UI/codec411=598通过/1本机RAW样本跳过、依赖审计、100MP/启动/浏览/动画观察、便携/安装版激活、真实0.5→0.6升级、同版本重装/卸载成功，Release job skipped。已合并为master 0849118；随后文档状态修正的最终master完整CI结果在该PR中记录，不能用前一轮绿色CI代替。PR CI和合并不会发布Release，不触发`publish_release=true`，不创建版本标签。

发布说明见 [0.6.0 Release Notes](release-notes/0.6.0.md)，支持范围见 [格式矩阵](decoder-support.md)，未完成能力集中于 [已知限制](known-limitations.md)。
