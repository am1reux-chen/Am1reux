# FSRS-6 实现与参考对照

## 依赖与参考版本

- 项目继续使用 `FSRS.Core 1.0.7`，项目包配置未升级。包元数据和源码 tag 均指向 [`TranPhucTien/FSRS.Core` v1.0.7](https://github.com/TranPhucTien/FSRS.Core/tree/a4a4a373174700d78578c93de5c186b613ed632f)；源码实现由 `Scheduler`、`StabilityCalculator`、`DifficultyCalculator`、`RetrievabilityCalculator` 和 `IntervalCalculator` 组成。
- 官方参考实现固定为 [`open-spaced-repetition/py-fsrs` v6.3.2](https://github.com/open-spaced-repetition/py-fsrs/tree/9446cb06605c597a063aeee49f7d188d42e34dc2)。其 21 个参数、评分定义、遗忘曲线和整天 elapsed 行为与 FSRS-6 算法说明一致，见[官方算法说明](https://github.com/open-spaced-repetition/fsrs4anki/wiki/The-Algorithm/bccece8e7888c8121784ef9d9e8022d4961baf83)。
- 额外检查了 [`FSRS-Sharp` 的参考测试](https://github.com/Overmiind/FSRS-Sharp/tree/871dfa092e7dfde95af25c3e31114bb8311ced23)。其 `ReferenceParityTests` 用 py-fsrs 生成 64 个场景、692 条 review 的 golden 输出，并分别锁定同日 Hard 和整天 elapsed 回归；这里借鉴该测试方法，golden 值仍由官方 py-fsrs 直接生成。

## 实现核对

FSRS.Core 1.0.7 实现了 21 参数 FSRS-6 形式，但内置参数向量与 py-fsrs 6.3.2 当前默认值不同：FSRS.Core 的 `w[20]` 是 `0.2`，参考实现默认 `0.1542`。FSRS 允许优化参数，这个差异本身不证明公式错误；为避免改变由旧参数计算出的既有卡片状态，此阶段保留了 FSRS.Core 原参数，并在相同参数下与 py-fsrs 对照公式和调度行为。

逐源码对照确认了两处行为差异：

1. FSRS.Core 的 `RetrievabilityCalculator` 用 `TimeSpan.TotalDays`，py-fsrs 按已完成的整天数计算 `R`。差异会传播到长间隔复习后的稳定度和下一间隔。
2. FSRS.Core 的 `StabilityCalculator` 只对 Good/Easy 的同日稳定度增长倍率施加 `>= 1` 下限；py-fsrs 对 Hard、Good、Easy 都施加该下限。没有下限时，同日 Hard 会降低稳定度。

没有改 FSRS 数学公式或包版本。`FsrsCoreEngine` 通过包已有的计算器接口，为这两处差异提供与 py-fsrs 6.3.2 一致的兼容实现。日志版本改为 `fsrs-core/1.0.7+lexio-fsrs6fix1`，使修正后的行为可与先前日志区分。

## Golden 对照测试

新增 [`FsrsReferenceParityTests.cs`](../tests/Lexico.Core.Tests/FsrsReferenceParityTests.cs)，使用固定 UTC 时刻、关闭 fuzzing，并用 FSRS.Core 1.0.7 默认权重在 py-fsrs 6.3.2 中生成期望值。覆盖：

- 新卡 Again、Hard、Good、Easy；
- 同日学习步进、同日 Hard 和同日 Again；
- 首次失败、短间隔重学和重新毕业；
- 14 天 23 小时与更长的小数天间隔；
- 目标保持率 `0.1`、`0.8`、`0.9`、`0.97`、`0.99`；
- 极端稳定度和 36500 天最大间隔；
- 小数天时刻的召回率预测。

每个 review 步骤逐项比较卡片状态、学习步、稳定度、难度和间隔；召回率另用参考输出比较。浮点值采用 `1e-10` 相对误差限。参数边界通过参考实现支持的目标保持率两端及最大间隔限值覆盖；Lexio 当前没有暴露自定义权重入口，因此没有在应用接口上测试任意权重向量的上下界。

## 验证与后续

- 2026-10-18 定向 Release 运行 `FsrsReferenceParityTests`：**15/15 通过**，失败 0、跳过 0。当前完整 Core Release 套件为 **112/112 通过**；完整 App/Core 测试和解决方案构建记录见[发布验收记录](release-final-acceptance-2026-10-18.md)。
- 后续若要改用 py-fsrs 官方默认参数，须先解决既有 S/D 状态的参数版本兼容与迁移策略；本阶段不做该变更。
- 官方参考测试不包含随机 fuzzing 的逐点对照；随机 fuzzing 继续由现有确定性关闭测试和区间边界测试覆盖。
