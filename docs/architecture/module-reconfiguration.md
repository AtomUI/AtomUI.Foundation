# 模块动态重配事务

本文定义同一个已初始化 `ModuleHost` 上的模块集合增量变更合同，以及它与消费端领域配置的联合提交边界。模块身份、Required 图和生命周期仍由[模块宿主](modularity.md)唯一管理；服务激活及释放继续遵循[服务登记](service-registration.md)与[受管生命周期](managed-lifetimes.md)。

## 状态与范围

1.0.3 实现本能力，发布产物由版本工作流从发布提交构建并校验。1.0.2 没有公开的运行中模块集合变更协议；1.0.3 的发布不改变该历史版本事实，也不代表真实 Core 联合资格已完成。当前公开接线见末节；后续扩展仍须先更新正式合同。

2026-11-03 本机批次的资格为运行时 170/170、生成器 37/37，以及本地 1.0.3 独立 NuGet 消费者的 net8/net10 JIT、裁剪和 macOS arm64 Native AOT 真实运行，详见[权威记录](../engineering/evidence/module-reconfiguration-20261103-local-1.0.3.json)。原始领域退休探针源码未改，修复前后分别证明循环存在与无需手动归还即可完成；该分支也进入六种包消费路径。LuminEditor 实际联合接入及其他机器/平台不在这些历史结果中。

上述为 1.0.3 历史批次的范围。本轮恢复失败隔离、回调重入与失败保留粒度修正已在未发布候选上实现；新的 200/48 测试、六条包消费及真实 LuminEditor 原场景/联合消费者资格见[2026-11-04 修复证据](../engineering/evidence/architecture-remediation-20261104.json)。不能把未发布工作区结果反写为旧包行为已修正。

1.0.2 的源码基线为 `a0fbe111813b70c902a3f613ec4c73be13032c51`。Host 构造时捕获 registrations 和启用根，解析结果被缓存；已初始化后的重复 `ResolveModules()` / `InitializeAsync()` 不变更集合。原始消费者先初始化 A，再修改原 registrations 和 enable 列表加入 B，观察到 A 构造一次、B 为零、A 引用保持。这与静态 Host 的输入快照和幂等初始化合同一致，不能解释为动态变更成功。

本设计接受预编译的显式注册与启用根变更，支持新增、局部停用及合同允许的替换。保留同一 Host、未受影响模块和共享服务的实例身份。运行期程序集发现、未知代码加载、程序集卸载及编辑器 Model/View/Undo 重建不属于本能力。沿用既有运行时包和单一服务所有权，不增加必需的 DI 依赖。

## 职责与权威

| 事项 | Foundation | 消费端领域参与方 |
| --- | --- | --- |
| 模块身份、描述、Required 闭包及级联 | 唯一解析和校验权威 | 提交真实公开注册与选择意图 |
| 候选模块与通用服务 | 创建、隔离、实例保留和自身清理 | 提供已编译领域绑定与服务需求 |
| 领域配置与执行目录 | 保存参与方已封存的候选，不解释业务字段 | 校验配置、Provider/Command 选择与执行绑定 |
| 当前视图与提交决定 | 协调同一次决定，公开一致的组合视图 | 经该视图消费领域载荷，不另设独立提交决定 |
| 实际工作与借用 | 管理服务 owner、lease 和退休证据 | 提交领域准入、工作退出、结果读者和释放依赖事实 |
| 文档及预算 | 不接管领域模型或预算根 | 保持文档身份、正文、Undo/Redo 与实际保留资源计费 |

领域参与方通过通用协议接入；Foundation 不引用 LuminEditor 类型。Required 图与实际 owner 使用关系分属不同用途，不在 Core 再实现模块拓扑，也不把服务借用误当作新的 Required 边。

按 2026-11-05 批准的[架构优化方案](2026-11-05-foundation-architecture-optimization-proposal.md)，本事务由 Host 内部的显式状态机驱动，状态为 Validated → Created → Configured → Prepared → Suspended → Published → Retiring → Completed，每个状态只有一种合法失败出口，退休所需资源以强类型记录。初始启动与最终关闭是同一事务的两个特例：启动是空组合到 S，关闭是 S 到空组合；[模块宿主](modularity.md)的阶段阶梯和关闭合同由该事务实现，不另写一套清理调度。状态机是内部实现约束，公开合同仍是本分册的结果、回执与回调顺序。

## 变更输入与结果

输入必须包含所属 Host 和当前基线的可核对标识、目标注册与启用根，以及显式的停用、替换或级联意图。基线标识不可跨 Host 使用，已退休代次的标识不可复用。注册值或 factory 委托的引用相等不足以推断一次替换；替换必须明确目标及预期旧绑定。无法满足候选隔离、提交前恢复和实际使用者处理合同的替换应在准备阶段拒绝。

结果分别表达无变化、准备拒绝、基线过期、提交前取消、已提交及提交后清理失败，并保存基线、实际提交代次、诊断和清理进度。不得将“未提交”与“已提交但仍有旧资源”混为一个失败布尔值。没有变化时不重放模块配置、初始化或工厂。

提交完成与退休完成是两个观察点。提交回执必须说明新代是否已公开；真实清理另有可重复观察的完成屏障。清理尚在等待旧 reader 时不能伪报 Released，也不能因请求方取消等待而丢失操作。候选清理失败同样保留真实对象、异常及所持借用，以供后续关闭和领域账本关联。

## 图预检与影响集

候选图复用现有重复身份、未知启用根、缺失 Required 和环的验证，在任何候选模块或业务 factory 前拒绝无效输入。静态图预检不声称已经验证尚未产生的服务或领域执行绑定。

移除启用根不等于禁止其作为依赖启用。显式停用 B 而仍有启用根 C Required B 时，默认拒绝；只有显式级联请求才由 Foundation 计算应停用的根和完整闭包，不能静默保留 B 并报告停用成功。

影响集同时考虑模块选择变化、注册替换、实际服务捕获、作用域关联以及领域参与方提供的真实工作关系。A 无 Required B 但捕获 B 服务时，A 的相关 owner 仍受影响。未受影响实例才享有保持原引用的保证；不能把实际使用者误列为无影响，也不能因此默认重建整个 A 模块或 Infrastructure。

长期捕获需要明确的重绑或退休处理。若停用 B 必须释放一个仍拟保留的 A 服务，而该服务没有合法的重绑合同，应在提交前拒绝这一候选。不能释放 A 正在使用的 B，也不能以永久悬挂的借用冒充已完成的局部停用。实际影响集在封准入时复核，防止准备期间新建的使用关系漏出关闭集合。

## 候选准备与隔离

候选在同一 Host 的协调下拥有独立的生命周期记录、服务登记和领域载荷，不创建第二 Host。受影响的新实例按依赖和共同配置屏障准备；未受影响实例不重放配置或初始化。候选可借用合法的稳定服务，但其私有新增或替换绑定不能进入当前请求的解析路径。

领域参与方先取得候选上下文和贡献，再封存配置、目录及执行表；Foundation 校验候选服务绑定，双方完成可清理的私有初始化。准备只组合已有静态 factory 和调用入口。所有可拒绝提交的准备工作都在发布前完成，不能提前编辑当前文档、公开订阅或执行不可补偿的外部动作。需要订阅时必须保持私有或具备真实撤销能力；提交后的退休异常按清理结果报告。

工厂成功交接的对象立即记账；失败只反卷候选实际成功记录，不调用当前集合的全局 Shutdown。清理不使用已取消的业务 token。候选归还所借旧服务时不能取得其释放权；候选清理失败可以继续持有借用，但不能借清理传播把仍合法服务的当前共享 owner 退休或关闭准入。现有运行时不能满足这一隔离时，须扩展接线后才取得本能力资格。

## 准入暂停与单一提交

事务顺序如下，其中 G 表示一份一致的模块、服务绑定和领域配置视图：

```text
Current G
  -> validate graph and affected set
  -> prepare private Foundation and domain candidates
  -> suspend affected admission and quiesce required work
  -> recheck baseline, readiness and cancellation
  -> one commit decision: publish coherent G+1
  -> stop and retire old affected owners
  -> observe real final completion
```

提交前的准入暂停是事务协调状态，不是 `LifetimeOwnerState.Retiring`。它阻止受影响的新工作和解析进入，保留已有实例、scope 与 lease；准备拒绝或取消时可以按原配置恢复准入。需要取消在途请求时，先封完整影响集，再向全部相关工作发送停止请求，随后在锁外等待必须退出的工作。已取消的请求不复活，由调用方重新发起。

现有 `LifetimeDomain.Stop()`、owner/scope 的 `BeginRetirement()` 及模块 Shutdown 都是不可逆的退休行为，不能作为提交前可恢复的暂停入口。提交前不得调用旧实例任意不可恢复的 Stop 钩子；参与方若只提供这种钩子，应在准备时拒绝其恢复能力声明。可补偿的工作停止与不可逆 owner 退休必须有不同的协议边界。

提交前排空的是会破坏发布一致性的工作，不是强制等待所有长期捕获和结果 reader 归零。仅在消费者 Release 中归还的依赖必须留给提交后的退休推进，否则会形成自依赖等待。允许跨提交存活的 reader 持有原代视图及真实 lease；它们不得参与新代的选择或把过期结果写入新代，领域写入仍校验自身修订和准入条件。

准备校验和用户代码在临界区外完成。临界区只复核基线、关闭请求和取消条件，并将已准备的组合视图发布为当前代；不等待 I/O、不执行 factory、领域提交回调或其他任意用户钩子。不得以两个顺序更新的“当前模块”和“当前领域配置”指针代替联合提交。

准备对象的 `State`、`AffectedModules` getter 以及影响集合的枚举同样属于用户执行入口，必须纳入与领域方法一致的重入保护，在锁外取得稳定值。发布只使用已经读取并封存的值，不能在决定期间重新调用用户 getter。getter 内再次请求本 Host 的变更或关闭必须立即收到重入拒绝，不能等待当前变更本身。

需要跨模块、服务和领域读一致性的调用先捕获一次组合视图，其后都从该视图读取；已准入读者看到合法的 G 或 G+1。视图使用权还必须连接真实资源保活，快照本身不延长已释放实例的寿命。原有分散属性读取不自动取得跨读取事务一致性，不能用它们的顺序访问冒充新合同。底层可使用锁、不可变根或等价机制，内部布局不属于公开合同。

## 服务绑定与身份保持

服务候选遵循[服务动态重配边界](service-registration.md#动态重配边界)。旧代登记保持冻结；新代有独立的冻结验证。未受影响 Singleton 和合法共享 Scoped 服务沿用原 owner，候选持有的是借用或绑定引用，不再次接管实例。涉及旧、新实现并存时，实例缓存必须区分绑定身份；只按服务类型缓存不能决定跨代复用或替换。

提交后新请求解析新代合法绑定；已准入旧请求与结果 lease 继续指向原对象。同一个服务类型的新旧 owner 可以在退休期间并存，但每个对象仍只有一个释放权威。同一代内原有中立覆盖规则与受管重复登记拒绝规则保持原合同。

仅新增 B 或停用独立 B 时，A 模块、稳定 Infrastructure 及未受影响共享服务保持原引用，未选 D 的 factory 始终为零。Model、双 View 逻辑身份、正文及 Undo/Redo 由 Core 保持；关闭整个 Host 后重建 A/B 不能取得本合同资格。

## 提交后的退休与失败

成功发布后，对完整旧影响集关闭新准入，分发全部 Stop 和模块退出票据；第一通知屏障完成前不释放 owner，也不在单个回调内等待另一尚未收到 Stop 的参与方。最终释放复用既有 owner 就绪调度、lease、捕获与失败保留机制，不强制要求所有依赖同时归零。局部退休的附加边界见[动态重配与局部退休](managed-lifetimes.md#动态重配与局部退休)。

Stop 屏障之后，立即启动领域完成和模块读者排空，让两者共同推进。已发布领域状态可以在 `CompletePreviousAsync()` 中归还其真实 `ModuleHostLease`，不能等模块关闭完成才调用该方法。受管服务 Release 仍等待模块回调结束，最终结果汇总领域、模块与服务三方；领域失败也不能跳过其他可完成的清理。旧代退休与候选撤销共用这条推进路径。

发布后旧资源清理失败不回滚当前代。记录真实失败及保留依赖闭包，继续其他独立可释放 owner；新代仍为当前代。预算由消费端按实际保留对象继续计费，不能因提交成功或等待超时归还额度。尚有真实使用的票据保持未完成，已知失败保留与在途使用分别表达。

退休记录分开保存不可变结果、仍未退出或清理失败的实际资源，以及已经成功退出的独立项。一个模块清理失败不授权永久保留整代组合、全部注册工厂或成功关闭的独立模块。失败项及其真实依赖闭包继续保持可达，成功项应从内部保留根解除；外部调用方自己保存的旧视图仍遵循原租约合同。

## 取消、并发与最终关闭

| 边界 | 必须行为 |
| --- | --- |
| 准备前或准备期间取消/拒绝 | 当前代继续服务；仅清理候选实际成功项 |
| 暂停准入后、提交前取消/拒绝 | 恢复合法旧配置准入，保持原实例；取消的请求由调用方重发 |
| 旧领域恢复本身失败 | 保留恢复失败诊断和受影响范围的隔离，不把暂停的旧状态重新开放；独立模块仍可读取，后续变更拒绝，最终关闭仍可执行 |
| 取消与提交竞争 | 在同一决定边界确定结果；先取消则不发布，先提交则报告已提交 |
| 发布后调用方取消或等待超预算 | 只结束其等待；不撤销新代，不取消真实清理 |
| 候选基线过期 | 不覆盖新当前代；清理该候选，返回可诊断的过期结果 |
| Host 关闭与变更竞争 | 决定前阻止发布并撤销候选；决定后纳入新当前代和所有待退休旧资源 |

同一 Host 的变更与关闭由同一操作权威协调，不引入第二状态机竞争当前集合。变更决定串行化；若允许并行准备，提交仍须校验基线。已有初始化幂等、回调重入拒绝和关闭共享结果合同继续有效，候选失败不得触发当前 Host 的终态回滚。

恢复失败不是普通的取消恢复成功。协调器保留该次影响集与服务准入暂停，直到最终关闭接管其退出责任；失败候选仍独立撤销。对外回执维持未提交及原取消/拒绝原因，并明确附带恢复失败诊断，不虚构恢复能力或把旧领域改成“正常”。此处不新增自动重试/修复领域状态的公开能力。

最终 Host 关闭覆盖当前集合、已交接的未提交候选，以及所有未完成或清理失败保留的旧资源；不能只关闭最新代。重复等待复用原票据和失败记录，不重试已经尝试的 Release。

最终关闭通过同一事务把当前代作为旧影响集退休：Host 保存发布当前 `State` 的准备对象，关闭时在模块读者排空之前启动 `StopPrevious` 与 `CompletePreviousAsync`，与模块回调及服务 Release 并行推进，和本分册“提交后的退休与失败”对旧代的要求一致。没有参与方时领域完成立即成功。此前最终关闭对当前代领域状态没有退休协议，是审计 R01 循环接线风险的根因；本修订以协议而非自动 Dispose 关闭该缺口。

## 验收与证据

动态变更的场景矩阵及包消费资格集中在[动态重配事务验收](../engineering/verification.md#动态重配事务验收)。本次实现的详细运行证据集中在[2026-11-03 本地 1.0.3 批次](../engineering/evidence/module-reconfiguration-20261103-local-1.0.3.json)及其压缩日志；总览只更新实现状态及其证据链接。此前批次保留为历史证据，不覆盖后来复现的退休失败。静态消费者、已有生命周期测试或文档完成均不能关闭动态变更资格。

本设计依据 LuminEditor 的 `docs/engineering/foundation-1.0.2-capability-gaps.md` 中同一 Host 原始复现与联合事务要求。该文件及其链接的权威批次记录保留消费端版本、哈希和原始日志；不在 Foundation 文档复制这些详细账本，也不以本设计更新改变消费端任务状态。

## 实施用公开协议

以下为 1.0.3 的公开协议：`ModuleHost.Current` 返回 `ModuleHostView`，保存所属 Host、代次、注册、模块、服务、受管绑定和领域 `State`；`AcquireView(IEnumerable<ModuleId>? modules = null)` 返回可释放的 `ModuleHostLease`，为实际使用的模块保活，省略集合时保活全部模块。跨代保留视图的使用者必须持有相应 lease，领域对象继续遵循自身的租约合同。Generation 0 属于初始装配；正式读准入从已初始化的视图开始，指定模块的借用通过索引取得，不扫描 Required 图。

`ChangeAsync(ModuleChangeRequest, CancellationToken)` 返回 `ModuleChangeResult`。请求捕获 `ModuleHostView` 基线、目标 registrations/options、显式 disabled/replaced 模块身份、级联选择和可选 `IModuleChangeParticipant`。状态使用 `ModuleChangeStatus`，退休通过 `ModuleChangeRetirement.FinalCompletion` 观察；其 `ManagedRetirement` 在服务 Stop 建立后暴露真实 `LifetimeRetirement`，可以从首次及最终快照核对 owner、reader、状态和失败，未接入服务时为空。提交回执与清理结果分离。

领域参与方的 `ConfigureAsync(ModuleChangeContext, CancellationToken)` 只配置候选，`PrepareAsync(ModuleChangeContext, CancellationToken)` 在候选服务冻结后返回 `ModuleChangePreparation`。准备对象保存待发布 `State` 和领域实际使用关系对应的 `AffectedModules`，通过 `SuspendAsync`、`ResumeAsync`、`AbortAsync`、`StopPrevious` 和 `CompletePreviousAsync` 参与可恢复暂停、撤销和旧领域退休。Foundation 将领域影响身份与模块、受管服务影响集合并，再统一关闭宿主准入；领域工作/结果仍由原权威提供真实退出证据。所有这些回调都在提交锁外；提交只接管预先封存的 State。初始集合仍由原装配合同建立，新协议不暗中重放原参与方。

`IModuleHostReconfigurationLifetime.ServiceRuntime` 让自定义 Host lifetime 适配器公开它实际拥有的 `ModuleServiceRuntime`；运行时本身实现此接口。未适配的自定义 lifetime 不能被假定支持局部变更，应明确拒绝；不通过另建 Runtime 绕过。

2026-11-05 批准方案追加：`ModuleServiceRuntime.PrepareTransitionAsync(previous, next, CancellationToken)` 以可等待方式排空在途工厂后返回 `ModuleServiceTransition`，Host 使用该入口；同步 `PrepareTransition` 保留为兼容入口。`ModuleHost.TryAcquireView` 在重配窗口返回 false 而不抛出，签名见[模块宿主](modularity.md#同步入口与诊断)。

请求的实际构造签名为：

```csharp
ModuleChangeRequest(
    ModuleHostView baseline,
    IEnumerable<ModuleRegistration> registrations,
    ModuleHostOptions options,
    IEnumerable<ModuleId>? disabledModules = null,
    IEnumerable<ModuleId>? replacedModules = null,
    bool cascade = false,
    IModuleChangeParticipant? participant = null)
```

`ModuleChangeStatus` 为 NoChange、Rejected、StaleBaseline、Canceled、Committed。提交后清理失败通过原 `ModuleChangeRetirement.FinalCompletion` 的结构化结果观察，不改写已经返回的提交决定；调用方可对该任务使用 `WaitAsync` 结束自己的等待。准备对象的 `State` 可以显式为 null，不沿用旧载荷冒充新状态。私有域撤销与服务释放共同推进，不能先等待域 scope 最终释放、再开放它所依赖的服务退休。

完整可执行公开调用见[独立包消费者](../../tests/fixtures/managed-runtime-consumer/Program.cs)，不以未编译的假想 API 示例作为资格。
