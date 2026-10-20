# GOAL — SN 追溯（PLC 字符串 SN + 追溯页面）

## Objective

当前 Tag 只支持数值/布尔，无法表达扫码内容；也没有 SN 追溯记录。
本批次：新增 **String 数据类型**（Modbus 寄存器串 → 字符串），把 **D4000 起的扫码内容**建成 Tag，
并在新页面「追溯」中展示当前 SN、SN 变更记录与检索导出。

## Requirements

### TR-R1 Tag 的 String 数据类型 — P0
- [ ] `TagDataType.String` + `Tag.StringRegisters`（1–125）+ `Tag.StringEncoding`（Ascii / Utf16LE）
- [ ] 编解码：ASCII（去尾部 NUL/空格）、UTF-16LE（逐字交换字节序）；`RegisterCount` 按寄存器数
- [ ] 采集：String 不参与数值/位合并分组，单独按 N 寄存器读取并解码；快照 `CurrentValue` 为 string
- [ ] 校验：仅寄存器类地址可建 String；强制只读；不得配置限值报警/量程
- [ ] UI：数据类型下拉含 String，出现「寄存器数/编码」字段；量程/限制区隐藏；寄存器占用提示
- [ ] 测试：ASCII/UTF16 解码、尾部填充清理、RegisterCount、服务级校验矩阵

### TR-R2 SN 来源与自动记录 — P0
- [ ] 设置键 `Trace.SnTagId`（String Tag；未配置时回退同名「扫码内容」；允许不启用）
- [ ] 存储：`SnRecords(Id, Sn, CapturedAt, Source)` + 索引（Sn、CapturedAt）；查询（关键词/分页/总数）
- [ ] 后台 `SnWatcher`：每秒读取 SN Tag 快照，值非空且变化时落一条记录（同值不重复）
- [ ] 保留策略：`DataRetentionOptions.SnRecordRetentionDays`（默认 90，0=永久）纳入清理与设置页
- [ ] 测试：变化记录/同值去重/空值忽略、查询分页、清理边界

### TR-R3 「追溯」页面 — P0
- [ ] 当前 SN 卡片（等宽大字 + 质量 + 最后变化时间）+「记录当前 SN」按钮（复测/手动打点）
- [ ] SN 来源配置（String Tag 下拉 + 不启用 + 保存写审计）
- [ ] 记录列表：服务端分页（50/100/200）、按 SN 搜索、时间倒序、CSV 导出、总条数
- [ ] 导航新增「追溯 SN」
- [ ] 测试：CSV 依赖服务端查询；页面 200

### TR-R4 现场数据 — P0
- [ ] 创建 Tag「扫码内容」：HoldingRegister[4000]、String、20 寄存器（40 字符）、ASCII、只读
- [ ] 文档 + 终验：README / verification / memory

## Definition of Done

```powershell
dotnet build PLCScada.sln -c Release --no-incremental   # 0 警告 0 错误
dotnet test  PLCScada.sln -c Release                     # 全量通过（含 TR-R1/R2 新测试）
```

## 批次

| 批次 | 内容 | 状态 |
| --- | --- | --- |
| TR1 | String 数据类型（模型/编解码/采集/校验/UI） | ✅ |
| TR2 | SN 来源 + 自动记录 + 保留清理 | ✅ |
| TR3 | 追溯页面 + 导航 + 现场 SN Tag | ✅ |
| TR4 | 文档 + 终验 | ✅ 187/187 × 2 |
| TR5 | 触发快照（布尔触发 → 抓取 → 复位） | ✅ 189/189 × 2 |
| TR6 | SN 记录与触发快照绑定 | ✅ 189/189 × 2 |

## 进度日志

- **2026-10-20 TR1–TR4**：`TagDataType.String`（StringRegisters 1–125、StringEncoding Ascii/Utf16LE）；编解码（ASCII 去尾部 NUL/空格、UTF-16 逐字交换）；`RegisterCount(Tag)` 覆盖全部调用点；String 不参与数值/位合并，逐点读取；校验（仅寄存器、强制只读、禁限值）。`SnRecords` 表（Sn/CapturedAt/Source + 索引）；设置键 `Trace.SnTagId`（未配置回退同名「扫码内容」，显式空=停用）；`SnWatcherService` 每秒检测 SN 变化自动落库；保留清理新增 `SnRecordRetentionDays`（默认 90，设置页可改）。「追溯 SN」页（当前 SN 大字 + 质量 + 记录按钮 + 来源配置 + 记录列表分页/搜索/CSV）。现场创建 Tag「扫码内容」HR[4000] String 20 寄存器 ASCII 只读。**端到端验证**：向模拟器写 ASCII「SN-TEST-0001」→ 3 秒内自动生成 SN 记录（Source=PLC）。测试：`StringTagTests` 2 个 + `SnRecordTests` 2 个；**187/187 × 2 轮全绿，0 警告 0 错误**。

- **2026-10-20 TR5（触发快照）**：`SnapshotRules/SnapshotRulePoints/SnapshotRecords`（迁移 `20261020170000`）；规则校验（触发源必须 Coil Bool、≥1 抓取点位、同名拒绝）；`SnapshotCaptureService`（读触发位为 True → 抓取点位值+质量 JSON（中文不转义）→ 保存记录（含当前 SN）→ 写 False 复位）；`SnapshotTriggerService` 后台每秒评估、规则 3 秒刷新、同规则 3 秒内不重复抓取；追溯页新增「触发快照规则」（新增/编辑/启停/删除，多选点位）与「快照记录」（分页+详情对话框）。**端到端**：置位 Coil[63] → 4 秒内落记录（值含 TRUE/测量值）且触发位读回 0。测试：`SnapshotRuleTests` 2 个；**189/189 × 2 全绿**；修复 DI 生命周期（CaptureService 必须 Singleton 供 Hosted 使用）。

- **2026-10-20 TR6（SN 与快照绑定）**：`SnapshotRecord.SnRecordId`（迁移 `20261020190000`，索引）；抓拍时按当前 SN 查找最近一条 SN 记录并绑定，**无对应记录则自动创建**（带设备名与当时配方），保证"一次触发 = SN + 该次读取的全部点位值"；`AddSnRecordAsync` 返回记录；新增 `GetLatestSnRecordBySnAsync` / `GetSnapshotsBySnRecordAsync` / `GetSnapshotCountsAsync`。追溯页 SN 列表新增「快照」列（N 次 → 弹窗按时间列出该 SN 的全部触发快照与点位值）。**端到端**：写 SN-BIND-01 → 触发规则 → SN 记录显示快照 1 次、快照行 SnRecordId 已绑定。**189/189 × 2 全绿**。
