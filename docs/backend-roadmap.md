# Lexio 后端路线图与阶段基线

本文记录后端专项工作的执行顺序、当前代码基线和每阶段完成标准。第一至第七阶段已完成；项目依赖保持原样。

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

### 第二阶段：每日规划器实现与验证

- 三档负载保留 `0.55 / 1.0 / 1.7`，现在先换算为个人基准秒数再选卡。新词、学习步骤、到期复习、逾期复习和提前复习分别使用有效历史耗时；类别均值以 20 个先验样本渐进平滑，单卡成本再按 difficulty 和 lapses 调整。
- 规划输入读取 FSRS 原始召回概率、词频排名、难度、遗忘次数和到期时长，并按估计价值/秒排序。轻量只抢救到期卡；常规先完成到期队列，再从剩余容量中最多分配基准时间的 20% 给新词；重度先处理到期卡，再最多分配 35% 基准时间给提前复习和 35% 给新词。估计分钟数等于实际入选任务估时之和。
- 计划及实际学习队列共用同一组选卡结果；候选按卡片 ID 去重。数据库查询支持可选词库范围，筛选同时作用于到期卡、提前复习和新词。任何超预算卡均保留原 due 时间；不通过修改 FSRS 状态或 due 日期隐藏积压。
- `StudyService.StartAsync` 通过服务内并发门闩避免并发调用重复建立活动会话。学习步骤仍按现有 30 分钟会话窗口重入队；重度计划选中的提前复习通过会话队列标记即可立即呈现，恢复后仍保留此行为及卡片原 due 时间。
- `TodayViewModel` 仅改为使用规划器生成的预览，未改动 UI 契约中的公开属性、命令或类型。没有编辑 GLM 所有的页面 XAML、页面代码、App 视觉资源或 MainWindow 布局。
- 验证：新增/更新的规划器、计时分类、词库筛选及时区表示和 StudyService 测试通过；全套 Core 测试实跑为 **40/40 通过，0 失败，0 跳过**。完整 WinUI solution build 尚未重跑：阶段一观察到 XAML 编译器退出代码 1，且页面仍由 GLM 维护；Core 和 Core.Tests 均能编译。
- 进程重启恢复和重复提交保护仍在数据可靠性阶段覆盖；到期比较使用 UTC，持久化计划日期使用当前本地日期，且不修改历史 due 时间。

### 第三阶段：FSRS 正确性对照

- 固定依赖仍为 FSRS.Core 1.0.7；没有改项目依赖或构建配置。对照参考使用官方 py-fsrs 6.3.2，另外审阅了 FSRS-Sharp 的 64 场景/692 review golden 测试结构。
- 源码对比发现 `RetrievabilityCalculator` 使用了 `TotalDays`，而 py-fsrs 使用整数天；FSRS.Core 的同日稳定度倍率下限也漏掉 Hard。通过 `FsrsCoreEngine` 的计算器接口提供兼容适配，没有直接改写 FSRS 更新公式。
- `FsrsReferenceParityTests` 包含新卡四评分、同日 Hard/Again、短步进和重学、长间隔小数天、目标保留率 0.1/0.8/0.9/0.97/0.99、极端稳定度、最大间隔上限以及召回率对照。所有期望值由 py-fsrs 6.3.2 生成，使用 FSRS.Core 1.0.7 的原参数向量隔离公式差异。
- 验证：完整 Core 测试实跑为 **55/55 通过，0 失败，0 跳过**。保留参数向量与 py-fsrs 官方默认权重的差异；目前应用封装没有自定义权重入口，因此任意权重向量边界还没有通过应用公共接口测试。

### 第四阶段：个人记忆校准器

- 新增独立 L2 正则 Logistic Regression 校准器，输入为 FSRS 原始概率、相对有效思考时间、近 10 次正确率、历史遗忘次数、提示使用、复习间隔和学习状态。思考时间需 timing_valid 且处于 300–120000 ms；失焦和异常时长不会进入当前或历史时间特征。
- 按 `reviewed_at` 与 ReviewLog ID 稳定排序，以前 80% 训练、后 20% 验证。至少 200 条、且训练与验证切片各有不少于 10 个成功和失败样本才可拟合。拟合使用确定性批量梯度下降与 L2 正则，保留 Log Loss、Brier Score 和 10 桶 ECE。
- 候选模型仅在验证集 Log Loss 与 Brier Score 均严格改善，且 ECE 不恶化超过 0.01 时用于后续影子预测；单次概率变化限制为 ±0.15。当前永不把校准概率送入 FSRS 或正式调度。
- Schema v3 追加 `hint_used`、训练检查点、profile 接受状态、模型版本、原始概率与特征 JSON；旧 ReviewLog 与旧模型预测数据保留，并将旧预测的模型版本关联回原 profile。迁移创建原始 FSRS 回退 profile。
- 答题时先由现有 FSRS 正式调度器确定卡片和 due，再将 ReviewLog 与同一模型 profile 的影子预测/特征在一个事务保存。模型每次开始会话时最多检查一次训练；新增日志不足 50 条不会拟合。当前 Learn UI 没有提示功能，现有调用默认 `HintUsed=false`，并提供了兼容的 `StudyAnswerContext` 新重载供未来使用。
- 新增测试覆盖样本不足回退、时间顺序切分及无数据泄漏、时间异常/失焦过滤、Log Loss/Brier/ECE、提示和模型版本落库、50 条训练间隔、迁移保留旧数据、以及影子写入失败时 ReviewLog 与卡片更新整体回滚。
- 验证：完整 Core 测试实跑为 **63/63 通过，0 失败，0 跳过**。没有修改页面 XAML、页面代码后置、App 视觉资源、MainWindow 或 ViewModel 公共绑定。

### 第五阶段：数据可靠性

- Schema v4 为 ReviewLogs 加入会话外键和索引，并以 SQLite 触发器拒绝更新/删除；v5 保存活动会话原始队列、位置、计划卡量和预估分钟数；v6 记录提前复习标记，以便活动会话重启后仍可立即呈现提前卡。迁移只追加版本，升级测试确认旧日志、模型预测和 schema 一起保留。
- `StudyService` 在开始时恢复唯一未完成会话；答题时在单个 SQLite 事务内更新卡片、追加不可变 ReviewLog、保存影子预测并删除已答队列项，Again/短学习步骤以新队列项持久化。进程重启测试在写入 Again 后重新打开数据库并恢复队列，确认不会重复提交已答记录；另有测试覆盖提前复习的中断与恢复。Finish 与 Answer 使用固定门闩顺序串行化，确保会话汇总包含已提交答题计数。服务内队列项 ID 也阻止同一项重复评分。
- 启动顺序先恢复会话，再补充内置词库；恢复错误与词库种子错误分别记录，不会互相阻断。现有 Today 的开始命令调用 `StartAsync`，若恢复出的会话仍活动，该调用返回现有计划，Learn 原有 `ResumeAsync()` 可以消费当前卡片。因此没有新增必需 UI 绑定。
- CSV 导入采用严格 UTF-8，先完成解析和校验再以事务写入；不匹配引号等文件错误不会部分写库。无效 UTF-8 字节会提示用户另存为 UTF-8，并保留解码异常供诊断日志记录。词头重复按大小写不敏感合并，已有词更新、导入文件内重复跳过。
- 备份使用 SQLite Backup API 写临时文件后替换目标。应用启动时验证主库并在后台创建滚动备份，运行期间每 15 分钟刷新一次；主库损坏时先校验备份完整性、迁移、全部 14 张必需 Lexio 表和外键，再将备份原子替换主库。替换前会把损坏主库及 WAL/SHM 侧车保留为 `.corrupt-*`；无有效备份时拒绝启动并保留主库。历史导出为 UTF-8 CSV，并包含原始复习记录和关联影子预测/模型版本/特征。
- v3 之前的 ReviewLogs 没有个人模型预测。训练快照现在基于当时保存的 FSRS 可检索率、评分、间隔、状态和先前记录重建保守特征；相对思考时间设为中性值，因为旧数据无法准确计算该特征。没有 FSRS 可检索率的新卡首评不会当作对照样本。所有原始日志仍保留并导出。
- 手动恢复 API 与启动自动恢复都会拒绝损坏或结构不完整的备份；测试覆盖坏备份、主库损坏后的备份恢复、损坏主库保留和 v5 队列升级。SQLite WAL 用于正常异常退出恢复；会话状态从事务提交后的队列和日志恢复。
- 验证：本阶段结束时 Core 测试为 **73/73 通过，0 失败，0 跳过**；最终代码审查和自动恢复修正后扩充到 **82/82**（涵盖数据迁移/备份/恢复/导出、Unicode 重复导入、坏文件、历史重建及中断会话恢复）。

### 第六阶段：性能与版本验证

- 新增无 BenchmarkDotNet 或其他 NuGet 依赖的独立控制台基准 [`performance-benchmarks.md`](performance-benchmarks.md) 和 `benchmarks/Lexico.Benchmarks`。默认创建 10,000 词 SQLite/FTS5 数据集，测量 FSRS 计算、单词读取、FTS 前缀搜索、10,000 候选规划，以及一场 10,000 题 StudyService 答题会话；记录进程和托管堆内存。基准不加入现有 Solution，不更改依赖或应用构建配置。
- 实测环境为 Intel Core Ultra 9 275HX、24 个逻辑处理器、63.4 GiB RAM、Windows 10.0.26300、x64、.NET Runtime 10.0.13、SDK 10.0.402。最新 10,000 词结果：FSRS 中位 0.0004 ms/次；SQLite 单词读取 0.0177 ms；FTS5 前缀搜索 0.0596 ms；10,000 候选规划 2.4026 ms；连续答题 10,000 条用时 1,945.13 ms，单答题中位 0.1142 ms、P95 0.2653 ms；测量进程峰值工作集 90,947,584 bytes，完整 GC 后托管堆 4,188,104 bytes。样本方法和原始输出在性能文档中。
- **版本核查日期：2026-10-17。** [微软 .NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)显示最新运行时补丁为 10.0.13（2026-10-13）和最新 SDK 为 10.0.402；当前本机 SDK 正好为 10.0.402。微软 [Windows App SDK 下载页](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)及[发布通道页](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)显示当前稳定版为 2.5.1（2026-09-16），1.8 最新补丁为 1.8.260921001（2026-09-24），其维护期已结束。项目仍固定在 Windows App SDK 1.8.250916003；未升级，升级需要先在独立实验分支验证。阶段一 XAML 编译器退出码 1 在最终完整 Release 构建没有复现。
- 基准结果只代表上述本机 Debug/Release 条件下的测量（基准本身为 Release）；它没有测量 WinUI UI 响应帧、长时间真实用户操作或应用全局峰值内存，不能据此宣称端到端 UI 响应目标已通过。

### 第七阶段：GLM 接口兼容与集成验证

- 对照 `ui-contract.md` 检查 Today、Learn、Library、Insights 的公开绑定。没有更改现有 VM 属性名、命令名、类型、命令参数、事件或页面调用方法。`TodayViewModel` 仍通过原 `StartCommand` 启动/接回会话；Learn 继续使用现有 `ResumeAsync()`。
- 可选新增的后端接口是 `StudyAnswerContext` 的提示标志重载、数据库备份/恢复/复习历史导出方法；当前前端无需修改即可使用现有流程。将来要显示专门的“继续上次会话”按钮或接入提示、备份入口时，可选择绑定这些能力。
- 最终验证：完整 Core Release 测试 **82/82 通过**；`dotnet build Lexio.slnx -c Release --no-restore` **成功，0 错误**。WinUI 生成器报告 32 条 `MVVMTK0045` 可 AOT 兼容性警告，分布于现有 `ObservableProperty` 字段；XAML 编译器退出码 1 的基线失败在本次最终构建未复现。没有编辑页面 XAML、页面 `.xaml.cs`、App 视觉资源或 MainWindow 文件。

### 代码审查修正（最终复验）

- `FinishAsync` 等待当前答题完成事务和内存计数更新；确定性并发测试确认 Finish 的 ReviewLog 与汇总一致。
- 重度提前复习以 `session_queue.is_early_review`（迁移 v6）标记并在恢复时重建；`PeekNext` 可立即提供卡片，`NextDueUtc` 不会让 UI 等到原 due，原 FSRS due 保持不变。
- 恢复数据库现在要求完整的 14 张 Lexio 表及必需列、完整迁移版本和 FTS/ReviewLog 不可变触发器；缺失 schema 对象的 SQLite 文件会在替换当前数据前拒绝。
- Unicode 词头导入走与 `OrdinalIgnoreCase` 一致的查找，避免 SQLite ASCII-only `NOCASE` 产生重复词。Library 导入状态同时显示新增、更新和跳过数。
- 启动现在异步打开并验证数据库，使用 `%LocalAppData%/Lexio/lexio.db.backup` 自动备份；运行中每 15 分钟刷新。有效备份可修复损坏主库，损坏副本在替换后仍保留。
- 最终复验：Core Release **82/82**；完整 Release Solution build **0 错误、32 条既有 AOT 兼容性警告**；性能基准已按最新 Core 代码重跑。

### 第一阶段检查时的后端基线（第二、三阶段改动之前）

- `DailyPlanner` 已有轻量 `0.55`、常规 `1.0`、重度 `1.7` 三档因子；容量仍以复习等价数量计，不是基于时间预算。新词用固定的“2 个复习等价量”扣减容量。耗时估算只使用新词/其他卡两档的有效答题均值和固定回退值。
- 规划器将到期学习卡和到期复习卡合并为主要复习量；重度档可补充 4 天内的提前复习。复习按逾期时间先后取卡，尚未按 FSRS 遗忘风险、词频/重要性、历史困难度排序。
- 当复习超过预算时，规划器只选择预算内卡片，没有改写未选卡的 due 时间；这保留真实积压，但还没有完整的债务上限、延期公平性或跨日时区策略。
- `StudyService` 在开始会话时从当日计划建立队列，并去重卡片 ID；Again/短间隔步骤可在 30 分钟会话窗口中重新入队。当前没有显式活动会话恢复流程，也没有服务层阻止重复调用 `StartAsync` 的保护。
- FSRS 包装器通过 `ISrsEngine` 隔离第三方实现，初始版本字符串为 `fsrs-core/1.0.7`。正式创建时默认开启 fuzzing；预测和调度使用同一 Desired Retention。基线测试覆盖新卡 Again/Good、连续 Good、遗忘进入重学、预测衰减和关闭 fuzzing 后的确定性；当时尚无逐案例官方参考实现对照。
- `EffectiveTiming` 从卡片呈现开始计时，扣除失焦时间；有效时长限定在 300–120000 ms 且失焦少于原始时长的 60%。时钟基于 UTC 墙上时间，异常/失焦样本会标记为无效。
- SQLite 使用 WAL、`synchronous=NORMAL`、外键；迁移目前是版本 1 和 FTS5 版本 2。答题在一个事务里更新卡片并插入 ReviewLog。备份 API 使用 SQLite Backup API；尚未看到恢复、显式导出和损坏恢复流程。
- 数据库连接由一个 `SemaphoreSlim` 串行化，工作放在 `Task.Run` 上执行。当前仓库没有独立性能基准项目；不能据此声称达到任何耗时或内存目标。
- Schema 已含 `model_profiles` 与 `model_predictions` 表，但当前尚无个人召回校准器训练/影子预测实现。
- `LearnViewModel` 的若干计算属性依赖其他属性：`ShowQuestion` 依赖 `Revealed`，`ShowNewBadge` 依赖 `NewBadge`/卡片状态，`NotFourGradeMode` 依赖 `FourGradeMode`。源码只为部分派生属性显式转发 `PropertyChanged`；后续需验证并补齐通知，避免界面状态绑定停留在旧值。

## 后续阶段与完成标准

| 阶段 | 工作 | 主要验证 | 状态 |
| --- | --- | --- | --- |
| 2. 每日规划器 | 使用个人基准时间预算；按新词、到期/逾期/提前复习耗时估算；组合遗忘风险、重要性、困难历史与成本；限制负载并处理跨日、时区、中断、重复入队和同日复习；不改写 FSRS 到期时间来隐藏债务。 | 三档比例、时间预算、优先级、积压延期、极端负载与跨日边界单元测试；基线只由有效历史渐进校准。 | 完成（40/40 Core 测试通过；进程恢复转阶段 5） |
| 3. FSRS 正确性 | 保持 FSRS.Core 1.0.7 / FSRS-6，先研究当前实现、官方参考实现与 FSRS-Sharp 参考测试，再建立逐案例比较。 | 新卡初始化、Again/Hard/Good/Easy、同日学习步骤、首次失败/重学、长间隔、不同目标保持率、极端间隔与参数边界；公式变化必须由差异证据驱动。 | 完成（55/55 Core 测试通过；默认权重差异已记录） |
| 4. 个人记忆校准器 | 独立的正则化 Logistic Regression；先影子预测，低样本回退 FSRS；记录模型版本和可复现输入。 | 异常反应时间、提示/失焦处理、时间顺序切分、Log Loss、Brier Score、校准误差；只有持续优于 FSRS 基线后才评估参与排程。 | 完成（63/63 Core 测试通过；不影响正式排程） |
| 5. 数据可靠性 | 检查追加式迁移、ReviewLogs 历史保护、会话恢复、CSV/TXT 错误与重复项、备份/恢复、数据导出及异常退出恢复。 | 旧库升级和保留数据、重复导入、异常文件、恢复/损坏路径、中断会话、重复答题提交。 | 完成（82/82 Core 测试通过；无有效备份时无法恢复原始数据） |
| 6. 性能与版本 | 建立可重复的调度、数据库、搜索、规划、内存和连续学习基准；性能工作不得阻塞 UI。核实 .NET 与 Windows App SDK 官方当前稳定版本；任何升级先独立实验验证。 | 10,000 词数据集、重复样本、机器和运行时信息；性能结论只报告实测结果。版本核查引用官方发布信息。 | 完成（见 `performance-benchmarks.md`；未升级项目版本） |
| 7. 集成报告 | 与 GLM 核对 `ui-contract.md`，说明后端新增接口和需配合项；优先向后兼容。 | 完整 Core 测试、最终构建尝试；列出修改、测试结果、兼容性、未解决问题。 | 完成（82/82 Core 测试；完整 Release solution build 成功） |

## 执行约束

- 继续在现有项目上增量开发，不重建项目。
- 后端所有权范围为 `Lexico.Core`、`Services`、`ViewModels`、数据层、测试和文档。页面 XAML、页面 `.xaml.cs`、`App.xaml`、`MainWindow.xaml` 由 GLM Agent 维护。
- 项目 SDK、NuGet 包和构建配置未升级。本次仅为复现基线，在临时目录安装 README 指定的 .NET SDK 10.0.402；阶段一的 XAML 编译器错误在最终 Release 构建没有复现。
- 每阶段报告只写已观察或已运行的结果。测试数量统计不等于测试执行成功。
