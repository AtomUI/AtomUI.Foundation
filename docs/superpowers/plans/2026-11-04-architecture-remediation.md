# Architecture audit remediation implementation plan

> **For agentic workers:** Use TDD and scoped implementation/review, preserving the current branch and checkout. Track the finite tasks below; do not add release/platform work.

**Goal:** 修复原 scope 泄漏、其失败汇总伴随缺陷，以及 2026-11-04 审计 A01–A08。

**Architecture:** 保留单一 Host/发布点/Runtime 与释放权威；在原协调器表达恢复失败隔离，在实际关系图维护失败闭包；scope 登记、owner batch 与最终回执各有明确责任。生成器保留符号引用身份并完整验证依赖输入。

**Spec:** `docs/architecture/{module-reconfiguration,managed-lifetimes,service-registration,source-generation}.md` 中本轮新增合同。

## Global constraints

- 用户已明确授权先更新架构再修复；当前分支/目录，无分支/worktree/clone，无提交/升级/发布。
- 公共 API 保持兼容，不新增第二 Host、DI 容器、预算账本或释放权威。
- 失败回执与真实 Pending 继续保留，Release 一次，完整 Stop 屏障、锁外用户回调及 Runtime → Lifetime 锁顺序保持。
- 新测试先 RED 后 GREEN；原联合场景仍为50轮预热+1000轮完整文档历史、1,048,576 bytes门槛、逐轮费用回基线、最终费用0。
- 旧证据复用；同一批源码稳定后合批双目标构建、运行时/生成器套件与适用包消费者，所有详细证据保存在一个修复批次。
- 审计 R01–R04 是未纳入本轮的协议/资格裁定项，不据此扩大到新公开关闭协议、CI重构或编译器升级。

## Review focus

- 同一 scope 跨绑定包装与异步 Release；最终成功必须已完成内部索引收尾。
- 失败闭包收缩不得解除执行中/失败 Release 的真实 pin。
- 恢复失败仅隔离实际影响范围；最终关闭覆盖保留的 transition。
- 整代清理部分失败，仍保留失败实例，但不再强留成功独立实例。
- extern alias 与 global 同名模块存在时，生成 Required 仍绑定原程序集。

## Task 1: Lifetime live graph maintenance (A03/A05)

Files: `src/AtomUI.Foundation/Lifetimes/LifetimeDomain.cs`; tests `Lifetimes/LifetimeRetentionTests.cs` 或聚焦新文件。

- [x] RED：A失败保留B，Active B新增再归还C capture，C退休实际Release=1且成功；A仍失败、B仍保留。
- [x] RED：128个成功 prerequisite 在长期 dependent 存活时可回收，snapshot不保留已满足关系。
- [x] 实现按失败根和当前有效边重算派生闭包、解除成功ordering双向边；保留现有Releasing capture延迟归还规则。
- [x] GREEN：精确新增项及现有全部Lifetime测试。只改此task文件，不碰service/host/generator。

## Task 2: Host transaction safety and retained resources (A01/A02/A07)

Files: `Modularity/ModuleHost.Changes.cs`, `ModuleHost.Retirement.cs`, 必要的同一coordinator字段；tests `Modularity/ModuleHostAuditRegressionTests.cs`。

- [x] RED：Resume抛错后影响集准入拒绝、独立模块可读、再Change拒绝、Shutdown可结束。
- [x] RED：State/AffectedModules getter与枚举中的Shutdown立即返回重入拒绝，Host仍Initialized。
- [x] RED：B失败、独立C成功关闭，Host存活时C弱引用可回收，B失败仍被重复关闭报告。
- [x] 统一封存用户准备成员；原协调器保留恢复失败隔离及transition退出责任；细化退休资源保持粒度，分离失败证据和实际失败资源。
- [x] GREEN：新增项及全部Modularity测试。

## Task 3: Scope registration and retirement receipts (original leak/A06/failed aggregation)

Files: `Modularity/Services/{ModuleServiceScope,ModuleServiceBindings,ModuleServiceRuntime}.cs`，必要内部LifetimeContracts回执组合接线；tests `Services/ModuleServiceRetirementRetentionTests.cs`。

- [x] RED：原4000scope/跨绑定包装不累积；真实reader继续等待；scope失败异常仍被候选回执汇总；异步Release和局部/整体竞争Release一次。
- [x] RED：128成功空候选票据可回收；Pending/失败候选仍参与最终关闭；共享根不保留原绑定注册。
- [x] 使用单一可解除scope登记身份和有序最终收尾；不将完整绑定视图永存到共享根。不用fire-and-forget观察器作为成功完成条件。
- [x] 真实owner batch与scope既有回执共同汇总，失败快照不依赖可清空owner列表；成功票据从runtime待汇总集合裁剪。
- [x] GREEN：新项、全部Service测试及Lifetime/Host相关回归。

## Task 4: Generator identity and dependency normalization (A04/A08)

Files: `Generator/Modularity/ModuleCatalogGenerator.cs`, diagnostics若需要；tests `ModuleCatalogGeneratorRegressionTests.cs`。

- [x] RED：null数组不崩溃；null元素/数组类型明确诊断；alias-only生成可编译；alias/global同名不同程序集绑定正确。
- [x] 保留可发射程序集别名，必要时明确诊断歧义，不能静默改身份；先规范化所有TypedConstant再模块验证。
- [x] GREEN：全部Generator测试，生成后编译与Required语义程序集断言。

## Task 5: Integration qualification and evidence

- [x] 批量Release双目标build、runtime/generator套件；`git diff --check`及文档链接。
- [x] 审查最终差异及失败路径，无未解决的本轮必修项。
- [x] 唯一候选包身份（仅验收，不修改版本源/不推包源）与现有net8/net10消费者；必要trim/AOT合批实际运行。
- [x] 同一候选运行LuminEditor原ResourceAuditBenchmarkTests，发现1项，保留原1000轮及账本断言；不得以缩小用例替代。
- [x] 一个权威修复证据记录保存源码指纹、包SHA、命令、数量与结果；更新本计划完成状态和架构实现状态。

## Decisions and execution state

- Ruling: 已有审计方案和本轮用户的“先更新…再开始修复”构成本轮范围授权；不重复请求设计许可。若实现需要超出这些合同的公开能力，另行裁定，不扩大任务。
- Ruling: 分离源码职责可独立处理，构建和运行日志统一串行调度，避免共享输出竞争。详细运行事实只进入本轮证据记录。
- Ruling: scope失败汇总检查发现Domain在pre-handoff/Stop失败owner压缩后也会丢失最终失败事实；在原Domain以不可变事实保留并去重，物理owner仍可回收，独立局部退休不染入其他失败。原GC测试的“没有owner记录”要求保留，末尾Domain.Dispose成功预期改为原失败可观察。这属于同一失败回执责任的根因补齐，不另建service错误账本。
- Final review: 对同一登记责任补齐全局scope收尾，对A07同时解除Host和释放闭包两条整绑定强根；生成器补混合秩数组及关键字namespace的精确语义回归。原193/45批次保留为历史，本轮最终资格等待这些修正及对应回归完成。
- Final resolution: review三项Important均由精确RED→GREEN解决；同一A07链还以“声明但未实际捕获的依赖”单变量回归证实并解除注册/缓存强根。最终Release 200/200、Generator48/48、六条JIT/trim/AOT与原Lumin1000轮（36,696 bytes、费用0）、Core84/84、联合1/1通过；详细事实仅见[权威修复证据](../../engineering/evidence/architecture-remediation-20261104.json)。Lumin默认依赖/输出已恢复、源码工作区干净；本仓库未提交或发布，不修改下游TaskId状态。
