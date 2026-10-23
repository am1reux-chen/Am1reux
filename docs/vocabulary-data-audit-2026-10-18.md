# 内置词库数据与许可审计（2026-10-18）

## 结论

当前已生成并加入 Core 的离线词库资源。常见词来源采用固定版本 wordfreq 3.0.2 的英语 `large` 频率数据与 FrequencyWords 英语 50k 表的并集，不使用许可无法确认的 BNC/COCA 文件。按词头匹配 Wiktionary 英文词条的普通话中文释义和词性后，常见词保留 **21,985** 个词头，超过批准设计要求的 20,000 下限。

另有大学学术词 **753** 条，以及 IELTS 备考核心 **3,395** 条。IELTS 词书是 NGSL 与 NAWL 的备考整理，不是 IELTS 官方词表。三本词书的词条有意重叠，以保留独立复习卡。

资源文件为 `src/Lexico.Core/Dictionary/builtin-vocabulary.json`，大小 **3,885,950 字节**，SHA-256：`466A0C617D36643335582B5D9FDCDF43FE721A38A90FE4DE04F3DC82C5C5EC40`。数据集和署名记录采用 CC BY-SA 4.0，机器可读的来源、版本、哈希及覆盖数见 [`licenses/vocabulary/manifest.json`](../licenses/vocabulary/manifest.json)，完整署名见 [`licenses/vocabulary/NOTICE.md`](../licenses/vocabulary/NOTICE.md)。

## 词量和交集

所有统计仅计算同时有词性和至少一个普通话中文释义的词头。通用 `zh` 翻译仅在非方言时接受，`cmn` 普通话记录也接受。常见词候选词形以 Unicode NFKC 和不区分大小写方式规范化；候选与 Wiktionary 英文词头/词形匹配。纯专名词性、短语和非词汇表面形式不计入常见词。

| 词书 | 候选规模 | 有词性和中文释义 | 候选覆盖率 | 词书成员 |
| --- | ---: | ---: | ---: | ---: |
| 常见英语：wordfreq 前 200,000 词形与 FrequencyWords 50k 并集 | 203,677 个不同词形 | **21,985** | 10.79% | 21,985 |
| 大学学术：NAWL 1.2 | 957 | **753** | 78.68% | 753 |
| IELTS 备考核心：NGSL 1.2 + NAWL 1.2 | 3,766 | **3,395** | 90.15% | 3,395 |

交集按合格词头计算：

| 词书组合 | 交集 |
| --- | ---: |
| 常见英语与大学学术 | 753 |
| 常见英语与 IELTS 备考核心 | 3,395 |
| 大学学术与 IELTS 备考核心 | 753 |

本版总计 21,985 个不同词头、26,133 个词书成员关系；跨词书重叠不会合并学习进度。数据包共有 63,605 个释义成员（重复词书中的释义会落在同一个词条上）。词频文件的词形数不是可直接学习的词条数；例如一个词形可能映射到 Wiktionary 的不同词头，未匹配到中文或词性的候选不会打包。

## 固定来源、许可与哈希

| 输入 | 固定版本/日期 | 字节数 | SHA-256 | 许可与用途 |
| --- | --- | ---: | --- | --- |
| wordfreq 英语 `large_en.msgpack.gz` | 3.0.2，commit `372f6dbb3bd2ad4b675a3b18b94d44d0dd5fea8b` | 1,494,836 | `DFFAE8066B78DCE0A6667CF5F58E567054F902674667090A7AC8A8A44628B05C` | README 说明数据文件可按 CC BY-SA 4.0 再发布，并称词频快照大致截至 2021 年；使用其英语前 200,000 词形。上游 README 同时列出 Google Books Ngrams / Syntactic Ngrams、Leeds Internet Corpus、Wikipedia、ParaCrawl、OPUS OpenSubtitles 2018、多个 SUBTLEX 词表及聚合 Twitter 频率数据，并要求保留相应署名，特别是 SUBTLEX 作者署名。 |
| wordfreq 前 200,000 英语词形 | 由上方固定输入解码 | 3,740,302 | `48D1A882FB9D77596A82BEA170A3AC28C3193536A49F3977FD5FF1BE3F2054A` | 审计生成的排序词形文件；应用不单独携带此中间文件。 |
| FrequencyWords 英语 50k | commit `525f9b560de45753a5ea01069454e72e9aa541c6` | 622,749 | `5351FF405B1126EF555791DD4D9798A48E3E9A501A9FC481A9DA957752CFB458` | 上游 README 将内容标为 CC BY-SA 4.0，说明 2018 频率表来自 OpenSubtitles 2018；规范化后 49,996 个不同词形。 |
| NGSL 1.2 基本统计 CSV | 官方项目文件 | 62,566 | `2098BAB8955A120A9766C6282A51D7D578C6CB0A7D946600D2FFB73BA25A0B44` | 官方页面将 NGSL 标为 Browne、Culligan、Phillips 的 CC BY-SA 4.0 作品。 |
| NAWL 1.2 字母表文件 | 官方项目文件 | 10,340 | `88A99099E10010EA40992CCA4A5119102D05A0F2888EE5D43D4C8B4AFD597FEF` | 官方页面标注 CC BY-SA 4.0；官方说明 NAWL 1.2 为 957 词，与 NGSL 1.2 配合使用。 |
| Kaikki/Wiktextract 英文 Wiktionary 原始 JSONL gzip | 标注 dump 日期 2026-09-02 | 2,981,058,381 | `3DAC8A09E57827BEF493E2E6B552FE917BF3B1FC55DEE05C4E4A3702AFF5DBDB` | Wiktionary 词条文本可按 CC BY-SA 4.0 再发布；只提取匹配到的词性及普通话翻译，不把原始词典放进安装包。完整压缩流读到 EOF，无校验错误。 |
| 最终 JSON 词库 | 版本 1 | 3,885,950 | `466A0C617D36643335582B5D9FDCDF43FE721A38A90FE4DE04F3DC82C5C5EC40` | 从上述 CC BY-SA 数据筛选和整理后，整个合并数据集按 CC BY-SA 4.0 发布。 |

许可核验链接：

- [wordfreq 3.0.2 固定版本 README](https://github.com/rspeer/wordfreq/blob/372f6dbb3bd2ad4b675a3b18b94d44d0dd5fea8b/README.md)
- [FrequencyWords 固定版本仓库与许可说明](https://github.com/hermitdave/FrequencyWords/tree/525f9b560de45753a5ea01069454e72e9aa541c6)
- [NGSL 官方许可页](https://www.newgeneralservicelist.com/new-general-service-list)
- [NAWL 官方许可页](https://www.newgeneralservicelist.com/new-academic-word-list)
- [Kaikki 原始数据说明](https://kaikki.org/dictionary/rawdata.html)
- [Wiktionary 版权说明](https://en.wiktionary.org/wiki/Wiktionary:Copyrights)

BNC/COCA 25k Range 压缩包为 600,930 字节，SHA-256 为 `AC81C7A60E5C76CD2BBF0C59B0501808F0D4FA026B2936919DD54329A9BB6A69`。经匹配后有 12,945 个合格词头，但官方资源总览仅概括说明不同资源分别采用 CC BY-SA 4.0 或 GPL v2/v3；下载包和官方词表页没有把其中某项许可明确对应到这个文件。因此它没有进入词包。

## 数据处理与限制

- 全量扫描 10,913,997 条 Wiktextract JSONL 记录，其中 1,546,132 条是英语词条对象；覆盖扫描耗时约 71 秒。最终审计候选文件 `candidate-lexicon-v6.jsonl` 有 8,553,090 字节，SHA-256 为 `15869D3C9FC71BB6378500F192A5EB6CDB6C35068F1603F0277112EFE63D2A04`。审计原始输入和中间文件留在 `%LOCALAPPDATA%\Temp\lexio-vocab-audit-20261018`，未加入安装包。
- 词频名次优先采用 wordfreq 的准确词头位置；如果词头不在前 200k，再采用 FrequencyWords 的词头名次；词头本身未出现时才回退到匹配到的词形名次。wordfreq 数据按频率桶组织，同一桶内部按字母排序，因此 Lexio 里的顺序是词频近似次序，不是精确统计名次。
- 释义只保留有 POS 的 Wiktionary 中文翻译，POS 映射到 Lexio 简写；繁简并列时保留原文，例如 `給 /给`。未人工校订全部词义，没有补 IPA、例句或音频。
- 这是可离线学习的精选词表，不是“全部英语单词”；音频仍由系统语音提供。IELTS 列表也不是官方完整词表。
- 早期不排除 `name` POS 的 v4 审计曾显示 22,920 个常见词头；该数已被本报告的专名过滤与词形清理版本取代，未打包。

## 实现验证状态

数据资源已经加入 Core 项目，Core 测试新增了版本化导入、重复导入、追加导入、同名冲突、保留现有释义和学习进度，以及真实词包数量断言。此执行环境没有可用的 .NET SDK（机器上只有 .NET 运行时），因此尚未能运行测试或 Release 构建；代码层结果不能描述为已构建或已通过测试。发布输出必须在具备 .NET 10 SDK 的环境重新构建，并检查安装目录中的数据署名文件。
