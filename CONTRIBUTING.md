# 参与贡献

谢谢你有兴趣改这个软件。它是一套真实在门店里跑收银的系统，所以「改完不炸账」比「写得漂亮」更重要。下面几条不是形式，是这个项目踩过坑之后留下的硬规矩。

---

## 一、先把项目跑起来

```bat
:: 前端
cd web
npm install
npm run build

:: 后端（进程内编译，不需要 Visual Studio）
cd ..
build.bat

:: 运行
beta\MemberManager.exe
```

默认账号 `店长 / 123456`。

**注意**：`build.bat` 会清空并重建 `beta\`，所以**不能一边开着软件一边编译**（exe 被占用会导致最后一步失败，产物不完整）。先关软件。

---

## 二、提交前必须做的检查

```bat
:: 1) 全量冒烟，必须 0 FAIL
%windir%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\_smoke_runner.ps1

:: 2) 前端类型检查
cd web
npx tsc --noEmit
```

冒烟必须用 **32 位 PowerShell**（`SysWOW64`），因为要加载 x86 程序集。用 64 位跑会直接报错。

---

## 三、五条不能破的规矩

### 1. 数据库只许增量改

`Sql.Ddl` 里的建表语句**一个字都不要改**。加列用 `ALTER TABLE ADD COLUMN`，加表用 `CREATE TABLE IF NOT EXISTS`，统一放在 `DatabaseInitializer.PatchSchema` 里。

原因：门店的老库里有几万条真实流水，升级必须幂等、必须无损。改已有 DDL 会让老库升不上来。

### 2. 钱必须有恒等式

```
余额 = 充值 + 赠送 − 消费 − 退款 ± 调整
Σ payments = 订单应收
```

任何改动都不能破坏这两条。做不平就出错、出差异清单，**绝对不允许「悄悄平账」**。

另外记住几个已经定死的口径：
- `payments` 只记资金流，**次卡核销不落 payments**
- 业绩 ≠ 营收：营收按实收，业绩按项目原价（含次卡抵扣），差额是次卡抵扣额
- 流水和日志只增不删，更正走红冲（唯一例外是操作日志按保留期清理）

### 3. PowerShell 脚本必须纯 ASCII

`tools\*.ps1` **不能出现任何中文，包括注释**。PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI 解码，中文字符错位后，用码点拼出来的断言字符串会静默失配——表现是「数据明明是对的，测试却报错」，非常浪费时间。

检查：

```bash
LC_ALL=C grep -nP '[\x80-\xFF]' tools/*.ps1
```

有输出就说明混进了非 ASCII。中文提示一律放到 C# 侧或交给前端。

### 4. 前端权限要在后端再校验一次

前端按权限隐藏按钮只是体验，不是安全。任何写操作都要在 `BridgeDispatcher.SensitiveWrites` 里登记「方法 → 需要的模块」，由 `PermissionService.RequireModule` 在服务端拦一道。

### 5. 界面有版式约定

- 最小窗口 1280×800，可用宽度约 1080px，超宽表格走内部滚动
- 每页最外层 `max-w-[1400px] mx-auto`
- 长列表用统一的分页条，不要自己造轮子
- 界面里不写说明书式长文案，一屏最多两层框

---

## 四、代码风格

- C# 用 **C# 7.3**、**.NET Framework 4.8**、目标 **x86**。别用 `.NET 8` 的语法（`record`、`init`、文件级命名空间等），编译不过。
- 不引新第三方库。现有依赖只有 System.Data.SQLite、Newtonsoft.Json、WebView2 SDK，加一个就多一份再分发义务。
- 注释写「为什么」而不是「做了什么」。遇到非直觉的处理（比如某个看似多余的判断），说明它挡住了什么线上问题。
- 前端 TypeScript 严格模式，`any` 尽量别用；提交前 `tsc --noEmit` 必须过。

---

## 五、提交信息

一句话说清「改了什么 + 为什么」，需要时补两行细节。例：

```
修复挂单取回后被重复计入当日营收

取单后原挂单行仍留在统计里，导致笔数多算一次。
过滤条件补 voided_at 与 status。
```

---

## 六、提 Issue 时请带上

1. 系统版本（Win7 SP1 / Win10 / Win11，32 还是 64 位）
2. 软件版本（设置页「关于」里有）
3. 复现步骤：从哪个页面、点了什么、期望什么、实际什么
4. 报错截图（**发之前把会员姓名手机号打码**）
5. 相关日志：程序目录 `logs\` 下当天的文件

只提「用不了」「有 bug」很难定位，谢谢理解。

---

## 七、安全问题

不要开公开 Issue。按 [SECURITY.md](SECURITY.md) 里的方式私下反馈。
