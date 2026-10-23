# Lexio

Windows 原生、离线优先的智能背单词软件。.NET 10 + WinUI 3 (Windows App SDK 2.5) + SQLite(WAL/FTS5) + FSRS-6。

今日学习支持回忆、跟写、看义拼写、听写和自测；Library 可管理收藏、错词与已掌握卡片，并导入 CSV/TXT、TypeWords JSON 或本地 TypeWords GitHub ZIP 词书；Articles 支持纯文本、TypeWords JSON 和本地 TypeWords ZIP 导入、双语全文阅读、逐句跟写/听写和进度恢复。语音与数据处理均在本地完成。

## 构建
要求：.NET 10 SDK。本仓库开发时使用 10.0.402。

```bash
dotnet build Lexio.slnx -c Release
dotnet test tests/Lexico.Core.Tests
```

运行（unpackaged，无需 MSIX）：

```bash
dotnet run --project src/Lexico.App -c Release
```

数据位置：`%LOCALAPPDATA%\Lexio\lexio.db`。

## 文档
- [产品规格](docs/01-product-spec.md)
- [技术架构](docs/02-architecture.md)
- [数据库设计](docs/03-database.md)
- [算法说明](docs/04-algorithms.md)
- [数据源与许可证](docs/05-licenses.md)
