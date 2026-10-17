# Lexio 后端路线图与阶段基线

本文记录后端专项工作的执行顺序、当前代码基线和每阶段完成标准。当前只完成第一阶段的静态基线与接口契约；未实现后续算法或数据层改动。

## 第一阶段：基线检查与 UI 契约

### 当前基线

- 分支：`master`。
- 第一次检查时工作区干净。之后 GLM Agent 在 `src/Lexico.App/App.xaml`、`src/Lexico.App/MainWindow.xaml`、`src/Lexico.App/MainWindow.xaml.cs` 和 `src/Lexico.App/Pages/` 下持续产生未提交 UI 改动；这些文件属于前端所有权，本阶段没有编辑它们。构建失败时页面正在变更。
- Solution 是 `Lexio.slnx`；仓库 README 的构建示例仍写作 `Lexio.sln`。
- 项目目标为 .NET 10；Core 使用 `FSRS.Core 1.0.7`、`Microsoft.Data.Sqlite 10.0.13`；App 使用 `Microsoft.WindowsAppSDK 1.8.250916003`。
- 测试项目有 29 个 `[Fact]` / `[Theory]` 声明，分布在 Planner、FSRS、StudyService、Migration、Dictionary 测试中。
- 初始环境的 `C:\Program Files\dotnet\dotnet.exe` 没有 SDK。为完成基线验证，已用微软官方安装脚本将 README 所列 .NET SDK `10.0.402` 并排安装到临时目录 `C:\Users\dlcmh\AppData\Local\Temp\lexio-dotnet-sdk-10.0.402`，没有修改项目配置或依赖。
- 使用该 SDK 执行 `dotnet build Lexio.slnx -c Release`：Core 与测试项目编译成功；WinUI App 的 XAML 编译阶段因 `XamlCompiler.exe` 返回代码 1 而失败（MSB3073）。编译器生成的 `output.json` 未记录具体文件/行错误，根因待查。构建时前端文件有未提交的进行中修改，因此需与 GLM 协调，在其页面修改稳定后重跑完整构建；目前不能判定失败由哪项 UI 修改造成。
- 执行 `dotnet test tests/Lexico.Core.Tests/Lexico.Core.Tests.csproj -c Release --no-build`：通过 29，失败 0，跳过 0，总计 29。此为实际运行结果。
- 页面绑定与命令契约见 [`ui-contract.md`](ui-contract.md)。本阶段没有改动 ViewModel 的公开属性、命令名或绑定类型。

### 当前后端实现观察

- `DailyPlanner` 已有轻量 `0.55`、常规 `1.0`、重度 `1.7` 三档因子；容量仍以复习等价数量计，不是基于时间预算。新词用固定的“2 个复习等价量”扣减容量。耗时估算只使用新词/其他卡两档的有效答题均值和固定回退值。
- 规划器将到期学习卡和到期复习卡合并为主要复习量；重度档可补充 4 天内的提前复习。复习按逾期时间先后取卡，尚未按 FSRS 遗忘风险、词频/重要性、历史困难度排序。
- 当复习超过预算时，规划器只选择预算内卡片，没有改写未选卡的 due 时间；这保留真实积压，但还没有完整的债务上限、延期公平性或跨日时区策略。
- `StudyService` 在开始会话时从当日计划建立队列，并去重卡片 ID；Again/短间隔步骤可在 30 分钟会话窗口中重新入队。当前没有显式活动会话恢复流程，也没有服务层阻止重复调用 `StartAsync` 的保护。
- FSRS 包装器通过 `ISrsEngine` 隔离第三方实现，当前版本字符串为 `fsrs-core/1.0.7`。正式创建时默认开启 fuzzing；预测和调度使用同一 Desired Retention。现有 FSRS 测试覆盖新卡 Again/Good、连续 Good、遗忘进入重学、预测衰减和关闭 fuzzing 后的确定性；尚无逐案例官方参考实现对照。
- `EffectiveTiming` 从卡片呈现开始计时，扣除失焦时间；有效时长限定在 300–120000 ms 且失焦少于原始时长的 60%。时钟基于 UTC 墙上时间，异常/失焦样本会标记为无效。
- SQLite 使用 WAL、`synchronous=NORMAL`、外键；迁移目前是版本 1 和 FTS5 版本 2。答题在一个事务里更新卡片并插入 ReviewLog。备份 API 使用 SQLite Backup API；尚未看到恢复、显式导出和损坏恢复流程。
- 数据库连接由一个 `SemaphoreSlim` 串行化，工作放在 `Task.Run` 上执行。当前仓库没有独立性能基准项目；不能据此声称达到任何耗时或内存目标。
- Schema 已含 `model_profiles` 与 `model_predictions` 表，但当前尚无个人召回校准器训练/影子预测实现。
- `LearnViewModel` 的若干计算属性依赖其他属性：`ShowQuestion` 依赖 `Revealed`，`ShowNewBadge` 依赖 `NewBadge`/卡片状态，`NotFourGradeMode` 依赖 `FourGradeMode`。源码只为部分派生属性显式转发 `PropertyChanged`；后续需验证并补齐通知，避免界面状态绑定停留在旧值。

## 后续阶段与完成标准

| 阶段 | 工作 | 主要验证 | 状态 |
| --- | --- | --- | --- |
| 2. 每日规划器 | 使用个人基准时间预算；按新词、到期/逾期/提前复习耗时估算；组合遗忘风险、重要性、困难历史与成本；限制负载并处理跨日、时区、中断、重复入队和同日复习；不改写 FSRS 到期时间来隐藏债务。 | 三档比例、时间预算、优先级、积压延期、极端负载与跨日边界单元测试；基线只由有效历史渐进校准。 | 未开始 |
| 3. FSRS 正确性 | 保持 FSRS.Core 1.0.7 / FSRS-6，先研究当前实现、官方参考实现与 FSRS-Sharp 参考测试，再建立逐案例比较。 | 新卡初始化、Again/Hard/Good/Easy、同日学习步骤、首次失败/重学、长间隔、不同目标保持率、极端间隔与参数边界；公式变化必须由差异证据驱动。 | 未开始 |
| 4. 个人记忆校准器 | 独立的正则化 Logistic Regression；先影子预测，低样本回退 FSRS；记录模型版本和可复现输入。 | 异常反应时间、提示/失焦处理、时间顺序切分、Log Loss、Brier Score、校准误差；只有持续优于 FSRS 基线后才评估参与排程。 | 未开始 |
| 5. 数据可靠性 | 检查追加式迁移、ReviewLogs 历史保护、会话恢复、CSV/TXT 错误与重复项、备份/恢复、数据导出及异常退出恢复。 | 旧库升级和保留数据、重复导入、异常文件、恢复/损坏路径、中断会话、重复答题提交。 | 未开始 |
| 6. 性能与版本 | 建立可重复的调度、数据库、搜索、规划、内存和连续学习基准；性能工作不得阻塞 UI。核实 .NET 与 Windows App SDK 官方当前稳定版本；任何升级先独立实验验证。 | 发布配置实测、可复现数据集和机器信息；性能结论只报告实测结果。版本核查引用官方发布信息。 | 未开始 |
| 7. 集成报告 | 与 GLM 核对 `ui-contract.md`，说明后端新增接口和需配合项；优先向后兼容。 | 构建与完整测试套件通过；列出修改、测试结果、兼容性、未解决问题。 | 未开始 |

## 执行约束

- 继续在现有项目上增量开发，不重建项目。
- 后端所有权范围为 `Lexico.Core`、`Services`、`ViewModels`、数据层、测试和文档。页面 XAML、页面 `.xaml.cs`、`App.xaml`、`MainWindow.xaml` 由 GLM Agent 维护。
- 不为消除构建错误而升级项目 SDK、NuGet 包或构建配置。本次仅为复现基线，在临时目录安装 README 指定的 .NET SDK 10.0.402；完整构建失败后仍需先查明 XAML 编译器错误。
- 每阶段报告只写已观察或已运行的结果。测试数量统计不等于测试执行成功。
