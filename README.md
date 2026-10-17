# Lexio

Windows 原生、离线优先的智能背单词软件。.NET 10 + WinUI 3 (Windows App SDK 2.5) + SQLite(WAL/FTS5) + FSRS-6。

## 构建
要求：.NET 10 SDK。本仓库开发时使用 10.0.402。

```bash
dotnet build Lexio.sln -c Release
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
