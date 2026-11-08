# 第三方组件声明 / Third-Party Notices

本项目（会员管家 / MemberManager）以 **Apache License 2.0** 发布，见 `LICENSE`。
下面列出**随本仓库源码一起分发**的第三方二进制组件，以及**构建或运行需要用户自行安装**的外部依赖。

---

## 一、随仓库分发的第三方组件

| 组件 | 文件 | 版本 | 许可证 | 来源 |
| --- | --- | --- | --- | --- |
| System.Data.SQLite（ADO.NET 提供程序） | `lib/SystemDataSQLite/System.Data.SQLite.dll` | 1.0.118.0 | Public Domain | https://system.data.sqlite.org/ |
| SQLite 原生互操作库（x86） | `lib/SystemDataSQLite/x86/SQLite.Interop.dll` | 1.0.118.0 | Public Domain | https://sqlite.org/ |
| Json.NET（Newtonsoft.Json） | `libs/Newtonsoft.Json.dll` | 13.0.4 | MIT | https://www.newtonsoft.com/json |
| Microsoft Edge WebView2 SDK | `libs/Microsoft.Web.WebView2.Core.dll`<br>`libs/Microsoft.Web.WebView2.WinForms.dll`<br>`libs/WebView2Loader.dll` | 1.0.1518.46 | Microsoft 软件许可条款（允许随应用再分发） | https://developer.microsoft.com/microsoft-edge/webview2/ |
| Inno Setup 简体中文语言文件 | `tools/_inno/ChineseSimplified.isl` | 随 Inno Setup 6 | Inno Setup License（允许再分发） | https://jrsoftware.org/isinfo.php |

这些 DLL 是**编译硬依赖**（各 `.csproj` 的 `HintPath` 直接指向它们），因此随源码提供。
若你需要替换为其他版本，请自行从上述官方渠道获取并更新 `lib/` 或 `libs/` 下的文件。

### 各许可证要点

- **SQLite / System.Data.SQLite**：SQLite 内核与 System.Data.SQLite 均声明为公有领域（Public Domain），可自由使用、修改、分发，无附加条件。
- **Json.NET**：MIT 许可证，保留版权与许可声明即可。
- **WebView2 SDK**：受 Microsoft 软件许可条款约束。它**不是** MIT/Apache 类许可，再分发时请保留原始声明，并遵守微软对 WebView2 运行时的分发要求。
- **Inno Setup 语言文件**：Inno Setup 允许在安装包中附带其语言文件；修改后的语言文件需注明来源。

---

## 二、构建时需要用户自行安装（不随仓库分发）

| 组件 | 用途 | 获取方式 |
| --- | --- | --- |
| .NET Framework 4.8 Developer Pack | 编译目标框架（`net48`） | https://dotnet.microsoft.com/download/dotnet-framework/net48 |
| MSBuild 或 .NET SDK | 编译 C#（`build.bat` 使用） | 随 Visual Studio / Build Tools / .NET SDK 提供 |
| Node.js 18+ 与 npm | 构建前端（Vite + React） | https://nodejs.org/ |
| Inno Setup 6 | 生成 Windows 安装包（`tools/_inno/setup.iss`） | https://jrsoftware.org/isdl.php |

## 三、运行或打包时需要用户自行准备（不随仓库分发）

| 组件 | 用途 | 说明 |
| --- | --- | --- |
| Microsoft Edge WebView2 Runtime | 渲染前端界面 | Windows 11 与较新的 Windows 10 已内置；旧机器需单独安装。本项目的 Win7 离线发行版自带 109.0.1518.140 内核，源码仓库不含该运行时（约 200 MB）。 |
| .NET Framework 4.8 运行时 | 运行程序 | Windows 10 1903+ / Windows 11 已内置；Windows 7 需手动安装。 |

上述运行时由微软免费提供，请从官方渠道下载。仓库与发行包中不再分发它们的离线安装程序，以避免许可与体积问题。

---

## 四、开发辅助工具（未随公开仓库分发）

原开发环境使用 `tools/roslyn/`（Roslyn 编译器程序集）在受限环境下做进程内编译，以及 `tools/wv2diag/`（WebView2 诊断工具）。二者均为第三方工具链，**不在公开仓库中**；公开版本直接使用 MSBuild / `dotnet build`。

---

如发现遗漏的第三方组件或声明有误，请提交 Issue。
