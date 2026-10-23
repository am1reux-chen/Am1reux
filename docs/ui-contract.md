# Lexio 前后端 UI 契约

本文件记录页面 XAML、页面代码后置和 ViewModel 的当前绑定，用于保持 UI 与后端接口兼容。公开属性、命令名称、类型和命令参数应保持不变；确需扩展时采用向后兼容新增项，并在页面与 ViewModel 更新后同步修改本文。

## Today

页面 ViewModel：`TodayViewModel`。`TodayPage` 将 `Vm` 暴露给 `x:Bind`，并在服务初始化完成或导航进入时调用 `LoadAsync()`。

| 属性或命令 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `Greeting` | `string` | 顶部问候语，单向绑定。 |
| `DateText` | `string` | 本地日期显示，单向绑定。 |
| `DueText` | `string` | 到期复习、新词数量摘要，单向绑定。 |
| `SelectedStudyMode` | `StudyMode` | 会话练习方式；默认 `Recall`，Today 选择器双向绑定。 |
| `StudyModes` | `IReadOnlyList<StudyModeChoice>` | 回忆、跟写、看义拼写、听写和自测的选项。 |
| `CanSelectStudyMode` | `bool` | 仅计划加载或会话启动期间禁止更改方式。 |
| `Loading` | `bool` | 页面加载或启动会话时为真；控制进度环。 |
| `CanStart` | `bool` | 当前是否存在至少一档有卡片的计划且不在启动中；与选项卡片数共同决定 `StartCommand` 是否可执行。 |
| `NoTasks` | `bool` | 当前三档计划都没有卡片时显示 Today 空状态提示。 |
| `LoadError` | `string` | 今日计划或学习会话启动失败时显示的可读错误。 |
| `Options` | `ObservableCollection<IntensityOption>` | 三种强度的可观察集合。 |
| `StartCommand` | `IAsyncRelayCommand<IntensityOption>` | 页面 VM 的异步开始命令；每个选项的 `Start` 引用此命令。 |
| `RetryCommand` | `IAsyncRelayCommand` | 计划加载失败时重新加载；会话启动失败时重试上次所选强度。 |
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
| `TotalCards` | `int` | 当前强度计划中的卡片总数；为零时对应按钮不可执行。 |
| `TagText` | `string` | 负载比例文本。 |
| `Start` | `ICommand` | XAML `{Binding Start}` 执行命令；`CommandParameter` 是当前 `IntensityOption` 对象。 |

当前 `TodayPage.xaml` 的紧凑和宽屏 `Options` 模板都使用 `Title`、`TagText`、`Description`、`ReviewsText`、`NewText`、`MinutesText`、`Start`；按钮参数是选项对象。卡片总数为零的强度不可启动；三档都无任务时，页面说明可以添加新词或等待复习到期。强度倍率当前为轻量 55%、常规 100%、重度 170%。

## Learn

页面 ViewModel：`LearnViewModel`。页面导航进入时调用 `ResumeAsync()`，由页面代码后置处理卡片动画、失焦计时通知和等待步骤的定时 tick。

`AppServices` 的 `ReduceMotion`、`FourGradeMode`、`ShowIpa` 和 `AutoPronounce` 在主窗口创建前异步读取并缓存；页面属性访问只读取内存，完整数据恢复后重新加载偏好缓存。

`AppServices.DispatchAsync` 在当前线程已有 `DispatcherQueue` 访问权时直接运行工作；否则排入 UI 队列并等待完成。队列关闭而拒绝入队时丢弃已经过时的 UI 更新，不从后台线程直接操作绑定对象。

| 属性或命令 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `InSession` | `bool` | 当前是否在学习会话中。 |
| `Revealed` | `bool` | 是否已显示释义；控制释义与评分区域。 |
| `Done` | `bool` | 会话结束状态；显示总结。 |
| `Waiting` | `bool` | 等待短间隔学习卡到时；显示等待说明。 |
| `IsPaused` | `bool` | 会话显式暂停状态；暂停不结束会话、不清队列、不提交评分。 |
| `CompletedCount` | `int` | 当前会话已成功提交的评分次数；从 `StudyService` 恢复，包含同一张卡的有效重学评分。 |
| `RemainingCount` | `int` | 后端队列尚余项目数，包含当前显示卡和会话内重学步骤。 |
| `Lemma` | `string` | 当前单词。 |
| `Ipa` | `string` | 当前美式音标文本。 |
| `IpaUk` | `string` | 当前英式音标文本。 |
| `IpaDisplay` | `string` | 标注“美”和“英”的可用音标；仅提供一种时也显示该音标。 |
| `ShowIpa` | `bool` | 是否显示音标，来源为用户设置。 |
| `ProgressText` | `string` | 已答 / 计划数量。 |
| `ProgressFraction` | `double` | 0–1 进度值。 |
| `WaitText` | `string` | 等待下一短间隔步骤的说明。 |
| `SummaryText` | `string` | 完成总结主文本。 |
| `SummaryDetail` | `string` | 完成总结细节。 |
| `NewBadge` | `string` | 新卡标识文本。 |
| `Current` | `StudyItem?` | 当前后端学习项；供 VM 流程持有，当前 XAML 不直接绑定。 |
| `FourGradeMode` | `bool` | 控制四档评分或简化评分按钮组。 |
| `Mode` | `StudyMode` | 当前会话模式，恢复中断会话时从 `sessions.study_mode` 读取。 |
| `InputText` | `string` | 跟写、看义拼写和听写的输入文本；按首尾 trim 与大小写不敏感比较。 |
| `InputFeedback` | `string` | 当前输入结果或保存错误的提示。 |
| `MeaningPrompt` | `string` | 看义拼写模式中显示的释义。 |
| `IsFavorite` / `IsMastered` | `bool` | 当前 Learn 卡片的收藏与已掌握状态。 |
| `SubmitInputCommand` | `IAsyncRelayCommand` | 提交输入；错误答案以 Again 保存并同时标记 Wrong，正确答案显示现有评分按钮。 |
| `ContinueCommand` | `IAsyncRelayCommand` | 错误输入或自测结果保存后继续队列。 |
| `DictationReplayCommand` | `IAsyncRelayCommand` | 重播当前单词的本地 TTS；失败时保留卡片并显示重试提示。 |
| `KeyboardHint` | `string` | 按当前评分模式显示键盘提示；`FourGradeMode` 改变时发出属性变更通知。 |
| `Senses` | `ObservableCollection<SenseLine>` | 当前词义和例句列表。 |
| `DetailSections` | `ObservableCollection<WordDetailSection>` | TypeWords 导入的例句、短语、近义词、词源和相关词；仅在答案揭示后出现。 |
| `ShowIdle` | `bool` | 派生状态：`!InSession && !Done`。 |
| `ShowCard` | `bool` | 派生状态：`InSession && !Waiting && !Done`。 |
| `ShowQuestion` | `bool` | 派生状态：`ShowCard && !Revealed`。 |
| `ShowNewBadge` | `bool` | 派生状态：当前显示卡片且 `NewBadge` 非空。 |
| `ShowPaused` | `bool` | 派生状态：显式暂停且总结尚未显示；暂停界面应优先于 `ShowIdle` 呈现。 |
| `NotFourGradeMode` | `bool` | `!FourGradeMode`，显示简化评分组。 |
| `RevealCommand` | `IRelayCommand` | 显示当前释义；仅会话中、未揭示且未完成时可执行。 |
| `PauseCommand` | `IAsyncRelayCommand` | 等待正在保存的评分完成后暂停；保留队列、当前卡、揭示状态和已保存日志。 |
| `ResumeCommand` | `IAsyncRelayCommand` | 恢复暂停会话；复用原队列和当前卡，暂停期间不计入有效思考时间。卡片耗时、显式暂停和窗口失焦间隔均使用单调时钟，不受系统时间或时区调整影响。 |
| `GradeCommand` | `IAsyncRelayCommand<string>` | 异步提交评分；参数是数字字符串。四档模式使用 `"1"`–`"4"`，简化模式使用 `"1"` 与 `"3"`。 |
| `EndCommand` | `IAsyncRelayCommand` | 结束活动或暂停中的会话并呈现总结；不会重写或删除已有 ReviewLogs。 |
| `SpeakCommand` | `IAsyncRelayCommand` | 仅在当前有可见学习卡片时播放发音；暂停、等待、空闲和总结状态不可执行。如果多次请求的合成结果乱序完成，较早请求不会覆盖最新请求。 |
| `CardChanged` | `event Action?` | 当前卡片变化时发出，页面用它触发入场动画。 |
| `SessionFinished` | `event Action?` | 会话完成时发出。 |

集合成员类型：

- `SenseLine` 是 `record SenseLine(string Gloss, string? Example)`；`HasExample` 为 `bool`，用于决定是否显示例句。
- `WordDetailSection` 持有标题与 `WordDetailLine` 列表；每行显示英文内容和可选中文说明。空详情不显示空白分区。
- `StudyItem` 是 `Lexico.Core.Domain.StudyItem`，包含卡片 ID、词头、音标、词义、新卡/重学标记、稳定度和到期时间。
- `Grade` 数值映射必须保留：`1=Again`、`2=Hard`、`3=Good`、`4=Easy`。

主要 UI 状态由上述布尔属性组合决定：空闲、展示卡片、问题面、揭示释义、输入面、自测选项、等待短间隔、显式暂停和完成总结。不要把这些属性替换成字符串状态或修改评分参数类型。

Today 默认回忆流程不变；模式存到 session 并随中断恢复。SelfTest 在开始前检查所选计划中的释义和同词书干扰项。输入错误不会重复评分：一次提交会原子地记录 Again、Wrong 和队列变化；正确输入后由现有二档/四档评分控件提交 FSRS 评分。词汇输入框按 Enter 提交；文章多行句子输入使用 Ctrl+Enter 提交，普通 Enter 仍可换行。`E`、`R`、评分键和 `Space` 都以 Learn 根元素为作用域；输入框有焦点时暂停/发音/评分等可冲突的字母快捷键会禁用，`Esc` 仍可结束。

当 `Done` 变为真、总结面板完成布局后，页面代码将键盘焦点移至总结中的“返回 Today”按钮，保证按键结束会话后有明确的下一步焦点目标。真实 WinUI 焦点行为仍需桌面端键盘验收。

Learn 底部键盘提示绑定 `KeyboardHint`：简化评分显示 `1/3 评分`，四档评分显示 `1–4 评分`；切换 `FourGradeMode` 时同步更新。提示文本启用自动换行，避免窄窗口横向裁切，并由 `LearnLayoutTests.KeyboardHintWrapsInNarrowWindows` 锁定 XAML 契约。真实键盘操作和窄窗视觉效果仍需桌面端验收。

Learn 的键盘加速键都以 `LearnRoot` 为 `ScopeOwner`，避免快捷键在焦点位于 Learn 页面之外时触发。评分键还按模式设置 `IsEnabled`：简化模式只启用 1 和 3，四档模式启用 1–4。Windows App SDK 的加速键默认是全局作用域，且即使关联控件不可见也可能执行；因此两种布局共存时，隐藏评分按钮上的快捷键必须显式停用（参见 [KeyboardAccelerator](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.input.keyboardaccelerator?view=windows-app-sdk-1.8) 与 [ScopeOwner](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.input.keyboardaccelerator.scopeowner?view=windows-app-sdk-1.8)）。`LearnLayoutTests` 覆盖页面作用域与两种评分模式的按键启用关系；真实键盘操作仍需桌面端验收。

发音命令和 `E` 快捷键只在 `ShowCard` 为真时可用。空闲、等待短间隔、暂停和总结状态隐藏发音提示，也必须停用发音，避免暂停遮罩取得焦点后误触发被遮挡卡片的发音按钮。`ShowCard` 的属性变更会同步更新 `SpeakCommand` 的 `CanExecute`；实际语音设备播放仍需桌面端验收。

语音合成或播放失败不会中断学习命令；`Speech` 将完整异常写入 `AppServices.StartupLogPath`。诊断日志写入失败时也不会向页面抛出异常。

导航离开 Learn 本身不会暂停或结束会话。每次返回 Learn 都会创建新的 `LearnViewModel` 并调用既有的 `ResumeAsync()`；进度、当前队列项、已揭示状态和有效计时来自进程内共享的 `StudyService`。Learn 页暂停按钮和遮罩现已绑定 `PauseCommand`、`ResumeCommand`、`IsPaused` / `ShowPaused`，无需增加页面缓存。ViewModel 的统一命令状态通知会在 `InSession`、`IsPaused`、`Revealed` 和 `Done` 变化时触发 `RevealCommand`、`PauseCommand`、`ResumeCommand`、`GradeCommand`、`EndCommand` 的 `CanExecuteChanged`。`LearnPage.xaml.cs` 仍保留对暂停、继续和结束命令的通知兜底；该兜底与 ViewModel 通知重复，因此暂不删除 GLM 的前端逻辑。

派生状态绑定必须随依赖属性更新：`ShowCard` 在 `InSession`、`Waiting`、`Done` 变化时通知；`ShowQuestion` 还随 `Revealed` 变化；`ShowNewBadge` 还随 `NewBadge` 变化；`NotFourGradeMode` 随 `FourGradeMode` 变化。保持现有属性名与 `bool` 类型。

后端新增了 `StudyService.AnswerAsync(StudyItem, Grade, StudyAnswerContext)` 重载；原有 `AnswerAsync(StudyItem, Grade)` 保持兼容并将 `HintUsed` 设为 `false`。当前 Learn UI 没有实际提示动作，因此不会记录提示使用。若页面后续加入提示，LearnViewModel 可在调用新增重载时传入 `new StudyAnswerContext(HintUsed: true)`；这不要求修改现有绑定属性或命令参数类型。

启动时 App 在后台打开、迁移并验证数据库；发现损坏时尝试恢复本地滚动备份，之后 `AppServices` 调用 `StudyService.RecoverInterruptedSessionsAsync()` 并在完成内置词库检查后发出 `Ready`。恢复会保留已提交评分计数和队列，但进程内的揭示状态与计时不跨重启恢复；重启后的会话按中断恢复为未暂停状态。显式暂停只存在于当前进程，进程重启由现有中断恢复流程接管。原有 Today `StartCommand` 调用 `StudyService.StartAsync()` 会返回活动计划，Learn 页 `ResumeAsync()` 展示队列。因此前端不需要新增数据库字段或恢复流程；如果希望给用户展示“继续上次学习”的专门入口，可作为可选 UI 改动讨论。

数据库另提供 `BackupToAsync`、`RestoreFromAsync` 和 `ExportReviewHistoryCsvAsync` 后端 API；本阶段没有给页面增加备份/恢复/导出绑定。备份管理入口仍需产品/UI 层决定保存位置和用户操作方式。

## Library

页面 ViewModel：`LibraryViewModel`。页面代码后置以 150 ms 防抖调用 `SearchAsync(string)`；Windows App SDK 1.8 `FileOpenPicker(WindowId)` 接受 `.csv` / `.txt` / `.json` / `.zip`，将返回的 `PickFileResult.Path` 交给 `ImportCommand`。CSV/TXT 走现有导入，JSON 按 TypeWords 词典格式映射词头、音标、释义和丰富详情；ZIP 只读取推荐清单引用的一份词书 JSON，并使用清单中的名称创建词书；坏文件或歧义内容在写入前拒绝。ZIP 不解压到磁盘，最大 64 MB，词条 JSON 最大 16 MB。Library 内完整备份恢复使用同一窗口关联方式并接受 `.lexio-backup`、`.backup` 和 `.db`；启动恢复选择器也传入自身 `AppWindow.Id`。恢复确认只显示所选路径的文件名。CSV/TXT 与 TypeWords JSON 必须为严格 UTF-8；解码失败时提示用户另存为 UTF-8，并将底层解码异常写入诊断日志。

| 属性或命令 | 类型 | 用途 / 状态 |
| --- | --- | --- |
| `Query` | `string` | ViewModel 公开的查询文本属性；当前输入控件通过 `TextChanged` 读取文本、不绑定此属性，`SearchAsync` 也不写回它。 |
| `Searching` | `bool` | 搜索进行中；控制进度环。 |
| `NoResults` | `bool` | 搜索完成且结果为空时设为真；当前 XAML 不直接绑定此属性。 |
| `StatusText` | `string` | 导入进行中、成功或失败提示。 |
| `ResultCountText` | `string` | 搜索结果数量；当前查询失败时显示可重试提示。 |
| `Results` | `ObservableCollection<SearchResultItem>` | 搜索结果列表；每个结果同时带具体 `CardId`、`DeckId`、词书名和三种标记状态。包含 TypeWords 详情时可展开完整词条内容。 |
| `Decks` | `ObservableCollection<DeckInfo>` | 词书集合。 |
| `SelectedMarkFilter` | `LibraryMarkFilter` | 全部、收藏、错词或已掌握筛选；变更后保留当前查询与词书范围并刷新结果。 |
| `MarkFilters` | `IReadOnlyList<LibraryMarkFilterChoice>` | Library 标记筛选器选项。 |
| `ImportCommand` | `IAsyncRelayCommand<string>` | 导入命令；参数是所选文件的路径字符串。 |
| `SearchAsync(string query)` | `Task` | 页面防抖后调用，刷新搜索结果。 |
| `LoadDecksAsync()` | `Task` | 页面就绪或导航后载入词书。 |
| `SetMarkAsync(long, CardMark, bool)` | `Task` | 写入或清除指定卡片标记后刷新列表；同词跨词书不会共用卡片状态。 |

`StatusText` 显示在搜索框下方的 `SearchStatus` 区域，与结果计数和搜索进度相邻。导入、备份和恢复状态不放在右侧词书列表底部，窄窗口时也保留在搜索区附近。

Library 的确认和结果 `ContentDialog` 不得让异常逃出 `async void` 事件。提示框无法显示时记录诊断并将结果留在 `StatusText`；备份或恢复已经完成的情况下，不得改报为操作失败。

导入成功状态同时显示新增、更新和跳过的数量，重复导入已有词时不会出现“导入 0 个词”的误导提示；TypeWords ZIP 使用推荐词书名称，若清单声明数和 JSON 实际条目数不一致，会同时提示清单数与文件数。这些变化只调整 `StatusText` 内容，不改变绑定契约。

文件读取或数据库写入异常通过 `ImportResult.Failure` 交给 App 写入诊断日志；结果区只显示可操作的中文错误提示，不直接呈现本地路径或原始系统异常。CSV 内容无效时继续显示具体校验说明。

CSV/TXT 导入忽略字段全为空白的记录，因此文件开头的空行不会遮住表头或被误导入为单词；空白记录也不计入跳过数量。
带表头的文件仅从 `zh` / `释义` / `back` 列读取中文释义；如果表头没有这些列（例如 `word,ipaUs`），不会把音标或其他元数据误当作释义。无表头两列格式仍使用第二列作为释义；第二列为空时按未提供处理。

更新已存在的 lemma 时只覆盖导入记录提供的非空字段；缺失列不会清空现有音标、词频、CEFR 或词义字段，记录未涉及的其他词义也会保留。这样可以安全导入只有部分列的词表。

导入 lemma 至少需要一个 Unicode 字母，并且其余字符只允许字母、空格、连字符和撇号；只含标点的行计为跳过项，若文件没有有效单词则显示现有的“没有可导入的有效单词”错误。

Library 搜索结果先显示词头，再在词头下方显示标注“美”和“英”的可用音标；音标可以换行，不挤占词头的弹性列。词头最多换行两行并以省略号收尾。该约束由 `LibraryLayoutTests.SearchResultPronunciationWrapsBelowTheLemmaWithoutNarrowingItsColumn` 覆盖。

当前搜索失败时，ViewModel 清除过期结果、停止加载指示器，并在 `ResultCountText` 显示“搜索失败，请重试。”；页面同时将技术异常写入诊断日志。被后续查询替代的旧搜索不能覆盖当前状态。

搜索中的标点按 FTS5 `unicode61` 词元边界处理，不把标点两侧的字母拼成一个新词；例如 `can't` 查询不能误返回只有 `cant` 前缀的 `canto`。Core 搜索回归测试覆盖这一行为。

集合项：

- `SearchResultItem` 保留 `record SearchResultItem(WordEntry Word, string FirstLine)` 的位置构造兼容性，并新增 `CardId`、`DeckId`、`DeckName`、`PronunciationDisplay`、`IsFavorite`、`IsWrong`、`IsMastered` 属性。XAML 使用词头/英美音标/释义和卡片级标记开关；筛选空结果显示对应状态说明。
- `DeckInfo` 是 `Lexico.Core.Domain.DeckInfo`；XAML 使用 `Name`、`Description`、`WordCount`。
- `WordEntry` 是 `Lexico.Core.Domain.WordEntry`，包含 `Senses` 与可选 `Details`；`SearchResultItem.DetailSections` 与 Learn 共用相同的详情分区格式。

## Articles

主导航 `articles` 映射 `ArticlesPage`；文章练习页使用 `ArticleLearnPage`，导航参数为 `ArticlePracticeRequest(ArticleId, Mode)`。导入选择器接受 `.txt`、`.json` 与 `.zip`。纯文本按严格 UTF-8 解码，去掉 UTF-8 BOM 后将每个非空行作为一句；TypeWords JSON 按严格 UTF-8 解析，把 `text` 与 `textTranslate` 按空行切分并配对到段落，保留 `titleTranslate`，忽略远程音频地址。TypeWords ZIP 只读取推荐清单引用的一份文章 JSON，并用推荐清单中的文章书名称反馈结果；不解压文件，最大 64 MB、文章 JSON 最大 16 MB。中英文段落数不一致、无效 JSON、缺少正文、缺失 ZIP 文件或歧义清单时在数据库写入前拒绝；重复的标题与双语内容会跳过。

`ArticlesViewModel` 的 `Articles`、`StatusText`、`SaveArticleCommand`、`DeleteArticleCommand`、`ImportCommand`、`PracticeCommand` 管理文章列表与编辑器。`TitleTranslation` 和 `ArticleEditorSentence` 的 `Text`、`Translation` 分别双向绑定标题译文、英文句子和可选译文。修改文章句子时重置文章进度到第一句并保留之前的练习模式；只修改标题或标题译文时保留句子行和现有进度。TypeWords ZIP 使用推荐文章书名称；清单声明文章数与 ZIP JSON 实际条目数不一致时，在成功状态中显示差值，帮助发现内容包不完整。删除文章会级联删除句子和进度。

`ArticleLearnViewModel` 绑定 `TitleTranslation`、`ReaderLines`、`CurrentReaderLine`、`CurrentSentence`、`InputText`、`TranslationVisible`、`Mode`、`ProgressText`、`ErrorText`、`SubmitCommand`、`PreviousCommand`、`NextCommand`、`ReplayCommand`、`EndCommand` 和 `RestartCommand`。左侧完整文章列表保留英中内容并高亮当前段落；选择其他段落时先写入位置，再切换练习句。练习开始时恢复保存的句序和方式；答对或跳过、前后切换时都先保存位置再更新当前句。输入采用首尾 trim 与 `OrdinalIgnoreCase` 比较，保留句子原有标点和 Unicode 字符。进度写入失败时仍显示当前句并允许重试；听写 TTS 失败时仍可重播或跳过。完成状态记录位置为句子总数；完成后切换方式会保留完成状态并保存为重新开始时的方式。全文列表仍可选择段落续学，并提供重新开始。

`ArticleReaderLine` 保存段落序号、英文、可选中文；`CurrentReaderLine` 驱动左侧 `ListView` 的选中高亮与自动滚动。完成后没有当前选中段落，但全文仍可点击并继续学习。

文章练习使用 `ArticlePracticeService`，不调用单词 `StudyService`，不创建单词 review log。文章位置和模式存于 `article_progress`，由 SQLite 快照备份。

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

## 启动错误恢复窗口

数据库启动失败时，恢复、打开数据文件夹和复制恢复指引操作纵向排列，以便窄窗口下仍可访问；较长的恢复说明在窗口内容区域纵向滚动。

## 兼容约定

- 页面和 ViewModel 可在同一功能中协同修改；前后端绑定需保持本文接口兼容。
- 不改动上述绑定名、类型、可观察集合类型、命令参数类型或页面代码后置调用方法。需要新能力时新增成员，并保留现有入口。
- `GradeCommand` 的数值字符串参数、Today 选项的 `IntensityOption` 对象参数、Library 导入命令的路径字符串参数均属于现有接口契约。
- 当前实现中 `CanStart` 通过命令 CanExecute 起作用；状态属性使用 `bool`。前端可调整视觉呈现，但不要要求后端把状态类型改成控件专属的视觉类型。
