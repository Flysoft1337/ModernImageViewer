# 0.6.2 发布验收

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

## 未完成人工验收

本次没有新增真实桌面人工验收证据。真实窗口视觉/鼠标、Shell默认应用、混合DPI多屏及跨设备低内存长期浏览继续待验。自动化像素/资源计数与安装检查不能替代这些项目，亦不证明整个进程绝不泄漏。其他格式、预算和分发限制见[已知限制](known-limitations.md)。
