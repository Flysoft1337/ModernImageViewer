# GitHub Release 手动发版

普通 push、PR 和未勾选发布的手动运行仍只生成 Actions artifacts。需要对外发布时，手动启动同一条 **CI** 工作流；它复用现有 Windows 构建、格式检查、测试、文件激活、安装器打包与安装/卸载检查。所有检查通过后才发布 GitHub Release。

## 在网页触发

1. 打开仓库 **Actions → CI → Run workflow**。
2. 分支选择 **master**。
3. 将 **publish_release** 勾选为 `true`。
4. 正式版本保持 **prerelease=false**；需要测试版时勾选 `true`。
5. 点击 **Run workflow**。成功后，运行摘要提供 Release 链接，仓库 **Releases** 页面也会显示安装包。

版本由该次运行提交的 `Directory.Build.props` 中 `<Version>` 唯一决定，不需要手动填写版本号。格式必须是 `major.minor.patch`，例如 `0.4.0`；标签为 `v0.4.0`。发布新的版本前必须修改版本号并合并到 master。`prerelease=true` 只改变 GitHub 的预发布标记，不改变包内版本、文件名或标签，所以正式版与测试版也需要各自独立的版本号。

命令行也可以触发：

```bash
gh workflow run ci.yml --ref master -f publish_release=true -f prerelease=false
```

0.6.0公开测试使用同一工作流，将`prerelease`设为`true`：

```bash
gh workflow run ci.yml --ref master -f publish_release=true -f prerelease=true
```

GitHub标记为Pre-release，保持稳定版Latest为v0.4.0。包内版本和标签仍为0.6.0/v0.6.0；公开资产不会覆盖，后续修改安装包必须提升版本号，例如0.6.1。人工验收缺口见[已知限制](known-limitations.md)。

## 下载文件

每个 Release 上传四个文件，名称与包内版本一致：

- `ModernImageViewer-<version>-win-x64-Setup.exe`
- `ModernImageViewer-<version>-win-x64-Setup.exe.sha256`
- `ModernImageViewer-<version>-win-x64-Portable.zip`
- `ModernImageViewer-<version>-win-x64-Portable.zip.sha256`

安装器为当前用户安装，便携包解压即可运行，两者都自带 .NET 运行时。安装器暂未签名。Release 文件不受 Actions artifact 的 30 天保留期限制。Release 说明包含精确源代码提交、安装信息、仓库中对应版本的 `docs/release-notes/<version>.md`（存在时）以及自动生成的提交/PR 说明。版本说明文件存在但为空会在远端修改前拒绝发布；旧版本没有该文件时沿用自动说明。

核对安装器下载的哈希：

```powershell
(Get-FileHash .\ModernImageViewer-0.4.0-win-x64-Setup.exe -Algorithm SHA256).Hash
Get-Content .\ModernImageViewer-0.4.0-win-x64-Setup.exe.sha256
```

## 提交、权限与重复运行

- 发布只允许 `workflow_dispatch` 且分支为 master；在其它分支勾选发布会在构建开始时明确失败。
- 构建与发布都使用触发时的同一个提交，不会在构建完成后切换到后来更新的 master。标签指向这份已验证源代码；如果 master 被改写导致提交不再属于主分支，发布会停止。
- 普通 CI 的 `GITHUB_TOKEN` 只有 `contents: read`。只有验证完成后运行的发布 job 获得 `contents: write`，使用 Actions 自带 token，无需个人访问令牌。
- 发布 job 按版本串行运行，不取消正在发布的任务；多个版本可以分别发布。GitHub 的 concurrency 最多保留一个等待任务，重复触发同版本时更早的等待任务可能被替换。
- 已存在的标签若指向不同提交，脚本拒绝移动标签。已公开的同版本 Release 始终拒绝再次发布或覆盖资产；更改代码或重新构建后需要提升版本号。
- 发布先建立草稿、上传四个文件并核对文件名、大小与可用的服务器 SHA256，最后才公开。安装包本身的 SHA256 文件也会在上传前验证。
- 上传中途失败会留下草稿。可以在同一个 Actions 运行里 **Re-run failed jobs**；脚本只修复本流程创建、版本与提交完全一致、预发布属性一致的草稿，删除其中未完成的同名资产并重新上传四个文件。带其它资产或缺少本流程标记的草稿不会被改动。
- 已成功公开后不要选择重跑发布 job；它会按不可覆盖规则失败。需要重新发布修复版本时，提升版本号后触发新的运行。

## 维护位置

- `.github/workflows/ci.yml`：手动输入、复用 Windows CI、最小发布权限与版本并发控制。
- `scripts/publish-release.py`：版本/提交/标签校验、SHA256 验证、草稿恢复与四文件发布。
- `scripts/build-installer.ps1`：从同一版本属性生成安装器、便携包与哈希文件。

该流程不会因为普通 push、PR、创建标签或合并代码自动发版；只有用户主动勾选发布的手动运行会创建 Release。
