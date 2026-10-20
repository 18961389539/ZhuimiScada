# 第一轮验收记录

验收日期：2026-10-15（Asia/Shanghai）。范围为 README 中的本轮功能，未使用真实 PLC。

## 命令验证

- 已执行 `dotnet restore PLCScada.sln`。
- 已执行 Debug 和 Release 构建，最后 Release 构建为 **0 警告、0 错误**。
- 已执行 `dotnet ef migrations add InitialCreate --project PLCScada.Infrastructure --output-dir Persistence/Migrations`，迁移源码已提交。
- 已执行 `scripts/Test.ps1`，Release 测试 **38 通过、0 失败、0 跳过**。
- 测试结果保存在本机 `artifacts/test-results/phase1.trx`；源码 ZIP 不打包测试临时输出。
- 本机已使用 `dotnet run --project PLCScada.Web --launch-profile http` 启动，端口 5188；Development 模拟器端口 15020。

## 自动测试分布

| 文件 | 覆盖点 |
| --- | --- |
| PersistenceTests.cs | Migration 可重入、六张表、设备 CRUD/审计、大小写名称冲突、设备/Tag 关联删除限制、非法设备和 Tag 配置 |
| CodecTests.cs | 六种数值类型、Bool、四种字节/字组合、Float64 字序、缩放、非法长度和 NaN |
| CommunicationTests.cs | 真实 FC03 通信探测、错误 Unit ID、八种功能码与写入读回、报警确认/恢复、新故障不受日志限流抑制、手动断开暂停重连、取消、24 个并发读取者和 8 个订阅者、订阅释放、多设备故障隔离、后台自动重连与停止释放 |
| ProtocolValidationTests.cs | 错误事务号、站号、功能码、字节计数、写回执不匹配被拒绝，写入执行后丢失响应不会自动重连/重放 |
| HmiBindingTests.cs | 使用 ASP.NET HtmlRenderer 渲染实际 TagTile.razor，按 TagId 订阅，接收真实 TCP 采集值，验证单位/精度/质量与离线显示 |

## 浏览器实际操作

- Dashboard 正常打开，SIM 设备在线，真实变量持续更新。
- 新增临时设备 `UI-CRUD-VERIFIED`，地址 127.0.0.1:15020、Unit 1、零基健康检查地址 10，保存后出现在列表。
- 编辑已有验收设备名称、描述、端口和健康检查地址，保存后列表更新；重启后原有设备配置仍存在。
- “测试通信” Toast 实际显示 `Modbus FC03 响应有效，寄存器[10] = 1234`。
- 删除验收临时设备并验证列表移除；未删除示例设备及原有配置。
- Tags 显示真实 Good 值、单位和更新时间；“手动读取”显示读取成功。
- Settings 停止模拟器，设备离线数增加，Alarms 显示真实 Active 报警；人工确认后状态变为 Acknowledged。
- 重新启动模拟器，自动恢复在线；HMI 绑定预览显示 SIM.Running、Good 和持续切换的 ON/OFF。
- 最终临时验收设备已清理，开发示例保留。

界面截图位于本机 `artifacts/screenshots/`，不放入源码 ZIP。

## 已知边界

- 未对真实 PLC、厂商寄存器表、硬件字节序和现场网络做验证。
- 当前身份是固定“本地操作员”；尚无登录/角色系统。
- 通信低层写入已做本地 TCP 测试，但本轮没有开放 HMI/Tag 写入入口。
- 本轮按 Tag 分别读取，未做连续地址批量优化和大规模负载测试。
- HMI 页面当前是输出绑定预览；完整编辑与输入控件、布局持久化属于后续阶段。
- 日志文件已滚动限量；SQLite 历史暂未配置保留期。
- 已执行的同步 IoTClient 调用不能被令牌立即中断，需等待 Socket 超时；适配器会等待调用结束并安全释放连接。

## 第二轮迭代验收记录（2026-10-16）

范围：配方管理、Tags、手动操作三个模块的成熟度增强。目标契约与逐批进度见仓库根目录 `GOAL.md`、`GOAL-Tags.md`、`GOAL-Manual.md`（含需求清单、完成定义、停止条件与 Non-Goals）。

- `dotnet build PLCScada.sln -c Release --no-incremental`：**0 警告、0 错误**。
- `dotnet test PLCScada.sln -c Release`：全量 **161 通过、0 失败、0 跳过**（第一轮 38 → 161）。
- 新增迁移（均为 AddColumn / CreateTable 安全变更，沿用本地时间递增 ID 序列并手工修正 EF 的 UTC 命名）：
  - `20261016200500_AddRecipeChangeNote`（配方变更说明）
  - `20261016201000_AddRecipeApplyRecords`（下发记录审计表）
  - `20261016210000_AddTagAlarmLimits`（Tag 限值报警）
  - `20261016213500_AddManualPulseMode`（手动输出脉冲模式，默认值手工修正为 300）
  - `20261016220000_AddRecipeParameterSortOrder`（配方参数排序）
- 本轮新增测试文件：`RecipeRevisionDiffTests`、`RecipeParameterOrderTests`、`RecipeBundleTests`、`RecipeApplyAuditTests`、`TagAlarmTests`、`TagWriteTests`、`TagAddressConflictTests`、`TagBundleTests`、`ManualPulseTests`、`ManualResetAndOrderTests`，另有通信审计容错回归测试（`AuditWriteFailureDoesNotFaultDeviceCommunicationOrLoseReadResult`）。
- 关键行为均有专项覆盖：配方下发安全链（预览指纹/读回/回滚/竞态）、限值报警滞回与失效标签清理、受控写入七类拒绝、脉冲取消仍复位（确定性注入）、批量导入逐项报告、全部归零失败不中断。
- 排查并修复一个并发环境下的真实缺陷：读取成功后的“记账”写入偶发失败会把设备误标为 Faulted（`SQLite Error 8`），记账写入已统一改为 best-effort。
- Tags 模块「连续地址合并采集」已完成——`TagReadGrouping` 分组（跨度 ≤120 寄存器、空隙 >8 拆组、按地址类型隔离）+ `TagRuntime.ReadGroupAsync`（单次读取、逐 Tag 偏移解码，复用同一套质量/量程/限值报警/发布路径，失败保留上次值），附批量解码等价、分组边界（类型/空隙/容量拆分）、失败保留、端到端轮询测试（`TagBatchReadTests` 4 个）。三个模块目标契约（`GOAL.md` / `GOAL-Tags.md` / `GOAL-Manual.md`）全部达成。
- 第三轮追加：配方识别（`MatchRecipesForDeviceAsync` + `RecipeMatchDialog`，识别"当前 PLC 在用哪个配方"）与两处一致性修正（新增配方默认设备、搜索持久化）；`RecipeMatchTests` 2 个。
- 第三轮追加（续）：Tags 批量启用/禁用（`SetEnabledAsync` 逐项校验与结果 + 列表多选）；配方包导入前预览（`PreviewBundleImportAsync` 模拟重命名不写库 + `RecipeBundlePreviewDialog` 确认流）；`TagBatchEnableTests` 1 个、`RecipeBundlePreviewTests` 2 个。测试设施：新增 `xunit.runner.json`（`maxParallelThreads=6`）——修复 24 核默认全并行下协议校验/跨进程锁测试的负载超时（此前连续 3 轮全量出现 6~7 个环境性失败）。
- 第三轮追加（三）：数据保留清理（`DataRetentionOptions` + `DataRetentionService` 后台周期任务 + `CleanupHistoryAsync` 使用 ExecuteDelete；未解除报警一律保留，清理动作写审计）与 Tag 质量报警（`Tag.QualityAlarmAfterFailures` 迁移 `20261016230000`，默认关闭；连续失败阈值触发、恢复自动解除，计数仅在通信/解码失败时累计，禁用类失败不计入）；`DataRetentionTests` 2 个、`TagQualityAlarmTests` 3 个。**161/161 × 连续 2 轮全绿**。
- SN 追溯（GOAL-Trace TR1–TR4）：Tag String 类型（寄存器数/编码、ASCII/UTF-16 编解码、强制只读）；`SnRecords` + `SnWatcherService` 变化自动记录；「追溯 SN」页（当前 SN/来源配置/记录分页搜索导出）；保留清理纳入 SN 记录。`StringTagTests` 2 + `SnRecordTests` 2；**187/187 × 连续 2 轮全绿**；端到端写模拟器 HR[4000]=ASCII 后 3 秒内自动落记录。
- 报警模块成熟化与运维增强（详见 GOAL-Alarms.md 与各批次记录）：自定义报警定义（位/枚举报警，M 点位与故障码）、优先级 P1–P4、未确认恢复（RTN）状态机、确认备注、同源 60 秒泛滥合并、报警页筛选/统计改版；迁移 `20261019120000`（报警定义表）、`20261019123000`（生命周期列 + 存量优先级回填）。测试：`AlarmDefinitionTests` 4 个 + `AlarmLifecycleTests` 4 个；**175/175 × 连续 2 轮全绿**。
- 报警搁置（AL-R6）：`ShelvedUntil/ShelveReason`（迁移 `20261019130000`）、带理由与到期的临时抑制、全程审计；`AlarmShelveTests` 3 个；**178/178 × 连续 2 轮全绿**。
- 报警统计分析（AL-R7）：区间流量与当前快照分离的 KPI（平均确认耗时/Top10 来源/常驻报警/搁置计数）+ CSV 导出；`AlarmStatisticsTests` 2 个；**180/180 × 连续 2 轮全绿**。
- 报警工况抑制（AL-R8）：按设备拦截新报警（四类来源解析、重复添加延长、到期自动失效、全程审计）；`AlarmSuppressionTests` 2 个；**182/182 × 连续 2 轮全绿**。
- 全局报警提示（AL-R9）：顶部常驻未确认徽标（P1 高亮、5 秒刷新、点击直达报警页）；`AlarmBadgeTests` 1 个；**183/183 × 连续 2 轮全绿**。
- 枚举报警语义增强（AL-R10）：整数类型限定（修复浮点四舍五入误命中）+ `值=文案` 与 `{label}` 模板；`AlarmDefinitionTests` 6 个；**185/185 × 连续 2 轮全绿**。
- 同一定义多值文案刷新（AL9）：活动期间取值 1→2 时消息跟随刷新（不新增记录）；`AlarmDefinitionTests` 7 个；**186/186 × 连续 2 轮全绿**。
- 报警页易用性全量改进（AL-R11）：服务端分页+CSV 导出、持续时长/设备列、统计卡筛选、批量确认、详情对话框、定义按设备分组与行内启停、首页报警概览；`AlarmQueryTests` 2 个；**188/188 × 连续 2 轮全绿**。
- 轮询调优：线圈合并读取 + 全局 Tag 扫描间隔（设置页维护、Tag 0=跟随全局、可一键重置），现场周期降至 250ms；`TagBatchReadTests` 6 个、`PollingIntervalTests` 3 个；**193/193 × 连续 2 轮全绿**。
- 触发快照（TR5）：`SnapshotRules/Points/Records`；触发源 Coil Bool=1 抓取点位→保存→复位为 0；追溯页规则管理与记录详情；`SnapshotRuleTests` 2；**189/189 × 2 全绿**；端到端置位后 4 秒内落记录且触发位读回 False。
- SN 与快照绑定（TR6）：快照记录 `SnRecordId` 绑定同次读取的 SN 记录（无则自动创建），追溯页 SN 列表「快照」列可展开点位值；**189/189 × 2 全绿**；端到端写 SN 后触发，SN 记录显示 1 次快照且绑定成立。