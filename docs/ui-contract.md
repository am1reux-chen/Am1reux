# Lexio 前后端 UI 契约

本文件根据当前页面 XAML、页面代码后置和 ViewModel 记录，用于 GLM 前端与后端并行开发时保持绑定兼容。它是当前绑定的只读清单，不要求改动页面。公开属性、命令名称、类型和命令参数应保持不变；确需扩展时采用向后兼容新增项。

> GLM Agent 正在修改 `App.xaml`、`MainWindow` 和多个 `Pages/` 文件。以下契约按本次最后检查时磁盘上的 XAML 记录；这些 UI 文件仍由 GLM Agent 所有，改动结束后应再核对一次。

## Today

页面 ViewModel：`TodayViewModel`。`TodayPage` 将 `Vm` 暴露给 `x:Bind`，并在服务初始化完成或导航进入时调用 `LoadAsync()`。

| 属性或命令 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `Greeting` | `string` | 顶部问候语，单向绑定。 |
| `DateText` | `string` | 本地日期显示，单向绑定。 |
| `DueText` | `string` | 到期复习、新词数量摘要，单向绑定。 |
| `Loading` | `bool` | 页面加载或启动会话时为真；控制进度环。 |
| `CanStart` | `bool` | 当前是否允许启动；`StartCommand` 的 CanExecute 谓词读取此状态。 |
| `Options` | `ObservableCollection<IntensityOption>` | 三种强度的可观察集合。 |
| `StartCommand` | `IAsyncRelayCommand<IntensityOption>` | 页面 VM 的异步开始命令；每个选项的 `Start` 引用此命令。 |
| `SessionStarted` | `event Action?` | 会话启动成功后通知页面导航至 Learn。 |

选项类型 `IntensityOption`：

| 成员 | 类型 | 当前用途 |
| --- | --- | --- |
| `Intensity` | `Intensity` | `Light`、`Normal` 或 `Heavy`；开始时传入 StudyService。 |
| `Title` | `string` | 当前 XAML 显示标题。 |
| `Description` | `string` | 强度说明，供页面呈现。 |
| `ReviewsText` | `string` | 计划复习数量显示文本。 |
| `NewText` | `string` | 计划新词数量显示文本。 |
| `MinutesText` | `string` | 预计时长显示文本。 |
| `TagText` | `string` | 负载比例文本。 |
| `Start` | `ICommand` | XAML `{Binding Start}` 执行命令；`CommandParameter` 是当前 `IntensityOption` 对象。 |

当前 `TodayPage.xaml` 的紧凑和宽屏 `Options` 模板都使用 `Title`、`TagText`、`Description`、`ReviewsText`、`NewText`、`MinutesText`、`Start`；按钮参数是选项对象。强度倍率当前为轻量 55%、常规 100%、重度 170%。

## Learn

页面 ViewModel：`LearnViewModel`。页面导航进入时调用 `ResumeAsync()`，由页面代码后置处理卡片动画、失焦计时通知和等待步骤的定时 tick。

| 属性或命令 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `InSession` | `bool` | 当前是否在学习会话中。 |
| `Revealed` | `bool` | 是否已显示释义；控制释义与评分区域。 |
| `Done` | `bool` | 会话结束状态；显示总结。 |
| `Waiting` | `bool` | 等待短间隔学习卡到时；显示等待说明。 |
| `Lemma` | `string` | 当前单词。 |
| `Ipa` | `string` | 当前美式音标文本。 |
| `ShowIpa` | `bool` | 是否显示音标，来源为用户设置。 |
| `ProgressText` | `string` | 已答 / 计划数量。 |
| `ProgressFraction` | `double` | 0–1 进度值。 |
| `WaitText` | `string` | 等待下一短间隔步骤的说明。 |
| `SummaryText` | `string` | 完成总结主文本。 |
| `SummaryDetail` | `string` | 完成总结细节。 |
| `NewBadge` | `string` | 新卡标识文本。 |
| `Current` | `StudyItem?` | 当前后端学习项；供 VM 流程持有，当前 XAML 不直接绑定。 |
| `FourGradeMode` | `bool` | 控制四档评分或简化评分按钮组。 |
| `Senses` | `ObservableCollection<SenseLine>` | 当前词义和例句列表。 |
| `ShowIdle` | `bool` | 派生状态：`!InSession && !Done`。 |
| `ShowCard` | `bool` | 派生状态：`InSession && !Waiting && !Done`。 |
| `ShowQuestion` | `bool` | 派生状态：`ShowCard && !Revealed`。 |
| `ShowNewBadge` | `bool` | 派生状态：当前显示卡片且 `NewBadge` 非空。 |
| `NotFourGradeMode` | `bool` | `!FourGradeMode`，显示简化评分组。 |
| `RevealCommand` | `IRelayCommand` | 显示当前释义；仅会话中、未揭示且未完成时可执行。 |
| `GradeCommand` | `IAsyncRelayCommand<string>` | 异步提交评分；参数是数字字符串。四档模式使用 `"1"`–`"4"`，简化模式使用 `"1"` 与 `"3"`。 |
| `EndCommand` | `IAsyncRelayCommand` | 结束当前会话并呈现总结。 |
| `SpeakCommand` | `IAsyncRelayCommand` | 播放当前词发音。 |
| `CardChanged` | `event Action?` | 当前卡片变化时发出，页面用它触发入场动画。 |
| `SessionFinished` | `event Action?` | 会话完成时发出。 |

集合成员类型：

- `SenseLine` 是 `record SenseLine(string Gloss, string? Example)`；`HasExample` 为 `bool`，用于决定是否显示例句。
- `StudyItem` 是 `Lexico.Core.Domain.StudyItem`，包含卡片 ID、词头、音标、词义、新卡/重学标记、稳定度和到期时间。
- `Grade` 数值映射必须保留：`1=Again`、`2=Hard`、`3=Good`、`4=Easy`。

主要 UI 状态由上述布尔属性组合决定：空闲、展示卡片、问题面、揭示释义、等待短间隔和完成总结。不要把这些属性替换成字符串状态或修改评分参数类型。

绑定通知注意项：源码显式为 `ShowIdle`、`ShowCard` 转发了部分依赖属性变化；`ShowQuestion`、`ShowNewBadge`、`NotFourGradeMode` 的依赖项变化目前没有对应的转发钩子。后端后续需验证这些绑定在运行时能及时刷新，并保持当前属性名和 `bool` 类型。

## Library

页面 ViewModel：`LibraryViewModel`。页面代码后置以 150 ms 防抖调用 `SearchAsync(string)`；文件选择器接受 `.csv` / `.txt`，将文件路径交给 `ImportCommand`。

| 属性或命令 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `Query` | `string` | ViewModel 公开的查询文本属性；当前输入控件通过 `TextChanged` 读取文本、不绑定此属性，`SearchAsync` 也不写回它。 |
| `Searching` | `bool` | 搜索进行中；控制进度环。 |
| `NoResults` | `bool` | 搜索完成且结果为空时设为真；当前 XAML 不直接绑定此属性。 |
| `StatusText` | `string` | 导入进行中、成功或失败提示。 |
| `ResultCountText` | `string` | 结果数量文本。 |
| `Results` | `ObservableCollection<SearchResultItem>` | 搜索结果列表。 |
| `Decks` | `ObservableCollection<DeckInfo>` | 词书集合。 |
| `ImportCommand` | `IAsyncRelayCommand<string>` | 导入命令；参数是所选文件的路径字符串。 |
| `SearchAsync(string query)` | `Task` | 页面防抖后调用，刷新搜索结果。 |
| `LoadDecksAsync()` | `Task` | 页面就绪或导航后载入词书。 |

集合项：

- `SearchResultItem` 是 `record SearchResultItem(WordEntry Word, string FirstLine)`。XAML 使用 `Word.Lemma`、`Word.IpaUs`、`FirstLine`；点击结果通过页面事件播放 `Word.Lemma`。
- `DeckInfo` 是 `Lexico.Core.Domain.DeckInfo`；XAML 使用 `Name`、`Description`、`WordCount`。
- `WordEntry` 是 `Lexico.Core.Domain.WordEntry`，`Senses` 为 `IReadOnlyList<WordSense>`。

## Insights

页面 ViewModel：`InsightsViewModel`。页面就绪或导航进入时调用 `LoadAsync()`；当前没有用户命令。

| 属性 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `NewCount` | `int` | 新词卡总量。 |
| `LearningCount` | `int` | 学习中卡片总量。 |
| `ReviewCount` | `int` | 复习卡片总量。 |
| `ReviewsToday` | `int` | 今日复习次数。 |
| `RetentionText` | `string` | 30 天记住率显示；无数据时为 `—`。 |
| `TotalReviewsText` | `string` | 累计复习数显示。 |
| `UpcomingText` | `string` | 未来 7 天到期总量说明。 |
| `Loading` | `bool` | Insights 数据加载状态；控制进度环。 |
| `Bars` | `ObservableCollection<BarItem>` | 未来七天的柱状图数据。 |
| `LoadAsync()` | `Task` | 异步刷新统计和未来负载。 |

`BarItem` 是 `record BarItem(string Label, int Count, double Height)`；XAML 绑定 `Label`、`Count`、`Height`。

## 兼容约定

- 页面文件由 GLM Agent 维护；后端修改只触及 Core、Services、ViewModels、数据库、测试和文档。
- 不改动上述绑定名、类型、可观察集合类型、命令参数类型或页面代码后置调用方法。需要新能力时新增成员，并保留现有入口。
- `GradeCommand` 的数值字符串参数、Today 选项的 `IntensityOption` 对象参数、Library 导入命令的路径字符串参数均属于现有接口契约。
- 当前实现中 `CanStart` 通过命令 CanExecute 起作用；状态属性使用 `bool`。前端可调整视觉呈现，但不要要求后端把状态类型改成控件专属的视觉类型。
