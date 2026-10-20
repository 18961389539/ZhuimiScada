# PLCScada

.NET 10 工业 SCADA 分层工程，Blazor Web App / Interactive Server、MudBlazor、IoTClient、EF Core / SQLite、Serilog。

## 本次交付范围

这是开发提示词中**第一轮**的可运行版本，并提前实现了基础 Tag 采集、通信报警和实时输出预览。以下项目已经接通真实后端，并非静态展示。

| 模块 | 本轮功能 |
| --- | --- |
| 基础工程 | 五项目解决方案、DI、Interactive Server、MudBlazor 导航、深浅色切换、响应式布局 |
| Devices | 对话框新增/编辑/删除、搜索/排序/分页、服务端校验、启用/禁用、手动连接/断开、FC03 通信测试、自动重连 |
| 数据库 | 六张业务表、初始 Migration、自动迁移、名称唯一约束、外键 Restrict、配置变更审计 |
| 通信 | IoTClient 单次请求传输、每设备独立连接与访问锁、异步后台轮询、取消/超时、状态及错误限流 |
| 模拟器 | 开发模式本地 TCP 服务、全部八种功能码、变量变化、写入接收、界面启停 |
| Tags | 核心配置模型、CRUD、设备筛选、实时值/质量/时间、手动读取、独立内存 TagRuntime、订阅释放；**String 类型**（寄存器数可配、ASCII/UTF-16 解码、只读） |
| Trace | 追溯 SN：当前扫码内容（PLC String Tag）实时显示与历史出现统计、来源配置、手动打点（可填备注，作为快照绑定键）；**SN 流水列表已移除**（后台仍记录用于绑定）；**触发快照**（系统仅保留一条规则：线圈 Bool 为 true 时抓取多选点位并保存快照、随后自动复位；快照与同次读取的 SN 记录绑定；设置以单卡片维护，含最近触发时间）；后台每秒检测 SN 变化与触发位 |
| Recipes | 按设备隔离的配方 CRUD/复制、数值与 Bool/Coil 设定项、版本冲突保护、下发预览、Modbus 写入、读回验证/失败回滚和操作审计 |
| HMI | 以 TagId 绑定的 TextDisplay 实时输出预览、Tag 搜索/选择、显示精度；当前预览设置不持久化 |
| Dashboard | 未处理报警概览（Top10 + 未确认/RTN/搁置统计 + P1 高亮），一键进入报警管理 |
| Alarms / Logs | 通信异常报警、自定义报警定义（位报警取 M/Coil 点位、枚举报警取整数状态字/故障码并支持 `5=急停, 6=堵转` 值文案与 `{label}` 占位符，持续秒数去抖与消息模板）；**优先级 P1–P4**、**未确认恢复（RTN）仍需确认**、确认备注、同源 60 秒泛滥合并计数、**搁置（Shelving，1–1440 分钟带理由与到期自动恢复、全程审计）**、**工况抑制（按设备拦截新报警，1–1440 分钟带理由、到期自动失效）**；**全局未确认报警徽标**（顶部常驻，P1 红色高亮，5 秒刷新，点击直达）；**统计分析**（区间报警总数/平均确认耗时/确认率/活动/未确认/搁置/已恢复未确认、Top10 来源、>24h 常驻报警，可导出 CSV）；页面按优先级排序、可按优先级/来源/状态筛选；运行日志服务端分页 + 级别/来源/时间筛选 + CSV 导出 + 手动按保留策略清理 |
| Settings | 启动自愈：每次启动在迁移后自动把全库小写 GUID 文本统一为大写（`SqliteGuidNormalizer`，动态扫描、幂等）；开发模拟器启停；**数据保留策略编辑**（…持久化到数据库并写审计…）；**全局 Tag 扫描间隔**（250–60000ms，持久化到数据库；所有 Tag 统一使用，实际采集周期不小于设备轮询周期）与**全局质量报警阈值**（连续读取失败次数，0=关闭，Tag 级逐点配置已移除）；**运行状态监控 Tag**（仅 Bool 类型 Tag 可选，也可选“不启用”；true 时顶部状态灯显示“运行中”，false“已停止”；保存写审计）；立即按策略清理；存储与运行信息（数据库路径与大小、日志目录、设备在线数、应用版本） |

**后续阶段尚待实现**：HMI 页面编辑和布局保存、全部八种 HMI 输入/输出控件、身份认证与角色授权。数据库中的 HmiPages/HmiControls 为后续功能建立了真实约束，本轮不宣称已有完整 HMI 编辑器。

## 模块增强（第二轮迭代 · 2026-10-16）

在保持第一轮能力不降级、全部既有测试通过的前提下，补齐三个模块的成熟 SCADA 能力。目标契约与逐批验证记录：`GOAL.md`（配方）、`GOAL-Tags.md`（变量）、`GOAL-Manual.md`（手动操作）。

| 模块 | 新增能力 |
| --- | --- |
| Recipes | 修订变更说明（随版本归档，恢复/复制/导入自动标注来源）；结构化下发记录（只读审计表，删除配方不摧毁记录）；版本对比 Diff（新增/移除/修改 + Tag 签名变化备注 + 名称/说明/变更说明变化）；参数自定义排序（快照按序保存，旧数据回退名称排序）；批量导出/导入包（`PLCScada.RecipeBundle`，重名自动「（导入 n）」后缀、逐项结果报告、导入前预览确认）；列表按设备筛选；配方识别（读取设备当前配置与各配方逐项比对，识别“当前 PLC 在用哪个配方”，只读操作） |
| Tags | 限值报警（高/低限 + 死区滞回、超限记录、恢复自动解除，确认语义与通信报警一致）；**质量报警**（连续 N 次读取失败，阈值可配、默认关闭、恢复自动解除，与限值报警独立来源）；受控写入（权限/量程/编码/设备在线校验、跨实例设备锁、写入读回验证、审计日志）；地址冲突检测提示（同设备同地址类型区间重叠，仅提示不阻断别名用法）；批量导出/导入（`PLCScada.TagBundle` v1，全字段、逐项校验与报告）；连续地址合并采集（数值寄存器与位线圈均按连续区间分组单次读取：寄存器跨度 ≤120、位跨度 ≤512，空隙自动拆组，附与逐读的等价性测试）；批量启用/禁用（多选 + 逐项结果报告） |
| Manual | 脉冲输出模式（服务端保证 写 1 → 延时 → 无条件复位，浏览器断开也会复位；50–10000 ms 可配）；一键「全部归零」（单点失败不中断、逐项结果）；按钮自定义排序（↑/↓ 持久化）；输出写入失败进审计 |
| 通信 | 审计/日志等记账写入改为 best-effort：写入失败仅告警，不再把读取成功的设备误标为故障（并发/存储异常下的假故障防护） |
| 运维/数据 | 数据保留清理（策略可在设置页编辑并持久化到数据库 `ScadaSettings`，代码默认值兜底；启动与每 6 小时执行，清理动作写审计；未解除与已确认未解除的报警一律保留）；运行日志页服务端查询（关键词/级别/来源/时间筛选、分页与跳页、CSV 导出、手动立即清理） |

## 快速启动

需要 .NET 10 SDK。工程使用 `global.json` 选择兼容的 .NET 10 SDK，并提交 NuGet 锁文件和 EF 本地工具清单。

在解决方案目录执行：

```powershell
dotnet restore PLCScada.sln
dotnet build PLCScada.sln --no-restore
dotnet test PLCScada.sln --no-build
dotnet run --project PLCScada.Web --launch-profile http
```

打开 <http://127.0.0.1:5188>。或者使用 `./scripts/Start-Development.ps1`。

首次 Development 启动自动应用 Migration；当设备表为空且 `Simulator:SeedDemo=true` 时建立一台清楚标记为 SIM 的设备及两个只读 Tag。删除所有设备后重启仍会重新创建演示配置；如不需要，关闭该设置。其他设备配置不受演示初始化修改。

SQLite 保存至 `PLCScada.Web/App_Data/plcscada.db`，程序每次启动会恢复已有配置。设备连接状态和 Tag 当前值仅存在内存，不逐轮写数据库。Serilog 文件位于 `App_Data/logs/`，按天/10 MB 滚动、最多保留 14 个文件；业务审计单独保存在 OperationLogs。SQLite 历史按保留策略自动清理（`Retention` 配置节：操作日志默认 30 天、已解除报警 90 天、配方下发记录默认永久保留，启动时与每 6 小时执行一次，清理动作自身写审计）。

默认仅绑定本机，`AllowedHosts` 为 localhost/127.0.0.1。`本地操作员` 是本轮固定身份标签，不代表已实现登录认证。正式接入网络和生产设备前，应在后续阶段完成认证、授权及部署配置。本项目用于监控与非安全关键控制，不能替代 PLC 互锁、急停或硬件安全回路。

## 十分钟验收

1. 在 Dashboard 查看 SIM 设备在线及两个实时 Tag。
2. Devices → 新增设备：名称 `TestPLC`、IP `127.0.0.1`、端口 `15020`、Unit ID `1`、健康检查地址 `10`，保存。
3. 点击该行“测试通信”。Toast 应显示 `Modbus FC03 响应有效，寄存器[10] = 1234`。这是真实 Modbus 往返，不是单纯 TCP 端口探测。
4. 编辑名称/描述并保存，刷新浏览器或重启程序，确认配置保留。删除 TestPLC；存在关联 Tag 的设备会被拒绝删除。
5. Tags 中手动读取 SIM.Temperature，查看当前值、单位、Good 和更新时间。可新增自己的变量配置。
6. HMI 中选择已有 Tag，观察绑定值自动更新，修改精度。控件通过 TagRuntime 订阅，不直接创建连接。
7. Settings → 停止模拟器；Devices 应出现通信故障，Tag 变为 Stale/Bad，Alarms 出现一条新报警。可以人工确认。
8. Settings → 启动模拟器；等待轮询，设备自动恢复在线、报警自动解除。
9. 手动“断开”设备，等待数秒应保持断开；“测试通信”不改变手动断开状态。点击“连接”恢复自动采集。禁用设备也不会自动连接。
10. 点击顶部主题按钮验证深浅色切换；Logs 可查看上述操作的真实记录。

## 模拟器

仅在 Development 且 `Simulator:Enabled=true` 时启动，监听 `127.0.0.1:15020`，Unit ID 固定为 1。模拟器不会被注入为虚假的客户端：采集端通过真正 TCP Socket 收发 Modbus 报文。

| 零基地址 | 行为 |
| --- | --- |
| HoldingRegister[0] | 模拟温度原始值约 170～330，每秒变化；示例 Tag Scale=0.1、单位 °C |
| Coil[1] | 每秒切换 |
| HoldingRegister[10] | 初始值 1234，适合通信验收 |
| 其他 Coil/HoldingRegister | 初始为 0，可使用外部 Modbus 工具写入并读取 |

模拟器提供 65536 个位和寄存器，FC02 映射到模拟位内存，FC04 映射到模拟寄存器内存。FC05/06/15/16 写入在进程内保留（重启进程清空，模拟地址 0/1 会被变化任务覆盖）。不正确的站号、数量、地址、功能码返回协议异常。Settings 启停服务用于断线测试。

`appsettings.Development.json`：

```json
{"Simulator":{"Enabled":true,"Port":15020,"SeedDemo":true},"DetailedErrors":true}
```

端口占用时 Web 仍可启动，Settings 显示具体错误。修改端口后需要同步编辑已保存的 SIM 设备配置；初始化不会静默覆盖已有设备。

## 通信与地址规则

- 所有配置与内部请求使用零基地址：传统 Holding Register `40001` → HoldingRegister 地址 `0`；不要直接输入 `40001` 代替该偏移。
- 每台设备的 FC03 健康检查地址可配置；真实设备必须支持该地址。仅 TCP 可连接、但 Unit ID/寄存器不正确，测试仍然失败。
- `PollInterval` 与 `ScanInterval` 均为毫秒，实际 Tag 读取频率不会快于设备周期。数值寄存器 Tag 在同一采集周期内按连续地址分组合并为单次多寄存器读取（跨度 ≤120 寄存器、空隙 >8 寄存器自动拆组，重叠区间按并集读取）；**位/线圈同理合并**（一次 FC01/FC02 读一段连续位，跨度 ≤512 位、空隙 >8 位拆组）。仍不建议直接扩展至大量高频 Tag，需按现场点位规模评估。
- 底层适配器支持 FC01/02/03/04/05/06/15/16，已通过本地 TCP 集成测试。本轮页面只有读取与配置功能，没有绕过 Tag 服务的设备写入入口。
- IoTClient `Read(..., byteFormatting:false)` 仍会反转数据，且常规 Read/Write 包含隐式重连重试。本适配器使用其命令生成器和 `SendPackageSingle`，统一处理网络字节序、MBAP/事务号/站号/功能码/长度与写回执校验，避免隐式重放。FC15 的 PDU 由本工程构造，同样使用 IoTClient Socket 传输。
- IoTClient Socket API 是同步的；适配器以 Task.Run 隔离并等待真实调用结束。取消令牌在排队和调用前后检查，不能即时打断已经进入的同步 Socket 调用；正在执行的操作受设备通信超时约束，不会提前释放访问锁去复用仍在运行的连接。
- 连接失败由设备独立工作任务按周期重试；故障设备不会阻塞其他设备；相同设备的后台读取与手动读取使用同一锁。
- ByteOrder 控制每个 16 位寄存器内字节顺序，WordOrder 控制多字寄存器顺序。默认高字节/高字在前；缩放公式 `工程值 = 原始值 × Scale + Offset`。Bool 不缩放。
- 运行值质量为 Good/Bad/Uncertain/Stale；通信故障保留上次值及其真实采集时间，不冒充新采集成功。当前只读输出会降低异常值显示亮度。

## 工程结构

```text
PLCScada.sln
├─ PLCScada.Core            领域配置、运行快照、枚举、服务契约
├─ PLCScada.Application     DeviceService、TagService、TagRuntime、类型解码
├─ PLCScada.Infrastructure  EF Core/SQLite、Migration、IoTClient 适配、轮询、模拟器
├─ PLCScada.Web             Blazor 页面、MudBlazor 布局/对话框、Tag 输出组件
└─ PLCScada.Tests           迁移/约束、服务规则、协议、模拟通信、并发与控件绑定测试
```

依赖方向：Application → Core，Infrastructure → Application/Core，Web → Application/Infrastructure。页面不引用 DbContext 或 Modbus 客户端。测试项目额外引用 Web 以验证真正的 Razor 输出组件。

重要文件完整清单见 [docs/phase1-files.md](docs/phase1-files.md)，验收记录见 [docs/verification.md](docs/verification.md)。

## 数据库与迁移

默认启动路径自动 `MigrateAsync()`，不使用 EnsureCreated。十一张表为 Devices、Tags、ManualOutputPoints、ManualOutputBindings、HmiPages、HmiControls、Recipes、RecipeParameters、RecipeVersions、AlarmRecords、OperationLogs；Device、Tag、Recipe 名称不区分 ASCII 大小写唯一，中文按原文比较。删除关联配置同时受服务规则和数据库 Restrict 约束保护。配方仅能绑定同一设备上启用的可写数值寄存器与未绑定手动输出的 Bool/Coil；每次保存都会在同一事务中留存不可变版本快照，升级迁移会为现有配方补当前版本基线。历史版本可查看并恢复为新的修订号；只有设备连接目标和 Tag 关键映射仍与快照一致时才允许恢复。下发前要求设备在线并逐项预览，写入后读取验证，失败时尝试按原值回滚。Modbus 多地址下发不是设备侧原子事务，现场仍须配置 PLC 联锁。

配方下发同时使用进程内互斥和本机文件句柄锁；进程异常退出时操作系统会释放文件锁。默认锁目录位于 SQLite 文件旁，多个实例若要共同保护同一 PLC，必须配置相同的 `RecipeApply:LockDirectory`（例如环境变量 `RecipeApply__LockDirectory=C:\ProgramData\PLCScada\recipe-apply-locks`）并确保运行账户有读写权限。此机制只针对共享本机文件系统的应用实例，不构成跨主机/集群锁；不同地址别名也可能被视为不同资源。身份认证与配方操作授权尚未实现，未经现场安全评审和 PLC 联锁验证不得视为生产安全控制。

建立后续迁移：

```powershell
dotnet tool restore
dotnet ef migrations add YourMigration --project PLCScada.Infrastructure --output-dir Persistence/Migrations
```

设计时工厂只用于建模，连接目标是当前目录的 `plcscada.design.db`。直接运行默认 `dotnet ef database update` 会操作设计数据库，并非 Web 运行库。应用迁移请启动 Web，或显式提供 `--connection` 指向目标 SQLite 文件。停机后备份 App_Data 目录可保留配置和历史。

## 测试与打包

```powershell
./scripts/Test.ps1
./scripts/Package.ps1
```

测试不依赖真实 PLC：使用临时 SQLite 文件与动态端口的真实 Modbus TCP 模拟器，并在完成后释放端口和数据库。覆盖 CRUD/审计事务、外键与唯一约束、无效配置、数据类型和字节/字顺序、FC01/02/03/04/05/06/15/16、协议异常响应、禁止隐式写重放、断线恢复、手动断开、故障隔离、取消、多客户端并发、订阅释放和真实 Razor 组件的 TagId 绑定。

ZIP 默认输出 `artifacts/PLCScada-phase1.zip`，包含工程、锁文件、迁移、文档与测试源码；不包含 bin/obj、真实运行数据库、日志、测试临时文件或本机配置。解压后按快速启动步骤运行。

依赖 API 参考：[MudBlazor 安装文档](https://mudblazor.com/getting-started/installation)、[IoTClient 源码](https://github.com/zhaopeiym/IoTClient)。
