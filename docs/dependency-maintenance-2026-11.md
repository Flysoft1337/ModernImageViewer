# 2026-11依赖PR收口

用户要求处理所有剩余PR，基线master08749ab（公开v0.6.2仍指向9f70293）。将#53–#57的原始提交保留并合入同一集成分支，避免DI与Abstractions分开升级或重复完整CI。不改应用/SDK版本、功能、解码预算、性能样本或门槛，不重发v0.6.2。

| 原PR | 包 | 原版本 | 候选版本 |
| --- | --- | --- | --- |
| #53 | SkiaSharp / SkiaSharp.Views.WPF | 4.153.1 | 4.156.0 |
| #54 | Magick.NET-Q8-x64 | 14.17.2 | 15.1.0 |
| #55 | Microsoft.Extensions.DependencyInjection | 10.0.4 | 11.0.0 |
| #56 | Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.4 | 11.0.0 |
| #57 | Microsoft.NET.Test.Sdk | 18.3.0 | 18.12.0 |

两项DI11包nuspec均包含net10.0目标；TestSdk18.12提供net8.0目标，可用于当前net10测试。项目不使用Magick15已移除API，实际全量构建/固定像素测试验证，而非仅依据主版本推断兼容。Skia动画、共享只读像素/最后引用释放、SVG及编辑导出继续复用现有回归，无新增测试堆数量。

Magick15.1.0实际包的Apache-2.0正文、Copyright与原来源文件一致；同步第三方README版本、源码commit、native大小/hash及Notice固定源码链接。必须带入实际15.1.0完整Notice，包含libheif/libde265 LGPL，不将native栈概括成纯Apache。Skia实际包LICENSE/THIRD-PARTY-NOTICES继续由既有打包入口收集；解码支持文档同步候选依赖版本，公开旧资产不变。

本地还原、format verify、Release0警告0错误、完整635通过/1真实RAW样本跳过、漏洞审计通过。100MP单次预览1600²/10,240,000字节/235.47ms、缩略图140²/78,400字节/193.63ms，非对照收益或P95。候选47文件包布局/native哈希验证通过，许可证来源README已重新封包带入并再次通过校验。

严格本机“公开0.6.2 vs 候选”原6对+1预热比较完成2对后失焦：原采样ForegroundWindowLost，RequireValid实际阻断，PerformanceAccepted=false。该记录不能称性能通过，也不据此判断产品退化。旧#54/#56历史CI失败发生在性能比较，其余旧PR也有升级失败/安装取消，不能沿用旧绿勾；本轮最终Windows CI、性能及三路隔离安装以新报告为准，未通过前不合并。

## 集成PR与Windows结果

[PR #59](https://github.com/Flysoft1337/ModernImageViewer/pull/59)保留五个原PR的提交，源码`6a02275d72a8e79719d715a3d1e9e77b6b83110e`。独立审查未发现产品代码或预算/性能规则变化，DI11/net10目标、Magick15实际元数据与notice一致；Apache正文及Copyright无需修改。后续仅同步文档。

[CI43663168509](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/43663168509)的格式/Release/635通过1跳过/依赖审计/100MP/启动/激活/普通浏览/动画多页及包检查通过。动画关闭后统计的像素、pin、bitmap、timer、native会话/decoder及保留字节均归零，不证明整个进程无泄漏。

历史与同源码包装各6对完整有效，Disposition=Valid、PerformanceAccepted=true、RiskAccepted=false；六项原10%门槛均通过，未放宽或重跑性能：

| 配对比值中位数 | 固定历史基线 | 同源码散装/单文件 |
| --- | --- | --- |
| 窗口 | 0.79877317 | 1.01867889 |
| 首图 | 0.95041602 | 1.03415995 |
| 目录 | 0.78425839 | 1.03278547 |
| 邻图 | 1.01787172 | 1.01863703 |
| 首次细节 | 0.95041573 | 1.03415581 |
| 主动细节 | 0.78102313 | 1.04649121 |

全新安装和公开0.4升级两份lifecycle均Success=true，用户文件保护、重装/卸载及清理成功，目标Setup SHA256均`63DEA1F9115B2DC7CB4751599AB1D206C1996A477EFB840A630CD2D88492BCDA`。

**公开0.6.1升级未完成，PR未合并。** attempt1的job132659535021与attempt2的job132662217388均超过5分钟步骤时限仍无结果，REST日志404、无该路lifecycle；主动普通/强制取消后，最终annotation仅确认由用户账号取消，不能据此宣称GitHub自动超时或判定产品根因。只补跑该job，已通过性能/构建及两路安装由GitHub复用，未重新执行。

第三次job132665140235自02:03:31 UTC执行至02:09:41仍无结果/日志，超过原5分钟时限；再次请求普通/强制取消并停止补跑，最终取消状态仍待GitHub回传。Computer-use实时日志检查因无法可靠识别浏览器网址被停止，未获得额外日志，未绕过限制。三次均不能当升级通过，也不能证明具体依赖不兼容；不增加猜测性的重试或安装兼容层。

PR #59转为草稿，原#53–#57仍保留，待实际升级验收完成后再决定合并。原始证据位于ignored的`artifacts/dependency-updates-20261115/`，包括三轮状态、失败API/取消记录、有效性能和两路安装证据。未操作本机安装/关联，公开v0.6.2 Tag及四资产已核对未变，未发新版本。

## 2026-11-19 复验

候选分支 `bdb6dc4` 已合入 `master` 的 `2280e2e`（含 ICO 多尺寸），保留原始 #53–#57 提交。GitHub 为 PR 构建的合并提交 `c54399b` 与候选分支同树。本机 Release 构建无警告、无错误，646 项测试通过，1 项真实 RAW 样本测试跳过。NuGet 审计未报告漏洞。

[常规 Windows CI](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44493809947) 首轮 F5 刷新测试超时，未生成安装包。第二轮全量测试、依赖审计和固定 0.6.0 历史对照通过，历史对照有 6 对有效样本。同源码包装对照缺少第 6 对的浏览报告，仅完成 5 对。第三轮历史对照同样缺少浏览报告，仅完成 1 对。两轮均因此跳过安装任务。

独立验证使用第二轮的 Setup（artifact `14514547180`，Setup SHA-256 `17323B6354EA3FA1FDD81E148D661DED779A8982C97742CA190C8E287BD99BA0`）和样本（artifact `14513934743`）。[第一批 Windows 2025](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44496662299) 全新安装、同版本重装、卸载、清理及用户文件保留均通过。公开 0.4.0 和 0.6.1 两路升级被 GitHub 判定超过任务 10 分钟时限，均无生命周期报告。

[首次独立包装复验](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44498309803) 对应同一份候选可执行文件及同源码平铺包。第 1 对平铺版缺少浏览报告，未得到完整样本。独立验收工作流均在临时分支，候选 PR 无工作流变更。

[直接执行安装脚本的 Windows 2025 验证](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44499127174) 已通过公开 0.6.1 升级。生命周期按顺序记录 `BaselineInstall`、`CrossVersionUpgrade`、`SameVersionReinstall`，关联迁移、用户文件保留、卸载和清理均通过。目标 Setup 和已安装程序 SHA-256 分别与全新安装报告一致。同批 Windows 2025 的 0.4.0 升级被 GitHub 判定超时，API 日志缺失。使用原有超时包装器的 [Windows 2022 两路升级复验](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44498317229) 也均被判定超时。

[有效的独立包装对照](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44499939533) 完成 6 对，双方应用版本标识均为 `0.6.2+c54399b4c157860b3336e6204426880f3c61f666`。单文件包 / 同源码平铺包的切图耗时配对比值中位数为 `1.15015854`，超过 `1.10` 门槛。其余五项配对中位比值在门槛内。`Disposition=Failed`、`PerformanceAccepted=false`，原因 `PerformanceReviewRequired:NeighborSwitchMeanMs`。该对照检验同源码的包装形式，无法单独归因于本次依赖升级。
最后一次 [Windows 2022 直接执行 0.4.0 升级](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/44500063395/job/135477170418) 同样被 GitHub 判定超过任务 10 分钟时限，未上传生命周期报告。0.4.0 的候选升级仍未验收，通过的是全新安装与 0.6.1 升级。#59 保留草稿，#53–#57 保持开放，0.4.0 升级与包装切图结果仍需处理。
