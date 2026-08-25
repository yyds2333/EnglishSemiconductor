# WordPin

WordPin 是一个面向 Windows 11 x64 的桌面单词收集与复习工具。它提供置顶悬浮窗、快速收词、上下文保存、熟练度评估和间隔复习。

## 当前阶段

项目当前处于 **S0：规格收敛与关键决策**。代码开发将在熟练度算法、词典数据源和单词数据模型完成决策后进入完整实现。

## 平台基线

- Windows 11 x64
- 最低系统基线：Windows 11 24H2（OS build 26100）
- 构建目标：`win-x64`
- .NET SDK：10.0.400（仓库通过 `global.json` 固定）

## 开发前置条件

- 在 Windows 11 x64 上安装 .NET SDK 10.0.400。
- 构建机需要访问 `https://api.nuget.org/v3/index.json` 以还原 NuGet 依赖。
- 词典导入需要一个已完成许可复核的 ECDICT CSV 文件；仓库不附带第三方数据包。

## 文档

- [项目实施规划书](docs/specifications/WordPin_项目实施规划书_v1.0.md)
- [产品与开发规格书 v1.1](docs/specifications/WordPin_Windows单词学习工具_产品与开发规格书_v1.1.md)
- [需求—验收追踪表 v1.1](docs/specifications/需求—验收追踪表_v1.1.md)
- [ADR-001：平台与发布架构](docs/adr/ADR-001-平台与发布架构.md)
- [ADR-002：工程技术栈](docs/adr/ADR-002-工程技术栈.md)
- [ADR-003：词典数据源与隐私](docs/adr/ADR-003-词典数据源与隐私.md)
- [ADR-004：安装、升级与数据目录](docs/adr/ADR-004-安装升级与数据目录.md)
- [ADR-005：单词、词形、词义和短语模型](docs/adr/ADR-005-单词词形词义模型.md)
- [ADR-006：熟练度与复习调度算法](docs/adr/ADR-006-熟练度与复习调度算法.md)
- [ADR-007：SQLite 日志、备份与恢复](docs/adr/ADR-007-SQLite日志备份与恢复.md)
- [当前状态](docs/STATUS.md)

## 工作原则

1. 先锁定影响数据和隐私的决策，再实现界面。
2. 核心学习算法必须有可重复的自动化测试。
3. 收词先落本地数据，再进行网络查询。
4. 默认不持续监听剪贴板。
5. 只提交可构建、可验证的变更。

## 计划中的命令

代码骨架完成后，根目录将提供以下可重复命令：

使用仓库脚本执行构建和发布：

```powershell
.\tools\build.ps1
.\tools\build.ps1 -Test
.\tools\build.ps1 -Publish
```

发布完成后运行 `artifacts\publish\win-x64\WordPin.exe`。当前窗口始终置顶，支持主动读取剪贴板或手动输入并立即保存到 `%LOCALAPPDATA%\WordPin\wordpin.db`；应用不会持续监听剪贴板。

导入已下载并完成许可复核的 ECDICT CSV 数据包：

```powershell
.\tools\import-ecdict.ps1 `
  -CsvPath .\local-data\ecdict.csv `
  -DatabasePath .\local-data\dictionary.db `
  -ProviderVersion 2026-08-25
```

词典数据包不提交到代码仓库；使用前请保留上游来源、版本、SHA-256 和许可证记录。

## 故障排查

### `NETSDK1004` 找不到 `project.assets.json`

先运行 `dotnet restore WordPin.slnx --runtime win-x64 --configfile NuGet.Config --packages .nuget-packages`，再运行构建脚本。依赖源不可访问时，检查网络和 NuGet 源配置。

### 导入命令提示 CSV 文件不存在

确认 `-CsvPath` 指向实际文件，并先完成 ECDICT 数据包的下载、校验和许可证记录。导入命令不会自动下载第三方数据。

### SQLite 数据库被占用

关闭正在使用该词典库的 WordPin 实例后重试。导入命令应写入独立的临时/目标数据库，不要覆盖正在运行实例打开的文件。

仓库配置只使用 `nuget.org` 公共源；生产构建应在依赖版本锁定后使用锁文件或内部镜像复核供应链。
