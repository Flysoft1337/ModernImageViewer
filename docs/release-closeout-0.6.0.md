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

## 正式发布前补验（2026-10-09）

起点master afbea62的[完整CI 37816669301](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37816669301)通过，598通过/1跳过；本节记录之后的发布前补验，不发布标签或Release。

### 首图差异定位

上一批八组有效配对首图差值（候选减0.5，ms）为+2.25、+30.03、+21.29、+23.13、-29.81、+3.71、-30.73、+36.84。22.2ms是两版中位数之差，不是每对固定增加；原成功/失败记录全部保留。

新增仅显式观察启用的LoadingPublished、PreviewPublished时点，使用原单调时钟；普通运行没有日志或诊断订阅。两版重建时仅共同添加相同观察补丁（0.5还沿用前轮完成marker），产品静态浏览路径未改，实际程序集hash保存在`static-diagnostic/index.json`。相同机器/样本/视口、SDK10.0.303与self-contained .NET10.0.11，八组AB/BA各四组、一次预热排除，无失败或补测：

| 阶段中位数（ms） | 0.5诊断包 | 0.6诊断包 |
|---|---:|---:|
| 输入受理→Loading通知 | 8.87 | 9.06 |
| Loading→Loaded预览通知 | 40.20 | 40.96 |
| Loaded预览通知→首绘 | 58.95 | 58.19 |
| 输入受理→首绘 | 109.47 | 109.21 |

各段分别取中位数，不应直接相加；Loaded时点包含调度/解码/UI通知，不是native纯解码计时，首绘也不是显示扫描。诊断补丁可能影响时序，不能用此结果独自排除回归。

随后再次使用前轮完全未修改的0.5/修复候选二进制，八组AB/BA各四组、一次预热排除，全部前台校验成功，无丢弃/补测。程序集集合SHA256与前轮`static-candidate-continuation`逐一相同：0.5为`9C72FC067DDF34C7DDF626D66A280C09D0E0EDAE42E52B3687BED06C35AAAF86`，候选为`A16AF8374EE1505915B6C87EEB5A954BCA730C7F2FE2D9E718A06124387AE2B0`；结果在`static-repeat-pristine`：

| 阶段中位数（ms） | 原0.5包 | 原0.6候选包 |
|---|---:|---:|
| 启动窗口/input-idle | 738.70 | 756.35 |
| 首绘 | 106.71 | 108.11 |
| 目录导航可用 | 130.20 | 156.55 |
| 每进程8次切图均值 | 29.16 | 29.06 |
| 两次所需细化均值 | 23.70 | 24.76 |

首绘配对差值-3.60至+3.38ms，配对差值中位数+0.22ms，配对比值中位数1.0021。诊断包与原包复测均未复现稳定+22.2ms；该数字不能归因为已证实的固定实现开销，也不能称具体波动原因已经查明。目录导航在本批仍偏慢26.35ms，但诊断批反向为154.27/146.03ms，该阶段也不能据单批确认稳定回归。

代码对照发现普通PNG新增同步完成的帧接口探测、6字节GIF头检查及帧UI绑定/通知；没有创建PNG帧会话、播放timer或动画native解码。首次JIT、绑定和绘制调度可能影响首图，具体成本没有由源码证明；不为漂亮数据改变普通运行、不强制GC、不把本批写成静态优化或P95。

**发布规则决定：** [性能规则](project-plan.md#112-测试层级)要求固定机确认超过基线10%的回归须记录原因和批准。本轮未确认稳定超过10%的首绘回归，因此没有接受这项回归，也没有登记豁免批准；保留前后批次证据和导航时点疑点。若后续更广样本/固定机再次确认超过10%，应先修复或向发布负责人提交明确理由和批准，不能用本次小PNG场景解除其它格式/设备的门槛。

### 真实桌面人工验收缺口

本轮再次调用Computer Use：真实查看器截图`FrameArrived timed out`，系统Explorer恢复后截图也`window capture timed out`；鼠标元素点击返回`coordinate input geometry is unavailable`，文件框set_value不可设置。Ctrl+O实际显示原生文件对话框；以既有真实GIF参数打开窗口后，辅助功能帧号变化且显示100帧与播放按钮，Alt+F4关闭后窗口消失。Ctrl+Space时焦点在帧输入框，未确认暂停；F11没有可核实的视觉结果。以上是部分键盘/辅助功能证据，不是完整人工验收。

| 必验项目 | 实际操作者步骤与通过条件 | 状态 |
|---|---|---|
| 打开/切图 | 对话框打开JPEG/PNG和大图，连续左右切图、F5；首图可辨认，无旧图回写/闪空 | 待人工验收 |
| GIF | 打开真实透明/局部更新GIF，观察画面连续合成；用按钮暂停/继续/重播/定位，切图旧动画停止 | 待人工视觉/鼠标验收 |
| WebP | 真实动画及非透明ANIM背景样本，检查透明边缘/局部销毁，暂停/继续与重播 | 待人工视觉/鼠标验收 |
| TIFF | 按钮与Ctrl+PageUp/PageDown翻页，输入页码及Ctrl+Home/End，异尺寸页面正确，左右键仍切文件 | 待人工验收 |
| 缩放/平移/DPI | 150%实际设备Fit/1:1/滚轮鼠标锚点缩放，放大后拖拽，播放时视口不重置；有条件再跨DPI屏幕 | 待人工验收 |
| 全屏 | 播放中F11进出，缩放/平移保留，闲置隐藏/鼠标唤回，退出后窗口恢复 | 待人工视觉/鼠标验收 |
| 文件关联 | 安装候选后用Shell“打开方式”选择本程序，JPEG/GIF/WebP/TIFF真实双击与重复激活；不抢占默认关联 | 待人工Shell验收 |

操作样本可复用本地`artifacts/release-closeout/real-samples`与`format-fixtures`，只操作测试图片；操作者需记录包版本/hash、Windows/DPI、项目结果与日期。0.5→0.6自动升级已通过，不等于公开0.4→0.6直升或Shell人工验收。正式发布前此表仍需真实操作者填写，当前没有宣布已通过。

### 发布资料与验证

Release Notes改为公开v0.4→v0.6累计更新，包含日常编辑八项调整/七种标注/预设/安全PNG JPEG WebP新文件导出、静态AVIF/HEIF与九种RAW内嵌预览、受限SVG/JPEG XR、排序/方向/剪贴板/偏好及自适应浏览，保留动画/分页和实际限制。本轮不增加上述功能。

本地Release发布两版成功；完整测试核心188+UI/codec411=599通过/1真实RAW样本跳过，增加一项时点/过期像素回归并调整既有样本数量断言。完整format verify与diff检查通过。本轮末次提交完整CI结果以补验PR中对应提交记录为准，不以起点绿色CI代替。

## 0.6.0公开测试准备（2026-10-09）

[PR #35](https://github.com/Flysoft1337/ModernImageViewer/pull/35)已合并为master 5c024fb，其[完整Windows CI 37876822463](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37876822463)成功：599通过/1真实RAW样本跳过，格式、Release构建、审计、100MP、启动、文件激活、浏览、动画及实际0.5→0.6升级/重装/卸载通过。动画无强制终止/丢失采样，关闭后项目计数全部0；升级hash改变、重装不变，卸载/清理成功，Release job skipped。

用户随后要求准备0.6 Pre-release。本轮仅调整资料，版本保持0.6.0，不修改产品、测试或CI矩阵。既有手动工作流publish_release=true/prerelease=true执行一轮最终发布检查后，才创建v0.6.0预发布及四个校验资产；实际发布提交与结果由Release提交标记/Actions记录，Latest保持v0.4.0。重复文档PR/push CI可以取消，最终发布运行保留全部必要检查。

公开测试不关闭七项人工清单，不登记性能豁免。真实视觉/鼠标、Shell双击、公开0.4直升、混合DPI与跨设备低内存验收仍有缺口，首图波动原因/导航时点疑点继续关注。公开资产不可覆盖，后续包修改使用新版本号。
