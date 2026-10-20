# GOAL — 报警模块达到成熟标准（ISA-18.2 核心子集）

> 目标模式契约。范围：补齐"基于采集值"的报警能力（M 位/枚举），并把生命周期、优先级、泛滥治理做扎实。

## Objective

当前只有「通信报警 + Tag 限值 + 读取质量」三类，**Bool/M 点无法报警**（限值校验显式拒绝 Bool）。
按成熟 SCADA 的三类值报警补齐：① 离散位（M 点）② 枚举/状态字（故障码）③ 限值（已有）。
同时补生命周期完整性（未确认恢复）、优先级、确认备注与泛滥抑制。

## Requirements

### AL-R1 报警定义（位/枚举 + 去抖 + 模板）— P0
- [ ] 模型 `AlarmDefinition`：TagId、Kind(Bit/Enum)、BitTarget、EnumValues、Severity、Message 模板（{tagName}/{value}）、DelaySeconds、Enabled，迁移建表
- [ ] 存储/服务：CRUD + 校验（位定义仅 Bool、枚举定义仅数值、枚举值可解析、同 Tag 同名唯一）
- [ ] 运行时：定义缓存（TTL 10s，变更自动生效/清理失效定义的活动报警）+ 在单读与合批读的发布路径评估；持续 N 秒（去抖）才触发；恢复即解除；报警源 `AlarmDef:{id}`
- [ ] UI：Alarms 页「报警定义」管理（列表 + 新增/编辑/删除）
- [ ] 测试：校验拒绝矩阵、位触发/恢复、枚举命中/恢复、延时去抖、停用不报、消息模板渲染

### AL-R2 生命周期 + 优先级 + 确认备注 — P0
- [ ] `AlarmPriority` P1–P4（迁移加列，存量按 Severity 映射：Error→P1、Warning→P2、Info→P3）
- [ ] 新状态 `ClearedUnacknowledged`（未确认恢复 RTN Unack）：恢复时若从未确认 → 该状态，仍可确认；确认后 → Cleared
- [ ] 确认支持备注（≤200 字，写审计）；所有来源（通信/限值/质量/定义）统一
- [ ] 保留策略：仅清理**已确认并恢复**的报警；RTN Unack 不清理
- [ ] 测试：四种状态流转（含确认后恢复）、RTN Unack 可确认、备注落库、清理边界

### AL-R3 泛滥抑制 — P1
- [ ] 同一 Source 在 60 秒窗口内重复「触发→恢复→再触发」合并为同一条记录（OccurredAt 刷新 + RepeatCount++），窗口外恢复为独立记录
- [ ] 测试：窗口内合并计数、窗口外独立、不同来源互不影响

### AL-R4 Alarms 页改版 — P1
- [ ] 统计卡：P1 未确认 / 未确认合计 / 活动 / 已恢复未确认
- [ ] 筛选：优先级、来源、状态（多选）；默认按「优先级→时间」排序
- [ ] 确认对话框（可填备注）；RTN Unack 行可补确认
- [ ] 测试：筛选/统计逻辑（服务级）

### AL-R6 搁置（Shelving）— P1
- [x] 模型：`AlarmRecord.ShelvedUntil` + `ShelveReason`（迁移 `20261019130000`）
- [x] 存储：`ShelveAlarmAsync`（1–1440 分钟、理由必填、仅未解除可搁置）/ `UnShelveAlarmAsync`，均写审计；到期按时间判定自动失效
- [x] UI：报警行「搁置」（时长 + 理由对话框）/「取消搁置」；搁置徽章显示剩余分钟与理由；统计卡排除搁置中报警、摘要显示搁置数量
- [x] 测试：搁置校验与审计、取消搁置、窗口内重复触发保留搁置状态

### AL-R7 统计分析（KPI）— P1
- [x] 聚合查询 `GetAlarmStatisticsAsync`：区间流量（总数/确认数/平均确认耗时/Top10 来源）与当前快照（活动/未确认/已恢复未确认/搁置/常驻 >24h）分离
- [x] UI：「统计分析」对话框（区间 24h/7d/30d、8 张 KPI 卡、Top10 来源解析为设备/Tag/定义名、常驻报警清单）+ CSV 导出
- [x] 测试：区间与快照口径、Top 来源排序、空库零值

### AL-R8 工况抑制（Suppression）— P1
- [x] 模型 `AlarmSuppression`（DeviceId、Reason、Until、CreatedAt；迁移 `20261019133000`，设备删除级联）
- [x] 存储：`AddSuppressionAsync`（1–1440 分钟/理由必填/设备必须存在；同设备重复添加延长而不重复建行）/ `RemoveSuppressionAsync`，均审计；`RaiseAlarmAsync` 前置检查（来源→设备解析：Device/Tag(:条件)/TagQuality/AlarmDef）拦截命中设备的**新**报警
- [x] UI：Alarms 页「报警抑制」对话框（设备 + 时长 + 理由、抑制列表含剩余分钟/过期态/解除）
- [x] 测试：四类来源全被拦截且其他设备不受影响+到期恢复、重复添加延长、校验与审计

### AL-R9 全局提示（Annunciation）— P1
- [x] `GetAlarmBadgeSummaryAsync`：未确认数（排除搁置）+ P1 未确认数 + 已恢复未确认数
- [x] 顶部常驻徽标 `AlarmBadge`：5 秒轮询、P1 红色/警告黄/仅 RTN 信息蓝、>99 显示 99+、tooltip 明细、点击跳转报警页
- [x] 测试：搁置排除、恢复转 RTN、确认后归零

### AL-R10 枚举报警语义增强 — P1
- [x] 枚举仅允许整数类型（Int16/UInt16/Int32/UInt32），运行时并行守卫（即使历史脏数据绑浮点也不会触发，杜绝四舍五入误命中）
- [x] `值=文案` 语法（`5=急停, 6=堵转`，兼容旧 `5,6`；文案 1–50 字、禁止分隔符/等号；保存时按值排序归一）
- [x] 消息模板新增 `{label}` 占位符（命中值的文案，无文案时回退为数值）；定义列表显示 `5→急停`
- [x] 测试：浮点拒绝/运行时跳过、混合语法解析归一、非法/超长文案拒绝、`{label}` 渲染

### AL-R11 易用性（全量改进）— P0/P1
- [x] 列表**服务端分页**（50/100/200 + 翻页），彻底去掉 500 条上限；**CSV 导出**当前筛选结果（含设备/来源/备注/恢复时间/重复次数）
- [x] **持续时长列**（>24h 常驻标红）、**设备列**与设备筛选（来源→设备解析）、**排序切换**（优先级/时间）
- [x] 统计卡**可点击筛选**；「只看搁置中」「只看常驻」快捷筛选
- [x] **批量确认**（多选 + 共用备注 + 逐项结果）
- [x] **单条详情对话框**（时间线：发生/确认/恢复、备注、搁置理由、重复次数、来源解析）
- [x] 报警定义：Tag 下拉**按设备分组 + 关键字筛选**；列表**行内启用/停用开关**
- [x] 首页（Dashboard）**未处理报警概览**卡片（P1 高亮 + Top10 + 统计）
- [ ] 声音提醒（需浏览器音频解锁交互，单独设计）、试触发（写操作需单独安全设计）、抑制到期提醒

### AL-R5 文档 + 终验 — —
- [ ] README 报警章节 + docs/verification.md 增补；DoD 全绿

## Definition of Done

```powershell
dotnet build PLCScada.sln -c Release --no-incremental   # 0 警告 0 错误
dotnet test  PLCScada.sln -c Release                     # 全量通过（含 AL-R1–R4 新增测试）
```

## Non-Goals（本轮不做）

- 报警合理化文档/主报警库（ISA-18.2 完整体系）
- 推送通知（企业微信/邮件）、升级链
- 报警推送通知（企业微信/邮件）与升级链——后续批次

## 批次

| 批次 | 内容 | 状态 |
| --- | --- | --- |
| AL1 | 报警定义（位/枚举）+ 评估 + 管理 UI | ✅ 171/171 × 2 轮通过 |
| AL2 | 生命周期 + 优先级 + 确认备注 + 泛滥抑制 + 页面改版 | ✅ 175/175 × 2 轮通过 |
| AL3 | 文档 + 终验 | ✅ README + verification.md；0/0 + 175/175 |
| AL4 | 搁置（Shelving） | ✅ 178/178 × 2 轮通过 |
| AL5 | 统计分析（KPI） | ✅ 180/180 × 2 轮通过 |
| AL6 | 工况抑制（Suppression） | ✅ 182/182 × 2 轮通过 |
| AL7 | 全局提示（Annunciation） | ✅ 183/183 × 2 轮通过 |
| AL8 | 枚举报警语义增强（P0 缺陷 + P1 文案） | ✅ 185/185 × 2 轮通过 |
| AL9 | 活动报警文案随值刷新 | ✅ 186/186 × 2 轮通过 |
| AL10 | 易用性全量改进 | ✅ 188/188 × 2 轮通过 |

## 进度日志

- **2026-10-19 AL0**：调研确认缺口（`TagService` 拒绝 Bool 限值、`TagRuntime` 跳过 Bool 评估；M0–M7999=Coil 位地址无报警通道）；基线 167/167。
- **2026-10-19 AL1（报警定义）**：`AlarmDefinition`（Kind Bit/Enum、BitTarget、EnumValues、Severity、Message 模板 {tagName}/{value}、DelaySeconds 去抖、Enabled；迁移 `20261019120000`，Tag 级唯一名称、Tag 删除级联）。`AlarmDefinitionService`（位定义仅 Bool、枚举定义仅数值、枚举值解析支持中文逗号、级别归一化）。`TagRuntime`：定义缓存 10 秒刷新（停用/删除自动清理残留活动报警）+ 单读/合批两条发布路径求值（去抖按首次命中时间、恢复即解除、`AlarmDef:{id}` 独立来源、写入 best-effort）。UI：Alarms 页「报警定义」管理（列表 + 新增/编辑/删除对话框，含占位符与延时说明）。测试：`AlarmDefinitionTests` 4 个（校验矩阵、位触发/恢复+模板渲染、枚举命中/恢复、延时去抖+停用不触发）。**171/171 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL2（生命周期 + 优先级 + 泛滥治理）**：`AlarmPriority` P1–P4（按 Severity 映射 Error→P1/Warning→P2/Info→P3，迁移 `20261019123000` 加列 + SQL 回填存量）+ 新状态 `ClearedUnacknowledged`（恢复时未确认 → 仍待确认，确认后转 Cleared）+ `AcknowledgeNote`（≤200 字，写审计）+ `ClearedAt` + `RepeatCount`。四类报警统一走 `RaiseAlarmAsync`/`ClearAlarmAsync`（活动去重、60 秒窗口同源合并计数、**Tag 高限/低限按 `Tag:{id}:High/Low` 分源**避免误合并）。确认支持备注且可对已恢复未确认报警补确认，重复确认明确报错。Alarms 页改版：4 张统计卡（未确认含 P1 计数/已确认未解除/已恢复未确认/已解除）、优先级与来源筛选、按「优先级→时间」排序、×N 重复次数标记、备注悬浮显示、确认对话框。保留策略仅清理**已确认并恢复**的报警。测试：`AlarmLifecycleTests` 4 个（未确认恢复→补确认、确认后直接解除+重复确认拒绝、窗口内合并/窗口外独立、清理不删未确认恢复）；同步更新受限值/质量/定义/通信测试的状态期望。**175/175 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL3（文档 + 终验）**：README 报警行补充优先级/RTN/确认备注/泛滥合并；`docs/verification.md` 增补。终验：`--no-incremental` 0 警告 0 错误、**175/175**。**报警模块目标全部达成（AL-R1–R4）**。
- **2026-10-19 AL4（搁置 Shelving）**：`AlarmRecord.ShelvedUntil/ShelveReason`（迁移 `20261019130000`）；`ShelveAlarmAsync`（1–1440 分钟、理由必填、仅 Active/Acknowledged 可搁置、写审计）/ `UnShelveAlarmAsync`（清除并审计、重复取消为无害空操作）；到期按时间判定自动恢复；60 秒泛滥合并**保留搁置状态**（维护期内再次触发仍抑制）。UI：行内「搁置/取消搁置」+ 剩余分钟徽章（tooltip 显示理由）+ 统计卡排除搁置中 + 摘要显示搁置数。测试：`AlarmShelveTests` 3 个。**178/178 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL5（统计分析）**：`AlarmStatistics`（区间总数、确认数、平均确认耗时、活动/未确认/已恢复未确认/搁置/常驻计数、Top10 来源、常驻清单）+ `GetAlarmStatisticsAsync`（EF 分组投影匿名类型后内存映射，规避 record 投影不可翻译）。UI：Alarms 页「统计分析」→ `AlarmKpiDialog`（区间选择 24h/7d/30d、8 张 KPI 卡、来源解析为设备/Tag/定义名、常驻报警表、KPI CSV 导出）。测试：`AlarmStatisticsTests` 2 个（区间与快照口径/排序、空库零值）。**180/180 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL6（工况抑制）**：`AlarmSuppression`（迁移 `20261019133000`）+ `RaiseAlarmAsync` 前置抑制检查（来源→设备解析覆盖 Device/Tag/TagQuality/AlarmDef 四类，命中设备抑制期则跳过新建与合并）；`AddSuppressionAsync`（重复添加延长同一条）/`RemoveSuppressionAsync` 均审计。UI：Alarms 页「报警抑制」对话框（设备/时长/理由 + 列表含剩余分钟与解除）。测试：`AlarmSuppressionTests` 2 个（四类来源拦截 + 其他设备不受影响 + 到期恢复；重复添加延长 + 校验 + 审计）。**182/182 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL7（全局提示）**：`AlarmBadgeSummary` + `GetAlarmBadgeSummaryAsync`（未确认排除搁置、P1 计数、RTN 计数）；`AlarmBadge` 组件挂在主布局顶栏（5 秒轮询、P1 红/警告黄/RTN 信息蓝、tooltip 明细、点击进报警页）。测试：`AlarmBadgeTests` 1 个。**183/183 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL8（枚举语义增强）**：**P0 缺陷修复**——枚举原本对任意数值按 `Math.Round` 匹配，Float32 的 5.4 会误命中 5：现服务端仅允许整数类型 Tag，运行时再加并行守卫（历史脏数据也不触发）。**P1 表达力**——`EnumValues` 支持 `5=急停, 6=堵转`（兼容旧 `5,6`；文案 1–50 字、禁分隔符；按值排序归一），消息新增 `{label}` 占位符（无文案回退数值），定义列表显示 `5→急停`，编辑框与帮助文案同步。测试：`AlarmDefinitionTests` 增至 6 个。**185/185 × 2 轮全绿，0 警告 0 错误**。
- **2026-10-19 AL9（同一状态字多值文案刷新）**：同一枚举定义在报警活动期间取值从 1→2（如 急停→超限）时，原实现会保留旧文案；现 `SetAlarmDefinitionAlarmAsync` 在活动记录存在且消息/级别变化时刷新（不新增记录、RepeatCount 不变），`TagRuntime` 用 `_alarmDefMessage` 缓存已渲染文案、仅变化时写库。测试：`ActiveEnumAlarmRefreshesMessageWhenValueChanges`（1→急停、2→超限同一条记录、9→解除）。**186/186 × 2 轮全绿**。
- **2026-10-19 AL10（易用性全量）**：`AlarmQuery/AlarmQueryResult` + `QueryAlarmsAsync/QueryAlarmsForExportAsync`（搜索/ActiveOnly/状态/优先级/来源前缀/来源集合/搁置/常驻/排序/分页）；Alarms 页重写（分页+导出 CSV、持续时长列、设备列与设备筛选、统计卡点击筛选、搁置/常驻快捷筛选、排序切换、批量确认、详情对话框）；报警定义 Tag 下拉按设备分组+筛选、列表行内启停；Home 页补报警概览卡（P1 高亮+Top10+统计）。测试：`AlarmQueryTests` 2 个。**188/188 × 2 轮全绿**。
