# 0.6.0 候选打包体积与布局

## 范围

版本保持0.6.0，不创建Tag/Release，不覆盖现有公开资产。起点1e64972，分支fix/compact-packaging。用户反馈安装239,721,641字节、476文件、根目录289文件，已只读复核一致。PDB原本就不进入安装器和便携包。

## 实现

- 保留self-contained .NET 10/WPF与全部既有codec，用户无需另装运行时。不使用WPF trimming。
- .NET托管程序集压缩进应用EXE；native/content不自解压，native依旧外置加载，不把磁盘开销转移到临时解压缓存。
- 项目只使用WPF/Win32，取消未使用的Windows SDK WinRT投影引用；语言资源只保留英文、zh-CN、zh-Hans。进一步核验CPU绘制路径后，按已核验版本排除未使用的OpenGL控件依赖，见下节。
- 安装版和候选便携包均为`app/`运行文件、`licenses/`许可证/必须附带源码；根目录保留项目LICENSE、dependencies清单，安装版另有Inno卸载文件。便携入口为`app/ModernImageViewer.App.exe`，安装版快捷方式与关联直接指向同一入口。
- runtimeconfig保留一个小sidecar供打包/观测工具读取；观察二进制身份包含EXE，兼容普通发布与托管单文件。RAW源清单、LibRaw源码ZIP、native许可证和哈希检查继续执行。
- 构建要求空publish目录，防止旧散装DLL混进新包；不主动清空用户指定目录。

## 升级安全

稳定AppId、当前用户安装和安装版/便携版独立身份不变。新文件安装完成后，只有旧标记与旧EXE同时匹配已知哈希才清理旧布局；每个旧文件还必须逐项匹配SHA-256。修改过或未知的文件保留，只删除空的旧卫星资源目录，不使用目录递归清除。已注册且由旧入口拥有的关联随入口迁移，用户原本未注册时仍尊重安装选项。

`legacy-layout.sha256`由已校验的公开v0.4/v0.6便携包及本地2978bc6/f36a436候选生成，记录867条文件哈希；更新工具为`scripts/update-legacy-layout.ps1`。未知旧构建的EXE不匹配时保留原文件，不能承诺所有非公开历史包都自动清理。旧布局许可证继续放在原licenses目录，不通过删除许可证缩小体积。

## 验证记录

本地Release构建0警告0错误，完整604通过/1真实RAW样本跳过，format verify与依赖审计通过。ZIP结构与4份图像native库哈希通过；测试数量不增加，针对包布局与升级安全复用脚本验证。

| 本地构建实测 | 字节 |
|---|---:|
| 原安装（含卸载器） | 239,721,641 |
| 新包有效载荷（不含安装标记/卸载器） | 116,945,656 |
| 新便携ZIP | 82,541,038 |
| 新EXE安装包 | 76,746,762 |

首轮载荷49文件、根目录2文件、app目录13文件；安装版增加标记与Inno卸载文件。PR #40最终[Windows CI 37930735561](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37930735561)升级后125,116,786字节/54文件/根目录6文件（含smoke专用保护文件），重装后125,158,269字节。原f36a436 ZIP为98,039,623字节、Setup为69,934,351字节；程序集先压缩降低了外层LZMA压缩空间，因此新Setup略增，安装落盘体积减少，二者不能混为一谈。

Windows CI启动、完整观察和安装升级已通过；交替性能报告已运行，但一次短暂桌面焦点丢失使该轮无有效成对统计。报告作为 artifact 保留，不把它描述为启动或浏览性能结论；后续应在固定人工桌面条件下重测。

复用既有Windows CI完整测试、依赖审计、100MP、启动、激活、浏览、动画与安装检查；额外验证新ZIP结构/native清单，以及公开0.6散装→本轮0.6布局迁移。同一Windows job以3对交替观察加1对预热比较同一提交/同一runtime的散装和压缩发布，避免把公开包不同源码/运行时的差异算成压缩开销；样本不称P95，不等同于跨设备性能保证。安装smoke只允许一次性GitHub-hosted runner，保护真实用户安装与关联。

首次CI已通过主包启动/激活/浏览/动画，0.4升级后运行正常且已知旧运行文件清理成功；布局断言发现createallsubdirs仍创建空app/licenses，已收紧目录排除并移除运行目录的空目录创建标记，重跑同一CI。该次未完成重装/卸载，不记作安装全生命周期通过。对用户安装的只读哈希比对确认旧EXE、标记及442份运行文件匹配，未执行用户安装迁移。

## 可选运行库与压缩优化（基线320886e）

应用只创建`SKElement` CPU绘制控件。固定[Skia上游源码](https://github.com/mono/SkiaSharp/blob/4783f51448f9b070dda4f87b83e941c9599e466e/source/SkiaSharp.Views/SkiaSharp.Views.WPF/SKElement.cs)使用WriteableBitmap和SKSurface；OpenTK/GLWpfControl只供同包的另一控件SKGLElement使用。使用NuGet版本限定的`PrunePackageReference`排除OpenTK 4.3.0、OpenTK.GLWpfControl 4.2.3及其下游GLFW；不裁剪Skia或WPF程序集。新版本超过核验范围时不自动排除，打包检查拒绝重新带入未核验OpenGL依赖。后续若改用GPU控件，必须重新评估这项排除。

10份OpenGL托管DLL未压缩5,060,096字节，GLFW外置225,792字节。程序集已在EXE中压缩，不能将未压缩大小冒充安装收益。安装器改用LZMA2/max、明确32MiB解压字典；便携ZIP使用SmallestSize。这两项只影响分发压缩，应用正常运行不新增解压或缓存。

| 同机同SDK/runtime重新构建 | master基线 | 优化候选 | 减少字节 |
|---|---:|---:|---:|
| 有效载荷（不含安装标记/卸载器） | 116,946,089 | 115,582,569 | 1,363,520 |
| 应用EXE | 62,759,755 | 61,626,665 | 1,133,090 |
| 便携ZIP | 82,541,211 | 80,250,600 | 2,290,611 |
| EXE安装器 | 76,745,255 | 75,174,700 | 1,570,555 |

候选47文件，根目录2文件、app12文件；原4份图像native哈希通过。版本0.6.0、SDK10.0.303/runtime10.0.11。上述候选来自本地工作区，不是已公开资产；安装后体积仍由隔离CI记录。保留.NET/WPF self-contained、Skia、HarfBuzz、Magick、RAW桥接及必要许可证/源码，未找到可证明冗余的大型.NET框架库。

旧app布局升级仅在安装前标记哈希匹配且旧入口存在时识别已知GLFW，安装后再核对GLFW哈希才删除；未知或修改过的同名文件保留。现有安装smoke复用原升级/重装步骤，验证修改文件保护与已知GLFW回收，不新增安装测试矩阵。公开旧散装布局继续由原哈希清单清理。

本地format verify、Release构建0警告0错误、604通过/1真实RAW样本跳过、在线依赖审计和100MP观察通过。测试在清理后的输出中运行，不含OpenTK/GLWpfControl/GLFW；不增加测试数量。

398f7b8的[首轮Windows CI 37937597264](https://github.com/Flysoft1337/ModernImageViewer/actions/runs/37937597264)格式/构建/604通过1跳过/审计/100MP/启动/激活/浏览/动画和两条安装升级路径都通过。CI载荷115,513,838字节、ZIP80,237,155字节、46文件/app12；与本地runtime版本不同，不混用为同机改善比例。公开v0.6升级后123,755,048字节，重装后123,797,576字节（包含smoke保护文件及保留的旧许可证）；已知GLFW回收、修改同名文件保护、关联保护和卸载通过。

该轮最终状态失败：性能预热中发生ForegroundWindowLost，报告无有效配对；已有CI的try/catch无法拦截观测脚本的exit 1。改为子PowerShell执行，仅在完整失败报告明确为焦点丢失/未获取焦点时记录warning，其它失败仍阻断CI。不改变测量逻辑、不忽略性能数据；最终CI状态以[PR #41](https://github.com/Flysoft1337/ModernImageViewer/pull/41)最后提交的检查和artifact为准。压缩包与散装的性能差异仍没有有效成对结论。
