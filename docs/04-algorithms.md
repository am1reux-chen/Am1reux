# Lexio 算法说明（三层）

## 第一层：FSRS-6（记忆状态估计）
- 引用 FSRS.Core 1.0.7（MIT），经源码核实为 FSRS-6 语义：21 参数，`decay=-w[20]`，`factor=0.9^(1/decay)-1`，遗忘曲线 `R=(1+factor·t/S)^decay`，短时稳定度用 w[17..19]，学习步骤 1min/10min，重学步骤 10min。
- 默认参数为 FSRS 官方通用参数；目标保留率默认 0.9（设置可调）。
- 业务侧只接触 `ISrsEngine`：
  ```csharp
  SchedulingOutcome Schedule(CardState state, Grade grade, DateTime now, int? elapsedMs);
  double PredictRecall(CardState state, DateTime now);
  ```
- 每条 ReviewLog 记录 `engine_version`（当前 `fsrs-core/1.0.7`）与复习时刻的 `fsrs_retrievability`，保证可复现、可对比。

## 第二层：Personal Recall Calibrator（阶段5，接口已预留）
- 正则化 Logistic Regression，特征：FSRS 预测 R、log(stability)、log(elapsed_days)、有效思考时长（截断+标准化）、提示使用、近 10 次错误率、词频排名。
- 输出经 **限制映射**：`p_final = clamp(p_fsrs·exp(β·clip(x,−δ,δ)), …)`，影响幅度限制在 ±20%，防止错误预测引起调度剧变。
- 训练样本 < 400 条或 AUC 无提升 → 回退纯 FSRS。指标：Log Loss、Brier、ECE（校准误差），存入 `model_profiles.metrics`。预测写入 `model_predictions`，不改动历史。

## 第三层：Daily Workload Planner
- 个人基准负载：初始 60 张/日（设置可调），随历史（近 14 天实际完成量）自适应。
- 档位：轻量 = 0.55×基准；常规 = 1.0×；重度 = 1.7×（常量集中定义，可调）。
- 组成：
  - 轻量：仅到期复习，按遗忘风险（retrievability 升序）取前 N；新词 0。
  - 常规：全部到期复习 + 新词 ≈ 20% 基准。
  - 重度：到期复习 + 未来 4 天内「低稳定度提前复习」+ 新词 ≈ 35% 基准。
- 预计时长 = Σ(类别 × 个人历史均秒/卡)：`avgSec(new)·nNew + avgSec(review)·nReview`，历史不足时回退默认 8s/4s。
- 债务控制：单日复习量超过重度上限时，超额部分**不重置到期日**、按风险顺延至后续日期自然消化（不制造虚假到期），Insights 页可见堆积。

## 计时（EffectiveTiming）
- 采集：卡片显示→显示答案 之间的墙钟；窗口失焦时段累加为 `unfocusedMs`。
- `effective = raw − unfocused`；`valid = effective∈[300ms,120s] 且 unfocused < 0.6·raw`。
- 思考时长**只**作为校准特征与统计，绝不直接映射评分，也不直接当作记忆强度。

## 模型演进规则
- 新算法（如 FSRS-7）一律先影子运行（并行记录预测、不改变调度），在真实用户数据上以 LogLoss/Brier/ECE 显著优于现行版本后方可切换主模型。
