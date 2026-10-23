# Lexio 技术架构

## 技术栈（2026-10 核实）
| 组件 | 选型 | 版本 | 依据 |
|---|---|---|---|
| 运行时 | .NET (LTS) | 10.0.402 SDK | 已安装，运行时 10.0.13；微软下载页于 2026-10-17 显示此为最新 SDK/Runtime |
| UI | WinUI 3 (Windows App SDK) | 1.8.250916003 | 微软当前稳定版为 2.5.1；1.8 的最后补丁为 1.8.260921001，维护期于 2026-09-24 结束。项目仍使用旧版本；最终 Release CLI 构建成功。迁移需在独立实验分支验证 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 | 微软官方维护 |
| 存储 | Microsoft.Data.Sqlite（SQLite + WAL + FTS5） | 10.0.13 | 项目固定依赖版本 |
| 记忆算法 | FSRS.Core（MIT，py-fsrs 的 C# 移植） | 1.0.7 | 21 参数 + decay=-w[20] + factor=0.9^(1/decay)-1 + 短时稳定度 w[17..19]，确认为 FSRS-6 语义；带完整单元测试；2026-02 仍更新 |
| 测试 | xUnit | 2.9.3 | — |

部署形态：**unpackaged**（WindowsPackageType=None + WindowsAppSDKSelfContained），无需 MSIX 签名即可本地运行；数据存于 `%LOCALAPPDATA%\Lexio`。程序集名 `Lexio`（**刻意不与命名空间 `Lexio.App` 同名**，规避 XAML 编译器装配体/命名空间歧义）。

## 命名空间规约（重要，防回归）
- App 工程统一前缀 **`Lexio`**（三个字符）：`Lexio.App`、`Lexio.App.Pages`、`Lexio.Services`、`Lexio.ViewModels`。
- 核心库前缀 **`Lexico.Core`**。**绝不混用 `Lexico.App.*` 拼写**——历史上正是这一拼写混用触发过整条 XAML 预编译链的连锁假错误。
- 教训（工具链行为）：①任何真实 C# 错误会让该文件声明的类型/命名空间在下游表现为「不存在」；②XAML 中指向不存在命名空间的 xmlns 或缺失的 StaticResource 会让 MarkupCompilePass2 **静默崩溃**（exit 1 无报错）。排查手段：掏空可疑文件二分。

## 解决方案结构
```
Lexio.sln
src/
  Lexico.Core/     # 纯 .NET 库（无 UI 依赖）：领域模型、调度、规划、SQLite 数据层、词典
  Lexico.App/      # WinUI 3 应用：页面、ViewModel、Composition 动画
tests/
  Lexico.Core.Tests/  # xUnit：算法、迁移、学习闭环、规划器、导入
tools/            # 词典离线构建管线（后续阶段）
docs/             # 规格、架构、数据库、算法、许可证
```

## 关键设计决策
1. **算法隔离**：业务只依赖 `ISrsEngine`；当前实现 `FsrsCoreEngine` 包装 FSRS.Core。未来 FSRS-7 先以影子模式（shadow）并行记录预测，评估优于现行后方切换主模型。
2. **UI 与数据解耦**：Core 不引用任何 Windows/App SDK 类型；所有 IO 异步；UI 线程零数据库操作。
3. **词典与学习数据分离**：`words/word_senses/decks/deck_words` 为词典区（可重建、可替换）；`cards/…/settings` 为学习区（用户资产，不可变日志）。备份=整个 SQLite 文件 + 版本号。
4. **可迁移性**：`schema_migrations` 版本表 + 顺序 SQL 迁移，幂等执行。
5. **避免过度工程**：无微服务、无 DI 容器（手工组合根）、无仓库抽象层叠。

## 分层
```
App (WinUI 3, MVVM, Composition 动画)
 └─ Core
     ├─ Study (StudyService, DailyPlanner, EffectiveTiming, ArticlePracticeService)
     ├─ Scheduling (ISrsEngine → FsrsCoreEngine)
     ├─ Dictionary (FTS 搜索, CSV 导入, 内置启动词库)
     └─ Data (Database, Migrations, Stores)
```

## 词汇与文章流程

单词回忆、跟写、看义拼写、听写和自测共用 `StudyService` 队列和 FSRS 排程。会话模式写入 `sessions.study_mode`，中断恢复时由数据库恢复相同模式和队列。收藏、错词和已掌握状态按 `cards.id` 保存；错误输入、Again 评分、复习日志和错词标记在同一数据库事务内完成。已掌握卡片从新词和到期候选中排除，但其 FSRS 状态和到期时间保持不变。

文章工作流独立使用 `ArticlePracticeService` 和 `articles`、`article_sentences`、`article_progress` 三张表。句序进度不加入单词计划或 `review_logs`；文章输入、跳过、前后切换、全文选择段落和完成操作先保存到本地 SQLite，完整 SQLite 快照备份因此同时保留词汇与文章数据。Articles 支持严格 UTF-8 纯文本和 TypeWords JSON 导入；JSON 按空行将中英文段落配对，拒绝数量不匹配的文件并跳过完全重复文章。练习页同时保留全文双语阅读区和当前句练习区，不连接在线翻译或远程音频服务。
