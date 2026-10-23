# Lexio 完整备份与恢复人工验收清单

日期：2026-10-18
状态：部分完成。A2（CSV 导入并搜索）已在 2026-10-18 的隔离 Release 实例中通过 Computer Use 实际操作和只读数据库核验；完整备份的保存选择器与生成文件也已在另一独立隔离 Release 实例中通过真实 UI 验证。本清单原 `data` 目录仍未用于这些操作；步骤 3 学习基线、步骤 5 后续导入、恢复与重启、取消和无效备份流程仍待验收。

## 隔离测试包

测试素材目录：

`C:\Users\dlcmh\AppData\Local\Temp\Lexio-Backup-UI-Manual-20261018-4931ca38510948399c4b6b95151e5822`

- 启动器：`launch-isolated.cmd`
- 隔离数据目录：`data`（此前已初始化，包含 lexio.db、自动备份和 SQLite WAL/SHM 文件；本次 UI 复验使用另一个全新 `LEXIO_DATA_DIR`，此目录未被本次导入修改）
- 首次导入：`fixtures\before-backup.csv`（2 个单词）
- 备份后导入：`fixtures\after-backup.csv`（1 个不同单词）
- 无效备份：`fixtures\invalid-backup.lexio-backup`（故意写入普通文本）
- 备份保存目录：`backups`
- 截图和记录：`evidence`
- 启动器显式设置 `LEXIO_DATA_DIR` 到上述隔离目录，不使用 `%LOCALAPPDATA%\Lexio`。
- 启动器使用源码对应的自包含发布目录：`C:\Am1reux-dc\artifacts\lexio-release-candidate\publish-recheck-20261018\Lexio.exe`。

这台机器已有多个 Lexio 进程。本轮没有关闭、复用或更改任何其他现存实例；为避免误连用户进程，使用源码 Release 可执行文件启动了独立进程，并只在该进程环境中设置 `LEXIO_DATA_DIR` 为此测试包的 `data`。首次 Today 页面显示内置 Starter 词书 100 词，随后进入 Library。启动后隔离目录出现 `lexio.db`、`lexio.db.backup`、`lexio.db-wal`、`lexio.db-shm`。未访问默认用户数据目录。

### 本轮实际进度（2026-10-18 后续）

- 已验证隔离源码 Release 实例启动并显示 Today、Library 页面；PID 34948，映像路径为 `C:\Am1reux-dc\src\Lexico.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Lexio.exe`。此进程仅针对本清单的隔离 `data` 目录。
- 已点击“导入 CSV / TXT”，真实打开 Windows“打开”文件选择器；尚未选择文件或修改词库。当前 UI 可见，但 `sky.list_windows()` 未返回 PickerHost。对文件名栏的点击被路由层拒绝为目标点属于 PickerHost 而非 Lexio；激活 Lexio、刷新并按运行时指导重试一次后仍被拒绝，故自动化停止，没有绕过 PickerHost。
- 隔离数据目录目前已由首次启动初始化，不能再称为空目录。尚未完成 `before-backup.csv` 导入、学习、备份、恢复、取消或无效文件流程。用户后来回复已选择文件，但对话框状态无法再通过自动化观察；若人工继续验收，请确认已通过当前隔离实例打开测试包 `fixtures\before-backup.csv`，不要选择其他文件。
- 用户真实数据和旧的 Lexio 进程均未触碰；恢复按钮尚未执行，隔离数据库尚未被覆盖。

步骤 A1 实际结果：**已完成（隔离启动）**。确认 Today/Library 页面，隔离目录创建数据库和自动备份；数据库内容未做直接查询或 integrity_check。

步骤 A2 实际结果：**未完成**。用户回复已选择 `fixtures\before-backup.csv` 后，隔离数据库只读检查仍未发现 `serendipity` 或 `ephemeral`，也没有导入成功提示或 Library 词书变化证据；不能记录为已导入。

### 用户手动选择后的只读核验（2026-10-18）

- 用户回复“已选”后，以 SQLite 只读连接检查隔离测试数据库，`PRAGMA integrity_check` 返回 `ok`；词书仍只有内置 Starter 来源，两个测试词均不存在。
- 因此数据库未显示 CSV 导入效果。Computer Use 随后仍报告 Escape 停止信号，无法检查文件对话框是否关闭或页面提示。没有执行备份、恢复或其他数据库写入。
- 继续验收前需确认文件确实在当前隔离 Lexio 实例的“打开”对话框中打开并出现“导入完成”提示；之后以搜索结果和数据库只读核对为证据。

## 预期 UI 行为（依据当前代码）

- Library 的“完整备份”通过 Windows 保存文件选择器创建 `.lexio-backup` 文件；成功提示应显示完整路径，并说明包括词库、自定义词书、学习进度、复习历史和设置。
- “恢复备份”先打开文件选择器，再展示确认对话框；确认恢复前生成可保留的恢复前安全快照。
- 成功提示显示快照路径以及自动恢复备份是否更新。
- 恢复时会暂停当前活动学习会话；成功后，运行中的会话、复习引擎设置和 Library 列表会从恢复后的数据重新加载。若所选备份包含未完成会话，应恢复该备份保存的队列；若不包含，则不应继续显示恢复前的旧卡片。
- 点击确认后会显示恢复进度遮罩，并暂时禁用主导航和页面输入；恢复成功或安全回滚完成后解除锁定。若数据库回滚或恢复后运行时重载不安全，窗口保持锁定并要求重启。
- 无效备份或恢复异常应显示“备份无法恢复”或“恢复未完成”提示，并说明现有数据/快照保留状态；技术细节只写入诊断日志。
- 如果数据库已恢复但运行时状态无法重载，或恢复前快照回滚失败，Lexio 应显示安全快照位置并锁定主界面的导航和操作，要求重启后继续使用。
- 取消文件选择或确认对话框应不改变数据库。
- 当前 Settings 导航被隐藏（MainWindow 的 IsSettingsVisible 为 False），现有 UI 没有可操作的设置页面。设置恢复目前可由 Core 测试验证，但不能声称已通过 UI 修改/比对设置。

## 人工执行步骤

在每一步的“实际结果”栏记录通过、失败或未执行，并在 evidence 目录保存相关截图。不要选择任何用户真实数据目录或备份。

### A. 初始化隔离数据与建立快照

1. 双击测试包中的 `launch-isolated.cmd`。
   - 预期：打开 Lexio；应用在测试包的 `data` 中创建 `lexio.db` 和自动备份文件。
   - 验证：记录窗口标题和数据库路径；不得出现默认用户数据路径。
   - 实际结果：________

2. 进入 Library，使用“导入 CSV / TXT”选择 `fixtures\before-backup.csv`。
   - 预期状态：`导入完成：「before-backup」新增 2，更新 0，跳过 0`；词书列表出现该词书。
   - 在搜索框搜索 `serendipity`，预期出现 1 条匹配结果。
   - 实际结果（本清单的 `data` 目录）：未执行。另一个全新隔离 Release 实例的等价 UI 流程已通过，见文末“CSV 导入与搜索端到端复验”。

3. 进入 Today，开始学习并完成至少 1 张新卡；在 Learn 显示答案并提交一个评分，然后明确结束会话并返回 Library。
   - 预期：卡片学习状态改变，Insights 能看到此次复习记录；记录当前待学/新词状态和复习数作为快照基线。
   - 实际结果：________

4. 回到 Library，点“完整备份”。在保存文件选择器中选中测试包的 `backups` 目录，文件名设为 `checkpoint.lexio-backup`，选择 Lexio 完整数据备份类型后保存。
   - 预期：收到“完整备份已保存”提示，显示测试包 `backups` 下的路径；该文件存在且非空。
   - 截图：保存选择器和成功提示；记录文件大小、修改时间和 SHA-256。
   - 实际结果：________

### B. 修改数据并通过 UI 恢复

5. 仍在隔离实例中，通过 Library 导入 `fixtures\after-backup.csv`。
   - 预期状态：`导入完成：「after-backup」新增 1，更新 0，跳过 0`；搜索 `resilience` 能找到新词。
   - 再学习并评分这张新卡，使备份后至少多出一条学习记录。然后通过导航返回 Library，但不要结束本次学习会话；记下当前 Insights 数值。
   - 实际结果：________

6. 点“恢复备份”，在 Windows 文件选择器中选择 `backups\checkpoint.lexio-backup`。
   - 预期：出现确认对话框，说明会替换词库、学习进度、复习历史和设置，并先保存当前数据安全快照。
   - 先截图确认对话框，点击“恢复”。
   - 确认后预期显示恢复进度遮罩，期间主导航和页面输入不可用；恢复成功后遮罩关闭。
   - 预期：出现“数据恢复完成”提示，显示恢复前安全快照路径及自动备份更新状态；测试包 `data` 中存在该安全快照。Library 词书和当前搜索结果回到备份时的数据；随后进入 Learn，应显示备份中的会话状态，不得继续显示只存在于恢复前内存队列的 `resilience` 卡片。
   - 实际结果：________

7. 关闭该隔离实例后，再次运行同一个 `launch-isolated.cmd`。
   - 预期：应用能重新打开恢复后的数据库；搜索 `serendipity` 和 `ephemeral` 各找到备份时的词条，搜索 `resilience` 无结果。
   - Today/Insights 的学习状态应回到第 4 步的快照基线，不再包含第 5 步新增卡片/复习；保留成功提示和重启后的页面截图。
   - 实际结果：________

### C. 取消与无效备份

8. 点“恢复备份”，在文件选择器中直接按“取消”。
   - 预期：文件选择器关闭，不出现恢复确认或错误提示；第 7 步核对的词库和学习状态不变。
   - 实际结果：________

9. 再次打开恢复选择器，选中有效备份，在确认对话框点击“取消”。
   - 预期：不恢复、不显示成功提示，当前测试数据保持不变。
   - 实际结果：________

10. 再次选择恢复，选中 `fixtures\invalid-backup.lexio-backup` 并在确认框点击“恢复”。
    - 预期：出现“备份无法恢复”或等效提示，说明当前数据已由恢复前快照还原，并显示快照路径；当前词库和学习状态保持不变。诊断日志记录技术错误，不在用户提示中暴露堆栈。
    - 截图错误提示；再次搜索 `serendipity` 和 `resilience` 确认数据未变。
    - 实际结果：________

11. 失败回滚边界：步骤 10 用普通无效文件验证 UI 错误提示与现有数据保留。若要验证“写入过程中失败后从安全快照回滚”，需要在隔离环境引入可控故障注入；当前 UI 没有故障注入入口。不要在有效测试数据库上更改权限、断电或手工破坏数据库来模拟此场景。把此项标记为未验证，并附上 Core 测试结果作为独立服务层证据。
    - 实际结果：________

### D. 记录和限制

- `PickerOperationTests` 覆盖成功选择、取消时执行无选择回调，以及异步选择器抛错时执行错误回调；取消与异常不会被混淆。它不替代在 Windows PickerHost 中验证真实文件选择和系统错误提示。
- `LibraryViewModelTests.ImportingAWordRefreshesTheCurrentSearchResults` 使用隔离 SQLite 覆盖 CSV 导入成功后刷新当前搜索；它不替代通过真实文件选择器选取 CSV 的人工步骤。
- 现有 Core 测试 `FullBackupRestoresVocabularySettingsAndReviewHistoryAndRefreshesAutomaticBackup` 覆盖词库、自定义词书、学习/复习历史、设置、恢复前快照和自动备份刷新。
- 现有 Core 测试 `FailedRestorePreservesCurrentDataAndLeavesSafetyBackup` 覆盖无效备份失败后当前数据仍可读取且安全快照存在。
- Core 测试 `ReloadAfterRestoreDropsActiveSessionThatReferencesCardsMissingFromRestoredDatabase` 与 `ReloadAfterRestoreRehydratesTheSessionSavedInTheBackup` 覆盖恢复后清除旧进程队列，以及从备份重载未完成会话；它们不替代下面的真实 Picker UI 操作。
- 这些服务测试不等同于文件选择器 UI 测试。PickerHost 若无法被自动化工具控制，应由人工完成 A、B、C 步骤。
- Settings 页面当前不可见，因此设置项的端到端 UI 恢复要标记“未验证”；不得自行启用设置页或改 UI。
- 报告中的“备份/恢复 UI 通过”必须有真实文件选择、保存、导入/学习状态变化、恢复和重启后的截图与记录。

### 用户再次选择导入文件后的核验（2026-10-18）

- 用户报告选择 `fixtures\before-backup.csv` 后没有提示、页面也没有变化。
- 当前 Computer Use 没有列出可检查的原生窗口（`apps: []`），无法判断 PickerHost 是否仍打开，也无法查看 Library 的导入状态。
- 对隔离数据库执行只读核验：`PRAGMA integrity_check` 为 `ok`，`words` 仍为 100 条，词书仍仅有内置 Starter；`serendipity` 与 `ephemeral` 均未找到。因此 A2 导入**仍未验证**，没有对隔离数据库执行写入。
- 源码链路在 Picker 返回非空文件后会调用 `ImportCommand`。本次修改后，Library 状态行会显示选择进度；Picker 返回空值时显示“未选择导入文件；词库未更改”，打开失败则仍显示错误对话框并记录诊断。由于当前没有可见的原生窗口，仍无法确认系统是否把所选文件交回 Lexio，也没有证据归因到 CSV 导入服务。A2 继续标记为未验证。
- 选择状态反馈修复后，完整 Release 测试为 App **21/21**、Core **88/88**，共 **109/109**；Release 构建 0 错误、32 条 MVVMTK0045 警告。测试不替代真实 PickerHost 人工操作。

### CSV 跨行字段导入回归（2026-10-18）

- 隔离 SQLite 测试发现并修复合法 CSV 引号字段含换行时被按物理行拒绝的问题。当前导入器只在引号外拆分记录，并保留引号字段中的 CRLF。
- 最新 Release 测试为 App **21/21**、Core **89/89**，共 **110/110**；构建 0 错误、32 条 MVVMTK0045 警告。该 Core 测试不等同于通过 Windows 文件选择器的导入验收；手动步骤 A2 仍未验证。

### CSV `front`/`back` 表头映射（2026-10-18）

- 导入器现在支持 `word`/`单词`/`front` 作为单词列，`zh`/`释义`/`back` 作为释义列；隔离 SQLite 测试覆盖 `back,front` 与 `front,back` 两种顺序。
- 最新完整 Release 测试为 App **21/21**、Core **91/91**，共 **112/112**；构建 0 错误、32 条 MVVMTK0045 警告。A2 的真实 Picker UI 步骤仍未验证。

### 用户选择 CSV 后的无可见反馈（2026-10-18）

- 用户报告选择 `fixtures\before-backup.csv` 后没有提示或页面变化。选择后对隔离数据库做只读核验：`integrity_check = ok`、词数仍为 100，且没有 `serendipity` 或 `ephemeral`；因此没有确认 CSV 已导入。默认用户数据库未访问或修改。
- 源码检查发现 `StatusText` 原先显示在右侧词书面板底部。现移到搜索框下方，与结果计数和进度环相邻；异步状态守护也覆盖启动错误窗口的恢复确认和恢复回调。
- 新增回归测试覆盖异步异常和成功路径、`StatusText` 搜索状态区绑定，以及 Learn 总结页焦点目标。最新完整 Release 测试为 App **26/26**、Core **91/91**，共 **117/117**；独立输出目录 Release 构建退出码 0、0 错误、32 条现有 MVVMTK0045 警告。Library 确认框和提示框也已纳入异常守护，避免状态误报。
- 手动测试包的 `launch-verified-build-isolated.cmd` 现指向本轮刚验证的 Release 构建 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-csv-fixture-final-20261018\artifacts\bin\Lexico.App\release_win-x64\Lexio.exe`，并将 `LEXIO_DATA_DIR` 设为测试包自己的 `data` 目录；启动器已核验，未执行。
- 后续用户再次选择 `fixtures\before-backup.csv` 后报告仍无提示、页面未变化。Computer Use 返回 `apps: []`；当时可见的 Lexio 进程都指向较早的构建，没有发现这个最新隔离构建进程，故无法确认该次操作由哪个版本或数据目录处理。隔离测试目录的数据文件修改时间仍停留在 13:43；本次未确认导入。排查过程没有直接访问默认用户数据库，但用户手工操作所用进程的数据目录无法确认。
- Core 新增精确复刻 `before-backup.csv` 表头与两行数据的隔离 SQLite 测试，确认新增 2 条，词性、双语释义和例句均保存正确；该测试不经过 PickerHost 或页面绑定。
- 最新源码 Release 测试为 App **29/29**、Core **92/92**，共 **121/121**；本轮隔离 Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。PickerHost 交回文件、状态区实际渲染、窄窗口布局及 CSV UI 端到端结果仍**未验证**；需在上述最新隔离启动器运行后复验。

### Learn 窄窗口键盘提示换行（2026-10-18）

- Learn 页脚绑定 `KeyboardHint` 的 `TextBlock` 增加 `TextWrapping="Wrap"`，并新增 `LearnLayoutTests.KeyboardHintWrapsInNarrowWindows`。测试先在旧 XAML 上因 `TextWrapping` 缺失失败，再在修复后通过。
- 最新完整 Release 测试为 App **30/30**、Core **92/92**，共 **122/122**；独立输出目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-wrap-verification-20261018\artifacts` 的 Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。
- 当前 Computer Use 仍未列出可检查的原生窗口（`apps: []`），因此窄窗视觉效果没有真实桌面验收。用户报告的 CSV 选择后无页面变化仍未能归因；隔离数据库只读检查未发现两个测试词，未执行数据库写入。
- 14:09 另启动最新验证输出中的隔离 Release 实例，PID 32184，映像为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-learn-wrap-verification-20261018\artifacts\bin\Lexico.App\release_win-x64\Lexio.exe`，数据目录为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Verify-20261018-02d79f89669441c6aae9ee8ea089c719`。进程窗口标题为 `Lexio`、响应正常；独立数据库、WAL/SHM 和自动备份已初始化。此实例没有访问默认用户数据目录；等待在此窗口重新完成 A2 文件选择并确认状态提示。
- 用户随后报告新实例显示“导入完成及数量”，但之后表示没记清具体数字。两个已知隔离数据库的只读检查均仍为 100 个 Starter 词、1 本词书、没有 `serendipity` / `ephemeral`，且 `integrity_check = ok`。这与成功导入 2 个新词不一致；当前不能把 A2 记为通过，也不能仅凭此矛盾判定数据层失败。需要在明确的隔离实例中复验并同时核对其数据目录；默认用户数据库未查询。
- 用户按要求在刚才导入的同一窗口搜索 `serendipity`，报告没有结果。因用户无法回忆导入计数，且没有截图可核实窗口身份，此轮端到端导入/搜索验收记为**失败/未确认**：UI 曾显示完成及数量，但当前可见词库中没有测试词。PID 32184 仍在运行，但窗口标题仍只是 `Lexio`；其隔离目录中的 DB、WAL、SHM 和自动备份时间戳在用户操作后仍为 14:09:33（进程启动时刻），显示本次交互没有写入这个指定隔离实例。结合同时存在多个同名 Lexio 进程，根因**很可能是操作了另一个 Lexio 窗口，但尚未证实**。已知隔离数据库完整性检查为 `ok`，其中仍没有两个测试词；默认用户数据库未访问。源码与现有 App/Core 回归测试覆盖成功导入、计数状态和导入后刷新，但不足以定位这次真实 Picker 操作的差异；下轮应先确认操作窗口对应 PID 32184，再决定是否存在代码缺陷。

### 隔离实例窗口标题识别（2026-10-18 后续）

- 当进程设置 `LEXIO_DATA_DIR` 时，主窗口标题现在附加自定义数据目录的末级名称；默认用户数据仍使用 `Lexio` 标题，标题不显示完整路径。
- `AppWindowTitleTests` 先以 4 个用例复现格式化器缺失，随后通过。完整 Release 测试为 App **34/34**、Core **92/92**，合计 **126/126**；完整 Release 构建退出码 0、0 错误、32 条既有 MVVMTK0045 警告。隔离 artifacts 目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-window-title-final-20261018`。
- 新构建已在全新隔离数据目录启动：PID **3744**，窗口实际标题 `Lexio — Lexio-UI-Verify-20261018-title-marker-01`，进程响应正常。该目录与默认用户数据分离；正在用此具名窗口重新执行 CSV 导入和搜索人工步骤，结果待记录。

### 具名隔离实例 CSV 导入复验结果（2026-10-18）

- 本轮再次请求用户在上述具名隔离实例导入 `before-backup.csv` 并搜索 `serendipity`。用户先回复“导入失败或取消”，随后确认“不确定 / 没看到”；没有可辨识的计数、错误提示或搜索结果截图，不能区分选择器取消、选择器/导入错误，或未在目标实例中完成操作。
- 仅检查 PID **3744**：其映像为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-window-title-final-20261018\bin\Lexico.App\release_win-x64\Lexio.exe`，仍在运行；目标隔离目录的数据库、WAL、SHM 和自动备份修改时间均为进程启动时刻 **14:20:31–14:20:32**，目录没有诊断日志。没有观察到此次导入写入。默认用户数据库未查询或修改，其他 Lexio 进程未关闭。
- Computer Use 此轮仍返回 `apps: []`，无法查看 Windows PickerHost 或目标窗口状态；未尝试绕过原生选择器。A2 的真实 Picker 导入和搜索验收继续标记为**未验证**，这次也没有足够证据归因于 CSV 导入代码缺陷。
- 源码路径显示：选择器返回空值时设置“未选择导入文件；词库未更改”；选择器异常时显示错误对话框并写诊断日志；只有非空文件才调用导入命令。现有测试覆盖这些回调边界和 Core 导入服务，但不能替代本次缺失的真实 UI 证据。

### 导入结果对话框（2026-10-18）

- Library 现在在 CSV/TXT 处理及词书/搜索刷新完成后弹出导入结果：成功时显示词书名和新增、更新、跳过数量；文件无有效单词等服务错误显示可读原因。意外异常沿用“导入未完成”错误提示；取消选择仍仅显示未更改状态，不弹结果框。
- 新增的隔离 SQLite ViewModel 测试分别验证成功计数摘要与无效内容错误反馈。先因反馈契约不存在而失败，接入后两项均通过。完整 Release 测试为 App **36/36**、Core **92/92**，共 **128/128**；构建 0 错误、32 条既有 MVVMTK0045 警告。独立 artifacts 目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-import-feedback-release-20261018`。
- 已用该构建启动新隔离实例 PID **3860**，标题 `Lexio — Lexio-UI-Verify-ImportFeedback-20261018-7c64b2e733b74dfb9a4873a510a02a42`，数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Verify-ImportFeedback-20261018-7c64b2e733b74dfb9a4873a510a02a42`。进程响应正常，数据库已初始化；PickerHost 和结果对话框尚未通过真实桌面交互验收，A2 保持**未验证**。
- 用户确认无法操作该新窗口/文件选择器，因此本次没有实际导入或搜索；不再重复请求同一项手工步骤。Computer Use 无可见原生窗口（`apps: []`）。

### CSV 导入与搜索端到端复验（2026-10-18，最新）

- 使用最新隔离 Release 构建 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-today-empty-final-20261018\bin\Release\net10.0-windows10.0.19041.0\win-x64\Lexio.exe` 启动 PID **2136**，仅在该进程环境设置 `LEXIO_DATA_DIR=C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Auto-Verify-20261018-c82f30b3f3114e6381c9e85a1e50e5cc`。未关闭或复用其他 Lexio 实例，也未访问默认用户数据库。
- 通过 Computer Use 在目标窗口进入 Library、打开 Windows 文件选择器并选取测试包 `fixtures\before-backup.csv`。Library 页面和结果对话框均显示成功：`before-backup` 新增 **2**、更新 **0**、跳过 **0**；词书列表出现 `before-backup`，数量为 **2**。
- 在同一窗口搜索 `serendipity`，页面返回 **1 个结果**，显示词义 `n. 意外发现的乐趣` 及英文释义 `a happy accident`。
- 对该临时数据库使用 SQLite URI `mode=ro` 只读核验：`PRAGMA integrity_check = ok`；`words` 表中存在 `serendipity` 与 `ephemeral`；`before-backup` 词书关联 2 个词。数据库路径为隔离目录下的 `lexio.db`。
- A2 的真实文件选择、导入反馈、词书刷新和搜索现在标记为**通过**。这次运行不等于本清单 `data` 目录的导入；A3–A11 的学习/完整备份/恢复流程仍未验收。截图在本轮 Computer Use 记录中可见，但没有另存到测试包 `evidence` 目录。

### 隔离导入目录身份复核（2026-10-18 后续）

- 只读复核仍运行的 PID **2136** 及其具名隔离目录 `Lexio-UI-Auto-Verify-20261018-c82f30b3f3114e6381c9e85a1e50e5cc`：窗口标题与该目录标记一致，`lexio.db` 有 **102** 个词，包含 `serendipity` 和 `ephemeral`，`before-backup` 词书关联 **2** 个词，`PRAGMA integrity_check` 为 `ok`。这再次确认 A2 在该实例通过。
- 另一个较新的 `Lexio-UI-Verify-ImportFeedback-20261018-7c64b2e733b74dfb9a4873a510a02a42` 目录只有 100 个 Starter 词、没有两个测试词、没有日志文件，且当前没有同标题运行进程。用户无法在该窗口/文件选择器继续操作，因此这份空结果不能推翻 PID 2136 的 A2 通过记录，也不能证明该次窗口导入失败；只能说明该隔离目录没有收到测试导入。
- 所有数据库检查均通过 SQLite `mode=ro` 执行；未写入或关闭任何实例，未访问默认用户数据。`apps: []` 仍使本轮无法继续进行原生窗口验收。

### 完整备份保存选择器修复与隔离 UI 验证（2026-10-18）

- 真实操作两次复现旧 `Windows.Storage.Pickers.FileSavePicker` 在“完整备份”按钮上的 `COMException (0x80004005)`；系统保存窗口中留下 0 字节占位文件，备份服务未被调用。修复范围仅限完整备份的保存选择器：改用 Windows App SDK 1.8 `Microsoft.Windows.Storage.Pickers.FileSavePicker(mainWindow.AppWindow.Id)`，保留默认文件名、`.lexio-backup` 类型和现有备份服务；打开与恢复选择器未改动。
- 新增 `LibraryBackupPickerTests.FullBackupUsesWindowIdBasedWindowsAppSdkSavePicker`，锁定 `AppWindow.Id`、扩展名和 `PickSaveFileAsync` 调用。完整 Release 测试为 App **53/53**、Core **94/94**，合计 **147/147**；完整 Release 构建退出码 0、**0 错误、32 条既有 MVVMTK0045 警告**。隔离构建根目录：`C:\Users\dlcmh\AppData\Local\Temp\Lexio-picker-fix-20261018\bin`。
- 用该构建启动独立 PID **39960**，窗口标题 `Lexio — Lexio-UI-BackupPicker-Fix-20261018-9f7e2c41b6cd4f0f937eaabc6c79881b`，数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-BackupPicker-Fix-20261018-9f7e2c41b6cd4f0f937eaabc6c79881b`。从 Library 使用真实保存对话框创建 `backups\checkpoint.lexio-backup`，页面显示“完整备份已保存”及目标路径。对备份文件只读执行 SQLite `PRAGMA integrity_check` 返回 `ok`，包含的 `words` 表有 100 条记录。
- 此实例不使用本清单的 `data` 目录，也未完成步骤 3 的学习基线。后续尝试导入 `fixtures\after-backup.csv` 时，Windows 原生打开框由 `PickerHost.exe` 承载；Computer Use 对 Lexio 窗口发起的“打开”点击报告目标窗口不匹配。按工具流程激活并刷新后重试一次仍被拦截，因此停止向原生选择器发送输入。文件没有提交给导入服务，本轮没有执行恢复按钮或替换数据库；恢复确认框、恢复前安全快照、重启与取消/无效文件流程仍未验证。
- 当前隔离实例和其他已有 Lexio 进程保持运行。未访问或修改默认用户数据库，也未删除 0 字节旧选择器占位文件或其他临时数据。截图保留在本轮 Computer Use 记录中，没有另存至测试包 `evidence` 目录。

### 恢复备份校验表约束、索引与触发器定义（2026-10-18 后续）

- 服务层隔离测试发现并复现：`foreign_key_check` 不会发现外键声明被删除，列名校验也看不出表级唯一约束被移除。当前校验在内存 SQLite 应用全部迁移并生成规范表 SQL，再与备份中每个必需表的定义比较；另核对全部 10 个迁移索引及 5 个触发器的定义。启动、备份创建和恢复共用此校验。
- 新增的两个表约束用例在修复前均未抛出预期异常；修复后拒绝缺少 `deck_words` 外键或 `daily_plans(date, intensity)` 唯一约束的备份。定向恢复拒绝测试 **13/13** 通过，当前数据保持不变。完整 Release 测试为 App **81/81**、Core **112/112**，合计 **193/193**；构建 **0 错误、32 条既有 MVVMTK0045 警告**。
- 隔离输出根目录为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-canonical-schema-final-20261018\bin`。本轮验证只使用测试生成的临时 SQLite 数据，没有操作人工验收包 `data`、默认用户数据库或现存 Lexio 进程。用户无法操作新的隔离窗口/PickerHost，完整导入 UI 复验仍未完成。

### Library 原生导入选择器运行复核（2026-10-18 后续）

- 使用独立 Release 实例 `Lexio-UI-Goal-Cycle-20261018-0f443d63225640afb0caebceae7aa262`，隔离数据目录同名。Library 页面及三个操作按钮正常显示；按下导入后状态行显示“正在选择导入文件…”。
- Computer Use 未列出可控制或观察的 PickerHost/系统文件选择器窗口。由于没有可见的文件选择结果，未选文件，未触发词库写入；原生 Picker 交互、取消和文件返回结果仍**未验收**。当前窗口状态不足以判断对话框是否已显示，也不能据此认定导入服务故障。
- 该测试进程的正常关闭请求失败；核对准确 PID/映像/标题后，仅结束本轮启动的 PID **24076**。没有操作其他 Lexio 进程或默认用户数据库。

### Library CSV/TXT 导入及完整备份恢复打开选择器迁移（2026-10-18 后续）

- 当前源码中，Library 导入和完整备份恢复的两个打开选择器均已改为 Windows App SDK `FileOpenPicker`，并传入主窗口 `AppWindow.Id`。导入保留 `.csv` / `.txt` 筛选；恢复保留 `.lexio-backup` / `.backup` / `.db` 筛选及现有确认流程，确认框文件名来自 Picker 返回路径。
- `LibraryOpenPickerTests` 定向验证 **3/3** 通过；完整 Release 测试 App **82/82**、Core **112/112**。完整 Release 构建 0 错误、32 条既有 MVVMTK0045 警告，隔离输出目录为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-migration-20261018-a31a713ee6194dbca961a0470bc27b73`。
- 原生 PickerHost 仍无法由当前桌面自动化控制，因此真实文件选择、取消及恢复确认/恢复后重启流程尚未完成验收。此前关于 PickerHost 不可操作的人工验收记录仍有效；自动化测试只使用临时数据，未访问默认用户数据库，也未启动或关闭应用实例。

### Library 导入与恢复选择器当前工作区验证（2026-10-18 后续）

- 按用户批准的范围复核当前磁盘源码：Library CSV/TXT 导入与完整备份恢复均通过 Windows App SDK `FileOpenPicker(mainWindow.AppWindow.Id)` 打开；扩展名筛选和返回路径交接保持原有业务流程。恢复确认框显示 Picker 返回路径中的文件名。对应的 `LibraryOpenPickerTests` 覆盖这两条路径。
- 定向选择器测试通过 **3/3**。当前完整 Release 测试：App **87/87**、Core **112/112**，共 **199/199**，失败 0、跳过 0。隔离 Release 构建成功，0 错误、32 条既有 MVVMTK0045 警告；artifacts 根目录为 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-approved-20261018-01`。
- 当前工作区已经包含迁移和回归测试，因此本轮没有重复改写业务源码。PickerHost 的真实选择、取消和恢复确认/恢复后重启仍未验收；当前桌面自动化无法操作原生选择器。未访问默认用户数据库，未启动或关闭 Lexio 实例，未升级依赖。

### 完整恢复期间退出的数据库生命周期协调（2026-10-18 后续）

- 代码路径复核发现：主窗口关闭事件同步等待关停任务，但关停原先只等待自动备份和学习操作；完整用户恢复包含多个数据库调用，因此数据库关闭可能插入在恢复与后续状态重载之间。
- 新增 `AsyncOperationGate`：完整恢复操作串行执行，关停开始后拒绝新恢复并等待已登记的恢复任务全部结束，再等待学习操作并关闭数据库。恢复流程的内部等待不捕获 UI 上下文，以免关闭事件同步等待时依赖 UI 线程续跑。
- 新增协调器行为测试 3 项及 `AppServices` 接线/无 UI 上下文等待测试 3 项；接线测试在修改前按预期失败，修改后定向测试 **6/6** 通过。完整 Release 测试 App **93/93**、Core **112/112**，共 **205/205**；隔离 Release 构建 0 错误、32 条既有 MVVMTK0045 警告，artifacts 根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-restore-shutdown-20261018-01`。
- 当前桌面自动化无法操作原生窗口，本轮没有实际在恢复过程中关闭窗口；该 UI 时序仍待可操作的隔离实例人工验收。没有访问默认用户数据库或启动、关闭任何 Lexio 实例。

### 恢复与 Library 数据操作串行化（2026-10-18 后续）

- Library CSV/TXT 导入、手动完整备份和用户完整恢复现在共享同一个数据操作闸门，三类操作不会并行访问数据库。窗口关停会停止接收新的闸门操作，并等待已经登记的操作结束后再释放数据库。
- `AppServicesShutdownTests` 验证导入、手动备份、恢复共用闸门以及关停先排空闸门；`AsyncOperationGateTests` 验证闸门的串行、停止接收与等待行为。当前 UI 自动化不能操作原生窗口，因此“导入/备份正在运行时请求恢复”及“操作排队期间关闭窗口”的真实桌面时序仍待人工验收。
- 本轮验证使用 Release 测试进程和隔离构建目录，没有访问默认用户数据库，也没有关闭现有 Lexio 实例。

### 当前 Release 隔离窗口可发现性复核（2026-10-18）

- 从刚验证的 Release 构建启动了独立实例 PID **19772**，进程环境使用全新数据目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-UI-Current-Verify-Restore-20261018-6e81f439`。窗口标题为 `Lexio — Lexio-UI-Current-Verify-Restore-20261018-6e81f439`，目录中只读列举到 `lexio.db`、WAL/SHM 和自动备份，确认启动落在隔离目录。
- Computer Use 的 `list_windows()` 能发现多个旧隔离窗口，却连续未返回 PID 19772 的窗口；没有猜测窗口句柄，也没有发送 UI 输入。新实例保持运行；本轮未打开 Picker、导入文件或恢复备份，因此这两项当前 Release UI 仍未验收。
- 默认用户数据库及其他 Lexio 进程未读取或修改，也未关闭。

### Library 打开选择器迁移复核（2026-10-18，批准后）

- 复核当前磁盘源码：CSV/TXT 导入和完整备份恢复都使用 Windows App SDK `FileOpenPicker`，通过主窗口 `AppWindow.Id` 关联窗口。筛选分别保留 `.csv` / `.txt` 与 `.lexio-backup` / `.backup` / `.db`；恢复仍使用既有 `RestoreUserDataAsync` 流程，确认框文件名由 Picker 返回路径提取。
- 定向 `LibraryOpenPickerTests` **3/3** 通过；完整 Release 测试 App **99/99**、Core **115/115**，共 **214/214**，失败 0、跳过 0。隔离 Release 构建成功，0 错误、32 条既有 MVVMTK0045 警告；输出根目录 `C:\Users\dlcmh\AppData\Local\Temp\Lexio-open-picker-approved-final-20261018\bin`，生成 `Lexio.exe`。
- `PickerOperationTests` 在完整 App 套件中覆盖选择器返回文件、用户取消和异常回调。此次没有操作原生 PickerHost；真实文件选择、取消行为、恢复确认与恢复后重启仍**未验收**。测试与构建未访问默认用户数据库，未启动或关闭应用实例，也未升级依赖。

### 自动备份与完整恢复数据库访问复核（2026-10-18 后续）

- 沿 `AutomaticBackupWorker`、`AppDatabase.BackupToAsync`、`RestoreFromAsync` 和 `UserDataBackupService.RestoreAsync` 检查并发边界：自动与用户备份、恢复对同一 `AppDatabase` 的每次操作均通过同一 `RunAsync` 信号量串行；恢复候选库经 staging 校验后，在单个串行数据库操作中写入。自动 worker 没有加入 AppServices 高层数据闸门，因而可在恢复的分步服务调用之间运行，但本次没有发现它能并发使用 SQLite 连接或捕获半写入状态的可复现路径，没有增加第二层闸门。
- `AutomaticBackupWorkerTests` **1/1**、`UserDataBackupServiceTests` **5/5** 通过，均使用临时目录和 SQLite 数据。现有用例没有覆盖自动 worker 与恢复回滚故障的并发注入，因此这项交错故障仍未做压力/故障注入验证。
- 本轮未改生产代码、未运行应用，也未访问默认用户数据库；真实 PickerHost 和恢复 UI 仍未验收。
