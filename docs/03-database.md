# Lexio 数据库设计

单文件 SQLite，WAL 模式，FTS5 全文索引。时间一律存 UTC ISO-8601（`DateTime.Kind=Utc`，格式 "o"）。

## 词典区（可重建）
- `words(id, lemma, ipa_us, ipa_uk, frequency_rank, cefr)` — 词头。
- `word_senses(id, word_id→words, pos, zh_gloss, en_gloss, example_en, example_zh, sense_order)` — 释义（一词多义多行）。
- `decks(id, name UNIQUE, description, source, built_in, created_at)` — 词书。
- `deck_words(deck_id, word_id, position, PK(deck_id,word_id))` — 词书内容，保留原始顺序。
- `words_fts` — FTS5（content=words, tokenize=unicode61），触发器与 words 同步。

## 学习区（用户资产）
- `cards(id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at)`
  - state 直接映射 FSRS State（0=Learning,1=Review,2=Relearning）；「新卡」= Learning 且 stability IS NULL 且 reps=0。索引 `(state, due)`。
- `review_logs(id, card_id, reviewed_at, rating, state_before, step_before, stability_before, difficulty_before, scheduled_days_before, elapsed_ms, raw_elapsed_ms, timing_valid, fsrs_retrievability, engine_version, next_due, next_interval_days)`
  - **不可变**。elapsed_ms=有效思考时长；raw_elapsed_ms=墙钟；timing_valid=计时是否未被窗口失焦污染。engine_version 保证预测可复现。
- `sessions(id, started_at, ended_at, intensity, reviews, again, hard, good, easy)` — 学习会话。
- `daily_plans(id, date, intensity, review_count, new_count, estimated_minutes, created_at, UNIQUE(date,intensity))` — 当日计划快照。
- `model_profiles(id, kind, version, weights JSON, trained_at, metrics JSON, is_active)` — 校准模型参数（阶段5）。
- `model_predictions(id, review_log_id→review_logs, profile_id, predicted_recall, created_at)` — 每条复习的校准预测（阶段5）。
- `settings(key PK, value)` — 键值设置。

## 迁移机制
`schema_migrations(version INTEGER PK, applied_at)`。迁移为顺序 SQL 数组；`Database.Migrate()` 在事务内逐个应用缺失版本，幂等（重放无副作用）。备份 = 复制数据库文件（含 checkpoint）；恢复 = 校验版本后整文件替换。

## 预设索引
- `idx_cards_due(state, due)` — 每日队列查询。
- `idx_logs_card(review_logs.card_id)`、`idx_logs_time(reviewed_at)` — 统计与训练数据提取。
- `idx_deck_words_word(word_id)`。
