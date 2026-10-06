# 0.6 动画与多页验收

基线 `master 9cdcde9`（0.5.0），开发分支 `codex/0.6-animation-multipage`（0.6.0）。本轮不发布 Release；公开下载仍为 v0.4.0。方案已获用户确认，格式边界见 [支持矩阵](decoder-support.md#当前能力矩阵)。

## 实际实现与修复

- 静态路径继续使用单个 PixelBuffer；仅 GIF/WebP 多帧与 TIFF 多页建立可释放会话。共享描述区分画布、局部矩形、时长、循环、混合、disposal、依赖、随机访问与当前索引。
- GIF/WebP 使用随包 SKCodec 的完整合成输出和 RequiredFrame/PriorFrame；发布数组不可变，不再次叠加局部帧。依赖逐帧在后台处理，归还 Detail 槽让前台打开优先，不在 UI 线程补绘所有错过帧。没有新增 native 依赖。
- TIFF 使用系统 WIC COM adapter，在多页专用串行线程复用流/decoder；按页尺寸和方向读取预览、完整图或 ROI。单页不创建 worker。
- 单调时钟与懒创建 DispatcherTimer 支持自动播放、暂停/恢复、定位、重播、有限结束和无限循环。后台、最小化、不可见或 viewport 卸载冻结时间，恢复不解除用户暂停。内部导航不改文件索引，不重启 F6。
- 切图/F5/关闭立即取消请求代次；已开始 native 需实际返回后才释放槽和资源。ReleaseCompletion 在真实释放后完成，过期像素不可提交。旧会话停止后禁止静态首页细化覆盖旧帧。
- 修复目录索引通知被帧更新吞掉、暂停/重播像素提交不同步、旋转后下一帧丢失预览目标、连续帧推迟预览升级、TIFF 同页重复完整分配及 ROI A→B→A 晚结果覆盖。超限代表帧保留不可编辑标记。
- 轻量帧控件按需出现；Ctrl+PageUp/PageDown、Ctrl+Home/End 内部导航，Ctrl+Space 播放/暂停，F1 双语帮助同步。输入页码时暂停，非法输入回到有效索引；信息面板不会遮挡控件。

## 资源边界

动画单输出最多 8MiB，应用掌控的帧、参考及交接按 32MiB 预留，不展开全部 BGRA 帧；源画布 BGRA 等价值最多 92MiB 才准入播放。该检查不是 native 或进程硬上限，代表帧降采样也可能需要源尺寸 native 工作区。输入256MiB、帧/页数10,000、元信息政策2MiB及32768单边/100MP源限制保持显式。

TIFF 当前页沿用静态32MiB预览、92MiB完整细节、2048×2048/16MiB ROI，不缓存全页集合。缩略图仍静态代表帧/首页、2MiB/24项，邻图缓存4MiB，不持有播放会话。剪贴板只持有当前不可变快照；多帧/多页及超限回退禁止进入静态编辑器。

Skia 与 WIC 会话持有只读来源流，释放后关闭句柄。观察计数覆盖项目帧会话/native decoder、共享 bitmap pin、帧 timer 和启用期间的 PixelBuffer wrapper；wrapper 字节可能重复计算共享数组，不等于托管堆、数组存活或进程像素唯一字节。绘制 surface/checkerboard 和 codec 内部缓存不由这些计数证明。

## 本地验证

- 最终 Release build：0警告、0错误；完整测试核心187、UI/codec382，共569通过，1项依赖本机真实RAW样本跳过。相比0.5基线451通过，增加118项通过案例。TRX位于 `artifacts/animation/test-results`，子集不累加。
- dotnet format --verify-no-changes --no-restore 与 git diff --check 通过；format 有既有 workspace 加载 warning。依赖在线审计本机因 NuGet TLS 凭证失败，交由正式 Windows CI，不写成审计通过。
- 固定像素/哈希覆盖 GIF 单帧、透明/局部/offset、background/previous、有限无限、0/10ms、损坏后帧、大图与超限；WebP 静态/动画、透明、Source/Over、dispose、有限无限及方向；TIFF 单页、多页、异尺寸、1–8方向、坏页、完整及 ROI。
- 回归覆盖最新代次、晚结果释放、F5、切图、Dispose/重复Dispose、关闭、unload/reload、四档DPI、Fit/Actual Size、缩放平移方向保持、内部页与目录隔离，以及帧更新不重启F6/预览稳定计时器。
- 已核对720×480的中英文深浅主题8张帧/页截图及信息浮层间距；深色页码白底已修正。截图在 `artifacts/animation/screenshots`。
- 100MP旧观察继续得到1600×1600/10,240,000B预览和140×140/78,400B缩略图；单次耗时约279/237ms，不作为速度提升或P95。framework-dependent静态启动一次输入空闲约789ms；8图/8切换静态浏览观察成功。

## 动画压力观察

Windows 10 19045、x64、同机Release framework-dependent，0.6未提交工作区。使用已通过像素断言的自有样本，复制进独占临时目录；不操作原样本，不强制GC、不记录文件路径或名称。原始报告 `artifacts/animation/pressure.json`。

10轮 GIF、4096×4096大GIF、WebP、TIFF与JPG交替（50次输入，另有每次F5、缩放/全屏和快速请求），随后无限WebP持续180秒；总观察约263秒。实际阶段覆盖播放/暂停/恢复、定位、后台冻结、用户暂停保持、重播、页细化、F5、快速切换及关闭。

采样主帧和参考各最多8,294,400B，native会话、decoder、帧timer各最多1。过程WS峰值约253.7MiB、private峰值约350.1MiB、handle峰值729；末次采样WS约190.9MiB、private约160.0MiB、handle681。切到JPG后帧资源归零、保留一个静态bitmap；清当前来源后wrapper/pin/timer/session/decoder计数全部为0，并在空闲和关闭再次为0。这证明该样本场景内项目计数有界并实际回收，不宣称整个进程绝不泄漏或长期P95达标。

最终构建另外完成4096×4096大GIF持续60秒观察（`artifacts/animation/large-gif-pressure.json`）：混合输入、缩放/全屏/F5/快速切换和关闭均成功，释放/空闲/关闭的wrapper、pin、timer、session、native计数全部归零。本地短smoke已完成六输入与资源归零。Windows CI、self-contained静态对照、文件激活和真实0.5→0.6安装/升级/重装/卸载结果待补。

## 保留边界

不提供动画编辑/导出、TIFF所选页另存、APNG、AVIF/HEIF序列、BigTIFF完整兼容或ICO尺寸选择。超限动画只尝试静态代表帧并反馈；不承诺所有损坏文件或编码变体。多页TIFF的嵌入ICC转换、显示器ICC、HDR/高位深、真实混合DPI多屏及跨设备长期观察仍未完成。GIF/WebP有效延时小于20ms统一回退100ms，不能解释成精确保留极短原始节奏。

下一阶段优先真实日常动画样本/混合DPI与低内存长测，再评估ICO尺寸选择；保持浏览优先，不扩编辑或完整RAW显影。
