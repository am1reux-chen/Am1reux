# Lexio 发布阻断项最终验收

日期：2026-10-18（复验更新）
结论：**当前不能进入 Release Candidate。** 最新安装包已在隔离目录安装并启动，Library 空状态在实际页面确认正确；完整备份/恢复的 Picker UI 流程未完成；干净 Windows 环境未验证。安装器未签名，正式分发前还需签名。

## 工作区保护

- 分支：`master`；HEAD：`8b521a7 Learn: replace stale derived-property bindings with x:Bind page functions`。
- 工作区在本轮开始前已经包含大量 GLM/Codex 未提交修改。本轮未重置、清理、提交或覆盖其既有工作；所有变更仍保留。
- 本轮新增 Inno Setup 脚本及说明，修正 Library 空状态提示，并新增本报告。复验期间发现原发布目录未包含该 XAML 文案改动；因此另外生成了独立的最新发布目录和安装包，没有覆盖旧产物。依赖版本、FSRS、Daily Planner、Learn 会话/暂停逻辑均未修改。
- `git diff --check` 通过；仅有 Git 对 LF/CRLF 转换的提示。

## 发布包与安装器

### 方案比较及选择

现有应用是 unpackaged WinUI 3。发布配置已把 .NET 和 Windows App SDK 依赖放入自包含输出；Microsoft 文档确认自包含 Windows App SDK 文件可以随应用部署，也可通过自定义安装器分发。因此 Inno Setup 只需包装当前发布目录，不需要把应用改成 MSIX 项目或改变应用启动/数据路径。

| 方案 | 对当前项目的改动 | 安装体验与风险 |
|---|---|---|
| Inno Setup | 最少：新增 `.iss` 与安装信息页，封装现有自包含目录。稳定 `AppId` 支持同路径覆盖升级。 | 支持标准安装、升级、卸载和快捷方式。卸载规则可明确不删除独立的 `%LOCALAPPDATA%\Lexio`。安装包本身仍需代码签名。 |
| WiX | 需新建/维护 MSI 作者文件、组件/快捷方式/卸载规则及 WiX 工具链。 | Windows Installer/MSI 部署适合企业软件分发；对本项目当前的单用户桌面交付，改动和维护面大于 Inno。此轮未实现或实测 WiX。 |
| MSIX | 需增加 MSIX manifest/打包项目并配置包身份、版本及签名；现有 unpackaged 发布要转为 MSIX packaging 流程。 | 安装、卸载、更新和 Store 管理更统一，也支持增量/自动更新；可减少自定义卸载规则风险。包必须有设备信任的签名证书。转包和签名配置比包装现有输出涉及更多变化。此轮未迁移或实测 MSIX。 |

此轮选择 Inno Setup，因为它是把已经验证过的 unpackaged 自包含目录交付给用户的最小改动方案。微软对自包含部署和打包分发的说明见[Windows App SDK 自包含部署文档](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)及[Windows 应用打包与部署概览](https://learn.microsoft.com/en-us/windows/apps/get-started/intro-pack-dep-proc)。MSIX 的签名和信任要求见[微软 MSIX 签名说明](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview)。

### 安装器构建和本机实测

- 安装脚本：[Lexio.iss](../installer/Lexio.iss)；安装和用户数据说明：[README](../installer/README.md)、[安装前提示](../installer/UserDataNotice.txt)。
- 工具：从 Inno Setup 官方发行获取 6.7.3，校验了下载 SHA-256 和 Authenticode 签名。工具原先未安装，随后使用用户临时目录中的编译器完成了实际构建；没有改动项目依赖。
- 首轮发现：旧目录 `artifacts/lexio-release-candidate/publish` 中的运行版仍显示旧 Library 提示，尽管源文件已修改。旧目录和既有安装包均予以保留，不再将其作为当前源码候选产物。
- 最新独立自包含目录：`artifacts/lexio-release-candidate/publish-recheck-20261018`，547 个文件，共 226,423,938 字节。包括 `Lexio.exe`、.NET `coreclr.dll`/`hostfxr.dll`/`hostpolicy.dll`、Windows App SDK bootstrap/runtime、SQLite `e_sqlite3.dll`、`Lexio.pri` 和 2 个 XBF 文件。`starter-dictionary.json` 计数为 0。
- 为避免覆盖先前验证产物，[Lexio.iss](../installer/Lexio.iss) 现在支持通过 `PublishSourceDir` 显式选择发布目录。最新包：`artifacts/lexio-release-candidate/installer-recheck-20261018/Lexio-Setup-x64-recheck.exe`，60,769,757 字节，SHA-256 `BD3089CC3E3A0A01D4C8DB75B63B991447770D90966047459DAAB7817B103AE1`。新旧输出目录互不覆盖。
- 另用同一最新发布目录构建 1.0.1 升级测试包：`Lexio-Setup-x64-recheck-1.0.1.exe`，SHA-256 `5321809E8CE8AABF67BE9799A4C40AAD6D30868D2176C5B9C43F4C4F70C25841`。
- 将 547 个发布文件安装到隔离安装目录后逐项比对：文件数量、长度和哈希均与发布目录相同；另外只有卸载器相关文件。
- 最新 1.0.0 包在隔离安装目录安装成功；547 个发布文件和安装目录中文件逐项哈希一致。安装后的应用以隔离 `LEXIO_DATA_DIR` 启动并创建数据库。它在受限 `PATH` 且移除 `DOTNET_ROOT` 等环境变量的条件下启动；进程加载的 `coreclr.dll`、`hostfxr.dll`、`hostpolicy.dll`、`Microsoft.WindowsAppRuntime.dll` 和 `e_sqlite3.dll` 均来自安装目录。**该主机预装了 .NET 10，因此不等价于无 .NET 的干净 Windows 验证。**
- 最新包中的 Library 页面实际运行并确认显示“当前没有预装词库。可从右上角导入 CSV / TXT 后开始学习。”（真实运行界面截图已在本轮工具输出中显示）。
- GUI 安装信息页显示用户数据路径及卸载保留说明的验证来自同一安装脚本的前一版安装测试；最新候选包本轮以静默方式安装。安装默认使用当前用户目录；脚本未定义删除 `%LOCALAPPDATA%\Lexio` 的卸载动作。
- 最新安装以稳定 `AppId` 更新到 1.0.1 测试包成功（退出码 0，卸载注册表 `DisplayVersion` 变为 1.0.1）；测试数据目录中的 `lexio.db` 和 marker 文件均保留。随后卸载成功（退出码 0，应用目录和卸载注册项移除，隔离用户数据仍存在）。没有访问真实 `%LOCALAPPDATA%\Lexio`。
- `WindowsSandbox.exe` 不存在；Hyper-V 查询因主机授权策略失败，未找到 VMware/VirtualBox 启动器。因此 Windows Sandbox/虚拟机干净环境测试**未验证**。
- `Get-AuthenticodeSignature` 对安装包结果为 `NotSigned`。供本机验证的包不能直接视为适合公开分发的签名安装器。正式发布前必须使用可被目标用户信任的发布者证书签署安装器，并实测签名。
- Inno Setup 编译器在无商业许可的运行状态下显示了 “Non-commercial use only”。官方说明要求商业用户购买许可，并指出通过构建脚本调用 IDE/编译器也属于使用。项目的商业使用属性尚未确认；若属于其定义的商业使用，应由发布方取得适用许可并在其许可环境中重建安装器。此处记录官方许可要求，不构成法律意见：[Inno Setup 商业许可说明](https://jrsoftware.org/isorder.php)。

### 安装器状态

| 验收项 | 状态 | 证据/限制 |
|---|---|---|
| 构建 Inno 安装器 | **已解决** | 独立发布目录、实际 Inno Setup 6.7.3 编译结果及 SHA-256 已记录。 |
| 现有 Windows 主机安装 | **已验证** | 最新候选包在隔离安装目录安装完成，547 个应用文件哈希与发布输出一致；信息页的 GUI 检查由同一脚本的前一版本验证。 |
| 覆盖升级 | **已验证** | 测试 1.0.0→1.0.1，稳定 AppId，退出码 0，测试用户数据仍在。 |
| 卸载且保留用户数据 | **已验证（隔离目录）** | 卸载退出码 0，应用文件移除；隔离 `%LOCALAPPDATA%` 数据文件保留。真实用户目录未触碰。 |
| 启动时不依赖机器级 .NET/Windows App SDK | **本机部分验证** | 运行时 DLL 从应用目录加载；主机已有 .NET 10，不能证明干净机器启动。 |
| 无预装 .NET 10 的干净 Windows 安装与启动 | **未验证** | 当前没有可用的干净 VM/Sandbox。 |
| 安装器签名 | **未解决** | 当前生成文件为 `NotSigned`；公开分发前必须签署并验证。 |
| Inno Setup 商业许可适用性 | **未验证** | 发布方的使用/商业状态未知；按官方许可说明确认后再生产构建。 |

## Library 空状态

- 修改 [LibraryPage.xaml](../src/Lexico.App/Pages/LibraryPage.xaml) 中的空词书提示为：**“当前没有预装词库。可从右上角导入 CSV / TXT 后开始学习。”** 不再声称首启会自动安装词库，没有添加静态词库数据。
- 这是最小文案变更，没有调整页面布局、资源或动画。最新发布包运行时实际打开 Library 并确认文案和空词书状态显示与当前数据一致，**已验证**。
- 没有加入真实英语词库；现有 starter JSON 来源未确认，并已从发布产物排除。若以后恢复词库，需先记录具体来源、版本、许可和再分发条件。

## 完整备份/恢复 Picker UI

核心服务和隔离数据库测试覆盖完整数据恢复及无效备份失败后的数据保护，但不能代替文件选择器 UI 验收。已取得的隔离 UI 证据如下：

- CSV 导入与搜索曾在一个独立 Release 实例中通过：`before-backup.csv` 新增 2 词，搜索 `serendipity` 命中；SQLite 只读检查为 `integrity_check = ok`。后续更新的导入反馈构建尚未重复完成这条 UI 流程。
- 完整备份保存已在另一个隔离实例中通过新版 Windows App SDK 保存选择器；`.lexio-backup` 文件生成成功，SQLite `integrity_check = ok`，包含 100 个词。
- 完整备份恢复仍未通过真实 UI 验收。尚未通过恢复选择器选取备份、确认恢复、核对恢复后状态并重启，也未验证取消、无效文件提示及恢复失败路径。

最近一次导入复验中，PickerHost 未被当前 UI 自动化工具列为可操作窗口，选择文件输入被拒绝，因此该次没有新的导入结果。该限制不推翻前述单次导入通过记录，也不证明新版导入反馈构建已端到端通过。**不得把打开 Picker 或服务测试当作完整备份恢复 UI 通过。**

恢复 UI 仍未完成的项目：

- 修改隔离数据库后，通过恢复文件选择器选取备份并确认恢复；核对词库、学习状态、复习记录和设置。
- 重启隔离实例，确认恢复后的数据可重新加载，并确认恢复前安全快照存在。
- 通过 UI 验证取消选择、无效文件提示、恢复失败时的数据保护和提示。

待人工在 disposable Windows 用户配置或设置 `LEXIO_DATA_DIR` 到全新临时目录后执行：

1. 导入仅含测试词的 CSV，开始学习并提交评分；进入 Library。
2. 通过“完整备份”按钮和系统保存选择器保存 `.lexio-backup`；确认文件存在、能重新打开，SQLite `integrity_check` 通过。
3. 再导入另一测试词、再提交一次评分，并修改一个可用设置；记录当前词库、卡片状态、ReviewLogs、设置值。
4. 点击“恢复备份”，通过文件选择器选择第 2 步文件，完成 UI 确认；重新打开或刷新应用，核对第 2 步状态恢复、后续新增/评分不再存在，并确认恢复前安全快照按设计生成。
5. 选择非备份文件，确认显示易懂失败提示且当前数据库内容不变；打开 Picker 后取消，确认无数据变化；如能稳定触发恢复写入失败，再验证回滚和提示。

在上述流程取得真实操作证据前，完整备份/恢复 UI 仍是发布阻断项。

## 数据库启动错误与自动恢复的最新实测

使用最新 `publish-recheck-20261018` 产物和三个独立临时数据目录完成了启动路径复验，未触碰用户默认目录：

- 主库为非 SQLite 内容、无自动备份：显示“Lexio 无法启动”窗口；正文展示主库、自动备份和诊断日志路径，说明数据库已保留且未自动删除/重建，并提供选择完整备份、打开数据目录、复制指引入口。损坏主库 SHA-256 前后相同，未生成自动备份；技术错误写入临时 `logs/lexio.log`。
- 主库损坏、有有效自动备份：应用正常进入主窗口；恢复后的数据库存在，原损坏主库被保留为 `.corrupt-*` 文件，SHA-256 与替换前一致。
- 主库与自动备份都无效：再次显示启动错误窗口；两文件 SHA-256 均保持不变，没有以空库覆盖。

正常首次启动也已在隔离目录验证：最新安装版实际显示 Today，创建 `lexio.db` 和自动备份。

## Release 构建与测试

- 最新复验再次使用当前项目 Release 配置构建 `Lexio.slnx`：**成功，0 错误，32 条 MVVMTK0045 警告保留**。随后完整 Release 测试：Core **86/86**，App **11/11**，合计 **97/97**；失败 0、跳过 0。本轮没有升级依赖或批量重构 ViewModel。
- 最新复验日志位于隔离临时目录：
  - `C:\Users\dlcmh\AppData\Local\Temp\Lexio-final-release-rerun-20261018\release-build.log`
  - `C:\Users\dlcmh\AppData\Local\Temp\Lexio-final-release-rerun-20261018\release-tests.log`

## 发布门槛结论

| 门槛 | 状态 |
|---|---|
| Release 构建、Core/App 测试 | **已通过** |
| 自包含发布目录和安装器生成 | **已通过** |
| 本机隔离安装、升级、卸载及数据保留 | **已通过** |
| 无预装 .NET 的干净 Windows 安装和启动 | **未验证** |
| 安装器签名与公开发布者身份验证 | **未解决** |
| Library 空状态文案与最新发布 UI | **已修复并通过实际运行确认** |
| 备份/恢复 UI 与实际文件选择器端到端流程 | **未验证，发布阻断** |
| Inno Setup 商业许可适用性 | **未验证；由发布方确认** |

**结论：不建议进入 RC。** 完整备份保存 UI 和生成文件现已在隔离实例验证；仍须完成真实 Picker UI 恢复、恢复后重启核对和无 .NET 预装的干净 Windows 启动验证。正式对外发布前还须签署安装器，并确认 Inno Setup 的许可适用性。Library 文案需在可操作 UI 中确认显示正确。

## 最终门槛补充检查（2026-10-18）

### 备份/恢复人工验收准备

- 新增可直接执行的[人工验收清单](backup-restore-manual-acceptance-2026-10-18.md)。测试包位于 `%TEMP%\Lexio-Backup-UI-Manual-20261018-4931ca38510948399c4b6b95151e5822`，包含显式指向独立 `data` 子目录的启动器、两份 UTF-8 CSV 测试词表、无效备份文件、空的 `backups`/`evidence` 目录。首次由测试启动器运行时才会初始化测试数据库；该目录不会指向 `%LOCALAPPDATA%\Lexio`。
- 检查到本机有 11 个 `Lexio.exe` 进程，来自数个先前测试输出路径。为避免关闭、复用或误连任何现存实例，本轮没有启动或终止这些进程。测试包的数据目录保持为空，等待人工通过专用启动器初始化。
- PickerHost 仍不可由当前 UI 自动化工具控制，因此本轮没有保存/选择文件，也没有实际导入或恢复。取消、无效文件提示、成功恢复和重启核验均**未验证**。清单明确要求真实人工操作和截图，且禁止在有效测试数据库上破坏文件或权限以模拟失败。
- 当前 MainWindow 隐藏 Settings 导航，没有可供人工修改的设置 UI。服务层设置恢复由既有 Core 测试覆盖；设置的端到端 UI 恢复仍**未验证**。

### 干净 Windows 环境可用性

- 当前系统为 Windows 11 专业版，版本 `10.0.26300`；本机安装了 `.NET Runtime 10.0.11` 和 `Windows Desktop Runtime 10.0.11`，因此本机启动不能证明未预装 .NET 的环境可运行。
- `WindowsSandbox.exe`、VMware `vmrun.exe` 和 VirtualBox `VirtualBoxVM.exe` 均不可用。查询 Sandbox 可选功能状态需要提升权限；本轮未申请或更改权限。Hyper-V PowerShell 命令存在，但 `Get-VM` 被主机授权策略拒绝（提示需联系 AM1REUX 计算机授权策略管理员）。没有创建或更改系统功能、权限或 VM。
- 无 .NET 10 的干净安装、首次启动和数据库初始化测试为**未验证**；本机运行不能替代该测试。

### 签名、分发范围与发布者决定

- 最新独立安装器 `Lexio-Setup-x64-recheck.exe` 的 `Get-AuthenticodeSignature` 结果仍为 `NotSigned`；SHA-256 为 `BD3089CC3E3A0A01D4C8DB75B63B991447770D90966047459DAAB7817B103AE1`。没有购买服务、创建证书或签署文件。
- 当前 `installer/Lexio.iss` 没有配置 Inno 的 `SignTool`/`SignedUninstaller`，其 `[Files]` 通配规则也未请求签署源文件；`AppPublisher` 当前显示为 `Lexio`。选定并验证发布者证书后，还需由发布者确定证书显示名并与安装器元数据统一，选择安全的签名工具配置，签署应用自身可执行文件及最终安装器/卸载器，并验签。微软建议对 Authenticode 新发布使用 SHA-256 并配置 RFC 3161 时间戳；Inno 的 `SignTool` 可在编译时签署 Setup/卸载器以及显式标记的源文件。当前配置差异依据[Inno SignTool 说明](https://jrsoftware.org/ishelp/topic_setup_signtool.htm)、[Inno 源文件签名标记](https://jrsoftware.org/ishelp/topic_filessection.htm)和[微软 Authenticode 时间戳说明](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)。未修改安装器脚本。
- 内部开发/自控测试可用未签名文件；自签证书只适合受控测试设备或由管理员预置信任的内部环境。小范围外部测试宜使用公开信任的代码签名，并向测试者说明新发布者仍可能出现 SmartScreen 提示。公开直发需要可信签名；新签名不会立刻消除 SmartScreen 警告，Windows 11 Smart App Control 也可能拦截无签名文件。微软对各种分发和风险的说明见[Windows 应用代码签名选项](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)和[SmartScreen 声誉说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)。
- Azure Artifact Signing（原 Trusted Signing）：个人开发者的公开信任资格目前限美国和加拿大；个人身份信息取自 Azure 账单帐户，法定姓名和售卖地址必须与政府身份证明一致。配置需要 Entra tenant、Azure subscription、Artifact Signing 资源/证书配置及门户身份校验。Basic 当前为每月 `$9.99`、包含 5,000 次签名，超出部分每次 `$0.005`；参见[官方快速入门](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)和[官方价格](https://azure.microsoft.com/en-us/products/artifact-signing)。发布者所在地/身份未确认，未注册 Azure 资源。
- 传统 CA 方案：微软给出的 OV 一般预算范围为每年 `$150–300`，但供应商、地区和密钥方案会改变价格。Sectigo 当前页面明确列出个人 OV 申请和政府照片证件视频核验、物理地址/联系方式核验及 FIPS 硬件密钥要求；其页面显示五年选项起价为每年 `$536.25`。自 2026-02-23 起公信代码签名证书最长有效期为 459 天，多年计划会按年重新签发并可能每年配发新硬件。发布者实际国家/地区是否可申请和交付、含税与最终报价仍需供应商按实际身份确认；参见[Sectigo 验证说明](https://www.sectigo.com/faqs/detail/OV-Code-Signing-Validation-for-Organizations-and-Individuals)及[价格和产品说明](https://www.sectigo.com/ssl-certificates-tls/code-signing)。这些是产品信息，不构成法律意见或购买建议。
- Inno Setup 官方说明：个人/自由职业者将其用于营利工作且年收入超过 `$5,000`，或年营收超过该额度的营利组织，属其定义的商业用户。作者请求商业用户购买许可；许可证按用户分配；运行 IDE/编译器属于使用，发布/运行已构建安装器本身不属于使用。官方称安装器到生产使用前不必先购买。本项目的商业性质和收入条件未知，故适用性为**需要发布者决定**；本轮未购买许可。参见[Inno Setup 商业许可说明](https://jrsoftware.org/isorder.php)。该记录不是法律意见。

### 本轮状态表

| 项目 | 状态 | 证据/后续动作 |
|---|---|---|
| 隔离测试包和人工步骤 | **部分验证** | A2 导入/搜索已在另一隔离实例通过；保存完整备份 UI 和文件完整性检查也已通过。测试包原 `data` 目录中的完整先后状态流程仍未执行。 |
| 保存完整备份与真实 Picker | **已验证（隔离实例）** | Windows App SDK 保存 Picker 成功写出 `.lexio-backup`；文件 SQLite 完整性检查通过。旧文件打开 Picker 的恢复选择、确认、恢复和重启核对仍未完成。 |
| 取消、无效文件错误提示和 UI 回滚 | **未验证** | 清单覆盖取消、无效备份；恢复中途故障注入需独立可控环境，不得破坏当前数据。 |
| 设置的端到端 UI 恢复 | **未验证** | 设置页隐藏；Core 测试覆盖服务层，当前 UI 没有修改设置的入口。 |
| 无 .NET 10 的干净 Windows 启动 | **未验证** | Sandbox 不存在，Hyper-V 枚举被主机策略拒绝，其他 VM 工具不存在。 |
| 当前安装器签名 | **未解决** | 安装器实际为 `NotSigned`。 |
| 个人签名地区、身份与费用 | **需要发布者决定** | 先确定发布者法定身份和国家/地区，再选择 Artifact Signing 或供应商可接受的传统证书；未创建账号或购买。 |
| Inno Setup 许可适用性 | **需要发布者决定** | 需要确认项目营利方式及官方收入门槛；未购买许可。 |
| Inno Setup 安装器构建、安装/升级/卸载 | **已解决且实际验证** | 见本报告前文的隔离安装验证记录；本轮未改动安装器。 |
| Release 构建和自动化测试 | **已验证（2026-10-18 本轮）** | 独立输出目录 Release 重建成功，0 错误、32 条 MVVMTK0045 警告；Core 94/94、App 53/53，合计 147/147。 |

### 恢复后运行时状态修复（2026-10-18 后续代码更新）

- 源码追踪确认：恢复服务会替换 SQLite 内容，但原 `StudyService` 仍保留进程内队列、活动会话计数和当前 FSRS 引擎。若用户在活动会话中从 Library 恢复较旧备份，Learn 可能继续显示备份后才存在的卡片，并尝试向恢复后的数据库写入过期会话状态。
- 恢复入口现在先暂停活动会话。成功后清空并重载 `StudyService`，按恢复后的 `desired_retention` 重建 FSRS 引擎，并刷新 Library 的词书及当前搜索结果。恢复失败且原数据已安全回滚时恢复原会话；若无法回滚则保持暂停，显示安全快照路径并锁定主界面操作，要求重启 Lexio。运行时刷新失败也会显示安全快照路径并锁定主界面操作。
- 新增隔离数据库回归覆盖：恢复后丢弃仅存在于恢复前内存中的活动队列；恢复所选备份内保存的未完成会话。
- 最新完整 Release 验证：`dotnet test Lexio.slnx -c Release --no-restore -v:minimal`，Core **88/88**、App **11/11**，共 **99/99**；`dotnet build Lexio.slnx -c Release --no-restore -v:minimal` 成功，0 错误、32 条 MVVMTK0045 警告。没有更新依赖或修改发布产物。
- 本轮没有操作正在运行的 Lexio 进程，也未触碰用户默认数据库。新恢复流程仍需按[人工清单](backup-restore-manual-acceptance-2026-10-18.md)真实操作文件选择器；Picker UI 端到端验收仍**未验证**。

**最终建议：暂不进入 RC。** 备份保存 UI 已实测；恢复选择、恢复后重启和无 .NET 的干净 Windows 启动仍未实测；安装器尚未签名；Inno Setup 商业许可与发布者签名身份仍需发布者决定。此轮没有更新安装器或既有发布产物，也没有触碰默认用户数据目录。

### 隔离 Picker UI 实测尝试（2026-10-18 本轮）

- 本轮按人工验收清单，使用源码 Release 可执行文件启动独立 Lexio 进程（PID 34948），并将该进程的 `LEXIO_DATA_DIR` 指向隔离测试包的 `data` 子目录。已实际进入 Today 和 Library，看到 Starter 词书 100 词；隔离目录创建了 `lexio.db`、`lexio.db.backup`、`lexio.db-wal`、`lexio.db-shm`。没有访问默认用户数据目录，也没有关闭或操作其他 Lexio 进程。
- 真实点击“导入 CSV / TXT”后系统“打开”对话框出现。PickerHost 未作为可操作窗口暴露；坐标点击报告目标点位于 PickerHost.exe，激活 Lexio、刷新并遵照运行时指导重试后仍被拒绝。因此按工具安全规则停止输入，未选择文件、未导入或更改词库。
- 该尝试只验证 Library 可打开原生 Picker；不代表导入通过。备份保存、恢复确认/覆盖、取消、无效文件提示、恢复后的 Learn 状态和重启核对依旧未验证。后续需由人工在隔离文件选择器中继续执行 `docs/backup-restore-manual-acceptance-2026-10-18.md`。

### Library 搜索竞态修复（2026-10-18 本轮）

- 根因：`LibraryViewModel.SearchAsync` 对异步结果无条件更新界面；用户清空搜索框时只清除列表，却不使旧查询失效。旧查询完成后会重新填入结果，清空时加载指示器也会继续显示。
- 修复：为每个搜索请求生成递增版本，只允许最新请求写入结果；清空查询也会使旧请求失效，并关闭加载指示器。
- 新增两个基于真实隔离 SQLite 数据库的 ViewModel 回归测试，分别验证清空后旧结果不回填、清空后加载状态立即结束。两项测试均先在旧实现上按预期失败，修复后通过。
- Release 测试：App **13/13**，Core **88/88**，总计 **101/101**。普通 Release 重建使用独立输出目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-search-release-01e620d857fd4169ad57707560e1b8ed`，**0 错误，32 条 MVVMTK0045 警告**；未覆盖正在运行的源码实例。
- 用户回复已选择导入 CSV 后的只读核验仍显示数据库完整、仅有 Starter 词书且没有测试词，因此未记录导入通过。备份/恢复的真实 UI 流程仍未验证。

### 原生文件选择器异常反馈（2026-10-18 后续代码更新）

- Library 导入、完整备份保存和完整备份恢复的选择器创建、窗口绑定、打开操作统一经过 `PickerOperation.TryRunAsync`。用户取消仍静默返回；系统调用或初始化抛错时记录具体操作的诊断日志，并显示相应的“导入/备份/恢复未完成”提示。
- 新增 `PickerOperationTests` 两项回归测试，验证异常上报和取消不报错。App **15/15**、Core **88/88**，共 **103/103** 通过。源码 App Release 重建输出到 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-error-release-a6b5349e9fbe4b5da7`，`Lexio.exe` 存在，0 错误、32 条 MVVMTK0045 警告。
- 测试覆盖共享异常边界，但没有通过真实 PickerHost 人工触发 Windows 系统级拒绝；文件保存、导入、恢复及取消的端到端 UI 流程仍**未验证**。

### CSV 导入后刷新当前搜索（2026-10-18 后续代码更新）

- 根因：Library 搜索命中空结果后，导入服务会写入词库并刷新词书列表，但 ViewModel 不会重跑当前搜索，页面继续显示过期的空结果。
- 修复：每次搜索保存当前查询；成功导入并刷新词书后，经 UI dispatcher 重跑该查询。导入失败时保留原结果，不额外查询。
- 新增真实隔离 SQLite ViewModel 回归测试；旧实现以“结果集合为空”失败，修复后通过。完整 Release 测试为 App **16/16**、Core **88/88**，共 **104/104**。源码 App Release 重建输出至 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-import-refresh-release-0e9a51ffbdcc45f08b`，0 错误、32 条 MVVMTK0045 警告。
- 验证覆盖 ViewModel 数据流，不等同于 PickerHost 的端到端导入 UI 验收；隔离人工导入尚未取得成功证据。

### Library 窄窗口标题操作区（2026-10-18 后续代码更新）

- 根因：标题和横排的备份/恢复/导入按钮一直共用同一行；页面两侧各有 40px 内边距，窄窗口时按钮组会挤压标题区域。
- 修复：窗口宽度低于 760 逻辑像素时将操作区放到标题下方，并顺延搜索、状态和内容行；低于 500 逻辑像素时按钮改为纵向排列。
- 新增 XAML 结构回归测试验证状态触发器与行映射。完整 Release 测试为 App **17/17**、Core **88/88**，共 **105/105**。源码 App Release 重建输出至 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-narrow-header-release-f42a086bf99a44a0a1`，`Lexio.exe` 存在，0 错误、32 条 MVVMTK0045 警告。
- Computer Use 当前未返回可操作的 Windows 应用窗口，因此窄宽度实际渲染、键鼠操作和 DPI 实测均**未验证**；结构测试不能替代 UI 验收。

### 正常退出时停止自动备份并释放资源（2026-10-18 后续代码更新）

- 根因：启动时的 15 分钟自动备份循环是无取消令牌的后台任务；`MainWindow` 关闭时没有停止循环，也没有释放 `Speech` 或 `AppDatabase`。
- 修复：抽出可取消的 `AutomaticBackupWorker`，关闭时等待正在进行的快照完成并取消定时等待；`MainWindow.Closed` 同步等待 `AppServices.ShutdownAsync`，后者逐项释放语音、worker 和数据库连接，并将清理异常写入诊断日志。
- 新增隔离 SQLite 测试：验证初始自动快照可校验，停止 1 小时定时 worker 会及时完成且重复释放安全。App **18/18**、Core **88/88**，共 **106/106**。源码 App Release 重建输出至 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-graceful-shutdown-release-7e106cafdcaf465fac`，0 错误、32 条 MVVMTK0045 警告。
- worker 与数据库释放通过自动化验证；实际关闭窗口的进程退出耗时和 TTS 正在播放时的退出表现仍**未验证**，本轮未启动或关闭用户实例。

### 连续改进续验（2026-10-18）

- 使用 Visual Studio Build Tools 的 VSTest 17.14.0 运行现有 Release 测试程序集：Core **88/88**、App **18/18**，共 **106/106**。程序集时间为 13:08–13:09，与当前源码最后修改时间相符；该操作执行的是已编译程序集，不是源码重建。
- 当前机器的 `dotnet --info` 显示仅安装 .NET Runtime 10.0.11，未安装 .NET SDK。源码 Release 测试/构建命令无法启动，错误为 `No .NET SDKs were found`；本轮未下载或安装 SDK。最新源码构建证据仍为上节 13:09 的 Release 构建记录。
- 隔离人工导入仍**未验证**：用户报告选择 `before-backup.csv` 后没有提示或页面变化；只读数据库核验仍显示 `integrity_check = ok`、100 个 Starter 词、没有 `serendipity`/`ephemeral`。Computer Use 再检查仍为 `apps: []`，无法区分文件选择器未返回文件与导入状态未显示；未对数据库写入。

### .NET SDK 临时恢复与源码 Release 续验（2026-10-18）

- 为恢复源码级验证，从微软官方 `dot.net` 下载带有效 Microsoft Authenticode 签名的 `dotnet-install.ps1`，将免费 .NET SDK **10.0.402** 安装到 `%TEMP%\Lexio-dotnet-sdk-10`；使用 `-NoPath`，未修改系统 PATH、项目依赖或默认数据库。
- 以 `--no-restore` 和独立输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-verified-release-87d04d7c7ddf47f3bf1b1e246f85f044\bin` 从当前源码重跑 Release 测试：App **18/18**、Core **88/88**，合计 **106/106**；随后完整 Release 构建成功，`Lexio.exe` 已生成，**0 个错误、32 条现有 MVVMTK0045 警告**。
- 新构建没有运行；Computer Use 仍返回 `apps: []`。真实 Picker 导入/备份/恢复 UI 验收仍未完成；测试数据库及默认用户数据库均未写入，本轮未关闭或覆盖任何 Lexio 进程。

### TTS 关闭竞态保护（2026-10-18）

- 根因：Learn 自动发音和手动发音会异步调用 `Speech.SpeakAsync`；窗口关闭时 `ShutdownAsync` 可释放 `Speech`，但尚未完成的语音合成在返回后仍可能创建播放器并开始播放，且释放后新调用仍可创建资源。
- 修复：`Speech` 在同一把锁下拒绝释放后的新请求，并在异步合成完成后再次检查状态；关闭后到达的流会立即释放，不会播放。`Dispose` 现在幂等，且与资源创建、播放串行化。
- 新增两个可控异步回归测试，验证“关闭后合成返回不播放且释放音频流”以及“关闭后不再合成、重复释放安全”。RED 阶段两项测试分别复现迟到播放和释放后仍启动合成；修复后通过。
- 最新源码 Release 测试：App **20/20**、Core **88/88**，合计 **108/108**；完整 Release 构建成功，输出目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-speech-final-88b09867de5f4f668dea25ede8a9ac13\bin`，`Lexio.exe` 已生成，0 错误、32 条现有 MVVMTK0045 警告。未升级依赖或操作用户数据库。
- 测试验证生命周期并发路径，不验证真实 Windows 语音设备输出、窗口关闭耗时或长时间播放；Computer Use 当前仍未提供可操作原生窗口。

### Picker 选择结果状态反馈（2026-10-18）

- 用户在隔离导入对话框选择 `before-backup.csv` 后报告没有提示或页面变化。随后只读检查隔离数据库：`integrity_check = ok`、100 个 Starter 词、没有 `serendipity` 或 `ephemeral`。没有向隔离库或默认用户库写入数据。当前窗口工具不能观察 PickerHost，无法判断该文件是否由系统选择器交回 Lexio。
- Picker 返回空值时之前静默返回，用户无法区分取消和操作无反馈；若确实返回文件，现有命令会更新 Library 状态行。修复后，导入/备份保存/恢复选择期间显示进度，取消明确说明未更改数据，选择器错误继续走诊断日志与错误对话框；备份和恢复结束状态也显示在 Library 状态行。
- `PickerOperationTests` 增至 3 项，覆盖非空选择、取消回调以及异常回调互不混淆。完整 Release 测试：App **21/21**、Core **88/88**，共 **109/109**。Release 构建成功，输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-status-verify-68795b6e\bin`，0 错误、32 条既有 MVVMTK0045 警告。
- 此修复缩小了取消/异常的诊断盲区，但未验证 PickerHost 的真实文件交回、CSV 导入、备份保存或恢复 UI。Windows 原生窗口仍不可由当前 UI 自动化会话检查；不得将 109 项自动测试视作端到端验收通过。

### CSV 引号字段跨行导入（2026-10-18）

- 根因：导入器用 `ReadAllLinesAsync` 按物理行处理 CSV；包含合法引号换行的字段会被 `ParseCsvLine` 判定为“未闭合双引号”，整份文件被拒绝。
- 修复：读取 UTF-8 文本后，仅在 CSV 引号外按 CR/LF 拆记录，再交给既有字段解析器。数据库写入及列映射未改动。
- 新增隔离 SQLite 回归测试，确认跨行例句内容保留原 CRLF，只写入两个预期词且不产生额外词条。RED 阶段复现原错误；修复后通过。既有单行 CSV、引号转义与畸形 CSV 测试均通过。
- 完整 Release 测试：App **21/21**、Core **89/89**，共 **110/110**。Release 构建成功，输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-csv-multiline-verify-fac11d6d\bin`，0 错误、32 条既有 MVVMTK0045 警告。
- 修复验证到 Core 导入服务边界；PickerHost、Library 页面实际选取 CSV 与 UI 提示仍未由当前原生窗口工具验证。未改写此前隔离手动测试数据库或默认用户数据库。

### CSV `front`/`back` 表头别名（2026-10-18）

- 根因：导入器把 `front` 识别为表头，但取词名时仍按首列回退；`back,front` 会将释义误导入为 lemma，随后真实英文词无法搜索。释义列也没有显式读取 `back`。
- 修复：表头模式优先按 `word`/`单词`/`front` 和 `zh`/`释义`/`back` 查映射，再沿用原来的首列/次列兼容回退。CSV 无表头路径不变。
- 隔离 SQLite 测试覆盖 `back,front` 与 `front,back` 两种顺序，并核对正确 lemma 和释义。旧实现的反向列顺序用例失败，修复后两种均通过。
- 最新完整 Release 测试：App **21/21**、Core **91/91**，共 **112/112**；构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-csv-header-alias-verify-ed76cf46\bin`。
- 该测试验证导入服务的数据映射，不替代 PickerHost 和 Library 导入 UI 的实机操作；真实 UI 仍未验证。

### Library 搜索失败反馈（2026-10-18）

- 根因：`LibraryViewModel.SearchAsync` 只在结果成功或查询清空时将 `Searching` 设为 false。数据库读取异常会让页面的 fire-and-forget 搜索任务故障，加载指示器不会停止，也没有用户可见的失败状态。
- 修复：当前查询失败时清除过期结果、结束加载、在搜索状态行显示“搜索失败，请重试”，并通过 Library 页面注入的错误回调写入诊断日志；已被更新查询替代的旧请求不能覆盖当前界面状态。
- 隔离测试使用未迁移 SQLite 触发真实 `no such table: words` 错误；旧实现以异常逸出失败，修复后断言任务正常结束、加载关闭、错误状态可见且诊断回调收到异常。
- 最新完整 Release 测试：App **22/22**、Core **91/91**，共 **113/113**；Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-search-error-verify-2cf43d8e\bin`。
- SearchStatus 的绑定已由源码检查；当前 Computer Use 没有可操作原生窗口（`apps: []`），错误状态的实际屏幕渲染仍未验证。Picker UI 导入/备份/恢复同样未完成端到端验收。

### Library 操作状态可见性与启动恢复异常边界（2026-10-18）

- 用户选择隔离测试文件 `fixtures\before-backup.csv` 后报告没有提示或页面变化。随后对专用测试数据库执行只读核验：`integrity_check = ok`、仍为 100 个词、没有 `serendipity` 或 `ephemeral`；未确认导入，也没有向默认用户数据库写入。
- 根因之一是 Library 虽已更新 `StatusText`，但它位于右侧词书面板底部，容易被忽略。现将导入、备份和恢复状态移到搜索框下方的状态区；结果计数与搜索进度环保持原位。
- 启动错误窗口的恢复按钮现在对整个异步流程设异常边界。选择器初始化/打开失败、未选择、取消确认、确认框或恢复回调异常都有可见状态；技术异常写入启动诊断日志。
- 增加异步守护回归测试和 XAML 状态区绑定测试。完整 Release 测试：App **24/24**、Core **91/91**，共 **115/115**。Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-feedback-startup-verify-20261018\build-bin`，`Lexio.exe` 已生成。
- 当前 Computer Use 仍返回 `apps: []`，未能查看 PickerHost 或实际状态区渲染。因此此修复改善可见性但不证明文件选择器已返回文件；隔离导入、完整备份和恢复端到端 UI 仍**未验证**。

### Library 导入命令异常反馈（2026-10-18 后续）

- 检查 `OnImportClick` 时发现，选择器成功返回文件后，`ImportCommand.ExecuteAsync` 不在异常边界内。CSV 服务会将常见解析/文件异常转换成结果，但后续刷新词书或搜索若抛错，可能逃出 `async void`，仅由全局处理器记日志。
- 现将导入命令也通过 `PickerOperation.TryRunAsync` 守护。意外异常会写入诊断日志并显示“导入未完成”提示，提醒用户先核对词书再决定是否重试，因为部分写入不能一概排除。
- 复用已新增的异步守护回归测试。完整 Release 测试仍为 App **24/24**、Core **91/91**，共 **115/115**；本次源码 Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-import-guard-verify-20261018\build-bin`。
- 隔离数据库本轮未写入。人工复验启动器已更新为该最新构建并继续设置专用 `LEXIO_DATA_DIR`；本轮没有启动启动器。Computer Use 仍返回 `apps: []`，Library 状态区和 PickerHost 的实际 UI 行为仍**未验证**。

### Library 备份与恢复对话框异常边界（2026-10-18 后续）

- `OnRestoreBackupClick` 的确认对话框原先位于恢复 `try/catch` 之外，显示异常会逃出 `async void`。另一个错误路径是：备份创建成功后，成功提示框异常会落入创建失败分支，造成结果误报。
- 现将恢复确认和所有 Library `ContentDialog` 显示操作纳入异步守护。确认框无法显示时不开始恢复；取消仍显示取消状态。提示框无法显示时记录诊断并更新状态，不覆盖已经完成的备份或恢复结果。
- 新增异步操作成功路径测试，配合既有异常路径测试覆盖守护行为。完整 Release 测试：App **25/25**、Core **91/91**，共 **116/116**。Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；最终验证输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-dialog-guard-final-20261018\build-bin`。
- 隔离启动器已更新指向最终验证构建，仍设置测试包专属数据目录；本轮未启动，未写入测试数据库。真实对话框行为和 PickerHost 交互仍因 `apps: []` 无法验收。

### Learn 会话完成后的键盘焦点（2026-10-18 后续）

- 源码检查确认 Learn 在新卡、答案揭示和暂停/继续时均设置了焦点，但会话完成后只切换到总结面板，没有把焦点移离已隐藏的卡片或评分控件。
- 总结按钮现在有明确的 `ReturnToTodayButton` 名称；`Done` 变为真后，页面在低优先级 UI 队列中等待总结布局完成，再将程序化焦点移到该按钮。
- 新增 XAML 回归测试，验证总结面板包含命名且连接到 `OnBackToToday` 的返回按钮。先按预期失败，再通过。完整 Release 测试：App **26/26**、Core **91/91**，共 **117/117**。完整 Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-summary-focus-verify-20261018\build-bin`。
- 真实键盘焦点迁移仍未由桌面 UI 验收；Computer Use 返回 `apps: []`。隔离备份/导入测试库未写入，启动器已更新至该构建。

### Learn 评分模式对应的键盘提示（2026-10-18 后续）

- 简化评分只接受 `1` 和 `3`，但页脚原先无论模式都提示 `1–4 评分`。`KeyboardHint` 现在按 `FourGradeMode` 显示 `1/3` 或 `1–4`，评分模式变化时发出属性通知，XAML 页脚绑定该属性。
- 新增 ViewModel 测试验证两种提示文本和模式切换通知，并新增 XAML 绑定测试。完整 Release 测试为 App **28/28**、Core **91/91**，共 **119/119**。本轮重新构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-import-followup-verify-20261018\artifacts`。
- 真实键盘提示显示和按键操作尚未由桌面 UI 验收。之后用户再次选择 `before-backup.csv` 并报告无提示或页面变化；当前可见进程未运行这次最新隔离构建，隔离测试目录也没有新写入，无法将该结果归因到最新源码。Picker UI 导入继续标记为**未验证**。

### 连续 TTS 请求的播放顺序（2026-10-18 后续）

- 根因：Library 点读或 Learn 自动发音可并行合成；旧请求若较晚完成，会在较新词语音之后重新设置播放器源，播放过期发音。
- 修复：每次朗读在锁内递增请求版本；合成完成后只允许当前最新版本播放。被新请求取代的合成流由现有 `finally` 路径释放；没有增加取消层或依赖。
- 新增确定性并发回归测试，先验证旧实现会播放两个结果，再验证修复只播放较新词且释放过期流。完整 Release 测试：App **29/29**、Core **91/91**，共 **120/120**。Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-speech-order-final-20261018\artifacts`。
- Windows 语音引擎、真实音频中断/替换体验尚未在桌面端验收；此修复验证的是合成完成乱序时不播放旧词。

### `before-backup.csv` 隔离导入复验（2026-10-18 后续）

- 在 Core 测试中按手动测试夹具的原始列布局和两行内容写入临时 CSV，通过独立 SQLite 导入；新增 2、更新 0、跳过 0，且词性、中英文释义和双语例句均正确保存。该结果确认 CSV 内容与解析/数据库服务可用。
- 新增精确夹具回归测试后，完整 Release 测试为 App **29/29**、Core **92/92**，共 **121/121**；Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-csv-fixture-final-20261018\artifacts`。
- 用户之前的无反馈操作无法关联到最新隔离构建，PickerHost、Library 状态行和实际 CSV UI 端到端仍未验证。手动复验启动器已更新到此次构建，并继续使用测试包自己的数据目录。

### `before-backup.csv` 后续导入结果与实例身份核对（2026-10-18）

- 用户报告隔离窗口曾显示“导入完成及数量”，具体新增/更新/跳过数量不记得；随后在其认为的同一窗口搜索 `serendipity`，没有结果。
- 用户无法确认操作窗口的进程 PID。指定隔离实例 PID 32184 仍在运行，但标题仍为普通的 `Lexio`；其独立目录内 `lexio.db`、WAL、SHM 和自动备份文件的修改时间都停留在 14:09:33（启动时刻），没有观测到本次导入写入。此前对该目录的只读 SQLite 检查为 `integrity_check = ok`、100 个 Starter 词、无 `serendipity` / `ephemeral`。
- 现有同名 Lexio 进程不止一个，因此本次用户操作很可能落在另一个窗口，但进程身份不能确认。此结果不能作为导入成功，也不能归因于指定隔离实例中的 CSV/SQLite 代码缺陷；默认用户数据库未访问。Picker UI 端到端验收保持**未验证**。
- 当前源码和 App/Core 自动化测试覆盖服务层成功导入、计数状态和导入后搜索刷新；未能覆盖这次进程身份不明的原生 Picker 操作。下次实机复验需先确保窗口可识别，再核对其 `LEXIO_DATA_DIR` 下的数据库。

### 隔离实例标题标识与 CSV 复验重启（2026-10-18 后续）

- 为避免不同数据目录的并行 Lexio 窗口都显示相同标题，设置 `LEXIO_DATA_DIR` 的实例现在将数据目录末级名称附加在窗口标题上；默认用户数据实例仍显示 `Lexio`，不泄漏完整路径。
- 新增的 4 项标题格式测试在实现前按预期失败，改动后通过。完整 Release 测试为 App **34/34**、Core **92/92**，共 **126/126**；Release 构建 0 错误、32 条既有 MVVMTK0045 警告，隔离输出位于 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-window-title-final-20261018`。
- 新构建的独立实例 PID **3744** 已实际启动，窗口标题为 `Lexio — Lexio-UI-Verify-20261018-title-marker-01`，只使用同名隔离数据目录。Picker 的 CSV 文件交回、状态提示和搜索命中仍待此次具名实例人工复验；此前 A2 仍标记为**未验证**。
- 后续用户报告“导入失败或取消”，并确认“不确定 / 没看到”；无法分辨取消与异常，也没有成功导入证据。PID 3744 对应隔离数据目录的 DB/WAL/SHM/自动备份修改时间仍停留在启动时刻，且没有诊断日志。Computer Use 返回 `apps: []`，无法现场检查原生选择器。A2 继续未验证；没有访问默认用户库。

### Library CSV 导入结果对话框（2026-10-18）

- 在异步导入和词书/搜索刷新结束后，Library 现在通过既有 `ShowMessageAsync` 呈现结果摘要：成功显示新增/更新/跳过数；服务拒绝文件时显示可读错误。选择器取消依旧只显示行内未更改状态，异常仍使用既有错误守护。
- 增加两个基于临时 SQLite 的 `LibraryViewModelTests`，覆盖成功摘要和无效内容反馈；RED 阶段由于缺少反馈契约而编译失败，接入后通过。完整 Release 测试：App **36/36**、Core **92/92**，共 **128/128**。Release 构建 0 错误、32 条既有 MVVMTK0045 警告，输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-import-feedback-release-20261018`。
- 该构建启动后的隔离 UI 实例 PID **3860**，标题 `Lexio — Lexio-UI-Verify-ImportFeedback-20261018-7c64b2e733b74dfb9a4873a510a02a42`，响应正常，使用全新临时数据目录。Computer Use 仍返回 `apps: []`，实际 Picker 文件交回和结果对话框尚未验收；CSV 人工步骤 A2 仍**未验证**。
- 用户确认无法操作该隔离窗口/文件选择器，本轮没有实际导入或搜索；不再请求重复操作。实际 UI 验收仍待有可操作 Windows 窗口的环境。

### 自动备份刷新失败时保留已恢复数据（2026-10-18）

- 在隔离 SQLite 回归测试中，把自动备份目标预先建成目录，确定性触发自动快照写入失败；确认用户选定的数据库仍成功恢复，`AutomaticBackupUpdated` 为 false 且返回错误，恢复前快照仍保存恢复前的数据。
- 本轮仅增加数据安全分支的 Core 回归测试，没有改变恢复服务行为。单项用例通过；完整 Release 测试为 App **36/36**、Core **93/93**，共 **129/129**。完整 Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；独立输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-automatic-backup-refresh-release-20261018\bin`。
- 测试只操作临时 SQLite 数据；没有关闭运行中的 Lexio 实例，也没有访问默认用户数据库。真实恢复对话框和自动备份失败提示的桌面 UI 仍未人工验收。

### 窗口退出前等待正在进行的 Learn 操作（2026-10-18）

- 根因：Learn 评分在读取近期复习历史后，再通过另一项数据库操作原子保存卡片、队列和复习日志；窗口关闭的资源清理可能在两步之间释放数据库，导致用户刚完成的评分未提交。
- 修复：`StudyService.WaitForPendingOperationsAsync` 按现有会话锁顺序等待会话/评分状态操作完成，但不结束或清空活动学习会话；正常关闭流程在停止自动备份后、释放数据库前等待该屏障。
- 新增隔离 SQLite 回归测试，用外部 `BEGIN IMMEDIATE` 阻塞评分写入，验证屏障保持等待，解除写锁后评分落库且活动会话仍保留。用例先因屏障 API 缺失而无法编译，接入后通过。
- 完整 Release 测试为 App **36/36**、Core **94/94**，共 **130/130**。Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-shutdown-drain-release-20261018\bin`。
- 验证覆盖 StudyService 屏障和数据库写入竞态；真实桌面关闭耗时、TTS 播放中关闭以及关闭时其他非学习页面的后台查询尚未通过 UI 运行验证。没有关闭现有 Lexio 实例或访问默认用户数据库。

### Learn 页眉暂停/继续切换与焦点恢复（2026-10-18）

- 根因：暂停后页眉按钮图标和提示已显示“继续”，但绑定的命令仅在活动学习状态可执行；同一 `P` 快捷键还同时绑定页眉和暂停浮层按钮。页眉按钮因此不可用，快捷键目标也不唯一；其辅助功能名称始终是“暂停”。
- 修复：页眉命令现在在活动/暂停状态之间切换；`P` 只绑定一个页眉目标，浮层按钮仍可点击继续；按钮辅助功能名称按状态更新。继续后焦点回到未揭示卡片的“显示释义”按钮，或已揭示答案区域。
- 新增 ViewModel 测试覆盖页眉命令暂停后仍可执行、继续后恢复会话，并验证辅助功能名称随状态变化；XAML 测试验证按钮绑定和唯一的 `P` 目标。旧 CanExecute 状态断言同步更新。
- 完整 Release 测试：App **39/39**、Core **94/94**，共 **133/133**。Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-pause-toggle-release-20261018\bin`。
- 当前没有可操作的原生桌面窗口，因此页眉按钮视觉状态、真实键盘快捷键路由和焦点迁移尚未桌面验收；本轮只通过 ViewModel/XAML 自动化和 Release 构建验证。未访问默认用户数据库或关闭现有 Lexio 实例。

### Today / Insights 加载失败反馈与重试（2026-10-18）

- 根因：Today 与 Insights 页面以 fire-and-forget 调用 `LoadAsync`；数据库查询失败时异常逸出，`Loading` 留在 true，页面只会一直显示加载状态，也没有重试入口。
- 修复：两个 ViewModel 现在捕获加载异常、记录诊断日志、停止加载并公开可读错误；页面错误条提供“重试”命令。Today 上次已有计划时保留旧结果并明确提示，初次加载失败时不启用开始学习。
- 新增隔离 SQLite 测试，未迁移数据库确定性触发 `no such table: cards`；验证异常不逸出、加载状态结束、错误回调收到异常，迁移并播种后点击 Retry 可成功加载。另有 XAML 结构测试验证两个页面的错误文字和重试按钮绑定。旧实现两项测试均因 SQLite 异常逸出而失败，修复后通过。
- 完整 Release 测试：App **43/43**、Core **94/94**，合计 **137/137**。Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-dashboard-load-release-20261018\bin`，`Lexio.exe` 已生成。
- 本轮没有操作默认用户数据库或运行中的 Lexio 实例。错误条实际桌面渲染、诊断日志位置和真实 Retry 点击尚未通过 UI 验收；当前原生窗口不可操作。

### Library 词书列表加载失败反馈与重试（2026-10-18）

- 根因：Library 首次导航及服务 Ready 后以 fire-and-forget 调用 `LoadDecksAsync`，词书查询错误会逸出且页面没有重试入口。现在这两条页面加载路径经过安全包装：记录诊断错误、显示“词书列表加载失败，请重试”，保留之前显示的词书，并提供重试命令；导入和恢复仍使用原先会向流程调用方报告失败的加载方法。
- 新增隔离 SQLite 回归测试：未迁移库触发列表查询失败，验证异常不逸出、旧词书保留、诊断收到错误；迁移并播种内置词书后，重试成功、错误清除、词书更新。另有 XAML 结构测试验证错误文字和重试命令出现在词书区。测试在实现前因安全加载入口/错误状态缺失而失败，接入后通过。
- 完整 Release 测试：App **45/45**、Core **94/94**，合计 **139/139**。Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-deck-load-release-20261018\bin`，`Lexio.exe` 已生成。
- 用户报告无法操作当前隔离窗口/原生文件选择器，因此词书区错误状态的实际桌面呈现和重试点击未验证；CSV 导入、备份/恢复 Picker UI 端到端仍未验证。自动化只操作临时 SQLite；未操作默认用户数据库或关闭现有 Lexio 实例。

### Learn 偏好设置异步加载，避免重复同步 SQLite 查询（2026-10-18）

- 根因：Learn 每张卡都会读取 `AutoPronounce`，卡片动画和页面转场也会读取 `ReduceMotion`；`AppServices.GetBool` 原先在属性访问时同步等待异步 SQLite 查询，且缺省值不写入缓存，未设置过的默认值会反复触发数据库访问并阻塞 UI 线程。
- 修复：新增 `UserPreferences`，在主窗口创建前异步加载四项 UI 偏好；同步属性读取现在只查线程安全的内存缓存。恢复完整备份后重新加载缓存，设置写入成功后才更新内存值。
- 新增隔离 SQLite 测试，覆盖已保存设置、缺省设置和数据库值改变后的缓存重载。测试在实现前因缓存类缺失而无法编译，接入后通过。
- 完整 Release 测试：App **47/47**、Core **94/94**，合计 **141/141**。Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-user-preferences-release-20261018\bin`，`Lexio.exe` 已生成。
- 这一轮通过代码路径与隔离测试验证了偏好加载/缓存行为；没有进行桌面性能计时或真实窗口动画验收。未操作默认用户数据库或关闭现有 Lexio 实例。

### Today 学习会话启动失败提示与重试（2026-10-18）

- 根因：Today 计划加载已有错误处理，但点击强度选项后 `StudyService.StartAsync` 的数据库异常会从异步命令逸出；按钮状态虽在 `finally` 中恢复，页面没有错误提示。另有状态属性原先在 `ConfigureAwait(false)` 后直接更新，可能从后台线程触发绑定通知。
- 修复：启动异常现在写诊断日志并通过现有错误条显示“学习启动失败，请重试”；重试按钮会重试上次的强度，计划加载失败时仍会重新加载计划。按钮加载状态和成功导航事件都通过 UI dispatcher 更新/触发。
- 隔离 SQLite 回归测试先加载有效计划，再关闭测试数据库，确定性触发会话启动失败；验证异常不逸出、加载状态归位、错误反馈和重试再次执行。另测成功启动后只发出一次导航事件且状态恢复。旧实现用例因 `ObjectDisposedException` 逸出而失败，修复后通过。
- 完整 Release 测试：App **49/49**、Core **94/94**，合计 **143/143**。Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-today-start-release-20261018\bin`，`Lexio.exe` 已生成。
- 用例只使用临时 SQLite。真实 Today 错误条显示和按钮点击未进行桌面 UI 验收；没有访问默认用户数据库或关闭运行中的 Lexio 实例。

### Today 空学习计划禁用启动（2026-10-18）

- 根因：Today 加载计划后不区分各强度实际卡片数，一律设置 `CanStart = true`。当三档都没有任务，或仅轻量档没有到期复习时，点击仍会创建零卡会话。
- 修复：每个 `IntensityOption` 记录计划卡片总数，只有总数大于零的强度按钮可执行；所有计划都为空时禁用开始并提示“今天没有待学词卡”。
- 新增隔离 SQLite 回归测试覆盖三档全空与“轻量档空、常规档有新词”两种情况，并用 XAML 契约测试锁定空状态提示。RED 阶段两项 ViewModel 测试都因零卡选项可执行而失败，改动后通过。
- 完整 Release 测试：App **52/52**、Core **94/94**，合计 **146/146**。Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-today-empty-final-20261018\bin`，`Lexio.exe` 已生成。
- 测试只使用临时 SQLite。原生窗口当前不可操作，Today 空状态的真实视觉效果和按钮禁用样式未桌面验收；未访问默认用户数据库或关闭运行中的 Lexio 实例。

### Library CSV 导入与搜索桌面验收（2026-10-18）

- 使用上节 Release 产物启动隔离 PID **2136**，标题 `Lexio — Lexio-UI-Auto-Verify-20261018-c82f30b3f3114e6381c9e85a1e50e5cc`，数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Auto-Verify-20261018-c82f30b3f3114e6381c9e85a1e50e5cc`。Computer Use 确认 Today 中轻量档在 0 卡时呈禁用状态，然后进入 Library。
- 通过真实 Windows 文件选择器选取 `before-backup.csv`；页面和结果对话框均显示新增 2、更新 0、跳过 0，词书列表显示 2 词。搜索 `serendipity` 后显示 1 条结果及双语释义。
- 对该实例的 SQLite 以只读模式核验：`integrity_check = ok`，`serendipity` 与 `ephemeral` 均存在，`before-backup` 词书关联 2 个词。此次完成 A2 导入/搜索步骤；完整备份、恢复、重启和错误分支 UI 仍未验收。未访问默认用户数据库或关闭其他 Lexio 实例。

### 完整备份保存选择器兼容修复（2026-10-18）

- 两次真实 UI 操作复现旧完整备份保存选择器抛出 `COMException (0x80004005)`，并留下 0 字节占位文件；备份服务没有执行。现改为 Windows App SDK 1.8 的 `FileSavePicker(WindowId)`，只替换完整备份的保存 Picker，保留原文件类型、建议文件名与备份服务。新增 `LibraryBackupPickerTests` 源码契约回归测试。
- 使用 Release 构建 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-fix-20261018\bin\Release\net10.0-windows10.0.19041.0\win-x64\Lexio.exe` 在全新 `LEXIO_DATA_DIR` 中实测，PID **39960**，标题 `Lexio — Lexio-UI-BackupPicker-Fix-20261018-9f7e2c41b6cd4f0f937eaabc6c79881b`。保存对话框成功生成 `backups\checkpoint.lexio-backup` 并显示完成提示；备份文件只读 SQLite 检查 `integrity_check = ok`，词数 100。
- 完整 Release 测试 App **53/53**、Core **94/94**，合计 **147/147**；Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**。`git diff --check` 通过；Git 仅另提示已有文件的 LF/CRLF 工作区转换告警。
- 恢复验证未完成：后续原生 PickerHost 的打开操作在 Computer Use 中仍返回非目标窗口拦截，按流程仅激活刷新后重试一次，之后停止 UI 输入。未点击恢复确认按钮，未替换任何数据库；恢复、重启与异常恢复路径仍待隔离 UI 验收。所有测试数据位于独立 `%TEMP%` 目录；没有访问或关闭默认用户实例或其他 Lexio 进程。

### Today 学习启动 UI 与 dispatcher 修复复验（2026-10-18）

- 本轮误选了 14:07 输出的旧隔离构建（PID **32184**，数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Verify-20261018-02d79f89669441c6aae9ee8ea089c719`）；点击常规计划后，该旧二进制记录 `COMException (0x8001010E)`，堆栈指向 `ProgressRing.IsActive` 的绑定更新。该构建早于当前源码中的 dispatcher 修复，不能代表最新 Release；旧实例仍保持运行。
- 使用 15:11 的当前 Release 产物，在新隔离数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-CurrentRelease-Learn-fa992a70f9804d11875057ce6ff3b7c1` 启动 PID **38644**。Today 显示 100 个待学新词和常规档 6 个新词；真实点击后 Learn 显示 `0 / 6`、首词 `ability`、音标和键盘提示。页面可响应，隔离目录未生成诊断错误日志；未提交评分，未访问默认用户数据库。
- 定向回归：`TodayStartFailureTests` **2/2 通过**，输出目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-today-start-live-check-20261018\bin`。当前源码的启动状态回收及 `SessionStarted` 事件均通过 `DispatchAsync` 更新/调用；此前完整 Release **147/147**、构建 **0 错误/32 条既有 MVVMTK0045 警告**结果不变。
- Learn 页脚在本次 1157 px 窗口中保持单行；Computer Use 的边框拖动与系统“大小”菜单未改变窗口尺寸，未达到 1100 px 自适应阈值，因此不把这次记录算作窄窗视觉验收。`TextWrapping` XAML 回归测试仍是窄窗布局证据；真正更窄的桌面截图待后续可控调整窗口时补验。截图只留在本轮 Computer Use 记录中。

### 当前工作树 Release 复验（2026-10-18）

- 使用已安装的 .NET SDK **10.0.402** 从当前磁盘工作树 Release 构建 `Lexio.slnx`，输出根目录为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-current-worktree-revalidate-20261018-03170307\bin`。构建成功、**0 错误、32 条既有 MVVMTK0045 警告**，并确认隔离输出中存在 `Lexio.exe`。
- 对同一输出运行完整 Release 测试：App **53/53**、Core **94/94**，合计 **147/147**，失败 0、跳过 0。
- 本次只重新编译和执行自动化测试，没有启动新 Lexio 实例；测试使用临时数据。此结果确认当前工作树可构建且自动化测试通过，不替代恢复 Picker UI、窄窗实际布局或干净 Windows 环境的验收。

### DispatcherQueue 关闭期间避免后台线程执行 UI 更新（2026-10-18）

- 根因：`AppServices.DispatchAsync` 在 `DispatcherQueue.TryEnqueue` 返回 `false` 时，会直接在调用线程执行 UI 回调。Windows App SDK 文档说明队列关闭后不再接受新任务并返回 `false`；后台搜索、加载等在窗口关闭时完成，可能因此从非 UI 线程更新 WinUI 绑定或集合。
- 修复：新增不依赖 WinUI 的 `UiDispatcher`，统一处理同步和异步 UI 工作。当前线程已有 UI 访问权时直接执行，避免 Learn 评分在 UI 线程上多排一轮队列；后台线程的工作仍通过队列执行并等待完成、传播异常。队列关闭并拒绝新任务时跳过已过时的 UI 工作，避免错误线程访问。
- 新增 10 项回归测试，覆盖 UI 线程直接执行、后台入队、拒绝入队、异步完成等待和同步/异步异常传播。测试先因缺少辅助实现/线程访问分支而编译失败；接入后 10/10 通过。
- 当前工作树完整 Release 测试：App **63/63**、Core **94/94**，共 **157/157**；构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。隔离输出根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-ui-dispatcher-final-20261018-03\bin`。
- 自动测试验证了关闭队列拒绝入队时不执行 UI 回调；真实窗口关闭期间的线程时序未通过桌面 UI 测试。测试只使用隔离输出及临时测试数据库，未启动新实例或访问默认用户数据库。

### Learn 计时改用单调时钟（2026-10-18）

- 根因：卡片总时长用 UTC 时间戳，窗口失焦用本地 `DateTime.Now`，显式暂停用 UTC `DateTime.UtcNow`。系统校时或时区/时间调整会让用于回忆校准的原始时长和失焦间隔失真。
- 修复：`EffectiveTiming` 和 `StudyService` 通过 `TimeProvider.GetTimestamp/GetElapsedTime` 计算卡片与暂停间隔；Learn 窗口失焦区间通过 `Stopwatch` 记录。既有公开构造调用保持兼容，`StudyService` 可在测试中注入可控时钟。
- 新增两项可控时钟 Core 回归测试，分别验证卡片计时与暂停剔除在 UTC 时间回拨后仍按单调经过时间计算；新增 Learn 激活处理器契约测试，锁定其不依赖本地墙上时钟。三项用例均先在旧实现下按预期失败，再通过。
- 完整 Release 测试：App **74/74**、Core **96/96**，共 **170/170**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-monotonic-timing-final-20261018\bin`，已确认生成 `Lexio.exe`。
- 本轮没有启动应用或访问默认数据库。Computer Use 没有可操作的 Lexio 窗口，用户也表示无法操作最新窗口/文件选择器，因此没有进行真实窗口交互；Library 导入结果框和窄窗 Learn 页脚仍以已有代码、回归测试验证，实际视觉/Picker 操作未在本轮验收。

### Dispatcher 新构建隔离启动与正常关闭冒烟（2026-10-18）

- 使用上述新 Release 输出启动隔离 PID **35292**，标题 `Lexio — data`，进程响应正常；仅在进程环境中设置 `LEXIO_DATA_DIR=C:\Users\dlcmh\AppData\Local\Temp\Lexio-UiDispatcher-UI-Verify-20261018-01\data`。
- 通过 `CloseMainWindow` 发送正常关闭请求，进程在 20 秒内退出。退出后以只读方式检查隔离 `lexio.db` 和 `lexio.db.backup`，两者 `PRAGMA integrity_check` 均为 `ok`，各有 100 个词。
- Computer Use 返回 `apps: []`，因此无法检查窗口内容、执行导航或 Learn 评分操作；本次只算启动/退出冒烟和数据完整性检查，不算页面 UI 或关闭评分竞态的端到端验收。没有访问默认用户数据库，也没有关闭其他 Lexio 进程。

### TTS 异常写入持久诊断日志（2026-10-18）

- 根因：语音合成和播放的异常只写到 `Debug` 输出；普通 Release 使用时没有持久诊断记录，难以区分语音引擎故障和无声播放。
- 修复：`AppServices` 将 `Speech` 的异常回调接入现有 `StartupFailureReporter`，记录完整异常到当前数据目录的 `logs\lexio.log`。日志回调自身失败会被隔离，TTS 仍保持不向学习命令抛异常；播放失败时待播放流仍按现有逻辑释放。
- 新增 4 项回归测试，覆盖合成失败被记录、播放失败被记录且流释放、日志回调失败不逸出，以及播放与流释放同时失败时仍记录两项异常。双重故障用例先因释放异常从 `finally` 逸出而失败；修复后 TTS 定向测试 **7/7** 通过。
- 完整 Release 测试：App **67/67**、Core **94/94**，合计 **161/161**。解决方案 Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-speech-final-20261018\bin`。
- 本次只验证错误回调和生命周期逻辑，未通过真实语音设备/UI 触发故障或确认实际发音；当前原生窗口不可操作。测试没有使用默认用户数据库，也没有关闭任何 Lexio 实例。

### 导入与恢复改用窗口关联的 App SDK 文件选择器（2026-10-18）

- 代码检查确认：Library CSV/TXT 导入、Library 完整备份恢复和启动错误窗口恢复仍用旧 WinRT `Windows.Storage.Pickers.FileOpenPicker`，依赖 HWND 初始化；完整备份保存已用带 `AppWindow.Id` 的 Windows App SDK 选择器。Windows App SDK 1.8 新 Picker 以 `WindowId` 构造，并为桌面 WinUI 消除显式 HWND 关联步骤，见 [Windows App SDK 1.8 发布说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8) 和 [FileOpenPicker API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.storage.pickers.fileopenpicker?view=windows-app-sdk-1.8)。
- 修复：三个打开流程改为 `Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId)`，保留 Documents 起始位置和各自扩展名过滤；路径由 Picker 的 `PickFileResult.Path` 传入导入/恢复服务，恢复确认使用该路径的文件名。`LibraryOpenPickerTests` 固定窗口关联、过滤器和路径交接；新增用例在修改前因仍使用旧 Picker 而失败。
- 真实 WinUI App 与完整解决方案 Release 构建均成功，0 错误，32 条既有 MVVMTK0045 警告；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-final-20261018\bin`，已确认生成 `Lexio.exe`。完整 Release 测试 App **69/69**、Core **94/94**，共 **163/163**，失败 0、跳过 0。
- 本次没有真实操作 PickerHost；当前自动化工具未能控制原生文件选择器，用户也表示无法操作该窗口，因此导入、恢复文件交回及对话框行为仍未 UI 验收。没有访问默认用户数据库或关闭其他 Lexio 实例。

### Learn 派生显示状态的属性通知（2026-10-18）

- 根因：`ShowQuestion`、`ShowNewBadge`、`NotFourGradeMode` 是依赖其他属性的绑定状态，但 `Revealed`、`NewBadge`、`FourGradeMode` 改变时没有转发对应的 `PropertyChanged`，可能让 WinUI `x:Bind` 保留旧的可见性或模式状态。
- 修复：`LearnViewModel` 在 `Revealed`、`NewBadge`、`FourGradeMode` 更新时通知依赖属性；`ShowCard` 的三个依赖状态 `InSession`、`Waiting`、`Done` 变化时统一通知 `ShowCard`、`ShowQuestion` 与 `ShowNewBadge`。
- 新增 4 项 ViewModel 回归测试，分别覆盖 `Revealed`、`NewBadge`、卡片显示状态、`FourGradeMode` 的依赖通知。旧实现的前三项依赖通知用例因缺少属性事件而失败；修复后通过。完整 Release 测试 App **73/73**、Core **94/94**，合计 **167/167**，失败 0、跳过 0。
- 完整解决方案 Release 构建成功、0 错误、32 条既有 MVVMTK0045 警告；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-derived-state-final-02-20261018\bin`，已确认生成 `Lexio.exe`。
- 本次验证的是 ViewModel 属性通知和构建，不是实际 WinUI 渲染/键盘操作；当前桌面窗口无法操作。测试使用临时 SQLite，没有访问默认用户数据库或关闭其他 Lexio 实例。

### Library 导入异常提示与诊断日志（2026-10-18）

- 根因：`DictionaryService.ImportCsvAsync` 将所有异常的原始 `Message` 作为 `ImportResult.Error` 返回，缺失文件时用户会看到本地绝对路径；`LibraryViewModel` 也拿不到异常对象写入诊断日志。
- 修复：导入结果现在在保留 `Error` 的同时携带内部 `Failure` 异常。App 写入现有诊断日志，结果框只显示“无法读取文件或写入词库。请检查文件访问权限并查看诊断日志。”；CSV 内容校验错误仍显示原有具体提示。
- 新增 Core 与 App 回归测试，使用隔离数据目录中的缺失文件验证安全提示、异常保留、诊断回调和不泄露文件路径。两项测试在修复前分别因缺少异常契约和暴露原始路径而失败，修复后通过。
- 完整 Release 测试：App **75/75**、Core **97/97**，共 **172/172**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-import-error-final-20261018\bin`，已生成 `Lexio.exe`。
- 本轮未启动应用或访问默认数据库。用户表示无法操作最新窗口/文件选择器，Picker 和实际结果框呈现未进行桌面验收；后续可在可操作的隔离窗口再验证 UI 交互。

### Library 标点词头搜索分词修复（2026-10-18）

- 根因：FTS 查询清理逻辑会移除标点并拼接两侧字符，`can't` 被改写为 `cant*`，与 SQLite FTS5 `unicode61` 把它拆成 `can`、`t` 的规则不一致。若词库还有 `canto`，FTS 非空结果会阻止 LIKE 回退，造成搜索返回错误词且漏掉目标词。
- 修复：搜索清理现在在标点处保留词元边界，并按 FTS5 的词元规则构造前缀查询。
- 回归测试先验证旧实现会错误返回 `canto` 且漏掉 `can't`，再验证修复后的搜索只匹配目标词。使用临时 SQLite 数据，没有访问默认用户数据库。
- 完整 Release 测试：App **75/75**、Core **98/98**，共 **173/173**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-search-punctuation-final-20261018\bin`，已生成 `Lexio.exe`。
- 本轮未启动应用；验证覆盖 SQLite 搜索行为，不包含 WinUI 搜索框的实际运行验收。

### PickerHost 隔离窗口复核（2026-10-18 后续）

- Computer Use 的 `sky.list_apps()` 本轮能列出多个 Lexio 窗口；按完整标题选中隔离实例 `Lexio — Lexio-UI-BackupPicker-Fix-20261018-9f7e2c41b6cd4f0f937eaabc6c79881b`。无障碍树确认其打开对话框当前位于测试素材 `fixtures` 目录，并列出 `before-backup.csv`。
- 文件名栏接受了测试 CSV 的完整路径。点击“打开”时 Computer Use 明确拒绝输入，指出目标点属于 `PickerHost.exe` 而不是 Lexio；激活 Lexio、重新观察后只重试一次，仍被同一目标窗口检查拒绝。按工具规则停止输入，没有绕过原生 PickerHost，也没有把文件交给导入服务。
- 对该实例隔离数据库只读检查：`PRAGMA integrity_check = ok`、100 个词、没有 `serendipity` 或 `ephemeral`。本次没有导入或修改数据库，默认用户数据未访问。
- 该检查解释了为何此环境无法继续完成 Picker UI 端到端复验；Library 导入和完整备份保存已有独立实例实测，但完整备份恢复、确认框和恢复后重启仍未验收。此轮没有发现需要据此修改导入/恢复服务的代码缺陷。

### CSV 开头空白记录不应遮住表头（2026-10-18 后续）

- 根因：CSV 解析原先只剔除字段数量为零的记录；物理空行会解析为一个空字段，导致开头有空行时首条 `word,zh,...` 表头被当作单词导入，后续列按无表头文件解释，丢失词性和英文释义等字段。
- 修复：在表头识别前忽略字段全为空或空白的记录。空白行不再导入伪词，也不计入跳过数量；有内容的记录仍按原 CSV 字段映射及事务路径处理。
- 新增隔离 SQLite 回归测试，覆盖开头多个空白行、表头列、词性及双语释义；测试在旧实现上因多导入伪词 `word` 而失败，修复后通过。
- 完整 Release 测试：App **75/75**、Core **99/99**，共 **174/174**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-import-blank-records-final-20261018\bin`。
- 本轮修改仅涉及 Core 导入解析；未进行实际 WinUI Picker 操作。测试使用临时 SQLite，默认用户数据库未访问。

### CSV lemma 不能只包含标点（2026-10-18 后续）

- 根因：lemma 字符白名单允许连字符和撇号，却没有要求至少出现一个字母；因此 `---`、`'` 等纯标点会被写入词库并生成学习卡片。
- 修复：有效 lemma 现在必须含至少一个 Unicode 字母，其他字符继续遵守原有的字母、空格、连字符和撇号白名单。
- 新增 3 个隔离 SQLite 用例，覆盖纯连字符、纯撇号及混合标点。旧实现下三项都会被接受；修复后均计为跳过，且不创建空词书。
- 完整 Release 测试：App **75/75**、Core **102/102**，共 **177/177**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-import-validation-final-20261018\bin`，包含 `Lexio.exe`。
- 本轮修改仅涉及 Core CSV 校验；没有进行真实 WinUI 文件选择器验收，默认用户数据库未访问。

### Learn 词头区按可用宽度换行（2026-10-18 后续）

- 根因：卡片头部用横向 `StackPanel` 并排放 NEW 徽标、词头和发音按钮。横向 `StackPanel` 不会按剩余窗口宽度约束子项；词头即使设置了 `TextWrapping`，仍可能按最多 560 DIP 加按钮宽度测量，窄窗下超出卡片。
- 修复：将头部改为 `Auto / * / Auto` 三列 Grid，词头占中间弹性列，按扣除徽标和发音按钮后的宽度折行；保留现有字体、字号、间距、IPA 显示和发音快捷键。
- 新增 `LearnLayoutTests.LemmaUsesRemainingWidthBetweenBadgeAndPronunciationButton`，验证徽标、词头和发音按钮列关系。该测试在旧 XAML 上先因词头没有 Grid 列约束而失败，修改后通过。
- 完整 Release 测试：App **76/76**、Core **102/102**，共 **178/178**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-header-layout-final-20261018\bin`，已生成 `Lexio.exe`。
- 本轮没有可操作的原生桌面窗口，实际窄窗渲染、DPI 缩放和主题外观未验收；结构回归测试与 Release 构建不等于视觉验收。测试未操作默认数据库或现有 Lexio 进程。

### 高对比度禁用按钮保留系统对比色（2026-10-18 后续）

- 根因：两个自定义按钮模板的禁用状态分别把整个控件设为 45% 和 40% 不透明度。高对比度字典虽使用系统颜色，整体透明度仍会把前景和背景与后方内容混合。
- 修复：新增主题资源 `LexioDisabledOpacity`；Default 和 Light 保持 0.45，HighContrast 使用 1。两个模板继续使用现有系统禁用背景色，并从主题资源读取不透明度。
- 新增 `ThemeResourceTests.DisabledControlOpacityUsesThemeResourceWithHighContrastOverride`，验证三个主题值以及两个模板均绑定该资源。用例在旧 XAML 下先因缺少资源失败，修正测试对 XAML 属性元素名称的解析后，在旧值下再次失败；改用主题资源后通过。完整 Release 构建确认 `x:Double` 与 WinUI XAML 编译通过。
- 完整 Release 测试：App **77/77**、Core **102/102**，共 **179/179**，失败 0、跳过 0。解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-high-contrast-opacity-final-20261018\bin`，包含 `Lexio.exe`。
- Computer Use 当前仍返回 `apps: []`，未能切换系统高对比度或检查实际禁用按钮渲染；本轮未访问默认用户数据库，也未关闭现有 Lexio 实例。

### Learn 暂停遮罩遵循高对比度窗口色（2026-10-18 后续）

- 根因：Learn 暂停遮罩直接使用半透明黑色 `#66000000`，高对比度系统窗口色会被混成灰色，固定颜色未遵循用户选择的配色。
- 修复：新增 `LexioPauseScrimBrush` 主题资源；Default/Light 保留原有半透明黑色，HighContrast 改用 `SystemColorWindowColor`。Learn 暂停层改为绑定主题资源。
- 新增 `ThemeResourceTests.LearnPauseScrimUsesThemeAwareBrush`，验证三套资源值及 Learn 遮罩绑定。用例先因资源缺失失败，接入资源后通过。
- 完整 Release 测试：App **78/78**、Core **102/102**，共 **180/180**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-pause-scrim-theme-final-20261018\bin`，已生成 `Lexio.exe`。
- Computer Use 仍返回 `apps: []`，无法切换高对比度或观察暂停状态；系统色资源与 XAML 绑定已由结构测试及 WinUI Release 编译验证，实际视觉效果仍待桌面验收。未操作默认用户数据库或现有 Lexio 实例。

### Library 空词书导入指引适配窄窗（2026-10-18 后续）

- 根因：空词书提示要求用户从“右上角”导入；Library 在窗口宽度低于 **760 DIP** 时会将操作按钮移到标题下方，低于 **500 DIP** 时还会改为纵向排列，因此旧指引在窄窗中定位错误。
- 修复：提示改为“请使用‘导入 CSV / TXT’添加单词后开始学习”，直接使用按钮名称，不依赖按钮的屏幕位置。没有词库数据或数据库逻辑变更。
- 新增 `LibraryLayoutTests.EmptyDeckHintNamesImportActionWithoutAssumingItsScreenPosition`，先在旧文案上因“右上角”定位而失败，修改后通过；同时确认指引中的导入按钮名称与实际按钮一致。
- 完整 Release 测试：App **79/79**、Core **102/102**，共 **181/181**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-empty-copy-final-20261018\bin`，包含 `Lexio.exe`。
- Computer Use 当前返回 `apps: []`，无法在真实窄窗运行截图验收；XAML 结构和 Release 编译已验证。未访问默认用户数据库或关闭现有 Lexio 实例。

### TTS 退出时分别释放语音资源（2026-10-18 后续）

- 根因：退出清理先后释放语音合成器和播放器，但第一项 `Dispose()` 抛出异常会直接跳过第二项；同时对象已标记为 disposed，后续调用不会重试，造成播放器资源可能留存。
- 修复：两个资源分别尝试释放，单项异常分别交给现有错误报告路径处理；一次失败不会阻止另一项清理，重复 `Dispose()` 仍保持幂等。保留现有测试构造方式，并增加独立释放注入点以覆盖两项均失败的情况。
- 新增 `SpeechTests.DisposeAttemptsBothResourcesAndReportsBothFailures`，检查两项清理都执行、两类错误都报告，且重复释放不重复清理。针对 TTS 生命周期的定向 Release 测试 **8/8** 通过。
- 完整 Release 测试：App **80/80**、Core **102/102**，共 **182/182**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-speech-dispose-final-20261018\bin`，已确认生成 `Lexio.exe`。
- `git diff --check` 通过（退出码 0）；Git 仅报告现有工作树文件的 LF/CRLF 转换提示。未用真实语音设备验证播放或关闭窗口期间的实际驱动行为；Computer Use 未提供可操作桌面窗口。未访问默认用户数据库、未关闭现有 Lexio 实例。

### Library 导入摘要的页面提示接线（2026-10-18 后续）

- 用户此前在隔离窗口报告导入提示与数据库内容不一致；最新一轮无法操作该窗口/文件选择器，当前不能把现象复现并归因到确定进程。源码检查确认：页面等待导入命令完成后读取 `LastImportFeedback` 并打开 `ContentDialog`；ViewModel 只有核心导入返回成功时才刷新词书和当前搜索。现有隔离 SQLite 用例覆盖同结构的 `before-backup.csv` 两行数据和数据库查询，但这些用例不能替代实际窗口验收。
- 新增 `LibraryOpenPickerTests.ImportResultIsShownAfterTheImportCommandCompletes`，验证文件导入命令结束后页面确实把结果标题和消息传入提示对话框。该源码接线检查与两个 Picker 用例定向通过 **3/3**。
- 完整 Release 测试：App **81/81**、Core **102/102**，共 **183/183**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-import-feedback-wiring-final-20261018\bin`，包含 `Lexio.exe`。
- `git diff --check` 通过（退出码 0），仅有既有 CRLF 转换提示。当前 `ImportFeedback` 目录经只读检查仍为 100 个 Starter 词、没有两个测试词且没有对应运行进程；较早真实通过 A2 的 PID **2136** 隔离目录则有 102 个词、两个测试词和 `before-backup` 词书，完整性检查为 `ok`。这支持两个结果来自不同隔离实例；最新目录因用户无法操作选择器而未完成导入，不能用于归因其旧提示。未访问默认用户数据库或关闭任何 Lexio 实例。

### 恢复前检查迁移表约束、索引与触发器定义（2026-10-18 后续）

- 根因：除缺失/错误索引和触发器外，SQLite `foreign_key_check` 不会发现外键声明本身已被移除；只检查列名也无法发现表的唯一约束、主键或 `NOT NULL` 被削弱。旧校验会接受列名完整但约束缺失的备份。
- 修复：共享 schema 校验在内存 SQLite 中应用当前全部迁移，生成规范表定义，再将每个必需表的 `sqlite_master.sql` 与规范 SQL 比较（忽略空白和大小写），因此会发现外键、主键、唯一约束、`NOT NULL` 和默认值的结构差异。同时核对全部 **10** 个迁移索引的所属表、唯一性、非部分索引属性、列顺序、升序和 `BINARY` 排序规则，并核对 **5** 个必需触发器的所属表与 SQL 正文。校验覆盖备份创建、数据库启动检查和用户备份恢复。
- 两个新增 SQLite 回归用例分别移除 `deck_words` 外键和 `daily_plans(date, intensity)` 唯一约束；修复前两者都未被拒绝，修复后均被拒绝，且当前数据不变。恢复拒绝测试 **13/13** 通过，包括缺表、缺列、错误索引/触发器及表约束变体。
- 完整 Release 测试：App **81/81**、Core **112/112**，共 **193/193**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-canonical-schema-final-20261018\bin`，包含 `Lexio.exe`。
- 用户这次无法操作标题带 `Lexio-UI-Verify-ImportFeedback-...` 的窗口或原生文件选择器，因此导入结果提示仍未完成新的真实桌面复验。数据库修复由隔离 SQLite 测试验证，没有访问默认用户数据库，也没有关闭现有 Lexio 进程。

### 启动恢复窗口复制指引的失败处理（2026-10-18 后续）

- 根因：启动数据库错误窗口的“复制恢复指引”直接调用 Windows Clipboard API；若系统剪贴板调用失败，异常会逸出按钮事件，用户看不到失败状态，也没有写入诊断日志。
- 修复：通过可测的同步操作守护执行剪贴板写入。成功时显示已复制；异常时写入本地诊断日志，并提示用户手动选择上方文字复制。
- `PickerOperationTests.GuardedSynchronousActionReportsFailureWithoutRethrowing` 在加入同步守护前因缺少方法而无法编译；修复后定向测试通过。完整 Release 测试：App **82/82**、Core **112/112**，共 **194/194**，失败 0、跳过 0。完整解决方案 Release 构建成功，**0 错误、32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-startup-copy-final-20261018\bin`。
- 测试用注入异常验证守护和失败回调；Computer Use 返回 `apps: []`，本轮未能在实际 Windows 剪贴板故障或启动错误窗口中做桌面验证。未访问默认用户数据库或关闭现有 Lexio 进程。


### Library 导入与恢复选择器迁移复核（2026-10-18 后续）

- 按用户批准的范围核对实际工作区：Library 的 CSV/TXT 导入和完整备份恢复都已使用带 `mainWindow.AppWindow.Id` 的 Windows App SDK `FileOpenPicker`。导入仍筛选 `.csv` / `.txt` 并把 `file.Path` 传给导入命令；恢复仍筛选 `.lexio-backup` / `.backup` / `.db`，确认框从返回路径提取文件名，确认后将原路径交给恢复服务。取消会显示数据未更改状态，选择器异常会写诊断日志并显示错误提示。两个路径都没有旧 `Windows.Storage.Pickers` / `InitializeWithWindow` 调用。
- 工作区已有 `LibraryOpenPickerTests` 覆盖两个窗口绑定 Picker、扩展名和文件路径接线、恢复确认显示文件名、导入结果在命令完成后显示；`PickerOperationTests` 覆盖选择、取消与异常回调。迁移及这些测试在本轮检查前已经存在，因此没有重复改写业务代码或测试。
- 本轮无法运行新鲜测试或 Release 构建：`dotnet --info` 报告当前没有安装 .NET SDK；尝试通过 Visual Studio Build Tools 的 MSBuild 运行 App 测试时，SDK 解析失败（`MSB4236: 找不到指定的 SDK“Microsoft.NET.Sdk”`）。因此本轮 App/Core 测试计数和构建结果均记为**未验证**，之前报告中的通过结果不作为本轮证据；未安装软件或升级依赖。
- 按用户约定，PickerHost 中的真实文件选择 UI 继续标记为**未验收**。本轮只读检查源码和测试，没有访问任何数据库、启动或关闭 Lexio 实例，也没有改动其他未提交成果。
### 补跑 Library 选择器迁移的 Release 验证（2026-10-18 后续）

- 前一轮发现当前系统没有注册 .NET SDK。为完成已批准的验证，使用微软官方 `dotnet-install.ps1`（Authenticode 签名验证为 Microsoft Corporation）将 .NET SDK **10.0.402** 安装到用户临时目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-dotnet-sdk-10.0-20261018`；未修改系统 PATH、仓库项目文件或依赖版本。
- 完整 Release 测试命令 `dotnet test Lexio.slnx --configuration Release --artifacts-path ...` 成功：App **82/82**、Core **112/112**，共 **194/194**，失败 0、跳过 0；四个项目均完成依赖还原。
- 完整解决方案 Release 构建命令 `dotnet build Lexio.slnx --configuration Release --no-restore --artifacts-path ...` 退出码 **0**，**0 错误、32 条 MVVMTK0045 警告**。已确认生成 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-run-20261018-ae79b8c5ba74470ca4d5d17ea3d494ce\artifacts\bin\Lexico.App\release_win-x64\Lexio.exe`。
- 隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-run-20261018-ae79b8c5ba74470ca4d5d17ea3d494ce\artifacts`。`git diff --check` 通过，仅报告工作区现有 LF/CRLF 转换提示。
- Library 的两个选择器和对应回归测试已由本次源码检查及完整测试编译验证；PickerHost 中的真实文件选择 UI 仍按用户约定标记为**未验收**。没有启动或关闭 Lexio 实例，没有访问数据库，也没有提交或清理工作区成果。
### 隔离 Release 的 Library Picker 运行观察（2026-10-18 后续）

- 使用上一轮隔离 Release 构建启动新实例 PID **24076**，窗口标题 `Lexio — Lexio-UI-Goal-Cycle-20261018-0f443d63225640afb0caebceae7aa262`，仅使用临时数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Goal-Cycle-20261018-0f443d63225640afb0caebceae7aa262`。Today 与 Library 页面均实际渲染，Library 显示备份、恢复和导入入口。
- 点击“导入 CSV / TXT”后，Library 状态变为“正在选择导入文件…”。Computer Use 的 `sky.list_windows()` 没有返回可观察的 PickerHost/系统选择器窗口；因此只能确认选择流程已开始，不能确认选择器实际呈现、文件返回或导入完成。没有选择文件，也没有调用导入服务；这次运行不判断为产品缺陷或 Picker 成功，真实 Picker UI 仍**未验收**。
- 对本轮唯一启动的 PID **24076** 请求正常关闭返回 false。确认 PID 的映像路径和具名隔离窗口后，结束了这个测试进程；没有关闭其他 Lexio 实例。未读取默认用户数据库或检查/修改其他数据库。
- 本轮没有改动生产代码；App **82/82**、Core **112/112** 与 Release 构建 0 错误、32 条既有 MVVMTK0045 警告是上一轮同一源码的完整验证结果。

### Library 导入与恢复打开选择器复核（2026-10-18 后续）

- 按用户批准的范围核对实际工作区：Library 的 CSV/TXT 导入和完整备份恢复均使用 Windows App SDK `FileOpenPicker(mainWindow.AppWindow.Id)`。导入保留 `.csv` / `.txt` 筛选，恢复保留 `.lexio-backup` / `.backup` / `.db` 筛选；返回的 `file.Path` 继续传给现有导入命令或恢复服务，恢复确认框从该路径提取文件名。取消和选择器异常沿用状态提示、诊断日志处理。
- 当前工作区已有 `LibraryOpenPickerTests` 覆盖两处窗口绑定 Picker、筛选和路径接线、恢复确认文件名及导入反馈时序；定向测试 **3/3** 通过。完整 Release 测试 App **82/82**、Core **112/112**，合计 **194/194**，失败 0、跳过 0。
- 完整解决方案 Release 构建退出码 **0**，**0 错误、32 条既有 MVVMTK0045 警告**。隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-migration-20261018-a31a713ee6194dbca961a0470bc27b73`。没有升级项目依赖。
- 当前窗口自动化仍不能控制 Windows 原生 PickerHost，因此真实选择文件、取消和文件返回的 UI 流程继续标记为**未验收**；测试与构建不能替代这项验收。本轮未访问默认用户数据库；自动化测试仅使用测试临时数据，也未启动或关闭 Lexio 实例。

### Library 长词搜索结果窄窗溢出（2026-10-18 后续）

- 根因：搜索结果行用横向 `StackPanel` 并排显示词头和音标。`DictionaryService.ImportCsvAsync` 对词头不设长度上限，所以横向面板会按长文本的期望宽度扩展，挤压或覆盖右侧操作图标。
- 修复：搜索结果首行改用 `* / Auto` 两列 Grid；词头最多换行两行并省略，音标限宽 120 DIP 并省略。原有行点击和搜索数据没有改动。
- 新增 `LibraryLayoutTests.SearchResultLemmaWrapsWithinItsColumnBesidePronunciation`。该用例先在旧 XAML 上失败（词头父级实际是横向 `StackPanel`），修改后通过。它锁定布局结构；因当前 CUA 会话没有原生窗口控制 API，窄窗实际渲染仍**未验收**。
- 当前完整 Release 测试 App **83/83**、Core **112/112**，合计 **195/195**；解决方案 Release 构建退出码 **0**，0 错误、32 条既有 MVVMTK0045 警告。隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-lemma-wrap-final-20261018-2b49c1a7a99243f395b8002f643f12fe`，已确认生成 `Lexio.exe`。
- 本轮未访问默认用户数据库；测试仅使用临时数据。之前启动的窄窗隔离实例仍在运行，没有成功调整其窗口大小，也未关闭它。

### Learn 评分快捷键作用域与模式隔离（2026-10-18 后续）

- 根因：Learn 的键盘加速键没有设置 `ScopeOwner`，因此采用全局作用域；两档和四档评分布局同时留在页面树中，而 WinUI 加速键即使关联控件不可见也可能执行。简化评分时，隐藏四档按钮上的 `2`、`4` 仍可能提交 Hard 或 Easy；在 Learn 页面外，快捷键也可能误触发 Learn 命令。
- 修复：Learn 页面根 Grid 命名为 `LearnRoot`，所有键盘加速键都限定到该范围；四档快捷键绑定 `Vm.FourGradeMode`，两档快捷键绑定 `Vm.NotFourGradeMode`，只启用当前模式可见的评分。
- 新增 `LearnLayoutTests.KeyboardShortcutsAreScopedToTheLearnPage` 和 `LearnLayoutTests.GradeShortcutsAreEnabledOnlyForTheirGradeMode`。两项用例在旧 XAML 上按预期失败，接入作用域和模式启停后通过；定向 Learn 布局测试 **8/8** 通过。
- 完整 Release 测试：App **85/85**、Core **112/112**，共 **197/197**，失败 0、跳过 0。完整解决方案 Release 构建退出码 **0**，0 错误、32 条既有 MVVMTK0045 警告；隔离 artifacts 根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-accelerators-final-20261018-01`，已编译 WinUI XAML。
- 键盘按键在真实窗口中的端到端操作仍未验收：当前 Computer Use 会话无法操作原生桌面窗口。本轮以 WinUI Release XAML 编译和结构回归测试验证绑定；没有访问默认用户数据库或关闭任何 Lexio 实例。

### Learn 暂停和等待期间停用发音快捷键（2026-10-18 后续）

- 根因：暂停时暂停遮罩将焦点移至“继续学习”按钮，仍处于 Learn 快捷键作用域内；发音 `E` 加速键没有状态约束，`SpeakCommand` 也始终可执行。暂停键盘提示只显示继续和结束操作，却仍可能通过 `E` 播放遮罩下当前词的发音。
- 修复：`SpeakCommand.CanExecute` 现在跟随 `ShowCard`；卡片状态变化时发送命令可执行性通知。`E` 加速键也绑定 `Vm.ShowCard`，因此空闲、等待、暂停和总结状态下均停用。
- 新增 `LearnViewModelTests.SpeakCommandIsAvailableOnlyWhileAStudyCardIsVisible` 和 `LearnLayoutTests.PronunciationShortcutIsEnabledOnlyWhileACardIsVisible`。旧实现下两项按预期失败，修复后通过；全量 Release 测试 App **87/87**、Core **112/112**，共 **199/199**，失败 0、跳过 0。
- 完整解决方案 Release 构建退出码 **0**，0 错误、32 条既有 MVVMTK0045 警告；隔离 artifacts 根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-speak-gate-final-20261018-01`，已编译 WinUI XAML。
- 当前 Computer Use 会话无法操作原生桌面窗口，因此暂停时实际按键和扬声器输出仍未端到端验收；没有访问默认用户数据库或关闭任何 Lexio 实例。

### 完整备份恢复失败路径复核（2026-10-18 后续）

- 复核 `UserDataBackupService.RestoreAsync` 与 `AppServices.RestoreUserDataAsync`：恢复前安全快照先于替换创建；数据库恢复使用临时库校验后写入；后续完整校验失败会尝试从快照回滚；回滚失败会标记 `CurrentDataRestored=false` 并由 UI 阻止继续学习；自动备份刷新失败保留已恢复主库并返回可展示的警告状态。
- 独立运行 `DataReliabilityTests` 与 `UserDataBackupServiceTests`，**24/24** 通过，涵盖结构无效备份拒绝、当前数据保留、安全快照、自动备份刷新失败、恢复后会话重载和启动损坏库恢复。
- 本轮源码与现有隔离测试未暴露新的可复现实现缺陷，因此没有改动恢复业务代码。手动清单第 11 步所述“替换后发生故障、回滚也失败”的真实 UI 故障注入仍未验证；当前 Computer Use 再次返回 `apps: []`，完整备份文件选择、确认、恢复和重启后的真实桌面流程继续标记为**未验收**。
- 本轮未访问默认用户数据库或关闭 Lexio 实例。下一步保留对恢复中途退出/失败的安全性审查，并在原生桌面可操作时执行手动验收清单。

### 恢复关停生命周期与选择器当前工作区复验（2026-10-18 后续）

- 完整备份恢复由 `AsyncOperationGate` 串行协调；主窗口关停先停止接收恢复操作并等待已登记的恢复流程排空，再等待学习操作并关闭数据库。恢复流程内部 await 使用 `ConfigureAwait(false)`，降低 `Closed` 事件同步等待时依赖 UI 线程续跑的风险。
- 协调器行为和 `AppServices` 接线测试共 **6/6** 通过；Library CSV/TXT 导入与完整备份恢复的 WindowId Picker 定向测试 **3/3** 通过。当前完整 Release 测试 App **93/93**、Core **112/112**，共 **205/205**。Release 构建 0 错误、32 条既有 MVVMTK0045 警告；隔离 artifacts 根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-restore-shutdown-20261018-01`。
- `apps: []` 表明当前 Computer Use 无可观察的原生应用窗口；真实 PickerHost 文件选择、完整恢复 UI，以及恢复执行时关闭窗口的实际桌面流程仍**未验收**。验证未访问默认用户数据库，也未启动或关闭 Lexio 实例。

### AppDatabase 排队操作与重复释放安全（2026-10-18 后续）

- 根因：`AppDatabase.DisposeAsync()` 只等待串行闸门本身，排队中的操作可能在闸门销毁时仍等待；释放已开始后仍可提交操作。另外重复调用 `DisposeAsync()` 会再次等待已经销毁的 `SemaphoreSlim` 并抛出 `ObjectDisposedException`。
- 修复：数据库操作在等待闸门前登记；释放开始后立即拒绝新操作，等待所有已登记操作（包括排队操作）排空，再释放连接和闸门。多次 `DisposeAsync()` 返回同一释放任务。
- 新增 `AppDatabaseLifetimeTests.DisposeDrainsAcceptedOperationsAndImmediatelyRejectsNewOnes` 与 `DisposeAsyncIsIdempotent`。两个测试在修复前分别复现释放期间仍接受新操作和重复释放异常，修复后均通过。
- 完整 Release 测试：App **93/93**、Core **114/114**，共 **207/207**，失败 0、跳过 0。完整解决方案 Release 构建退出码 **0**，0 错误、**32 条既有 MVVMTK0045 警告**。隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-db-lifetime-20261018-01`。
- `git diff --check` 已通过。测试只使用测试临时目录，没有访问默认用户数据库；本轮没有启动或关闭 Lexio 实例。Computer Use 未提供可操作的原生应用窗口，数据库释放行为以隔离 Core 回归测试验证，桌面关窗真实流程仍未单独验收。

### 完整备份恢复期间锁定窗口交互（2026-10-18 后续）

- 根因：恢复会跨多个串行数据库调用，而 Library、主导航和学习操作在等待期间仍可被用户操作；此外，`PauseAsync()` 只等待正在答题的操作，不等待可能尚在进行的会话创建。并发操作可能与备份替换交错。
- 修复：用户确认恢复后，主窗口显示主题适配的进度遮罩并禁用导航和页面输入。恢复入口先排空正在运行的学习操作，再暂停当前会话并再次排空，之后才创建安全快照和替换数据库。恢复完成或成功回滚后解除锁定；数据/运行时状态不能安全继续时，遮罩关闭但窗口保持锁定，要求重启。
- 新增 `RestoreInteractionTests` 两项和 `AppServicesShutdownTests.UserRestoreDrainsStudyWorkBeforePausingAndReplacingTheDatabase` 一项。改动前三项均按预期失败，改动后通过。新遮罩布局与恢复状态接线由测试检查；本轮没有可操作的原生窗口，真实交互阻断、进度显示和主题外观仍待 UI 验收。
- 完整 Release 测试：App **96/96**、Core **114/114**，共 **210/210**，失败 0、跳过 0。完整解决方案 Release 构建退出码 **0**，0 错误、**32 条既有 MVVMTK0045 警告**；隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-restore-interaction-20261018-01`。
- `git diff --check` 通过。验证只使用测试临时数据，未访问默认用户数据库，也未启动或关闭仍运行的 Lexio 实例。完整恢复 PickerHost、恢复 UI、取消/无效备份和恢复中关闭窗口的实际人工验收仍未完成。

### 启动错误恢复阻止重复提交（2026-10-18 后续）

- 根因：启动错误窗口的“选择完整备份并恢复”按钮在 Picker、确认和数据库恢复流程等待期间仍可重复点击。多次点击可能同时启动多个 `AppServices.InitializeAsync(backupPath)`，争用同一主数据库并启动互相覆盖的启动流程。
- 修复：恢复处理器增加单次执行保护，并在流程期间禁用触发按钮；无论选择器取消、恢复失败或异常，`finally` 都会清除保护并重新启用按钮。恢复成功则窗口关闭。
- 新增 `StartupErrorWindowTests.RestoreClickRejectsReentryAndReenablesTheButtonAfterCancellationOrFailure`。该用例在旧实现上因缺少重入保护而失败，修复后通过。它检查处理器接线，不替代启动损坏库窗口中的真实鼠标验收。
- 完整 Release 测试：App **97/97**、Core **114/114**，共 **211/211**，失败 0、跳过 0。完整解决方案 Release 构建退出码 **0**，0 错误、**32 条既有 MVVMTK0045 警告**；隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-startup-restore-reentry-20261018-01`。
- 本轮 Computer Use 仍返回 `apps: []`，启动错误窗口和 PickerHost 中的交互尚未真实验收。测试使用隔离临时数据，未访问默认用户数据库，也未启动或关闭现有 Lexio 实例。

### Library 选择器与恢复期间数据操作协调（2026-10-18 后续）

- 按用户批准复核当前磁盘源码：Library CSV/TXT 导入和完整备份恢复均使用 Windows App SDK `FileOpenPicker(mainWindow.AppWindow.Id)`，保留原扩展名筛选、返回路径、恢复确认文件名、取消状态和异常日志反馈。迁移代码与选择器回归测试已在工作区，因此没有重复改写选择器。
- 额外发现已开始的多语句 CSV 导入或手动完整备份没有加入恢复/关停协调；恢复期间可能发生并行数据库操作。现用单一 `_dataOperations` 闸门串行化导入、手动完整备份和用户恢复；主窗口关闭时停止接收并排空闸门中的操作，再释放数据库。导入起始状态也经 UI dispatcher 更新，兼容闸门续跑到非 UI 线程的情况。
- `AppServicesShutdownTests`、`LibraryOpenPickerTests`、Library ViewModel 与 `AsyncOperationGateTests` 定向测试 **19/19** 通过。完整 Release 测试：App **98/98**、Core **114/114**，共 **212/212**，失败 0、跳过 0。完整解决方案 Release 构建退出码 **0**，0 错误、**32 条既有 MVVMTK0045 警告**；已生成隔离版 `Lexio.exe`，artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-restore-gate-20261018-01`。
- 当前 Computer Use 没有可操作的原生窗口，PickerHost 与本轮新增的导入/备份/恢复串行时序未做新的桌面验收。既有记录中的 A2 导入实测只证明当时构建的导入界面流程；不替代当前构建的 PickerHost 与恢复完整 UI 验收。没有访问默认用户数据库、启动或关闭 Lexio 实例，也没有升级依赖。

### 稳定失焦计时边界测试（2026-10-18 后续）

- 上一轮完整复跑中，`Timing_Invalid_WhenWindowUnfocusedTooLong` 曾因等待 `Task.Delay(50)` 后计时结果在舍入前略小于 50ms 而偶发失败。测试原本只需验证长失焦后计时无效，不需要依赖系统调度时长。
- 测试现使用已有 `AdjustableTimeProvider` 将单调时钟精确推进 50ms，再断言 `RawMs == 50`；运行时计时逻辑未修改。定向用例 **1/1** 通过。
- 完整 Release 测试：App **98/98**、Core **114/114**，共 **212/212**，失败 0、跳过 0。Release 构建退出码 **0**，0 错误、**32 条既有 MVVMTK0045 警告**；隔离 artifacts 根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-restore-gate-20261018-01`。
- 本轮仅运行测试与 Release 构建，没有启动桌面实例或访问用户数据库；原生 PickerHost 和完整恢复 UI 仍按前一节记录待验收。

### TTS 生命周期当前工作区复核（2026-10-18 后续）

- 复核 `Speech` 服务：新请求通过版本号抑制迟到的旧合成结果；释放与播放共用锁，释放后的合成流不会播放且会被释放；合成、播放、流释放和诊断回调异常都有隔离处理。现有测试覆盖释放竞态、连续请求顺序、资源释放和异常报告。
- 当前 Release 配置下 `SpeechTests` 定向运行 **8/8** 通过。未修改生产代码，也未启动应用或访问用户数据库。
- 这些测试使用注入的合成与播放回调，不验证 Windows 原生语音选择、真实扬声器输出或长时间重复播报。最近一次完整 Release 测试仍为 App **98/98**、Core **114/114**；本轮没有重跑完整套件或完整构建。真实 TTS 桌面验收仍待可操作窗口。

### Library 打开选择器与 CSV 编码错误提示复验（2026-10-18）

- 用户批准的 Library CSV/TXT 导入和完整备份恢复选择器在当前工作区中已使用 Windows App SDK `FileOpenPicker(mainWindow.AppWindow.Id)`。文件扩展名筛选保持原值，返回路径继续交给既有导入命令和恢复服务；取消状态与选择器异常提示/诊断日志保留，恢复确认框从所选路径提取文件名。没有升级 Windows App SDK 或其他依赖。
- `LibraryOpenPickerTests`、`PickerOperationTests` 在整套 App 测试中验证窗口绑定、筛选、路径接线、导入结果顺序、取消和错误回调。它们是代码与回调测试，不能证明原生 PickerHost 的实际选择行为。
- 另修复严格 UTF-8 解码失败时误显示通用读写错误的问题：明确提示“文件必须使用 UTF-8 编码。请另存为 UTF-8 后重试。”，保留 `DecoderFallbackException` 供既有诊断回调记录；文件编码契约及事务写入行为不变。回归用例确认无效文件未写入词库。
- 当前 Release 验证：App **98/98**、Core **115/115**，共 **213/213**，失败 0、跳过 0。完整解决方案 Release 构建退出码 0，**0 错误、32 条既有 MVVMTK0045 警告**。已生成 `Lexio.exe`；隔离 artifacts 根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-final-20261018-b45b`。
- Computer Use 当前无法操作原生 PickerHost，因此本轮实际选文件、取消以及恢复选择 UI 仍**未验收**。验证未访问默认用户数据库，未启动或关闭 Lexio 实例，未更改项目依赖。

### 启动错误恢复窗口窄窗操作区（2026-10-18 后续）

- 根因：启动错误窗口把“选择完整备份并恢复”“打开数据文件夹”“复制恢复指引”放入横向 `StackPanel`。窗口允许缩窄，外层 `ScrollViewer` 禁止横向滚动，该操作行又不会换行，因此窄视口下按钮可超出可用宽度。
- 修复：只将操作区改为纵向排列；窗口现有纵向滚动负责较长的错误说明。新增 `StartupErrorWindowTests.RecoveryActionsStackVerticallyForNarrowWindows`，修改前按预期失败，修改后通过。
- 完整 Release 测试：App **99/99**、Core **115/115**，共 **214/214**，失败 0、跳过 0。隔离 Release 构建退出码 0，**0 错误、32 条既有 MVVMTK0045 警告**；输出根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-startup-narrow-layout-final-20261018-b822`。
- 为复现根因，使用布局修改前的隔离 Release executable 打开损坏库窗口（PID 39892，数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-Narrow-StartupError-20261018-29b80e4e`）。进程属性确认标题为“Lexio 无法启动”，但 Computer Use 没有返回此窗口的可操作句柄；因此窄宽度下的实际渲染仍**未验收**。测试进程已正常退出；只创建并读取了隔离无效数据库，未访问默认数据库或其他 Lexio 实例。

### Library 无表头词表首词识别（2026-10-18 后续）

- 复现无表头文件 `word,词语\nserendipity,意外发现的乐趣` 只导入第二行：原实现把第一行中任意 `word` / `front` / `单词` 单元格都判作表头，导致合法词条 `word` 丢失。
- 新增隔离 SQLite 回归测试 `CsvImport_HeaderlessListCanStartWithTheWordWord`：旧实现失败（预期新增 2 条，实际 1 条）；修复后完整 `DictionaryTests` **22/22** 通过。表头需包含词条列以及至少一个受支持的释义/元数据列；单列 `word` 表头继续识别。
- 完整 Release 测试 App **99/99**、Core **116/116**，共 **215/215**，失败 0、跳过 0。隔离 Release 构建成功，0 错误、32 条既有 MVVMTK0045 警告；输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-library-word-first-header-fix-20261018\bin`。
- 本轮未启动桌面应用，PickerHost 和真实 Library UI 仍未验收；导入测试使用临时 SQLite 数据，没有访问默认用户数据库或升级依赖。

### 窗口退出不阻塞等待 UI dispatcher（2026-10-18 后续）

- 根因：`MainWindow.Closed` 同步等待 `AppServices.ShutdownAsync()`，而关停会排空数据闸门；进行中的 Library 导入可能正在等待 UI dispatcher 刷新词书或搜索。若用户此时关闭窗口，UI 线程会被同步等待占住，导致导入无法完成其 dispatcher 回调，关停也就无法排空。
- 修复：在 `AppWindow.Closing` 中取消首次关闭、禁用导航和页面输入，并异步等待关停；完成后再次关闭窗口。这样 dispatcher 可继续处理正在等待的数据操作 UI 更新。
- 新增 `MainWindowShutdownTests.WindowCloseDrainsAsyncOperationsWithoutBlockingTheUiDispatcher`。旧实现因没有可取消的异步 `Closing` 路径而失败，修改后通过。完整 Release 测试 App **100/100**、Core **116/116**，共 **216/216**，失败 0、跳过 0。
- 隔离 Release 构建成功，0 错误、32 条既有 MVVMTK0045 警告；输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-async-close-shutdown-20261018\bin`，已生成 `Lexio.exe`。Computer Use 当前返回 `apps: []`，因此导入/恢复进行中真实点击关闭的桌面时序仍**未验收**。未访问默认用户数据库、未启动或关闭现有 Lexio 实例，也未升级依赖。

### 批准的 Library 打开选择器范围复核（2026-10-18 后续）

- 当前磁盘源码已经满足本次批准范围：Library CSV/TXT 导入与完整备份恢复均使用绑定 `mainWindow.AppWindow.Id` 的 Windows App SDK `FileOpenPicker`；筛选、选择路径传递、取消状态、异常日志与错误提示、恢复确认文件名均保留。`LibraryOpenPickerTests` 和 `PickerOperationTests` 已覆盖这些接线及选择、取消、异常回调；因此本轮没有重复修改既有代码或测试。
- 先前默认 `dotnet` host 未找到 SDK；后续确认用户级目录已有 .NET SDK **10.0.402**，直接调用该 host 后完成了全量 Release 测试：App **100/100**、Core **116/116**，共 **216/216**，失败 0、跳过 0。隔离 Release App 构建退出码 0、0 错误、**32 条既有 MVVMTK0045 警告**，并生成 `Lexio.exe`；输出根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-continuous-validation-20261018-03\bin`。项目依赖未变更。
- `git diff --check` 通过，审查文件未发现尾随空白；仅有工作区原有 LF/CRLF 转换提示。原生 PickerHost 的实际选文件、取消和错误 UI 仍未验收；Computer Use 当前返回 `apps: []`。本轮未访问用户数据库、未启动或关闭 Lexio 实例，也没有安装或升级依赖。

### Library 带表头元数据列误作中文释义（2026-10-18 后续）

- 根因：带表头的 CSV/TXT 没有 `zh` / `释义` / `back` 列时，`back` 映射仍回退到物理列索引 1。像 `word,ipaUs` 这样的有效表头会把第二列音标写入中文释义，污染导入词条。
- 新增隔离 SQLite 回归测试 `DictionaryTests.CsvImport_HeaderWithoutGlossColumnDoesNotUseMetadataAsZhGloss`。旧实现下测试按预期失败：`ZhGloss` 实际为 `/bʊk/`；修复后通过。修复只取消带表头分支的错误回退；无表头两列文件仍以第二列作为释义。
- 完整 Release 测试：App **100/100**、Core **117/117**，共 **217/217**，失败 0、跳过 0。WinUI App Release 构建退出码 0，0 错误、**32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-headered-metadata-import-fix-20261018-01\bin`，已确认生成 `Lexio.exe`。
- 测试使用临时 SQLite；没有访问默认用户数据库、启动或关闭 Lexio 实例，也没有升级项目依赖。本轮是解析行为修复，没有新增 UI；原生 PickerHost 的实际选文件流程仍按前文记录未验收。

### Library 打开选择器批准项与部分列导入保留（2026-10-18 后续）

- 按用户批准的范围核对 Library CSV/TXT 导入与完整备份恢复：两处均使用绑定主窗口 `AppWindow.Id` 的 Windows App SDK `FileOpenPicker`，原文件扩展名筛选、路径传递、取消状态、异常日志和提示、恢复确认框文件名提取均保留。选择器代码及接线回归测试已存在于当前工作区，本轮完成验证，没有重复改写。
- 另补全部分列导入的安全语义：更新已有 lemma 时，只覆盖导入记录提供的非空字段；缺失列和空白单元格不会清空现有音标、词频、CEFR 或词义字段，记录未涉及的其他词义也会保留。新增 `CsvImport_UpdatesOnlySuppliedFieldsAndPreservesOtherSenses`；缺列与空白值在修改前均复现过字段丢失，修复后定向测试通过。
- 最终源码 Release 测试：App **100/100**、Core **118/118**，共 **218/218**，失败 0、跳过 0。完整解决方案 Release 构建退出码 0，0 错误、**32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-import-merge-final-20261018\release`，已生成 `Lexio.exe`。
- `LibraryOpenPickerTests` 与 `PickerOperationTests` 已包含在完整 App 套件内；测试验证代码接线与取消/错误回调，不等同于原生 PickerHost 手动验收。当前 Computer Use 无可操作的原生窗口，因此本轮实际打开、选文件和取消仍**未验收**。测试使用隔离临时数据；没有访问默认用户数据库、启动或关闭 Lexio 实例，也未升级项目依赖。

### 无表头空释义导入保留已有内容（2026-10-18 后续）

- 复现部分导入数据丢失的剩余路径：无表头两列记录 `book,` 会把空释义作为空字符串传入数据库，覆盖已有释义。新增 `CsvImport_BlankHeaderlessGlossPreservesExistingValue`，修改前预期失败、修复后通过；无表头格式的空释义现在按“未提供”处理，避免清空已有词义。
- 定向 `DictionaryTests` **25/25** 通过。最终 Release 测试 App **100/100**、Core **119/119**，共 **219/219**，失败 0、跳过 0。完整解决方案 Release 构建退出码 0，0 错误、**32 条既有 MVVMTK0045 警告**；隔离输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-headerless-import-preservation-final-20261018\release`，已生成 `Lexio.exe`。
- 本轮是导入解析修复，没有改动 UI；实际 PickerHost 文件选择验收状态不变。测试使用隔离 SQLite，没有访问默认用户数据库、启动或关闭 Lexio 实例，也未升级项目依赖。
