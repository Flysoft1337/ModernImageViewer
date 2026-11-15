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

本地还原、format verify、Release0警告0错误、完整635通过/1真实RAW样本跳过、漏洞审计通过。100MP单次预览1600²/10,240,000字节/235.47ms、缩略图140²/78,400字节/193.63ms，非对照收益或P95。初次候选47文件包布局/native哈希验证通过，许可证来源README更新后须重新封包带入。

严格本机“公开0.6.2 vs 候选”原6对+1预热比较完成2对后失焦：原采样ForegroundWindowLost，RequireValid实际阻断，PerformanceAccepted=false。该记录不能称性能通过，也不据此判断产品退化。旧#54/#56历史CI失败发生在性能比较，其余旧PR也有升级失败/安装取消，不能沿用旧绿勾；本轮最终Windows CI、性能及三路隔离安装以新报告为准，未通过前不合并。

原始证据位于ignored的`artifacts/dependency-updates-20261115/`。最终PR/CI与合并状态待补，不操作本机安装、关联或已发布资产。
