# Foundation 架构优化重构方案

状态：2026-11-05 已批准；第 8 节列出的正式分册修订已于同日完成；P0–P5 已于同日在 `release/1.0` 实施，未完成项见第 6 节末尾。日期：2026-11-05。基线：`release/1.0` / `0d0a361` 加本地工作区 1.0.4 修复（26 个文件未提交）。本文保存决策依据与阶段安排，不作为独立权威合同；正式行为以修订后的各分册为准，后续按第 6 节形成逐任务实施计划。产品实现不因本文更新而交付。

正式入口为[架构总览](overview.md)，具体合同见[模块宿主](modularity.md)、[模块动态重配事务](module-reconfiguration.md)、[服务登记](service-registration.md)、[受管生命周期](managed-lifetimes.md)、[源码生成](source-generation.md)与[验证合同](../engineering/verification.md)。冲突时以这些正式分册为准。

评估依据：通读运行时与生成器全部源码及正式分册；本机 Release 双目标构建 0 警告 0 错误，运行时 200/200、生成器 48/48 通过；[2026-11-04 架构审计](../engineering/2026-11-04-foundation-architecture-audit.md)的 8 项缺陷已有回归覆盖。本文不重复那 8 项，只记录边界内部仍可优化的结构、并发、生成器与工程问题。

## 1. 结论与范围

单 Host、单 Required 图、中立与受管双模式、显式 owner/lease、不引入运行时 DI 依赖、用户回调一律在锁外执行，这些边界在源码中得到一致贯彻，无需推倒重建。可优化的问题集中在边界内部：

1. 三条生命周期路径（初始启动、动态重配、最终关闭）各自实现一遍阶段阶梯与清理调度，当前代领域状态没有关闭协议。
2. Transient 服务只有 scope 一个释放触发点，从 Root 借用的 Transient 直到 runtime 关闭才释放。
3. 同一次退休在七八处对象里记账，`LifetimeRetirement` 同时承担内部 batch 与公开回执两种角色。
4. 并发模型混用四类 gate、全局静态锁、`Monitor.Wait` 同步阻塞与 ThreadStatic 重入计数；推进循环按全量扫描实现。
5. 生成器增量管线模型持有 `ISymbol` 导致缓存失效；含产品特定启发式；Roslyn 5.0 依赖限制了可加载的 SDK。
6. API 形态与工程保障：热路径用异常表达常规状态，诊断在事务内退化为字符串，公开 API 兼容与 AOT 注解没有机器保障。

本文范围是上述六项的目标行为、方案选择、兼容边界与阶段验收。它不包含下游产品接入、跨平台资格或版本发布；不新增项目、包或 Microsoft DI 依赖。

## 2. 保留边界

以下边界由现有正式分册定义，本方案不改变：

- `AtomUI.Modularity`、`AtomUI.Modularity.Services`、`AtomUI.Lifetimes` 既有公开签名保留；1.0.1 中立描述、instance Getter、调用方所有权与冻结语义不变。
- `ModuleHostPhase` 成员与数值不变；首次 Initialize 的令牌身份、加入者只取消自身等待、回调重入拒绝、每模块 Shutdown 最多一次等合同不变。
- owner 唯一释放权威、lease 不取得关闭权、两个屏障、失败保留闭包、Runtime → Lifetime 锁顺序的对外可观察行为不变。
- Generator 保持 `netstandard2.0`，不引用运行时程序集；运行时对 Generator 仅为 analyzer 引用。
- 生成目录输出的类型名、注册顺序和诊断编号保持兼容；新诊断只追加。

## 3. 问题清单与优先级

| 编号 | 问题 | 位置 | 等级 | 实际影响 |
| --- | --- | --- | --- | --- |
| O01 | 启动、重配、关闭三条路径重复实现阶段阶梯与清理 | `ModuleHost.Stages.cs`、`ModuleHost.Changes.cs`、`ModuleHost.Coordination.cs` | P1 | 行为修复须三处同步；`CompletePhase` 中 Generation 0→1 特判是缝隙；审计 R01 的当前代领域 reader 归还循环无协议解决 |
| O02 | Transient 没有短于 scope 的释放触发 | `ModuleServiceScope.ResolveOwner`、`LifetimeDomain.Create` | P1 | `Root.Borrow<Transient>()` 每次新建 owner 挂入 `scope.Owners`，lease 归还只减 Readers，实例与 owner 累积到 runtime 关闭；现有测试仅断言实例不同 |
| O03 | 变更事务是隐式状态机 | `ModuleHost.Changes.cs` `ChangeCoreAsync` | P1 | 160 行单方法加 try/catch 承载七个阶段；`RetirementResources.Services` 为 `object`；审计 A01/A02 两个 P1 均源于此 |
| O04 | 退休簿记对象过多且角色重叠 | `LifetimeContracts.cs`、`ModuleServiceBindings.cs`、`ModuleServiceRuntime.cs`、`ModuleServiceScope.ScopeState` | P1 | `LifetimeRetirement` 以可空字段区分 batch 与聚合回执；Suspension、Transition、ChangeRetirement、ScopeState.Retirement、Bindings `_retirement`/`_retirementBatch`、Runtime `CandidateRetirements`/`ShutdownRetirement` 分别记账；审计 A05/A06 泄漏由此产生 |
| O05 | 锁拓扑过深并混用阻塞等待 | 全部运行时源码 95 处 `lock`；`ModuleServiceBindings.PrepareRetirement`、`ModuleServiceTransition` 构造函数 4 处 `Monitor.Wait` | P2 | 构造函数内同步阻塞线程等待在途工厂；`_stopCallbackDepth` 为 ThreadStatic，Stop 回调跨 await 后失效；Host 侧已使用 AsyncLocal，两套重入检测并存 |
| O06 | 进程级静态状态 | `LifetimeDomain._nextOwnerId`、`Ownership`、`OwnershipGate` | P2 | 每次 Own/Create 争用同一把全局锁；任何持有已释放裸实例的代码延长整个 domain 的可达性（审计 R04） |
| O07 | 推进循环按全量扫描实现 | `LifetimeDomain.Progress`、`PruneOwnersUnsafe`、`RecomputeFailureClosureUnsafe`、`Suspend` | P2 | 每次 `lease.Dispose()` 扫描全部 `_retirements`；剪枝对每个 scope 的 Owners 做 RemoveAll；失败闭包重算遍历全图；owner 数上千时每次归还为 O(N) |
| O08 | 缺少并发保障测试 | `tests/AtomUI.Foundation.Tests` | P2 | 200 个用例全部确定性，无 Borrow/Dispose 与 Retire 并发的压力或受控调度用例 |
| O09 | 生成器模型持有符号，增量缓存失效 | `ModuleCatalogGenerator.ModuleModel`、`CreateTypeReference` | P2 | 模型含 `INamedTypeSymbol` 与 `Location` 且无值相等，每次编辑重跑 EmitCatalog；每个依赖遍历全部 `compilation.References` 并逐个取程序集符号 |
| O10 | 生成器含产品启发式与死代码 | `InferDisplayName`、`Visit` | P3 | 硬编码剥除 `DataGrid` 前缀属于产品逻辑；`Visit` 两个分支均 return；1042 行单文件难以审阅 |
| O11 | Roslyn 5.0.0 依赖 | `Directory.Packages.props` | P2 | 运行时支持 net8，但 .NET 8 SDK 无法加载生成器；净效果是 net8 消费者只能手写注册 |
| O12 | 热路径用异常表达常规状态 | `ModuleHost.AcquireView`、`ModuleServiceScope.Borrow` | P3 | 重配窗口内每次视图获取都抛 `InvalidOperationException`，消费端须在热路径 catch |
| O13 | 诊断链在事务内退化 | `EnsureCandidateStage`、`RejectedChange` | P3 | 结构化诊断被拼成字符串再抛出，模块 id 与诊断码丢失；`ATOMUICHANGE001` 为字面量，`ModuleDiagnosticCodes` 编号 004–006、008 缺口 |
| O14 | 兼容与 AOT 没有机器保障 | 构建配置 | P2 | 无 PublicAPI 基线或 ApiCompat；运行时项目未开 `IsAotCompatible`，AOT 只靠消费者端到端；三个同步入口走 `GetAwaiter().GetResult()` |
| O15 | 文档与发布治理 | `docs/`、发布工作流 | P3 | docs 848K 其中证据 584K 含 gz 二进制；总览状态表有一行列数与表头不符；发布工作流可对任意 SHA 发布而不跑消费者验证（审计 R03） |
| O16 | 已准入的 Scoped 工厂在 scope 同时退休时被误判失败 | `ModuleServiceScope.ResolveOwner`、`ModuleServiceActivationContext.GetRequiredService` | P1 | P0 并发骨架发现：工厂执行期间 scope 开始退休，依赖解析先对正在关闭的 scope 执行 `EnsureOpen`，即使 Singleton 依赖来自仍开放的 Root 也抛 `ObjectDisposedException`；owner 记为 Factory 失败，scope 退休结果伪报清理失败 |

等级含义：P1 决定其他重构的形状或存在真实资源累积；P2 影响长期运行、性能或维护成本；P3 可独立处理且不阻塞前两类。

## 4. 方案

### 4.1 W1：统一生命周期事务（O01、O03）

目标：Host 只有一种"组合变更"协议。初始启动是从空组合变更到 S，最终关闭是从 S 变更到空组合，动态重配是 S 到 S′。三者共用候选准备、单一提交、退休调度与失败隔离。

- 新增内部类型 `ModuleChangeTransaction`，显式状态为 Validated → Created → Configured → Prepared → Suspended → Published → Retiring → Completed，每个状态只有一种合法失败出口。`ChangeCoreAsync` 退化为对该类型的驱动，`RetirementResources` 字段改为强类型。
- 初始启动通过同一事务推进 `created` 全集，保留 `ModuleHostPhase` 的逐阶段可观察值与手动阶段 API；手动 Resolve/Create/配置入口映射为事务的分步推进，不重跑工厂。`CompletePhase` 中 Generation 0→1 的特判随之消失，初始视图即 Generation 1 的提交结果。
- 最终关闭通过同一退休调度器把当前代退休。Host 在视图中保存发布 `ModuleHostView.State` 的准备对象；关闭时调用其新增可选成员 `StopCurrent()`，再让 `CompleteCurrentAsync()` 与模块读者排空同时启动，与模块回调及服务 Release 并行推进。这直接关闭审计 R01 的循环接线风险，不要求 Host 自动 Dispose 任意 State。（2026-11-05 实施修正：批准稿原写作沿用 `StopPrevious`/`CompletePreviousAsync`，但这两个成员退休的是准备对象的前任状态，照字面实现会在关闭时重复退休前任，故改为新增成员。）
- 没有 participant 的中立消费者行为不变：空领域完成立即成功。
- 全局失败回滚与候选失败清理合并为"退休一个未发布候选"，初始集合失败时退休的是尚未发布的 Generation 1 候选。

不做：不改变模块回调签名；不把领域 State 变为 Foundation 已知类型；不引入第二 Host。

### 4.2 W2：Transient 请求 owner 语义（O02）

目标：Transient 实例有短于 scope 的真实释放触发，且仍由唯一 owner 释放。

- Transient owner 增加"请求 owner"标记：首个 lease 归还且 Readers 归零、无 outgoing capture、无 WaitForRelease dependent 时，由推进循环自动进入 Retiring 并按既有屏障释放。Stop 回调仍在锁外分发。
- 存在 capture 的 Transient（它捕获了 Scoped 或 Singleton 依赖）在自身释放时归还 capture，不改变被捕获 owner 的寿命。
- 另一 owner 通过 `Capture` 长期持有 Transient 时，该 Transient 转为被捕获寿命，直到消费者释放；这是现有 capture 合同的自然结果，不新增状态。
- `ServiceRegistrationGraph.Validate` 增加 Singleton 对 Transient 的传递捕获检查，与 Singleton 对 Scoped 同级处理：Singleton 捕获 Transient 合法但使其寿命提升，需作为 Info 诊断可见。
- 服务登记合同中 Transient 的描述改为"每次激活创建，归属请求 owner；无长期捕获时在最后一个 lease 归还后退休"。

备选方案是禁止 Root 借用 Transient 并诊断。该方案不能解决非根长寿 scope 的累积，故不采用。

### 4.3 W3：退休簿记合并（O04）

目标：一次退休在生命周期层只有一份账，服务层与 Host 层只持有对它的引用。

- 拆分 `LifetimeRetirement`：internal `LifetimeRetirementBatch` 持有 Members、PendingFinalOwners、Batch 分发状态；public `LifetimeRetirement` 只保留 `Completion`、`FinalCompletion` 与聚合能力，内部指向一个或多个 batch。公开签名不变。
- `LifetimeSuspension` 变为 batch 的"可恢复前置阶段"，Stop 后与 batch 共用同一对象，不再各持一套 Members 与完成标记。
- `ModuleServiceRuntime` 的 `CandidateRetirements`、`ShutdownRetirement`、`ModuleServiceBindings` 的 `_retirement`/`_retirementBatch`、`ScopeState.Retirement` 合并为一个 `RetirementLedger`：按登记身份索引 live、Pending、失败三类票据，成功票据在最终完成时由 ledger 统一移除。审计 A05/A06 的修复方式由"逐集合清理"改为"按 ledger 分类裁剪"。
- `ModuleChangeRetirement` 保持公开形态，内部改为持有 ledger 条目引用而非整份 `RetirementResources`。

### 4.4 W4：并发模型收敛（O05、O06、O07、O08）

目标：一套锁层级、一套重入检测、无同步阻塞等待、推进循环按索引推进。

- 锁层级文档化为 Host `_stateLock` → Runtime `Gate` → Lifetimes `Gate` → `ScopeState.Gate`，禁止反向获取；Debug 构建增加层级断言。
- Pending 排空改为可等待：`ServiceRegistration.Pending` 与 `ScopeState.Pending` 归零时完成一个 TaskCompletionSource，`PrepareTransition` 与候选 `PrepareRetirement` 改为 async 内部实现；公开 `PrepareTransition` 保留同步签名作为兼容入口，新增 `PrepareTransitionAsync` 供 Host 使用。（2026-11-05 实施修正：改为把准入检查与 owner 预留在 Runtime 锁内原子完成，空窗消失，`Pending` 与等待一并删除，不再新增 `PrepareTransitionAsync`。）
- `_stopCallbackDepth` 改为 AsyncLocal，与 Host 的 `CallbackFrame` 统一为一个 `CallbackScope` 内部类型，供 Host、Runtime、Lifetimes 共用。
- `Ownership` 弱表与 `_nextOwnerId` 下沉到 `LifetimeDomain` 实例；跨 domain 重复接管改为显式合同："同一实例只能属于一个 domain 的一个 owner"由 domain 内检查保证，跨 domain 的接管由消费端负责，分册写明这一代价变化。
- 推进循环索引化：`_retirements` 待完成集用 HashSet 加按 owner 的反向索引；`scope.Owners` 改为 HashSet 或带删除标记的列表；`RecomputeFailureClosureUnsafe` 只对脏根的可达子图重算；`Suspend` 用 owner 自身的 domain 归属判定代替 `_owners.Where`。
- 新增受控并发回归：随机交错的 Borrow/Dispose/CreateScope 与 BeginRetirement、ChangeAsync 与 AcquireView 的交错；断言每个 owner 恰释放一次、无 lease 在 Released 后可读、FinalCompletion 不伪报成功。用例数据固定种子以便复现。

### 4.5 W5：生成器增量管线与兼容（O09、O10、O11）

目标：IDE 内增量缓存有效，产品逻辑出库，生成器可被当前支持的全部 SDK 加载。

- `ModuleModel`、`ModuleDependencyModel` 改为纯字符串与原始值的 record，持有可比较的 `LocationInfo` 而非 `Location`；别名解析所需的程序集到 alias 映射由 `CompilationProvider.Select` 一次计算后 Combine，不在每个依赖上遍历引用。
- 拆分 `ModuleCatalogGenerator.cs` 为建模、校验、发射三个文件，公开类型不变。
- 删除 `InferDisplayName` 的 `DataGrid` 前缀剥除；仅保留去 `Module` 后缀与驼峰分词。需要自定义显示名的消费者使用 `DisplayName`。该行为变化列入兼容说明。
- 删除 `Visit` 死分支。
- Roslyn 依赖降至能被 .NET 8 与 .NET 10 SDK 共同加载的最低版本（候选 4.8.0，需实际验证所用 API），并在验证工作流增加 .NET 8 SDK 加载 analyzer 的消费用例；若确有 5.0 专属 API，则在分册写明"生成器要求 .NET 10 SDK"并在 README 标注。

### 4.6 W6：API 形态与工程保障（O12、O13、O14、O15）

- 新增 `ModuleHost.TryAcquireView(IEnumerable<ModuleId>?, out ModuleHostLease?)` 与 `ModuleServiceScope.TryBorrow<T>(out LifetimeLease<T>?)`；现有抛异常入口保留。
- 事务内部以 `ModuleGraphException` 或同形的携带诊断异常传递结构化诊断，`RejectedChange` 直接转发原诊断列表；`ATOMUICHANGE001` 进入 `ModuleDiagnosticCodes`，编号缺口以注释说明历史原因，不复用。
- 运行时项目启用 `IsAotCompatible`、`IsTrimmable`，使 trim/AOT 分析器在构建时运行；引入 `Microsoft.CodeAnalysis.PublicApiAnalyzers`，以 1.0.3 已发布包为 `PublicAPI.Shipped.txt` 基线，新符号进入 `PublicAPI.Unshipped.txt`。
- `CreateModules()` 标 `[Obsolete]` 指向 `CreateModulesAsync`；`ResolveModules()`、`FreezeServices()` 保留，分册注明其失败路径可能同步等待回滚。
- 发布工作流在打包前运行消费者验证脚本，并要求目标 SHA 的验证工作流已成功；资产清单加入 Generator `buildTransitive` 属性桥。
- 文档治理：引入 `CHANGELOG.md` 承载版本状态，总览状态表只保留能力与正式目标两列；证据 JSON 与 gz 归档迁移到发布附件或独立分支，仓库保留索引与哈希。修正总览状态表列数不符的行。

## 5. 兼容边界与不做的事

- 公开签名只增不删；行为变化限于 Transient 释放时机、`InferDisplayName` 不再剥除 `DataGrid`、跨 domain 重复接管不再被进程级拒绝。三项均进入分册与 CHANGELOG。
- 不新增项目或包；不引入 Microsoft DI；不把领域 State 类型化；不默认父子 scope 继承解析。
- 不以"重建 Host"或"重建 provider"代替任一项修复。
- 不在本方案内承诺下游产品接入或跨平台资格。

## 6. 分阶段交付

| 阶段 | 范围 | 前置 | 验收 |
| --- | --- | --- | --- |
| P0 基线与红色用例 | 为 O02、O05、O07、O09 各写最小失败用例；锁定 1.0.3 公开 API 基线；补并发回归骨架 | 本地 1.0.4 修复先提交 | 用例确实失败；基线文件生成；`git diff --check` 通过 |
| P1 事务统一 | W1 全部；W3 中 `LifetimeRetirement` 拆分 | P0 | 200 个既有用例不改断言通过；启动、重配、关闭共用一条代码路径；R01 场景有回归 |
| P2 寿命与簿记 | W2；W3 其余；W4 的 ledger 相关部分 | P1 | Transient 在最后 lease 归还后释放；A05/A06 场景按 ledger 验证；原 LuminEditor 1000 轮场景托管净增长不高于当前记录 |
| P3 并发收敛 | W4 其余 | P2 | 服务层与宿主内部路径无 `Monitor.Wait`（公开同步 Stop 的并发通知屏障按既有合同保留）；单一重入检测；单次 lease 归还开销不随待退休数增长；并发回归通过 |
| P4 生成器 | W5 | 可与 P1 并行 | IDE 增量缓存命中（以 `GeneratorDriverRunResult` 的 step 状态断言）；48 个既有用例通过；.NET 8 SDK 消费用例通过或分册明确要求 |
| P5 工程保障 | W6 | 可与 P1 并行 | PublicAPI 无未声明变更；AOT 分析器 0 警告；发布工作流门禁生效；CHANGELOG 与证据迁移完成 |

P4、P5 不依赖 P1–P3，可并行；P1–P3 严格串行。任一阶段不得以 Skip 掩盖红色用例。

P0 已于 2026-11-05 在工作区完成（未提交，1.0.4 修复的提交前置仍待授权）：

- 红色用例位于 `tests/AtomUI.Foundation.Tests/Optimization/P0RedBaselineTests.cs` 与 `tests/AtomUI.Foundation.Generator.Tests/P0RedIncrementalCacheTests.cs`，以 `Category=P0Red` 标识，覆盖 O02、O05、O07、O09、O16，均按预期原因失败。该标识只用于跟踪归属阶段，修复后随修复一并移除，不得在发布验证中用来过滤。
- 并发回归骨架位于 `ConcurrencyRegressionTests.cs`。模块变更与视图读者交错稳定通过；Borrow 与 scope 退休交错因 O16 不确定失败，暂带 `P0Red` 标识，O16 修复后移除。
- O16 归入 P2，与 Transient 请求 owner 一起处理：已准入构造期间的依赖解析不得因所在 scope 开始退休而失败，退休应等待该构造完成并按正常屏障释放。
- 公开 API 基线：`src/AtomUI.Foundation/PublicAPI.Shipped.txt` 由 1.0.3 源码 `0d0a361` 经 PublicApiAnalyzers 5.6.0 生成，共 422 个符号；当前工作区相对 1.0.3 无公开 API 变化，Unshipped 为空。RS0016 与 RS0017 提升为错误。

P1 已于 2026-11-05 在工作区完成（未提交）：

- 新增内部 `ModuleChangeTransaction` 与 `ModuleChangeStage`。启动是从 Generation 0 空基线出发的初始事务，手动阶段 API 是其分步推进，首次 Initialize 由事务发布 Generation 1；重配由同一事务驱动；Generation 0 到 1 的特判已移除。
- 旧代退休、候选撤销与最终关闭共用一个退休调度器，三者只在停止与完成委托上不同，诊断阶段名与观察者报告方式逐字保留。退休资源改为强类型 `ModuleRetirementResources`。
- `ModuleChangePreparation` 新增 `StopCurrent()` 与 `CompleteCurrentAsync()`，已登记到 `PublicAPI.Unshipped.txt`。
- `LifetimeRetirement` 拆为内部 `LifetimeRetirementBatch` 与公开回执，公开签名不变。
- 验证：Release 双目标构建 0 警告；运行时非红色用例 206 个连续 6 轮通过，新增 4 个事务统一与 R01 验收用例；生成器 48 个通过；红色集合与 P1 前一致。真实消费者的生成器四种配置、net8/net10 JIT 与 net10 trim 通过；Native AOT 在还原阶段被 NuGet 漏洞审计拦截（`Microsoft.DotNet.ILCompiler` 10.0.8，GHSA-p3jp-9v83-76w9），需更新本机 SDK 后补跑，未取得本轮 AOT 证据。

P2 已于 2026-11-05 在工作区完成（未提交）：

- O16：服务层对已准入工厂的依赖解析不再复查自身 scope 的关闭状态；生命周期层允许正在构造的 owner 捕获仍为 Active 的依赖。在正在退休的 scope 内新建依赖仍被拒绝。O16 红色用例与并发骨架转绿，并发骨架 20 轮重复无失败。
- W2：Transient 由内部请求 owner 承载，首次使用归还后、无捕获与 release dependent 时由推进器自动退休。Singleton 捕获 Transient 产生 Info 诊断 `ATOMUISVC004`，已登记到 `PublicAPI.Unshipped.txt`。O02 红色用例转绿。
- W3：新增内部 `ModuleServiceRetirementLedger`，取代运行时的 `LiveScopes`、`CandidateRetirements`、`ShutdownRetirement` 字段、绑定集的 scope 列表与 `ScopeRegistration` 类。实施中发现若以绑定集对象为键，存活 scope 会经 ledger 根住历史绑定视图，既有保留用例拦下了这一点，已改为按绑定集身份对象分组。
- 既有保留用例中三个用反射读取内部计数的辅助方法改读 ledger，断言未改。
- 验证：Release 双目标构建 0 警告；运行时非红色用例 217 个连续 10 轮通过，新增 5 个 Transient 验收用例；生成器 48 个通过；剩余红色用例为 O05、O07、O09。真实消费者的生成器四种配置、net8/net10 JIT 与 net10 trim 通过；Native AOT 仍被同一 NuGet 安全审计拦截。原 LuminEditor 1000 轮场景不在本仓库，未重跑。

P3 已于 2026-11-05 在工作区完成（未提交）：

- 服务激活的准入检查与 owner 预留在 Runtime 锁内原子完成，生命周期层为此拆出预留与构造两个内部入口。服务层 3 处 `Monitor.Wait` 与 `Pending` 计数全部移除，不再需要 `PrepareTransitionAsync`。
- 新增内部 `CallbackFrame`，宿主与生命周期域共用同一套沿异步上下文传递的重入检测，替代 `[ThreadStatic]` 计数。O05 转绿。
- 实施修正：生命周期层两处通知屏障等待保留。既有用例 `ConcurrentCallsWaitForAllStopNotifications` 锁定了"并发第二调用者等待全部 Stop 通知"的合同，删除等待会改变它的断言；本层只改为用回调帧识别重入。
- 所有权表与 owner 编号下沉到 `LifetimeDomain` 实例，去掉进程级锁。
- 推进循环索引化：首次快照、可完成 batch 与已完成 scope 各有索引，owner 与 scope 集合支持 O(1) 移除，排序按 domain 内 owner 编号。O07 转绿。失败闭包重算未索引化，只在失败路径触发。
- 验证：Release 双目标构建 0 警告；运行时 219 个用例连续 15 轮全部通过，运行时侧已无红色用例；生成器 48 个通过，O09 留待 P4。真实消费者的生成器四种配置、net8/net10 JIT 与 net10 trim 通过；Native AOT 仍被同一 NuGet 安全审计拦截。

P4 已于 2026-11-05 完成：

- 生成器拆为模型、建模、校验、发射四个分部文件。增量模型只含字符串、基元、值相等集合与位置信息，不再持有 `ISymbol`；位置以语法树引用加区间保存，诊断仍是源码位置，`#pragma` 抑制有效。O09 转绿，至此全部红色用例已转绿。
- 引用别名索引改为每个编译只建一次，以弱引用表按编译缓存，不再对每个依赖遍历全部引用。
- 删除显示名推断里的 `DataGrid` 前缀剥除与 `Visit` 死分支。
- 生成器改为针对 Roslyn 4.8.0 编译。验证脚本新增 `--sdk8-loader`，验证工作流启用它；本机只有 SDK 10，该用例未在本机运行，资格以 CI 结果为准。
- 验证：Release 双目标构建 0 警告；生成器 52 个用例通过（新增 3 个：模块编辑重新生成、诊断保持源码位置、无产品前缀）；运行时 219 个通过。真实消费者除 Native AOT 外全部通过。

P5 已于 2026-11-05 完成，至此 P0–P5 全部完成：

- 新增 `ModuleHost.TryAcquireView` 与 `ModuleServiceScope.TryBorrow`；`TryBorrow` 对调用期间并发开始的暂停不作吸收，已写入分册。
- 变更拒绝保留候选阶段的结构化诊断；`ATOMUICHANGE001` 以 `ModuleDiagnosticCodes.ModuleChangeRejected` 公开，退役编号在注释中说明。
- `CreateModules()` 标记 `[Obsolete]`；两个有意覆盖该入口的既有用例以局部 pragma 抑制 CS0618，断言未改。
- 运行时开启 `IsAotCompatible` 与 `IsTrimmable`，Release 构建 0 警告。
- 发布工作流：打包前运行消费者验证，资产清单加入 `buildTransitive` 属性桥，发布到 nuget.org 前要求目标提交的验证工作流已成功。
- 验证：运行时 224 个用例连续 10 轮通过（新增 5 个），生成器 52 个通过；真实消费者除 Native AOT 外全部通过。

未完成项及原因：

- 证据 JSON 与 gz 归档迁出仓库：需要发布附件或独立分支，均属对外发布或新分支，留待单独决定。
- `PublicAPI.Unshipped.txt` 并入 Shipped：按规则在正式发布时进行。
- Native AOT 消费者证据：本机 SDK 10.0.300 自带的 `Microsoft.DotNet.ILCompiler` 10.0.8 被 NuGet 漏洞审计（GHSA-p3jp-9v83-76w9）拦截，需更新 SDK 后补跑。
- .NET 8 SDK 装载用例：本机无 .NET 8 SDK，只在 CI 运行。
- 原 LuminEditor 1000 轮联合场景：不在本仓库，未重跑。

## 7. 验收矩阵

| 场景 | 核心断言 |
| --- | --- |
| 启动即变更 | 空基线到 S 的提交结果 Generation 为 1；手动阶段 API 与一次性 Initialize 得到相同实例与工厂次数；预取消不推进阶段 |
| 关闭即变更 | 当前状态的发布者收到 StopCurrent 与 CompleteCurrentAsync；持有模块 lease 的领域状态在后者中归还后关闭完成；被取代的发布者不再收到；无 participant 时结果与现状一致 |
| Transient 释放 | Root 与子 scope 借用 Transient，最后 lease 归还后 Release 恰一次；被 Singleton 捕获的 Transient 随 Singleton 释放；并发多次借用无重复释放 |
| 簿记 ledger | 128 次成功候选退休后 ledger 为空；失败与 Pending 票据保留到最终关闭；重复关闭不重试 Release |
| 锁与重入 | Stop 回调跨 await 后再调用 Stop 被正确识别为重入；工厂内阻塞等待不再可能形成构造函数自等待 |
| 推进复杂度 | 10k owner 下单次 lease 归还耗时不随 owner 总数增长 |
| 生成器增量 | 修改非模块文件时 catalog 输出步骤为 Cached；extern alias、嵌套类型、关键字转义输出不变 |
| API 兼容 | PublicAPI 基线无删除；`TryAcquireView` 在重配窗口返回 false 不抛异常；旧入口行为不变 |
| 发布门禁 | 未通过验证工作流的 SHA 不能发布；包内含 `buildTransitive` 属性桥 |

## 8. 对正式分册的修订清单

以下修订已于 2026-11-05 完成；实施只能对照修订后的分册条目进行，不得先于分册改动实现。

| 分册 | 修订内容 |
| --- | --- |
| [模块宿主](modularity.md) | "阶段与操作协调"增加"启动与关闭均为组合变更事务的特例"；"关闭合同"增加当前代领域状态的 StopCurrent/CompleteCurrentAsync 顺序；"同步入口与诊断"标注 `CreateModules` 弃用及 `TryAcquireView` |
| [模块动态重配事务](module-reconfiguration.md) | "职责与权威"写明事务状态机；"取消、并发与最终关闭"把最终关闭纳入同一事务；"实施用公开协议"追加 `PrepareTransitionAsync` |
| [服务登记](service-registration.md) | "显式受管模式"表中 Transient 行改为请求 owner 语义；"scope 与退休索引的保留责任"改为 ledger 表述；增加 `TryBorrow` |
| [受管生命周期](managed-lifetimes.md) | "角色与关系"增加请求 owner；"退休调度与两个屏障"写明 batch 与回执的分离；"Host 与外部容器接入"更新跨 domain 接管代价；"必须验证的性质"追加并发与复杂度性质 |
| [源码生成](source-generation.md) | "输入与输出"写明增量模型约束与显示名推断规则；"编译和打包边界"写明 Roslyn 最低版本与 SDK 要求 |
| [构建与打包](build-and-packaging.md) | 增加 PublicAPI 基线、AOT 注解与发布门禁 |
| [验证合同](../engineering/verification.md) | 增加并发回归、推进复杂度基准、生成器增量缓存与 .NET 8 SDK 加载矩阵 |
| [架构总览](overview.md) | 分层图把"阶段协调"与"重配事务"合并为一个事务节点；状态表改为两列并链接 CHANGELOG |

## 9. 风险与未决

- W1 触及 Host 全部路径，是本方案唯一的高风险项。缓解：P0 先把 200 个既有用例作为不可修改的行为快照，P1 只允许新增用例。
- W2 改变 Transient 释放时机。现有消费者若依赖"Transient 在 scope 关闭前一直存活"，需在 CHANGELOG 标注；评估认为该依赖不符合既有分册对 Transient 的描述，但需下游确认。
- O06 下沉后，两个独立 domain 可分别接管同一实例。这是把进程级保证换成实例级保证；需要跨 domain 唯一性的消费者应共用一个 Runtime。
- Roslyn 降版本是否可行取决于 `ResolveForwardedType`、`IsFileLocal` 等 API 的最低版本，P4 第一步先验证。
- 推进复杂度基准的阈值需以原 LuminEditor 1000 轮场景的实际 owner 规模标定，不以本文给出的 10k 作为最终门槛。
