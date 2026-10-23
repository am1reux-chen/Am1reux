# Lexio 算法说明（三层）

## 第一层：FSRS-6（记忆状态估计）
- 使用 FSRS.Core 1.0.7（MIT），21 参数和 FSRS-6 的 D/S/R 公式；同日稳定度使用 `w[17..19]`，学习步骤为 1/10 分钟，重学步骤为 10 分钟。
- FSRS.Core 1.0.7 的内置权重为 `[0.2172, 1.1771, 3.2602, 16.1507, 7.0114, 0.57, 2.0966, 0.0069, 1.5261, 0.112, 1.0178, 1.849, 0.1133, 0.3127, 2.2934, 0.2191, 3.0004, 0.7536, 0.3332, 0.1437, 0.2]`。这与 py-fsrs 6.3.2 / 官方 FSRS-6 文档当前列出的默认权重不同；现有卡片的 S/D 已由旧权重产生，因此此次保持权重不变，先锁定实现公式的一致性。目标保留率默认 0.9。
- Lexio 封装已修正两项经 py-fsrs 6.3.2 验证的兼容差异：遗忘曲线计算按整天取 elapsed（而非小数天）；同日 Hard/Good/Easy 的短时稳定度增长倍率下限为 1。适配后的日志版本号为 `fsrs-core/1.0.7+lexio-fsrs6fix1`。详细对照见 [`fsrs-conformance.md`](fsrs-conformance.md)。
- 业务侧只接触 `ISrsEngine`：
  ```csharp
  SchedulingOutcome Schedule(CardState state, Grade grade, DateTime now, int? elapsedMs);
  double PredictRecall(CardState state, DateTime now);
  ```
- 每条 ReviewLog 记录 `engine_version`（当前 `fsrs-core/1.0.7+lexio-fsrs6fix1`）与复习时刻的 `fsrs_retrievability`，保证可复现、可对比。

## 第二层：Personal Recall Calibrator（影子评估）
- 使用 L2 正则 Logistic Regression。输入为 FSRS 原始召回率、相对个人中位数的有效思考时间、此前 10 次正确率、历史遗忘数、提示标记、实际复习间隔和学习状态。
- 思考时间只有 `timing_valid=1` 且在 300–120000 ms 范围内才参与；失焦或异常停顿不参与。时间特征只作为辅助输入。
- 最少 200 条影子样本、训练集和验证集分别至少有 10 个成功与失败样本后才拟合。按时间升序切分，前 80% 训练、后 20% 验证。每个用户会话开始时检查一次；距上次训练检查不足 50 条 ReviewLog 时跳过。
- 只有验证集 Log Loss 与 Brier Score 均优于原始 FSRS，且 ECE 不恶化超过 0.01，候选权重才会用于后续影子预测。预测相对 FSRS 原始概率的调整限制在 ±0.15；样本不足或模型未通过时直接使用 FSRS 概率。
- 迁移 v3 为 `ReviewLogs` 增加 `hint_used`，并扩展模型表保存训练检查点、模型版本、原始/校准概率和特征 JSON。ReviewLog 与对应影子预测在同一事务提交；profile 中保留权重和评估指标。
- 当前校准结果只用于影子评估，不影响 FSRS 输入、due 时间或正式排程。即使单次时间顺序验证通过，也不自动参与正式调度；未来需要持续验证和独立发布门槛。

## 第三层：Daily Workload Planner
- 个人基准：`baseline_daily_reviews` 设置，默认 60。规划器将它乘以平滑后的到期复习耗时，得到个人基准秒数；三档时间预算分别是基准的 55%、100%、170%。
- 耗时样本只取最近 1000 条 `timing_valid=1` 的有效历史。新词按 `reps_before=0` 区分；学习步骤按 `state_before` 区分；复习的原 due 时刻由同一卡片上一条日志的 `next_due` 还原。提前复习为早于 due，逾期复习定义为晚于 due 至少 24 小时，其余归到期复习。
- 每类均值用 20 个先验样本向默认值平滑，避免少量答题记录使时长估算大幅跳动。初始回退为新词 8 秒、复习 4 秒；学习/逾期/提前复习以复习均值为先验。单卡估时再根据 FSRS difficulty 与历史 lapses 调整，并限制在合理范围内。
- 选择优先级按价值/秒排序：遗忘风险（`1 - retrievability`）、词频排名（排名越高越重要）、FSRS difficulty、历史 lapses 和逾期时长共同决定价值；预计耗时作为成本。并列时先选到期更早的卡，再按卡片 ID 保持确定性。
- 轻量只选 due 的学习步骤和复习卡，不加入新词或提前复习。常规先用预算处理 due 卡，再从剩余时间中最多拿基准时间的 20% 学新词。重度先处理 due 卡，再最多拿 35% 基准时间做未来 4 天内的提前复习，之后最多拿 35% 学新词。新词数量由实际耗时决定，不是固定词数。
- 超出预算的卡片留在原 due 状态，不改 FSRS 到期时间；重复候选按卡片 ID 去重。StudyService 的并发 Start 返回已启动的会话，不重复创建活动会话。重度档选入的提前复习在会话队列持久化为 `is_early_review`，恢复后会立即呈现，调度卡片 due 保持原值。
- `EstimatedMinutes` 是选中卡片的类别耗时和难度调整之和，四舍五入到 0.1 分钟。持久化的每日计划日期使用启动时的本地日期；卡片比较始终使用 UTC 时间。

## 计时（EffectiveTiming）
- 采集：卡片显示→显示答案 之间的墙钟；窗口失焦时段累加为 `unfocusedMs`。
- `effective = raw − unfocused`；`valid = effective∈[300ms,120s] 且 unfocused < 0.6·raw`。
- 思考时长**只**作为校准辅助特征与规划统计，绝不直接映射评分，也不直接当作记忆强度。

## 模型演进规则
- 新算法（如 FSRS-7）一律先影子运行（并行记录预测、不改变调度），在真实用户数据上以 LogLoss/Brier/ECE 显著优于现行版本后方可切换主模型。
