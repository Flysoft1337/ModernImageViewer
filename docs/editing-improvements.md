# 编辑补全：比例裁剪与WebP

> 2026-10-06，开发0.5.0，公开版本仍v0.4.0。本批不发布Release；[PR #29](https://github.com/Flysoft1337/ModernImageViewer/pull/29)功能提交b7286b2的[Windows CI](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37346291867)已通过，最终合并状态见PR。

编辑窗口新增自由、原比例、1:1、4:3、3:2、16:9、9:16。比例按当前显示方向计算，90°旋转/翻转后仍映射回EXIF校正后的原图坐标；原比例拖选随当前方向变化。预设在已应用裁剪区域内居中写入参数草稿，点击“应用裁剪”或另存为后进入历史；切换未应用的预设不会反复收缩取景。选择比例后拖选锁定比例，数字字段仍可微调，自由模式保持原行为。像素取整每轴可能偏离理想比例不足一个像素，极小图至少1×1，不越界。

PNG/JPEG/WebP共用现有安全新文件导出。WebP为有损品质1–100，100仍为有损；透明Alpha保留，RGB因有损编码允许变化。沿用SkiaSharp，不增加codec或启动初始化。导出直接读取共享像素与现有有界ROI，源/输出各64MiB；原文件和已有目标均不覆盖，临时CreateNew→flush→Move，取消/失败清理。文件与剪贴板来源均适用。

源EXIF/XMP/ICC等元数据不复制，沿用现有解码颜色管线；不声称已完成显示器ICC或可保留源profile策略。任意角度、调整/标注、WebP无损选择及完整M2留后续。

## 验证

标准Release构建0警告/0错误，编辑核心6项与相关UI/codec35项通过；涵盖旋转/翻转/反向拖选、边界、取整、比例不变的历史、预设草稿和撤销，以及WebP透明度/尺寸/方向、品质边界、元数据移除、源hash/共享像素不变、已有目标/源版本/预算/取消拒绝。复用既有STA窗口和双语主题截图、Windows CI，不增加测试矩阵。最终格式/CI与截图检查见[进度](planning/progress.md)。

完整Windows CI核心68+UI/codec185=253通过、1项本机RAW样本跳过；Release构建0警告/0错误，格式/依赖审计、100MP观察、启动/12类格式激活、安装/重装迁移/卸载通过。Release job跳过；format有既有workspace加载warning。末次仅补Markdown验收，功能源/测试/CI流程与已通过b7286b2一致。
