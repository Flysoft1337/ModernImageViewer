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

最终发布CI、精确提交、四资产大小/哈希与Latest状态待实际运行后补记；当前准备记录不代表已发布。

## 未完成人工验收

本次没有新增真实桌面人工验收证据。真实窗口视觉/鼠标、Shell默认应用、混合DPI多屏及跨设备低内存长期浏览继续待验。自动化像素/资源计数与安装检查不能替代这些项目，亦不证明整个进程绝不泄漏。其他格式、预算和分发限制见[已知限制](known-limitations.md)。
