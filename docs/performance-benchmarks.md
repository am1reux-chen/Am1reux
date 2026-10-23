# Lexio 后端性能基准

## 重跑方法

基准使用仓库内现有 Lexico.Core 和 .NET 标准库，不引入新的 NuGet 包。默认创建临时数据库与 10,000 个词条，测量完成后删除临时目录。

在装有 .NET 10 SDK 的 PowerShell 中运行：

```powershell
dotnet run --project benchmarks/Lexico.Benchmarks/Lexico.Benchmarks.csproj -c Release
```

第一个参数可改变词条/卡片数量（100–50,000），第二个参数控制重复样本数（3–15）。基准会输出 JSON。FSRS、数据库读取、FTS 搜索和规划每个样本分别运行 10,000、100、30 和 1 次；每组先做最多 100 次预热，然后报告各样本“每次平均耗时”的中位数和 P95。连续学习指标则来自单场真实 `StudyService` 会话，P50/P95 基于该场每个答题调用各自的耗时。

每次运行都会从头导入固定生成的 `lexiobench000000` 等词条，之后将卡片置为到期复习状态并以重度强度连续完成全量复习。这样同时覆盖调度、Planner、SQLite 读取、FTS 搜索、ReviewLog 写入、会话队列移除和影子预测持久化。`managedHeapBytesAfterFullGc` 是全部测量结束后的托管堆值；`processPeakWorkingSetBytes` 是进程启动以来峰值工作集，含运行时和 SQLite，不是该功能单独分配量。

## 记录结果

运行日期：2026-10-17。配置：Intel(R) Core(TM) Ultra 9 275HX，24 个逻辑处理器，63.4 GiB RAM；Windows 10.0.26300 x64；.NET SDK 10.0.402，Runtime 10.0.13。数据量 10,000 个词条/卡片；7 个样本。

| 场景 | 每样本操作数 | 样本中位数 | 样本 P95 |
| --- | ---: | ---: | ---: |
| FSRS Schedule | 10,000 | 0.0004 ms/次 | 0.0010 ms/次 |
| SQLite 单词读取（含词义加载） | 100 | 0.0177 ms/次 | 0.0222 ms/次 |
| FTS5 前缀搜索 | 30 | 0.0596 ms/次 | 0.0828 ms/次 |
| Daily Planner（10,000 候选） | 1 | 2.4026 ms/次 | 7.7380 ms/次 |
| StudyService 连续答题（10,000 个 ReviewLog） | 10,000 个实际调用 | 0.1142 ms/题 | 0.2653 ms/题 |

- 10,000 词事务导入：1,632.02 ms。
- 连续学习会话：1,945.13 ms，提交 10,000 条复习记录。
- 全测量后完整 GC 的托管堆：4,188,104 bytes；进程峰值工作集：90,947,584 bytes（约 86.7 MiB）。

这些是单机 Release 基准结果，不是跨设备性能保证。它们不涵盖 WinUI 渲染、页面导航、真实键盘/动画输入、应用长时间常驻或最坏存储设备。FSRS 单次计算约 0.0004 ms；Planner 对 10,000 个候选的样本中位数约 2.40 ms；数据库异步 API 内部在线程池执行串行 SQLite 操作。当前没有 UI 帧时间基准，所以不能仅凭这里的数据宣称整体 UI 无卡顿。

## 版本核查

截至 2026-10-17，微软 [.NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)列出 .NET Runtime 10.0.13（2026-10-13）及 SDK 10.0.402。当前机器使用 SDK 10.0.402，基准输出 Runtime 10.0.13。

微软 [Windows App SDK 下载页](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)和[发布通道/生命周期页](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)列出的当前稳定版是 2.5.1，发布时间为 2026-09-16；1.8 的最新补丁是 1.8.260921001（2026-09-24），其维护期已结束。Lexio 当前依赖仍是 1.8.250916003。本阶段没有改依赖；需要在独立实验分支验证更新至受支持版本后再决定迁移。阶段一 XAML 编译器错误在最终完整 Release 构建没有复现。
