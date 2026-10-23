# Lexio 数据库设计

单文件 SQLite，WAL 模式，FTS5 全文索引。时间一律存 UTC ISO-8601（`DateTime.Kind=Utc`，格式 "o"）。

## 词典区（可重建）
- `words(id, lemma, ipa_us, ipa_uk, frequency_rank, cefr, details_json)` — 词头和可选的 TypeWords 例句、短语、近义词、词源与相关词详情。
- `word_senses(id, word_id→words, pos, zh_gloss, en_gloss, example_en, example_zh, sense_order)` — 释义（一词多义多行）。
- `decks(id, name UNIQUE, description, source, built_in, created_at)` — 词书。
- `deck_words(deck_id, word_id, position, PK(deck_id,word_id))` — 词书内容，保留原始顺序。
- `words_fts` — FTS5（content=words, tokenize=unicode61），触发器与 words 同步。

## 学习区（用户资产）
- `cards(id, word_id, deck_id, state, step, stability, difficulty, due, last_review, reps, lapses, created_at)`
  - state 直接映射 FSRS State（0=Learning,1=Review,2=Relearning）；「新卡」= Learning 且 stability IS NULL 且 reps=0。索引 `(state, due)`。
- `card_marks(card_id PK→cards, favorite, wrong, mastered)` — 按词书卡片保存的用户标记。已掌握卡片不进入新词或到期计划；标记不改写 cards、FSRS due 或 review_logs。
- `review_logs(id, card_id, reviewed_at, rating, state_before, step_before, stability_before, difficulty_before, scheduled_days_before, elapsed_ms, raw_elapsed_ms, timing_valid, fsrs_retrievability, engine_version, next_due, next_interval_days)`
  - **不可变**。elapsed_ms=有效思考时长；raw_elapsed_ms=墙钟；timing_valid=计时是否未被窗口失焦污染。engine_version 保证预测可复现。
- `sessions(id, started_at, ended_at, intensity, reviews, again, hard, good, easy, planned_cards, new_count, estimated_minutes, study_mode)` — 学习会话及开始时计划摘要；`study_mode` 保存回忆、跟写、看义拼写、听写或自测，旧数据默认回忆。
- `session_queue(id, session_id→sessions, card_id→cards, position, is_early_review)` — 可恢复的活动队列；v6 的提前复习标记让重度档提前卡可在恢复后立即呈现而不更改 FSRS due。
- `daily_plans(id, date, intensity, review_count, new_count, estimated_minutes, created_at, UNIQUE(date,intensity))` — 当日计划快照。
- `model_profiles(id, kind, version, weights JSON, trained_at, metrics JSON, is_active)` — 校准模型参数（阶段5）。
- `model_predictions(id, review_log_id→review_logs, profile_id, predicted_recall, created_at)` — 每条复习的校准预测（阶段5）。
- `settings(key PK, value)` — 键值设置。

## 文章学习区
- `articles(id, title, title_translation, created_at, updated_at)` — 用户本地文章和可选标题译文。
- `article_sentences(id, article_id→articles, position, text, translation)` — 按 `position` 排序的英文句子和可选用户译文；文章删除级联删除句子。
- `article_progress(article_id PK→articles, current_sentence, mode, is_complete, updated_at)` — 每篇文章的当前句序、练习方式（0=跟写、1=听写）和完成状态；文章删除级联删除进度。完成时 `current_sentence` 等于句子总数，未完成时从 0 开始计数。

文章进度不写入 `cards`、`sessions`、`review_logs` 或每日 FSRS 计划。文章与整库数据共用 SQLite 快照备份和恢复。

## 迁移机制
`schema_migrations(version INTEGER PK, applied_at)`。迁移为顺序 SQL 数组；`AppDatabase.MigrateAsync()` 在事务内逐个应用缺失版本，幂等（重放无副作用），当前 schema 到 v9。v7 增加 `card_marks` 和 `sessions.study_mode`；v8 增加文章、句子和进度表；v9 增加词条详情与文章标题译文列。备份和恢复使用 SQLite Backup API；恢复会校验 SQLite `quick_check`、全部必需表及列、FTS/ReviewLog 不可变触发器、迁移版本和外键后再替换运行数据库。旧备份恢复时先应用缺失迁移，再校验完整 schema。App 启动时验证主库并创建 `.backup` 快照，运行期间每 15 分钟更新；损坏主库的原文件与 WAL/SHM 侧车会保留为 `.corrupt-*`。

## 预设索引
- `idx_cards_due(state, due)` — 每日队列查询。
- `idx_logs_card(review_logs.card_id)`、`idx_logs_time(reviewed_at)` — 统计与训练数据提取。
- `idx_deck_words_word(word_id)`。

## 数据库对象生命周期
`AppDatabase` 在数据库操作排队前登记操作。开始释放时停止接收新操作，等待已登记的运行中和排队操作完成，再释放 SQLite 连接与串行闸门；重复调用 `DisposeAsync()` 共用同一释放任务。释放开始后提交的新数据库操作以 `ObjectDisposedException` 结束。
