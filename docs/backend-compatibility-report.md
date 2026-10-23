# Lexio 后端阶段交付与 GLM 兼容性报告

日期：2026-10-17  
分支：`master`  
范围：Lexico.Core、Services、ViewModels、数据库、算法、测试和接口文档。页面视觉及动画文件保持 GLM 所有权。

## 前后端兼容结果

| 页面 | 当前接口 | 后端变更对绑定的影响 | GLM 必需改动 |
| --- | --- | --- | --- |
| Today | `TodayViewModel` 属性、`StartCommand(IAsyncRelayCommand<IntensityOption>)`、选项的 `Start` | 原有属性、命令和参数类型保留。中断会话启动时 `StudyService.StartAsync()` 返回已恢复的活动计划。 | 无。Today 现有的导航事件继续进入 Learn。 |
| Learn | `ResumeAsync()`、`GradeCommand(IAsyncRelayCommand<string>)`、`StudyItem` | 原流程保留；添加 `StudyAnswerContext` 的答题重载供将来传入提示标志。原两参数答题方法继续可用。 | 无。当前页面没有提示动作，默认按未使用提示记录。 |
| Library | `SearchAsync(string)`、`ImportCommand(IAsyncRelayCommand<string>)` | 搜索和文件路径接口不变。导入改为严格解析并事务提交，重复词计入跳过/更新，错误文件不产生半份导入。 | 无。现有 CSV/TXT 选择与状态绑定继续可用。 |
| Insights | 现有统计属性与 `LoadAsync()` | 没有改变绑定；底层统计仍从 ReviewLogs 读取。 | 无。 |

`ui-contract.md` 是属性、类型、命令与页面行为的详细契约。本阶段没有修改 `Pages/*.xaml`、`Pages/*.xaml.cs`、`App.xaml`、`MainWindow.xaml` 或 MainWindow 代码后置。

## 后端交付

- Daily Planner 保留 55% / 100% / 170% 三档，按真实有效耗时预算选择复习和新词，结合 FSRS 召回风险、词频、遗忘次数与耗时排序。未选中的卡片保留原 FSRS due 时间。
- FSRS.Core 1.0.7 保持不变；通过适配层修复经 py-fsrs 6.3.2 逐案对照发现的整数天和同日 Hard 差异。参考向量及限制见 `fsrs-conformance.md`。
- 新增独立 L2 Logistic Regression 个人召回校准器，按时间切分验证、记录 Log Loss / Brier / ECE，影子运行且不改变正式调度。低样本回退 FSRS 原概率。
- SQLite schema 只追加迁移至 v6。ReviewLogs 追加不可变触发器；会话队列和提前复习标记持久化并在启动恢复；答题、卡片状态、日志、预测和队列更新事务化。结束会话会等待当前答题完成。
- 增强 CSV/TXT 导入校验、重复处理、事务提交；增加校验式备份恢复和 UTF-8 复习历史 CSV 导出。旧 ReviewLogs 可保留和导出，旧模型特征会按原始日志重建保守值。
- 增加无额外 NuGet 依赖的可复现性能基准，10,000 词测试数据、机器与 .NET 版本、操作次数、耗时和内存均记录在 `performance-benchmarks.md`。
- 启动在后台校验主数据库，采用本地滚动备份（启动时及每 15 分钟）自动恢复损坏主库，并保留 `.corrupt-*` 数据库副本。
- 项目依赖和目标框架未升级。微软官方页面截至 2026-10-17 列出 .NET 10.0.13 / SDK 10.0.402、Windows App SDK 稳定版 2.5.1，以及 Windows App SDK 1.8 最后补丁 1.8.260921001；当前项目固定为较旧的 1.8.250916003。1.8 维护期已于 2026-09-24 结束，需要在独立实验分支验证升级至受支持版本。

## 验证结果

- 原始基线：29/29 Core 测试通过。
- 最终：Release Core 测试 **82/82 通过，0 失败，0 跳过**。
- 完整构建：`dotnet build Lexio.slnx -c Release --no-restore` **成功，0 错误**。输出 32 条 `MVVMTK0045` WinUI AOT 兼容性警告，涉及 `ObservableProperty` 字段；此最终构建没有再出现基线记录的 XAML 编译器退出码 1。
- 基准：Release 下 10,000 张卡连续完成 10,000 次 StudyService 答题，提交 10,000 条 ReviewLog，用时 1,945.13 ms。分项结果及单次运行限制见 `performance-benchmarks.md`。
- `git diff --check` 无空白错误；仅报告工作区 LF 文件在 Windows 下可能被 Git 转为 CRLF 的警告。

## 尚未解决

- 如果主数据库损坏且自动备份缺失或损坏，应用会拒绝启动并保留主库副本；当前没有备份恢复错误对话框。有效滚动备份可由启动流程自动验证和恢复。
- 当前没有 WinUI 自动化或 UI 帧时间基准；Core/Release 和完整编译通过不替代人工界面验收。
- 32 条 `MVVMTK0045` 警告需要由后续 AOT 目标决定是否将相关 ViewModel 字段迁移为 partial properties。为避免干扰并行 UI 工作，本阶段没有批量改动这些公共 ViewModel 属性实现。

## GLM 配合项

**当前没有必需的前端接口变更。** 可选后续工作包括：添加显式“继续上次学习”入口、在页面将来出现提示时传入 `StudyAnswerContext(HintUsed: true)`，以及设计备份/恢复/导出的用户入口。页面所有绑定契约和兼容说明见 `ui-contract.md`。
