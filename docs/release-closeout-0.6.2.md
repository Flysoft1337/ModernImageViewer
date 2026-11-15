# 0.6.2 发布验收

## 最终公开记录（2026-11-15）

[v0.6.2正式版](https://github.com/Flysoft1337/ModernImageViewer/releases/tag/v0.6.2)于香港时间2026-11-15 09:09:57公开，draft=false、prerelease=false、Latest=true。PR #49、#50、#58已按依赖顺序合并，Tag精确指向master提交`9f70293e5796f1b10dc775eb87020c73a9ab93da`，其完整内容树与合并前b5ce79e一致；后续文档提交不改变该Tag或公开资产。

最终[CI43659387534](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/43659387534)手动输入publish_release=true、prerelease=false。格式、Release、依赖审计、635通过/1真实RAW样本跳过、100MP、启动、文件激活、普通浏览、动画/多页资源生命周期、包清单通过。历史与同源码包装各6对完整有效，Disposition=Valid、PerformanceAccepted=true、RiskAccepted=false，原10%门槛无放宽。发布事件自动RequireValid已在实际运行使用，没有以Inconclusive绿色放行。

| 最终CI配对比值中位数 | 固定历史基线 | 同源码散装/单文件 |
| --- | --- | --- |
| 窗口 | 0.81214761 | 1.02804109 |
| 首图 | 0.93671404 | 1.02150237 |
| 目录 | 0.78366164 | 1.01490010 |
| 邻图切换 | 0.86857222 | 0.99020959 |
| 首次细节 | 0.93671158 | 1.02149564 |
| 主动细节 | 0.62913611 | 0.86651249 |

包装主动细节两组每轮均值中位数17.93705/16.331075ms、邻图42.0866375/44.4413875ms；按逐对比值中位数判定，不能用组中位数之比替代。小样本不是P95或跨设备承诺，不能单独归因某一改动。

首次attempt1主检查及公开0.4/0.6.1升级成功，全新安装job132648032652超时取消，annotation明确超过10分钟；普通及强制取消后结束，job日志404、无该路lifecycle，具体卡点未知。保留失败证据，只对该job调用rerun；attempt2未重新执行已通过性能/构建或两路升级，GitHub复用其成功依赖。全新安装job132652159689在新隔离环境通过，worker30.236秒、退出0、checkpoint=Verified；随后原publisher成功，整体conclusion=success。不是重跑性能至绿色，也不把首次超时解释成已知环境或产品根因。

三路lifecycle均Success=true、UserFilesPreserved/UninstallVerified/CleanupSucceeded=true；公开0.4/0.6.1两路CrossVersionUpgradeVerified=true，包含同版重装、关联/激活与用户文件保护。三路目标安装器SHA256与公开Setup一致。本机未安装或修改实际关联。动画观察InstrumentationComplete=true，关闭后统计的帧/快照字节、pin、bitmap、timer、native session/decoder、PixelBuffer均归零，不证明整个进程无泄漏。

公开四资产已重新下载，与候选及GitHub资产digest/大小逐项核对，两个校验文件内容也正确：

| 资产 | 字节 | SHA256 |
| --- | --- | --- |
| ModernImageViewer-0.6.2-win-x64-Setup.exe | 61,865,581 | `26fa3458da74e2bbd033976e1a20d49ad9d3f844cec4e85c12705b8641490f2f` |
| ModernImageViewer-0.6.2-win-x64-Setup.exe.sha256 | 108 | `520c1901dfe01cee36362b3b716819992cbf7aaedbd9f5e176f10568e411bb9c` |
| ModernImageViewer-0.6.2-win-x64-Portable.zip | 80,148,747 | `a3881ad564b1d0df21da358f5f164a7904ff402f9db1913cf78c77a80d24e73d` |
| ModernImageViewer-0.6.2-win-x64-Portable.zip.sha256 | 111 | `b6784c4b1ec42e2d5b10d57b567df31dd0a54607cdb3965b51f6157f4d93a36e` |

证据保存在原Actions运行及ignored的`artifacts/release-0.6.2-final-20261115/`。最终源码没有引入dependabot依赖升级、扩大功能或提升版本。此前有效性能失败、失焦无结论和人工验收缺口继续保留；下文的暂停/待验是历史状态，以本节最终结果为准。

## 2026-11-15：授权有效包装通过后合并发布

用户明确要求包装性能通过后合并发布0.6.2。以df875c4相同产品源码重新构建两种包装，原入口`-Pairs 6 -WarmupPairs 1 -RequireValid`完整通过：6有效对、Valid、PerformanceAccepted=true、RiskAccepted=false。六项配对比值中位数均不超过1.1，切图1.04842117、主动细节0.99581196。原始数据在ignored的`artifacts/release-0.6.2-packaging-20261115/`，完整统计见[性能记录](wic-preview-performance.md#2026-11-15发布授权后的严格包装对照)。不改判旧有效失败或失焦无结论。

补齐发布硬门槛：手动发布事件自动RequireValid，无结论不能再作为成功发布依赖；普通观察规则、workflow、计时和10%标准不变。51项比较断言、9项publisher边界通过。同步发布说明中的像素复制修复、635/1实际验证及人工边界；不增加功能或改变版本。

接下来按PR #49→#50→#58依赖顺序合并；最终master的完整CI、历史和包装有效对照及三路隔离安装均通过后才发布。最终提交、CI与公开四资产尚待记录，不提前标记完成。

## 2026-11-15：绘制像素复制修复，包装性能仍待有效验收

d7a136c的CI43654805397同源码包装主动细节6对+10.7056%超原10%门槛，三路安装跳过，仍为有效失败。本批仅对共享只读像素包装标记immutable，避免DrawBitmap转换SKImage时再次整图复制；新增1项实际地址/绘制颜色/最后引用释放回归，未改decoder、预算、样本、计时或门槛。

本地format/Release0警告0错误、完整635通过/1RAW跳过。既有真实WPF非激活harness 1预热+6对交替，大图需求→绘制38.30→33.13ms，发布→绘制14.89→9.42ms；非CI性能验收。原包装协议仅2对后ForegroundWindowLost，Inconclusive、未接受性能，未重跑；完整证据和边界见[性能记录](wic-preview-performance.md#2026-11-15共享只读像素消除每次绘制的整图复制)。远端结果如下，不合并或发布，不建Tag/Release。

修复97db6e3的[CI43657166316](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/43657166316)已最终success：635通过/1真实RAW跳过，格式/构建/审计/100MP/启动/激活/浏览/动画观察通过；历史6对Valid、无风险豁免。三路隔离安装lifecycle均Success、最终Verified、退出0，公开0.4/0.6.1升级及全新安装的用户文件保护/卸载/清理成功，目标Setup哈希一致。

包装性能仍欠验收：预热ForegroundWindowLost、0有效对、Inconclusive/PerformanceAccepted=false。原入口对无结论警告退出0，故整体绿色不能证明包装六项通过；不改规则、不重跑取绿，继续保留d7a136c的有效失败，不合并/发布。远端观察及安装证据在ignored的`artifacts/ci-43657166316/`，详见性能记录；文档后续提交不改变本次已验证的产品源码。

## 范围和来源

用户于2026-10-19授权合并PR并发布0.6.2正式版。PR [#48](https://github.com/Flysoft1337/ModernImageViewer/pull/48)已合并至master 7979bca，包含失败导航恢复、普通WIC无缩放预览复用及常规PNG临时native会话。产品实现及边界见[性能记录](wic-preview-performance.md)与[发布说明](release-notes/0.6.2.md)。本次发布准备只调整版本、文档与既有CI升级基线，不扩功能。

## 已完成的PR验证

[CI39191776912](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/39191776912)对应c53ce0c，619通过/1真实RAW样本跳过。format、Release、依赖审计、100MP、startup、activation、browsing、animation/multipage、包清单及三路隔离安装/升级/重装/卸载通过。

历史与包装均6对Valid、六项原10%门槛全部通过，无风险批准。包装邻图切换配对比值中位数1.00620854，主动细节0.90809349；历史分别0.86807740和0.67302226。原失败不改判，小样本不是P95或跨设备承诺。0.6.1风险批准不适用于此版本。

## 本地发布准备验证

0.6.2版本的format verify、Release构建（0警告0错误）、完整619通过/1真实RAW样本跳过、依赖审计通过。既有9项publisher边界与静态观察脚本边界通过；安装脚本解析及0.6.1参数检查通过，未在本机执行安装。最终Windows观察和隔离安装以发布CI为准。

## 最终发布流程

同一最终master提交通过既有CI后由publisher发布正式版/Latest，Tag v0.6.2指向该提交。三路安装仍在独立disposable GitHub-hosted Windows：全新安装、真实公开v0.4.0升级、真实公开v0.6.1升级，均包含同版重装和卸载；不在本机操作用户安装或文件关联。

公开0.6.1升级基线为ModernImageViewer-0.6.1-win-x64-Setup.exe，61,872,144字节，SHA256 `2f1130974ec61d583f5b56e57e18ed9fb382a707a49b10ba1a271fcab5df1ba3`，已核对GitHub公开资产元数据，并下载核对本地SHA256及大小，CI下载后再次校验。保留固定0.6.0历史性能基线320886e、同机AB/BA、6对与原10%门槛。完整失焦报告仍按原规则记Inconclusive，不称性能通过；有效超阈值及真实错误仍阻断。

最终发布CI未通过，未发布；实际结果见下节。

## 最终候选CI失败（2026-10-19）

最终源 `78c9b23c2bc33af96c70daf7e840fd1b4716fe6b` 的[发布运行39195941435](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/39195941435)失败，未创建v0.6.2 Tag/Release，Latest仍为v0.6.1。619通过/1真实RAW样本跳过；format、Release、依赖审计、100MP、启动、文件激活、普通浏览、动画/分页生命周期、打包及目录/native清单检查通过。

固定0.6.0/候选历史对照为6对完整有效样本，原始采集成功、无失焦；门槛检查为Failed，`PerformanceReviewRequired:RequestedDetailMeanMs`，PerformanceAccepted=false。主动细节配对比值中位数1.3453819793（+34.54%），超过原10%规则；A/B每轮均值的中位数为22.8625/27.925025ms。两种统计量不能混用，亦不能因每组仅约5ms差值而改判达标。

| 六对配对比值中位数（候选/固定0.6.0） | 结果 |
| --- | --- |
| 窗口可见 | 0.77277156 |
| 首个可辨认画面 | 0.89852863 |
| 目录导航 | 0.91190806 |
| 邻图切换 | 0.89424791 |
| 首次细节 | 0.89853072 |
| 主动细节 | 1.34538198，失败 |

失败后包装性能对照未执行，三路隔离安装和publisher均跳过，不称安装/升级通过。此前PR CI成功不能替代本次最终提交的检查。保留该失败，不重跑到绿，不延用0.6.1风险批准；按原规则暂缓发布，用户已选择先修复、暂缓发布，不批准本次风险。

已生成但未公开的同提交候选：

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| ModernImageViewer-0.6.2-win-x64-Setup.exe | 61,858,088 | `63723ad0b37e1635d38ff6af1c299d7fa012dec6c0a7d3aea8511fba1d2722fa` |
| ModernImageViewer-0.6.2-win-x64-Portable.zip | 80,149,711 | `dd96fce5a1bf840fd93c729c7e49bda3f88b331cb3012bad87995f00653774be` |

四个候选文件的名称/大小/哈希已下载核对，安装器ProductVersion为0.6.2；这些不是公开Release资产。原始CI报告与完整日志保存在该Actions运行；本地副本位于ignored的`artifacts/release-0.6.2/ci/`。100MP单次为1600²/10,240,000字节/206.16ms及140²/78,400字节/143.82ms，不是P95或对照收益。

## 后续主动细节修复候选

用户选择先修复后，已将普通PNG有预算完整细节复用既有临时WIC会话，特殊输入回退保持不变。本地622通过/1跳过、format/Release通过；六对非激活窗口诊断中完整解码27.75→18.79ms、需求到绘制44.75→35.97ms，GC2仍每次增加1。该结果不能代替原前台CI门槛。完整方法与后续CI见[性能记录](wic-preview-performance.md)，CI39205852804两组各6对有效性能和完整622通过/1跳过，原10%规则全部达标；整体因0.6.1升级smoke旧布局假设失败，正在修正验证脚本并复用CI，不改安装器产品。发布继续暂停。

## 后续有效性能仍需收口

源f334b2c的CI39207120698再次在历史主动细节6对有效样本失败，配对比值中位数1.12258481（+12.26%），不能用两组耗时中位数15.58/14.90ms改判。包装和安装跳过，仍不发布。继续优化时已撤回未在实际浏览形成收益的数组/像素转换实验；仅保留同源静态细化的UI通知收紧，局部进入decoder约少0.33ms、整体绘制持平，不宣称该CI问题已解决。细节见[性能记录](wic-preview-performance.md)。

## 2026-11-15：性能阶段通过，升级超时待定位

后续诊断脚本提交3cc35b2的[CI43653095970](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/43653095970)在Test失败：633通过、1失败、1RAW跳过；离线检查点回归、格式与构建通过，性能和安装阶段均未执行。失败是目录错误回归refresh=true分支在新图片目录索引仍运行时期待普通提示，实际显示原有优先级更高的扫描提示；OpenPathAsync只等待像素，不保证目录索引完成。

本批仅修正该已有用例的时序：事件屏障固定新索引在途，旧F5读错误返回后确认新图保留、新索引仍运行；再释放索引并等待WaitForIndexingAsync，确认新消息保留。finally释放两路并等待退出，不增加sleep、不改生产代码、状态优先级或原有消息断言，也不增加用例数量。定向2例、format verify、Release0警告0错误及完整带coverage的634通过/1RAW跳过；新CI结果待实际执行，原失败保留。

PR #58源码2cd2eb6的[CI43648378250](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/43648378250)最终cancelled。主检查634通过/1真实RAW样本跳过，历史与包装两组各6对Valid、RiskAccepted=false；包装主动细节+9.6891%，接近原10%门槛，先前+38.23%失败保留，不宣称根因已经解决。新阶段记录与逐项性能见[性能实测](wic-preview-performance.md)。

同次全新安装及公开0.4升级job/lifecycle通过，重装、激活、关联/用户文件保护、卸载和清理有实际证据。公开0.6.1升级job 132613973350超时取消：GitHub check annotation明确“The job has exceeded the maximum execution time of 10m0s”；该路日志下载为BlobNotFound、没有lifecycle。只证明验收没完成，不能判定具体安装阶段或产品缺陷，不能用另外两路成功替代。

本批给现有隔离安装脚本补最小诊断，不改安装器产品、超时或验收断言：

- worker到达安装、校验、激活、升级、重装、卸载阶段前，记录固定阶段名与UTC时间。只保留一个checkpoint.json和一个同名pending文件，单条阶段名最多64字符，不含安装路径或异常文本。
- 外层worker启动及转发检查点时，额外发出GitHub notice检查注释；artifact不可用时可尝试从check annotations获取最后已回传阶段。主机/服务失联或注释未送达时仍可能没有证据，不保证日志一定保留。
- 检查点文件被占用或没有写权限时继续原验收、保留正常输出；原始安装/激活/保护断言仍决定失败。阶段名或“Verified”注释都不能代替worker退出状态和成功lifecycle。
- 新脚本回归只AST提取日志helper，使用自建临时目录检查快照替换、无success输出污染、notice格式、非法标记拒绝、文件占用失败隔离及解除占用后恢复。不运行安装器/viewer，不访问注册表或用户关联。

本地脚本语法、检查点回归、原49项比较边界/9项发布边界及diff检查通过。首轮时间断言遇到PowerShell自动将ISO时间转为本地DateTime，已改读原始JSON时间字符串；审查发现可选快照I/O可能改变验收结果，已修正并补真实文件占用回归。因本批只改诊断脚本与CI接线，复用已通过的634/1产品测试，不重复本机构建/WPF测试；新源码的Windows CI以实际运行记录为准。

检查点回传本身还需GitHub-hosted Windows验证。本轮继续使用原完整CI，没有新增workflow/矩阵，没有对旧失败运行重跑至绿色；原性能/安装门槛和0.6.2发布暂停保持不变。

## 未完成人工验收

本次没有新增真实桌面人工验收证据。真实窗口视觉/鼠标、Shell默认应用、混合DPI多屏及跨设备低内存长期浏览继续待验。自动化像素/资源计数与安装检查不能替代这些项目，亦不证明整个进程绝不泄漏。其他格式、预算和分发限制见[已知限制](known-limitations.md)。
