# 模块系统与宿主设计

本文定义 Foundation 的模块身份、Required 图和 Host 生命周期合同。已发布的 1.0.1 提供公开模块机制，但尚不具备本文完整的操作协调与关闭合同；1.0.2 发布范围实现 Host 协调、异步创建、无副作用预检、统一回滚及可选受管生命周期接线。受管 owner 收尾的附加规则见[受管生命周期](managed-lifetimes.md)。

## 模块身份与依赖

`ModuleId` 通过 `FromModuleType<TModule>()` 取得。消费者引用真实模块类型，不复制身份算法，不根据硬编码类型名重建模块身份。`FromSerializedValue` 用于明确的序列化或兼容边界，不用于新的手写静态模块声明。

`ModuleDescriptor` 保存身份、展示名称、来源和 Required 依赖。`ModuleRegistration.For<TModule>(descriptor, factory)` 绑定显式工厂，描述身份必须与注册模块类型匹配。注册与描述的组合不提前执行工厂。

目录和依赖图验证重复身份、缺失 Required 依赖与环，并给出依赖先于消费者的拓扑序。结构化诊断应保存完整路径，不能只把路径放入 Message。图解析不构造模块或业务资源。

## 显式启用与组合

注册不等于启用。Host 通过 `ModuleHostOptions` 或 `ModuleHostOptionsBuilder.Enable<TModule>()` 选择根，由 Required 闭包带入依赖。空启用集合法，表示运行零模块；面向使用者的启动示例必须显式启用所需根。

跨程序集由各包显式导出注册，应用合并后交给同一 Host 校验。看到依赖类型元数据不意味着已经取得其注册；缺失注册应在任何工厂执行前失败。Foundation 不运行期扫描 DLL、发现未知实现或复制另一份消费者依赖图。

## 运行中模块集合变更边界

1.0.2 Host 捕获构造时的注册与启用根；修改原输入集合、创建新 options 或重复 Resolve/Initialize 都不更新运行中的集合。这是现有静态合同，不通过取消防御性复制或重置已完成阶段来扩展。

1.0.3 的[动态重配事务](module-reconfiguration.md)在同一 Host 上准备候选并提交增量变更，复用本分册的身份、Required 解析和操作协调。它单独记录变更基线、候选进度与提交结果，保留现有 `ModuleHostPhase` 的成员和数值；候选失败只清理候选成功项，不走当前集合的全局失败回滚。

初始启动的共同配置屏障和全局失败清理规则仍适用于初始集合；重配只对候选新增或替换实例推进这些准备阶段，不重放未受影响实例。该协议与领域准备共享一次提交，覆盖关闭竞争、过期候选和未完成旧资源；单独的 `RunModulePhaseAsync` 分发不承担模块集合变更职责。

## 阶段与操作协调

共同阶段顺序为 Resolve → Create → PreConfigureServices → ConfigureServices → PostConfigureServices → FreezeServices → Initialize。所有模块完成某个配置阶段后才进入下一共同阶段；初始化按拓扑序。关闭按逆拓扑次序调度全体模块，Required 依赖等待其消费者回调真正退出；独立模块可分别推进，不因一个模块等待读者而阻止另一个模块归还该读者的 lease。初始失败、局部退休和最终关闭共用同一调度规则。

保留 `ModuleHostPhase` 既有成员和数值。已完成阶段与正在执行的操作分开表达；实现可用内部协调器和 ledger，不以互相独立的 `_isInitialized` / `_isShutdown` 判定终态。

按 2026-11-05 批准的[架构优化方案](2026-11-05-foundation-architecture-optimization-proposal.md)，Host 只有一种组合变更协议：初始启动是从空组合到 S 的变更，最终关闭是从 S 到空组合的变更，运行中重配是 S 到 S′ 的变更。三者共用候选准备、单一提交、退休调度与失败隔离，由[动态重配分册](module-reconfiguration.md)定义的显式事务状态机驱动。本节的逐阶段可观察值、手动阶段 API 和幂等 Initialize 保持不变：手动 Resolve/Create/配置入口映射为同一事务的分步推进，不重跑工厂；首次 Initialize 的提交结果即 Generation 1 的当前视图，不再有 Generation 0 到 1 的特判。初始集合失败时退休的是尚未发布的候选，与候选失败清理共用一条路径。

| 调用情形 | 目标行为 |
| --- | --- |
| 第一次 Initialize | 从当前合法已完成阶段继续，不重建已创建模块 |
| 同一 Initialize 仍在执行时再次调用 | 共享实际执行，加入者只取消自己的等待 |
| 手动 Resolve/Create 后调用 Initialize | 从后续配置阶段推进，保持工厂次数 |
| 冲突阶段调用或非法越级 | 返回明确的阶段错误，不更改状态 |
| 用户回调重入同一 Host | 明确拒绝，不能等待自身 |
| 关闭与初始化重叠 | 关闭后续阶段准入，等待当前回调退出，不允许初始化覆盖关闭终态 |

不持有内部锁执行工厂、生命周期钩子、诊断接收器或其他用户代码。关闭请求不承诺强制中断不合作回调；停止推进发生在可验证的阶段边界。

## 取消与失败

首次 Initialize 的令牌控制实际启动，并保持传入模块回调的原令牌身份。加入者的取消只结束自己的等待。原请求真正取消时，完成可完成的回滚后再向仍等待的调用方报告取消；回滚不使用已取消的请求令牌跳过清理。

回调抛出的 `OperationCanceledException` 只有在本次操作令牌已取消时才作为调用方取消处理；操作令牌未取消时，它是该回调阶段的失败，必须保留模块和阶段诊断。

可取消阶段在进入工作前及提交阶段状态前检查原令牌；即使显式启用集合为空，预取消也不得被当作成功或推进阶段。

每个工厂返回非 null 实例后立即记入宿主 ledger，再执行下一工厂。取消、null 返回、后续工厂异常和配置失败都必须能够找到此前已创建实例。工厂内部尚未交接的局部资源仍由构造方清理。

初始化失败时逆序 Shutdown 所有已创建模块，包括尚未完成初始化者。模块 Shutdown 必须能处理部分构造/配置状态，不为清理读取尚未绑定的服务或创建占位资源。

`RunModulePhaseAsync` 保持正序/逆序委托分发。`rollbackOnFailure=false` 时，异常返回失败、取消保留取消语义，Host 保持调用前已完成阶段；既有副作用由领域协调器补偿。true 时进入统一回滚，不能把 false 当成自动补偿机制。

## 关闭合同

重复关闭共享同一实际操作及保存的结果。每个模块 Shutdown 最多尝试一次；失败也记账，后续调用不能把首次失败改成 Success。独立模块的清理异常聚合，其他模块仍获得关闭机会。

关闭请求在操作开始前已取消时不启动操作；一旦开始，调用方取消只取消等待，实际清理继续。运行中或部分关闭不能因为调用方退出而丢失已创建实例与清理结果。

默认中立模式只处理模块生命周期，不接管外部服务实例。受管模式还必须：关闭全体准入、分发全部 Stop、接纳真实退出票据、按 owner 就绪条件推进释放，并以最终结果确认关闭；模块 Shutdown 返回不是完整退出证明。具体顺序以[退休协议](managed-lifetimes.md#退休调度与两个屏障)为准。

最终关闭中的当前代领域状态与旧代遵循同一退休协议。Host 保存发布 `ModuleHostView.State` 的参与方准备对象；关闭时先分发全部 Stop，再在模块读者排空之前启动领域完成，使 `StopPrevious` 与 `CompletePreviousAsync` 和模块回调、受管服务 Release 并行推进。持有模块 lease 的领域状态可以在 `CompletePreviousAsync` 中归还 lease，无需手动解除循环。没有参与方的中立消费者行为不变：空领域完成立即成功。Host 不自动 Dispose 任意 State，也不把领域 State 类型化。

`ModuleRegistration.For` 的工厂与 Shutdown 是模块生命周期合同。模块额外实现 IDisposable/IAsyncDisposable 不自动增加第二条 Dispose 路径；通用服务的释放权威单独登记。

## 同步入口与诊断

同步 `CreateModules` 保持兼容入口，但其失败回滚可能同步等待异步关闭，不能作为 UI 上下文安全的依据。新接线应优先使用当前提供的 `CreateModulesAsync(CancellationToken)`；保留旧签名时必须明确其同步上下文限制，并按验证合同测试，不能通过阻塞 UI 获得异步关闭资格。`CreateModules()` 标记 `[Obsolete]` 并指向 `CreateModulesAsync`；`ResolveModules()` 与 `FreezeServices()` 保留，其失败路径同样可能同步等待回滚。

重配窗口内的视图获取是常规状态而非异常。新增 `TryAcquireView(IEnumerable<ModuleId>? modules, out ModuleHostLease? lease)`：Host 未运行、模块正在关闭或准入已暂停时返回 false 且不抛出；现有 `AcquireView` 抛异常入口保留。参数非法（未启用的模块）仍抛 `ArgumentException`。

事务内部以携带结构化诊断的异常传递失败，`ModuleChangeResult.Diagnostics` 直接转发候选阶段的原诊断列表，保留模块 id、诊断码与阶段，不把诊断拼接成字符串后再包装。变更拒绝码 `ATOMUICHANGE001` 进入 `ModuleDiagnosticCodes`；历史编号缺口以注释说明，不复用。

诊断接收器是观察者，其异常不得遮盖原始模块错误或打断回滚。原始错误及接收器故障分开保存，不将后者递归报告给同一接收器。Result 成功语义为不含 Error，Warning/Info 不自动使操作失败；1.0.1 仍按诊断数量判断成功，1.0.2 改为按 Error 严重级别判断。

当前 `ModuleHost.Validate()` 是无副作用的模块图预检，不执行模块工厂；服务配置尚未产生时不能声称其构造图已验证。目标 Result 成功语义及结构化错误已进入当前实现，仍须由验证合同持续保护。
