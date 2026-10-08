# GIF/WebP固定样本

项目自有、确定生成的样本，生成器为 `../AnimationCodecFixtures.cs`。
GIF 使用固定调色板与显式LZW clear codes；WebP使用固定SkiaSharp 4.153.1无损负载，显式构造RIFF/ANIM/ANMF。
测试验证完整合成像素、随机定位和缩放，不能只检查未抛异常。仅通过实际像素断言的样本才按环境变量导出供CI观察。

| 样本 | 尺寸/帧 | 字节 | SHA-256 |
|---|---|---:|---|
| animation-infinite.gif | 4×2/5；透明、offset、previous/background；repeat=0 | 173 | 178b60e146c39de16d5ceddb5578de101187022e36000516131f44cae6ef1731 |
| animation-large.gif | 4096×4096/3；中央1024方形RGB；50/70/110ms；无限 | 2368659 | ce9835d20e5d2b882cec56669e00abbd8895d0ca772d0f7f0e7921d6118ae0d0 |
| animation-infinite.webp | 4×2/3；alpha、Over/Source、background；ANIM=0 | 188 | 53f3926f9b6ce603206054b28b6f612585c4c3f521cc435e56f4361bc796e75b |
| animation-finite.webp | 4×2/3；相同像素；ANIM=2，即两轮 | 188 | 6104f78506633d1a2bd8b4ddfb319a1bbad63c77f66c86a1c4dd3cf7c9974c76 |

额外固定参数样本覆盖单帧、局部首帧、连续previous、有限GIF重复、0/10/20ms、截断后帧、EXIF方向、超帧数/输入/输出预算与超源画布。WebP上述哈希依赖固定encoder版本，像素断言是正确性依据；不同合法编码不能凭字节差别判断解码错误。

## ANIM 背景固定像素

`WebPAnimationBackgroundTests.cs` 直接断言 BGRA 预乘像素，不以播放器是否抛异常判定合成结果：

| 参数样本 | 断言 |
|---|---|
| 6×2/5帧；背景RGB=(120,80,40)，alpha=0/128/255 | 背景分别为BGRA=(0,0,0,0)/(20,40,60,128)/(40,80,120,255) |
| 局部首帧Source/Over；帧内透明及半透明RGB | Source透明替换为零；Over透明保留已有画布；半透明像素按固定值合成 |
| dispose=1后下一帧；随机seek、回到首帧、再次seek | 只清被销毁帧矩形为ANIM背景；首帧重新初始化；已发布数组不被改写 |
| 同负载的一帧ANIM与多帧ANIM | 主图代表、预览、缩略、预取与会话首帧一致；一帧不创建播放会话 |
| 6×4缩到3×2；EXIF方向1–8 | 原画布坐标缩放后再方向转换；背景和透明Source正确；缩到零的帧矩形跳过native解码 |
| 自有线性RGB ICC；背景及Source均为RGB=128/alpha=128 | ICC转sRGB后BGRA=(94,94,94,128)，代表路径和会话一致 |
| 有ALPH+VP8的有损半透明帧 | 有损RGB允许小范围舍入误差；背景清除与Source透明值精确断言 |
| VP8X无Alpha标志，但ANIM背景alpha=128、帧均不透明 | 合法非不透明背景仍保留alpha，不被错误强制不透明 |
| 错误帧尺寸/截断、超过10000帧、256MiB输入、2MiB元信息 | 代表帧及播放入口明确拒绝；不会经缩略/预取绕过限制；缩成零的帧也核对VP8/VP8L原始头尺寸 |
| 前台占槽时取消/Dispose；取消后随机seek | 晚结果不发布；实际释放完成后文件解锁、retained和native会话计数回落 |

实现依据：[WebP RIFF规范](https://developers.google.com/speed/webp/docs/riff_container)的ANIM/ANMF与渲染步骤，以及[Skia m153 WebP codec](https://github.com/google/skia/blob/chrome/m153/src/codec/SkWebpCodec.cpp)的透明清除和保守局部缩放。现有SkiaSharp为4.153.1；仅使用其静态WebP位流解码和ICC转换，不增加依赖或播放器。

资源说明：每个帧输出最多8MiB。播放时BGRA暂态按当前显示、未旋转参考、新合成、局部解码各最多8MiB计费；局部解码离开方法后才分配方向输出副本，此时四项为当前显示、旧参考、新合成、新方向输出。方向转换的有界位图标记约为每源像素1bit，最多256KiB，属于辅助存储，不是BGRA帧预算。`RetainedPixelBytes`只记录一个持久参考帧，不包含暂态局部帧、交接输出或native内部存储。暂态局部SKCodec计入活动解码器，实际Dispose后才减计数；持久容器解码器仍按既有计数方式计费。背景ICC转换的两个1×1 SKBitmap由using立即释放。

32MiB是项目控制的BGRA工作预算，不是进程/native总内存上限；源画布播放准入仍为92MiB BGRA等价值。代表帧可对更大但仍满足100MP/32768尺寸限制的画布降采样，输出仍最多8MiB。全部帧的压缩负载不展开成像素缓存；局部payload流引用原只读流。Over沿用现有预乘sRGB合成，尚未新增规范建议的线性光混合或显示器ICC/HDR管线。
