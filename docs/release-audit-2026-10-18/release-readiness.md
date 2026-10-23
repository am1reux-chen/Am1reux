# Lexio 发布准备审计

日期：2026-10-18  
审计范围：当前磁盘版本的 Release 构建、测试、发布产物检查、隔离数据运行和许可证资料。

## 结论

**暂不建议作为面向普通 Windows 用户的 Release Candidate。** 普通 Release 构建和自动化测试通过，隔离实例的 Insights 页面已实际打开并显示与测试记录一致的数据；但目前没有完整安装交付方案，应用依赖系统安装 .NET 10，且损坏数据库没有可见错误窗口。发布包中的许可证通知和词典数据来源也需要在发布前核实。

## Git 与工作区保护

- 分支：`master`；审计开始时 HEAD：`8b521a7` (`Learn: replace stale derived-property bindings with x:Bind page functions`)。
- 开始时工作区含有多项已有修改和未跟踪成果，涵盖前端、ViewModel、Core、测试及文档。本次未重置、清理或提交这些内容，也未改动应用源码。
- 构建与测试后复查，原有修改集合保持不变。本报告与 Insights 截图是本次新增的验收附件。

## 构建与测试

| 检查项 | 结果 | 证据 |
|---|---|---|
| Release 构建 | **通过** | `dotnet build Lexio.slnx -c Release --no-restore`，0 错误、32 条 MVVMTK0045 警告 |
| 完整测试 | **通过** | Core 84/84、App ViewModel 8/8，共 92/92；失败 0、跳过 0 |
| AOT/裁剪发布 | **未验证** | 本次检查的是普通 Release；项目没有配置 AOT 发布 |

32 条 MVVMTK0045 仍保留。它们不阻断本次普通 Release 构建，但当前 ViewModel 不能据此宣称通过 Native AOT 发布验证。

## 交付与依赖

- 当前项目设为 unpackaged（`WindowsPackageType=None`），并启用了 `WindowsAppSDKSelfContained`；未找到 MSIX、App Installer、安装器或正式发布配置。README 只描述开发运行方式。
- 临时发布目录含 329 个文件、约 137.3 MiB；包含 `Lexio.exe`、Lexico.Core、FSRS.Core、Microsoft.Data.Sqlite 和 SQLite 原生库 `e_sqlite3.dll`。
- 发布目录的 `Lexio.runtimeconfig.json` 仍引用 `Microsoft.NETCore.App` 10.0.0；没有随包的 `coreclr.dll` 或 `hostfxr.dll`。这表明 .NET 运行时仍是外部前置条件。`WindowsAppSDKSelfContained` 不等于 .NET 自包含部署；微软文档区分 Windows App SDK 与 .NET runtime 的发布方式：[Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)。
- 实际界面验证运行在当前机器的临时构建副本中，并重定向到隔离测试数据目录。由于没有可用的干净 Windows 虚拟机/沙盒，**未验证**一台未安装 .NET 10 的 Windows 电脑能否直接启动该发布目录，也未验证安装、升级或卸载体验。

## 实际运行与隔离数据流程

实际启动的隔离副本使用临时数据库，没有读写用户的 `%LOCALAPPDATA%\Lexio` 数据。

| 场景 | 结果 | 观察 |
|---|---|---|
| 空数据库首次启动 | **通过** | 应用显示 Today，并完成内置约 100 个词的初始化 |
| 导入 CSV | **通过** | 导入结果提示“新增 1，更新 0，跳过 0” |
| Library 搜索 | **通过** | 可搜索并找到刚导入的隔离测试词条 |
| Learn 显示答案、评分、下一题 | **通过** | 显示释义后评分，进度从 0/6 到 1/6，再到 2/6 |
| 退出后重启并继续 | **部分通过** | 通过结束隔离进程模拟意外退出，重启后恢复到 1/6；再次评分后进度到 2/6。正常菜单退出/Alt+F4 未验证 |
| 切换 Library 后返回 Learn | **通过** | 进度仍为 2/6，当前卡片状态未复位 |
| Insights 页面 | **通过** | 页面实际显示，无崩溃；当前隔离数据为复习中 0、学习中 2、待学新词 99，今日/累计复习 2，未来七天到期 0 次。与刚完成的两条学习记录相符 |

Insights 实际截图：[insights-isolated.jpg](./insights-isolated.jpg)。

## 数据保存、恢复与导出

- 正常用户数据路径为 `%LOCALAPPDATA%\Lexio\lexio.db`，自动备份为同目录的 `lexio.db.backup`，见 [AppServices.cs](../../src/Lexico.App/Services/AppServices.cs)。
- 应用启动时创建/更新一份备份，之后每 15 分钟覆盖同一个备份文件；没有版本化的多代备份。
- Core 对迁移、备份校验、从有效备份恢复、保留损坏主库以及无有效备份等情况有自动化测试；当前 Release 测试全部通过。迁移夹具覆盖旧 schema 示例 v2→v3、v5→v6。
- Core 提供恢复和 CSV 导出 API，但 App UI 没有手动恢复、导出或完整备份入口。当前 CSV 导出是复习/训练历史，不包含完整词库、设置和可直接恢复的数据库。
- 在隔离副本中用损坏主库且无有效备份启动时，Core 产生了可读的 `InvalidDataException`；实际应用进程没有显示主窗口或错误对话框。根因是 [App.logic.cs](../../src/Lexico.App/App.logic.cs) 在数据库初始化成功前才创建主窗口，异常处理只写调试日志并标记已处理。**这是发布阻断项。**

## 第三方数据与许可证

- [docs/05-licenses.md](../05-licenses.md) 将 V1 内置启动词库描述为项目自编内容；代码将 `starter-dictionary.json` 嵌入 Core。该来源声明没有独立的创作记录或逐词来源证据，本次无法确认其实际出处。
- 发布目录中未发现 `LICENSE`、`NOTICE` 或第三方声明文件，也没有应用内开源声明页面。文档计划使用的 Wiktionary 提取数据、WordNet 和 CMUdict 尚需在实际纳入前核对具体版本、文件来源和对应许可。
- 发布前核对可参考：[Wiktionary 版权说明](https://en.wiktionary.org/wiki/Wiktionary:Copyrights)、[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)、[Princeton WordNet 许可说明](https://wordnet.princeton.edu/license-and-commercial-use) 和 [CMUdict 官方许可证](https://github.com/cmusphinx/cmudict/blob/master/LICENSE)。文档写的是 WordNet 3.1；应将将要分发的具体数据版本与其适用的许可证文本对应确认。

## 发布检查清单

| 项目 | 状态 | 发布判断 |
|---|---|---|
| Release 构建和自动化测试 | **通过** | 0 构建错误；92/92 测试通过 |
| 当前机器上的隔离运行、Insights 真实渲染 | **通过** | 不等同于干净系统安装验收 |
| 安装器/安装前置条件/升级流程 | **未通过** | 尚无普通用户交付方案；须确定安装 .NET 10 前置条件或改为 .NET self-contained，并在干净系统验证 |
| 损坏数据库且无备份时的用户提示 | **未通过** | 需要启动错误对话框/恢复说明，不能仅记录 Debug 日志 |
| 迁移与数据库恢复算法 | **通过（自动化测试）** | 真实旧版本用户数据库升级未做端到端演练 |
| 手动备份恢复和完整数据导出 | **未通过（产品入口缺失）** | Core API 不等于用户可用的数据迁移功能 |
| 第三方许可证与数据来源 | **未通过发布核对** | 需确认内置数据来源并随包提供所需声明 |
| 浅色主题、极窄窗口、DPI 缩放、TTS | **未验证** | 需要在实际界面补测 |
| Learn 动画性能/连续答题长时间稳定性 | **未验证** | 本次只验证了短流程和页面切换 |
| 干净 Windows 安装、升级、卸载 | **未验证** | 当前环境没有干净 Windows 沙盒 |

## 进入 RC 前必须解决

1. **确定可交付形态**：提供安装器/应用包和清楚的运行时前置条件；若目标是免安装 .NET 的普通用户，生成 .NET self-contained 发布，并在干净 Windows 环境实际启动。
2. **修复启动失败反馈**：捕获数据库初始化和恢复失败，在创建窗口失败时显示可读的本地化错误界面，提示数据文件位置和下一步；保持原始主库不被覆盖。
3. **完成许可证清单**：确认随包数据来源和版本，添加第三方组件/数据的许可证与署名文件；不能只依赖开发文档中的计划说明。

手动导出/恢复入口建议列为面向用户公开发布前的必备数据可携带性工作。完成以上三项阻断，并补齐干净系统、浅色/DPI/窄窗、TTS 与动画性能验证后，再重新评估是否进入 RC。
