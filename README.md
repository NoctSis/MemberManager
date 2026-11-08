# 会员管家 / MemberManager

面向中小门店的 **Windows 单机会员管理与收银系统**：会员储值、次卡、开单收银、挂单、员工提成、交接班日结、积分商城、报表导出。全部数据存在本机 SQLite，**不联网、不上传、不需要服务器**。

界面用 WebView2 承载 React 前端，外壳是 WinForms，目标框架 .NET Framework 4.8（x86），最低支持 Windows 7。

> 本项目以 [Apache-2.0](LICENSE) 开源。第三方组件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

## 功能

- **会员**：建档、等级与折扣、余额与积分、标签、生日提醒、会员归档（不物理删除）
- **储值与次卡**：充值方案、赠送金额、次卡模板、售卡、核销、冻结/解冻
- **收银开单**：项目与商品、会员抵扣、次卡核销、多种支付方式（现金/微信/支付宝/余额）、找零
- **挂单**：暂存未结账单，点卡片取单；「清空」= 彻底作废（保留行、不删账）
- **提成**：按员工、按项目/卡核销计算，先对账再结算，幂等
- **日结与交接班**：营收按支付方式拆分，恒等式校验（余额 = 充值 + 赠送 − 消费 − 退款 ± 调整）
- **积分商城**：礼品维护、兑换扣减
- **报表**：营业汇总、员工业绩、消费明细，导出 Excel（文件落在程序目录 `exports\`）
- **权限**：店长 / 收银员 / 技师三角色 + 按人授权；敏感接口在服务端二次校验

## 技术栈

| 层 | 选型 |
| --- | --- |
| 外壳 | WinForms + WebView2（Microsoft.Web.WebView2 1.0.1518.46） |
| 前端 | React 18 + TypeScript 5 + Vite 5 + Tailwind CSS 3 |
| 业务 | C# 7.3 / .NET Framework 4.8，五个项目分层（Shell / BLL / DAL / Models / Common） |
| 存储 | SQLite（System.Data.SQLite 1.0.118，WAL + synchronous=FULL） |
| 通信 | 前端 `postMessage` ↔ C# `BridgeDispatcher` 方法分发 |
| 打包 | Inno Setup 6（per-user 安装，无 UAC） |

```
web/src (React)  --postMessage-->  MemberManager.Shell (WinForms/WebView2)
                                          |
                                   MemberManager.BLL (业务规则)
                                          |
                                   MemberManager.DAL (SQLite / DDL / 种子)
                                          |
                                   MemberManager.Models (DTO)
```

## 构建

### 环境要求

- Windows 10/11（编译 Win7 目标也可在 Win10 上完成）
- .NET Framework 4.8（运行时；编译由仓库自带的 Roslyn 完成，**不需要 Visual Studio**）
- Node.js 18+（仅前端需要）
- Windows PowerShell 5.1

### 1. 构建前端

```bat
cd web
npm install
npm run build
```

产物在 `web\dist\`。首次克隆后必须先做这一步，否则程序会缺界面（构建脚本会提示 `WARN web\dist not found`）。

### 2. 构建后端并汇总

```bat
build.bat
```

它调用 `tools\compile-inprocess.ps1`：加载 `tools\roslyn\` 里的 Roslyn 程序集在进程内编译五个项目（不启动 `csc.exe`/MSBuild），把产物、依赖 DLL、`web\dist`、清单与图标统一整理到 `beta\`。

### 3. 运行

```bat
beta\MemberManager.exe
```

首次启动自动建库。默认账号：

| 账号 | 密码 | 角色 |
| --- | --- | --- |
| 店长 | 123456 | 店长（全部权限） |
| 收银 | 123456 | 收银员 |

**首次登录后请立刻修改密码**（登录后右上角菜单）。程序未做代码签名，首次运行 SmartScreen 会提示，选择「更多信息 → 仍要运行」即可。

### 4. 打安装包（可选）

需要先安装 [Inno Setup 6](https://jrsoftware.org/isdl.php)，然后：

```bat
:: 轻量包：不打包 WebView2 运行时和 .NET 4.8，适合已装好运行时的 Win10/11
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DLIGHT=1 tools\_inno\setup.iss

:: 完整包：额外打包 WebView2 109 运行时 + .NET 4.8 离线安装程序（需先准备 deps\，约 500MB，不随仓库分发）
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" tools\_inno\setup.iss
```

`tools\_pack_release.py` 会把安装包和免安装版整理成三个 zip。

### 5. 冒烟测试

```bat
%windir%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\_smoke_runner.ps1
```

31 个脚本、380+ 条断言，覆盖 DDL 升级、余额恒等式、退款红冲、提成、挂单作废、权限拦截等。必须用 32 位 PowerShell，因为要加载 x86 程序集。

## 数据与隐私

- 数据库文件：`<程序目录>\data\member.db`（便携式，跟着 exe 走；备份在 `backups\`，导出在 `exports\`）
- 所有数据只存本机，**程序不包含任何联网代码**，不上传、不遥测
- 会员手机号等属于个人信息，请注意备份文件的保管：备份是明文 SQLite 拷贝，**未加密**

## 已知限制

- **备份不加密**：`backups\*.db` 是数据库原样拷贝，含全部会员资料，请勿随意外发
- **无单实例锁**：重复双击可能开出两个窗口（WAL 不会损坏数据库，但可能重复开单）
- **无自动更新**：升级需手动覆盖程序目录，数据会自动迁移
- **无单元测试工程**：目前靠 `tools\` 下的 PowerShell 冒烟脚本做回归
- **仅 Windows x86**：SQLite 原生互操作库为 32 位
- **Win7 需要固定版 WebView2**：只能用 109.0.1518.140 及更早版本，官方已停止对 Win7 的支持
- 默认密码为公开信息，部署后必须修改

## 目录结构

```
src/                    C# 五个项目
  MemberManager.Shell   WinForms 外壳、WebView2、BridgeDispatcher
  MemberManager.BLL     业务服务（收银、会员、提成、报表、权限）
  MemberManager.DAL     SQLite 访问、DDL、建库与升级
  MemberManager.Models  DTO
  MemberManager.Common  路径、运行形态识别等公共件
web/                    React 前端（src 为源码，dist 为构建产物）
lib/ libs/              编译依赖的第三方 DLL
assets/                 应用与安装包图标
tools/                  构建、打包、冒烟脚本；tools/roslyn 为内置编译器
  _inno/setup.iss       Inno Setup 安装脚本
```

## 参与贡献

欢迎提 Issue 与 Pull Request。提交前请：

1. 跑一遍 `tools\_smoke_runner.ps1`，确保 0 FAIL
2. 前端改动跑 `npx tsc --noEmit`
3. 数据库结构变更只做**增量**（新增列/表用 `ALTER TABLE`/`CREATE TABLE IF NOT EXISTS`），不要改动已有 DDL，老库必须能平滑升级
4. 涉及金额的逻辑请保持恒等式：余额 = 充值 + 赠送 − 消费 − 退款 ± 调整

安全问题请见 [SECURITY.md](SECURITY.md)，不要直接开公开 Issue。

## 许可证

[Apache License 2.0](LICENSE)。"会员管家" 为项目中文名，不构成商标声明。
