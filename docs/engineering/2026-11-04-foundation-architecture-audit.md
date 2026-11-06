# Foundation 1.0.3 架构审计

日期：2026-11-04。基线：`release/1.0`，`0d0a361cdef64f0000f19bb6ac5a8263c3b8e8aa`。

## 结论与范围

除已交接的 `_ownedScopes` 成功退休元数据累积问题，本轮确认 **8 项其他问题：3 项 P1、5 项 P2**。它们集中在事务恢复失败、回调准入、失败保留关系的收缩、成功历史裁剪及生成器的符号身份保持。当前单一 Host、单一组合发布点、显式 owner/lease 和中立服务兼容边界无需因此推倒重建；这些边界内部的失败及长期运行合同仍不完整。

本轮审查产品 C# 源码、正式架构合同、相关测试、根构建配置、两个包与两个工作流。宿主、生命周期内核、生成器/构建分别作只读检查，主审查核对服务绑定和跨层行为；未创建分支/worktree，未修改正式实现、测试、版本或发布配置。

完成条件限定为：识别其他缺陷、核对公开触发与真实后果、分级并给出根因层修复方向。没有执行下游产品全量验收、跨平台资格或新版本发布；本报告不覆盖正式架构合同，也不构成修复交付。

使用正式 1.0.3 两个包的实际 DLL，合批运行一个临时审计驱动，得到 13 条实际观测。命令退出 0 只表示驱动完成；逐项结果包含失败合同，不能当成测试通过。详细输入、输出、源码和包身份集中在[唯一证据记录](evidence/foundation-architecture-audit-20261104.json)及其压缩归档。没有修改产品源码，不重复运行未受影响的整套构建、AOT 和历史联合验收。

## 已确认问题

### A01 / P1：恢复失败后仍开放受影响旧代的准入

位置：[ModuleHost.Changes.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.Changes.cs)，第 214–220 行。

取消或拒绝发生于暂停之后时，`ResumeAsync()` 抛错只增加诊断，随后仍无条件清空 `_suspendedModules` 并解除服务 transition。Host 保持 Initialized、Current 保持旧代，却没有证据证明旧领域已恢复。

实际触发：先发布 `Running=true` 的领域状态；下一次变更把它暂停为 false，取消请求，Resume 抛 `resume failed`。结果为 `Canceled` 并包含 `ResumeModules` 诊断，但随后 `AcquireView([A])` 仍成功，拿到的旧状态 `Running=false`，Host 仍 Initialized。

这不是要求 Foundation 替领域恢复业务数据。缺陷是已知恢复失败后仍把受影响配置当成合法可用配置。应为恢复失败保留明确的隔离/失败状态和真实票据；仅在恢复成功后解除相应准入，独立范围可另行判断。不能通过吞掉 Resume 异常或强行改回状态绕过。

### A02 / P1：准备对象 getter 绕过回调重入保护

位置：[ModuleHost.Changes.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.Changes.cs)，第 177、191 行；关闭等待见 [ModuleHost.Coordination.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.Coordination.cs)，第 128 行。

`AffectedModules` 和 `State` 是用户可重写成员，却未通过 `InvokeCallback` 调用。getter 内的 `ShutdownAsync()` 因此被当成外部请求接受；Shutdown 等 Change 结束，而 getter 若等待 Shutdown，会形成自等待。

安全探针未让进程永久挂起：State getter 调用 Shutdown、记录其任务当时未完成，然后返回。实际结果为 Change 被拒绝，内部 Shutdown 真正成功，Host 进入 Shutdown。相同调用放在受保护的领域回调中应立即得到重入拒绝，不应启动真实关闭。自等待结论来自这条已验证的准入与等待链，并非本轮制造一个无法清理的挂起进程。

应把所有用户可执行的准备成员纳入同一回调边界，读取并封存 State/影响集后再参与决定；集合枚举也属于用户执行边界。不能只给某个 getter 增加特例。

### A03 / P1：合法解除依赖后，失败保留闭包不会收缩

位置：[LifetimeDomain.cs](../../src/AtomUI.Foundation/Lifetimes/LifetimeDomain.cs)，第 144–147、190–195、418、591–607 行；[LifetimeOwner.cs](../../src/AtomUI.Foundation/Lifetimes/LifetimeOwner.cs)，第 114–116 行。

`BlockedByFailure` 只置 true，不在真实依赖解除后重新计算。公开触发为：A 捕获 B；A 的 Release 失败，使仍为 Active 的 B 进入失败保留关系；随后 B 捕获 C，再合法归还这个 capture。此时 B→C 的真实边已经删除，但 C 的保留标记仍在。

实际结果：C reader 为 0，C 自己没有失败，退休时仍进入 `RetainedByFailedOwner`，Release 次数为 **0**，最终结果失败且自身失败记录为 0。A 的失败继续保留是正确的；已经解除关系的独立 C 永不释放是错误的。

应以失败根和当前真实关系维护保留闭包，在允许的依赖解除后收缩派生阻塞；失败 Release 尚未确定结果时的 pin 仍须保留，不能把这项修复实现成遇到 Dispose 就无条件清 pin。

### A04 / P2：生成器丢失 extern alias，可能悄悄改变模块依赖身份

位置：[ModuleCatalogGenerator.cs](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 514–518、866 行。

原属性可以精确写 `typeof(ext::External.CoreModule)`，但生成器把符号统一转为 `global::External.CoreModule`，丢失引用别名。

已运行两个对照：

- 只有 ext 引用时，原源码无编译错误，生成代码出现 `CS0400`。
- 另有一个全局引用程序集也定义同名类型时，原源码和生成后源码均为 0 编译错误；原属性指向 `AuditExternal`，生成 Required 的实际符号却来自 `AuditGlobalOther`。

后者不是名称显示错误，而是 Required 模块身份错误。应保留可发射的引用路径与实际程序集身份，或对无法正确生成的引用方式给出明确诊断；不能仅以生成后可编译作为身份正确的证明。

### A05 / P2：已满足的 WaitForRelease 关系永久保留历史 owner

位置：[LifetimeDomain.cs](../../src/AtomUI.Foundation/Lifetimes/LifetimeDomain.cs)，第 164–165、574–578、660–665 行；[LifetimeOwner.cs](../../src/AtomUI.Foundation/Lifetimes/LifetimeOwner.cs)，第 29–30 行。

成功释放会排队推进 dependent，但不删除 `Prerequisites` / `ReleaseDependents` 双向关系。长寿 owner 连续等待短寿 prerequisite，再逐个成功退休 prerequisite，会保留累计历史 owner、票据及快照。

实际 128 次公开操作后，GC 后 **128 个已 Released owner 仍存活**；长寿 owner 最终成功退休的快照仍包含 **128 个历史 prerequisite**。这是成功路径的独立保留机制，不依赖 `_ownedScopes`。

应在成功前置条件得到满足后解除不再需要的强关系；如需历史诊断，采用不再持有真实 owner 图的记录，并明确保留范围。失败和未满足的边不能一起删除。

### A06 / P2：成功的候选退休票据从不从 runtime 历史集合裁剪

位置：[ModuleServiceBindings.cs](../../src/AtomUI.Foundation/Modularity/Services/ModuleServiceBindings.cs)，第 109–111 行；[ModuleServiceRuntime.cs](../../src/AtomUI.Foundation/Modularity/Services/ModuleServiceRuntime.cs)，第 12、76–80 行。

`CandidateRetirements` 持续追加，并在最终关闭时读取；没有成功完成后的移除路径。即使 Host 自身已经忘记成功的候选撤销，runtime 仍保留其生命周期票据和结果。

实际创建并成功退休 128 个空候选后，runtime 集合和 GC 后存活票据均为 **128**。这里只用反射读取内部集合数量辅助诊断；创建、退休和结果观察均走公开 API，正式回归不应依赖该临时反射探针。

应裁剪已成功完成且无需承担真实退出责任的票据，继续保留 Pending 与失败票据。成功历史和必须汇总的失败/在途责任需要不同保留策略。

### A07 / P2：一个模块清理失败会保留整代已成功清理的独立模块

位置：[ModuleHost.Changes.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.Changes.cs)，第 202–203 行；[ModuleHost.Retirement.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.Retirement.cs)，第 7–27 行。

退休票据持有整个 baseline composition，只有整体成功才解除该引用。独立 B/C 同时移除、B.Shutdown 失败、C.Shutdown 成功时，Host 仍保留整个旧代，也包括 C 的实例和载荷。

实际 A/B 对照：C 携带 1 MiB 数组，丢弃调用侧旧视图与回执引用，Host 继续存活。全部关闭成功时 C 可回收；只有独立 B 失败时，成功关闭的 C 仍可达。它不是正在服务的外部旧视图保留，也不是已知 `_ownedScopes` 泄漏。

失败资源和依赖闭包应继续保留，但不应以整代对象作为永久保留的最小粒度。应拆开不可变失败证据、确实未退出的资源以及已经成功退出的独立项；不能通过直接丢弃整个失败回执修复。

### A08 / P2：生成器依赖输入在验证前崩溃或被静默丢弃

位置：[ModuleCatalogGenerator.cs](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 539–552 行；正式要求见[源码生成合同](../architecture/source-generation.md#可生成类型与错误定位)。

已运行合法 C# 输入：

- `[Module(Dependencies = null)]`：原输入 0 编译错误；生成器抛 `NullReferenceException`，产生 `CS8785`，生成目录数 **0**。
- `[Module(Dependencies = new[] { typeof(int[]) })]`：数组类型不是模块，按合同应诊断；实际生成目录数 **1**，没有生成器诊断，也没有输出编译错误。数组类型在转为 `INamedTypeSymbol` 时被过滤，声明的依赖被静默删掉。

应先处理空常量和全部类型形状，再按模块合同验证；不支持项必须诊断，不能在建模时过滤成“没有依赖”。

## 已知伴随缺陷，不重复计为新发现

前一轮已确认：scope 的 Release 失败，scope 回执失败，但候选整体退休回执成功、失败数为 0。底层会清空已完成 scope 的 owner 列表，候选汇总又只从该列表取 owner；保留失败 ScopeState 本身不足以保留汇总能力。

这不是原元数据泄漏本身，但已在前一轮报告，本轮沿用该证据，不再搭建相同复现或增加“新发现”数量。原外部 patch 尚未应用；其中 `OriginBindings` 引入的历史强引用风险也不属于当前 1.0.3 已存在缺陷。

## 协议风险与资格缺口

以下观测与已确认缺陷分开，不能直接转述为全部接线均已损坏。

### R01：最终关闭的领域完成与模块 reader 排空仍有循环接线风险

[ModuleHost.Coordination.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.Coordination.cs) 第 160、166 行先等待模块，再进入 lifetime `CompleteShutdownAsync()`。自定义 lifetime 若在 Stop 发出停止、在 Complete 中归还真实 ModuleHostLease，则 Complete 永远进不去。实际探针记录：Stop=true、CompleteEntered=false、Shutdown 未完成；手动归还后才全部结束。

但公开 lifetime 合同也明确把 Complete 放在模块回调之后，领域对象保持自身资源责任。因此这里不直接判为“Host 必须自动 Dispose 任意 State”，也不把采用了不相容接线的挂起当成内置 runtime 的已复现故障。需要在正式设计里明确：当前领域的 reader 归还由哪个阶段启动，如何与模块 drain 并行，以及它与服务 Release 屏障如何分开。旧代/候选的并行推进已经实现，最终当前代的扩展协议应形成同等清晰的接线与回归。

### R02：Domain.Stop 并发调用的返回不代表全部通知已分发

[LifetimeDomain.cs](../../src/AtomUI.Foundation/Lifetimes/LifetimeDomain.cs) 第 348 行在已有 retirement 时直接返回。实际 barrier 探针中首个 Stop 回调被阻塞，第二次 Stop 已返回，后一个 owner 尚未收到 Stop。

`_notificationBatches` 仍阻止真正 Release，本轮没有发现因此提前销毁资源。应明确 Stop 的并发返回语义，并验证 Host/适配器是否把它误用成完整通知屏障；不能据此声称存在提前 Release。

### R03：发布入口与同一提交的包消费资格未强关联

[发布工作流](../../.github/workflows/release-atomui-foundation.yml)允许指定任意 branch/tag/SHA；发布前执行 net10 单测及包内容校验，没有运行消费者验证脚本，也没有验证对应 SHA 的消费工作流已成功。资产清单还未要求 Generator 的 `buildTransitive` 属性桥。

当前 [verify.yml](../../.github/workflows/verify.yml) 已包含源代码 PR 触发、真实生成器消费与适用 net8/net10 JIT/trim/AOT 入口，不能沿用旧审计的“没有 CI”结论。缺口是发布可以绕过这份资格，而非没有验证代码；本轮不推断外部 GitHub 分支保护，也不声称已发布包因此发生故障。

### R04：最低编译器版本和防重复接管的保留代价尚需明确

- Generator 引用 Roslyn 5.0，现有消费矩阵使用最新 SDK；net8 运行证据不证明 .NET 8 SDK 能装载 analyzer。本机只有 SDK 10.0.300，本轮不另装 SDK，也不把未运行项认定为不支持。
- 静态 CWT 保留 object→owner 映射以防重复接管，owner 又持有 domain。外部保留已 Released 裸实例可能延长整域可达性。这是防重复接管策略的设计代价，尚未取得足够证据将其列为独立缺陷；不能直接删除映射改变所有权合同。

## 修复收敛建议

建议先处理 A01/A02/A03 及此前失败汇总缺陷，再处理成功历史裁剪与失败保留粒度，随后补齐生成器符号身份及输入诊断。

根因层工作集中在四条边界：

1. 事务协调器统一所有用户执行入口，并明确恢复失败后的准入状态。
2. 以真实图和失败根维护保留闭包，兼顾扩展与合法收缩。
3. 明确 live/Pending、失败保留、成功历史的不同责任，统一裁剪索引、关系和票据，不逐集合做无合同清空。
4. 生成器在建模阶段保留完整符号身份与引用路径，对不支持输入给出原位诊断。

修复验收应先让本轮具体触发成为正式回归，再合批执行受影响套件与包消费者；原 LuminEditor 1000 轮性能场景仍是 `_ownedScopes` 问题的必要联合验收，不能用本轮缩小探针替代。没有发现新证据支持改建第二 Host、另加 DI 容器、强制重建 scope 或扩大包边界。
