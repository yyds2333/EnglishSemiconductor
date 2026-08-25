# WordPin

WordPin 是一个面向 Windows 11 x64 的桌面单词收集与复习工具。它提供置顶悬浮窗、快速收词、上下文保存、熟练度评估和间隔复习。

## 当前阶段

项目已完成 v0.1.0 发布，正在开发 **v0.2.0：本地释义管理与 AI 释义补全**。

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
- [v0.2.0：本地释义与大模型补全实施计划](docs/specifications/WordPin_本地释义与大模型补全实施计划_v1.0.md)
- [当前状态](docs/STATUS.md)

## 工作原则

1. 先锁定影响数据和隐私的决策，再实现界面。
2. 核心学习算法必须有可重复的自动化测试。
3. 收词先落本地数据，再进行网络查询。
4. 默认不持续监听剪贴板。
5. 只提交可构建、可验证的变更。

## 常用命令

使用仓库脚本执行构建和发布：

```powershell
.\tools\build.ps1
.\tools\build.ps1 -Test
.\tools\build.ps1 -Publish
.\tools\build.ps1 -Installer
```

`-Installer` 会先生成 `win-x64` 自包含发布目录，再调用 Inno Setup 6 的 `iscc.exe` 生成 per-user 安装包；安装器未安装时命令会明确失败并提示安装依赖。

运行 `artifacts\publish\win-x64\WordPin.exe` 或安装包中的 WordPin。当前窗口始终置顶，支持 `Ctrl+Shift+D`（先复制、后按快捷键）主动读取剪贴板，或手动输入并立即保存到 `%LOCALAPPDATA%\WordPin\wordpin.db`；应用不会持续监听剪贴板。每次保存后提供 5 秒撤销。

主窗口支持“导入 CSV 词典”和“AI 设置”。本地释义缺失时，如果已配置并启用 AI 补全，应用会异步生成带“AI 生成 · 未确认”标记的候选；点击“编辑并采用”后才保存为用户释义。AI Key 使用 Windows 当前用户 DPAPI 加密保存，不写入 SQLite 或日志。

AI 设置使用 OpenAI 兼容的 `/chat/completions` 接口，Base URL 必须为 HTTPS，默认每日最多请求 30 次。同一个单词的未确认候选会缓存 24 小时；网络失败不会阻止收词和复习。

用户释义保存在 `%LOCALAPPDATA%\WordPin\wordpin.db`，可通过“编辑释义”修改或通过“恢复本地释义”删除用户覆盖。词典导入会先写入 staging 数据库，成功后备份并替换 `%LOCALAPPDATA%\WordPin\dictionary\dictionary.db`。

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
