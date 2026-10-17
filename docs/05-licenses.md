# 数据源与第三方许可证

## 词典数据源（构建管线，后续阶段接入）
| 来源 | 内容 | 许可证 | 使用策略 |
|---|---|---|---|
| kaikki.org（Wiktionary 提取） | 英文词条、IPA、释义、例句、同义/反义 | CC BY-SA 4.0 | 离线工具转换为发行词典库需**署名 + 同协议**；随包附带 NOTICE 与源链接，衍生数据库以 CC BY-SA 4.0 提供 |
| WordNet 3.1 | 英文语义关系、定义 | Princeton WordNet License（类 BSD） | 可自由再分发，保留许可证声明 |
| CMUdict | 美音 ARPAbet | BSD-2-Clause | 可再分发，保留版权声明 |
| 词频 | Wiktionary 频率表 / SUBTLEX | CC BY-SA / 学术免费 | 仅在构建期使用，产物仅含排名数值 |

**V1 策略**：安装包内置的启动词库为项目自编内容（< 200 词，原创释义与例句，无第三方版权）；词书（四六级等）由用户经 CSV/导入管线自行构建，规避再分发问题。导入工具按上表注明署名要求。

## 第三方组件
| 组件 | 许可证 |
|---|---|
| Windows App SDK / WinUI 3 | MIT（源码）+ WinUI 运行时随包分发许可 |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Data.Sqlite（含 SQLite 原生库） | Apache-2.0 / SQLite Public Domain |
| FSRS.Core 1.0.7 | MIT |
| FSRS 算法本身 | 公开研究算法（open-spaced-repetition 社区） |

发布时在应用内「关于/开源声明」页汇总以上条目。
