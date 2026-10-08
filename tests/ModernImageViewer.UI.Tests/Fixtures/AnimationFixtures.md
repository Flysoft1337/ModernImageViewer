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
