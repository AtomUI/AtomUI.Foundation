# 服务登记与运行时接线设计

Foundation 保留中立描述模式，并增加显式受管模式。1.0.1 已实现中立描述与现成实例获取，但不包含受管实例激活、scope 和真实 lease；1.0.2 在既有程序集内实现本分册的受管 API。两种模式的解析与释放权威必须明确，不能因登记 API 名称相似就混用。

## 中立描述模式

| 现有入口 | 行为与所有权 |
| --- | --- |
| `AddSingleton<TService>(instance)` | 保存现成实例和 Singleton 描述；调用方仍负责寿命与释放 |
| `AddSingleton<TService,TImplementation>()` | 保存实现类型和 Singleton lifetime，不创建实例 |
| `AddScoped<TService,TImplementation>()` | 保存实现类型和 Scoped lifetime，不创建 scope |
| `AddTransient<TService,TImplementation>()` | 保存实现类型和 Transient lifetime，不执行工厂 |
| `GetRequiredService<TService>()` | 返回显式 instance；类型描述本身不会使 Getter 激活服务 |
| `Freeze()` | 幂等地冻结登记；不创建 provider 或转移所有权 |

每个服务类型最后一次登记覆盖此前登记；Replace、Remove、Clear 保持这一语义，冻结后拒绝修改。instance 入口继续支持现有的值类型与委托实例。

`ModuleServiceDescriptor` 保留“实现类型或实例恰有一个”的合同。Descriptors 是只读集合视图；它不保证实例对象本身不可变，也不是借用凭证。

外部容器适配把类型、实例和 lifetime 映射到消费端容器，由该容器承担自己的构造与释放合同。默认 Host 不另建 provider；CLI 一类消费者不应因为升级而得到第二套单例或释放权威。

调用方 instance 可在模块关闭后继续存活。旧裸 Getter 没有自动保活保证，也不应因新增受管模式而被统一改成自动 Dispose 或关闭后失效。

## 显式受管模式

受管模式必须由宿主显式选择，并具备完整接线：静态 typed factory、服务与 scope 绑定、实例 owner、真实借用、Stop 及 Release。只增加一个工厂委托或引用计数，不代表此模式已经实现。

| lifetime | 实例边界 |
| --- | --- |
| Singleton | 当前 Host/runtime 根唯一，不是进程静态单例 |
| Scoped | 同一明确 scope 分组内唯一；不同分组隔离，共享通过原 owner 关联 |
| Transient | 每次激活创建，创建时明确归属作用域/请求 owner，不能成为无 owner 临时对象 |

受管运行时的注册表达与旧 descriptor 分开。中立类型描述如需执行，必须有匹配的静态工厂与依赖绑定；缺失时受管配置冻结失败，指出服务和注册来源。中立模式仍允许该描述合法导出，不为其反射构造。

新增工厂 API 使用独立名称。不得添加会使旧 `AddSingleton(delegateInstance)` 改选“执行 factory”语义的同名重载，也不添加新的 class 约束来破坏原 instance 入口。

## 激活与所有权交接

描述登记、执行绑定冻结与业务激活分开。读取目录、拒绝未知服务或禁用模块不执行 factory。冻结验证匹配、可分析的依赖与捕获规则；任意用户代码内部隐藏的调用不能被声称已完成静态验证。

工厂成功返回实例后，接入层立即登记真实 owner，再公开可用句柄。交接前构造方清理局部资源，交接后 owner 是唯一 Release 权威；失败回滚不能丢失已经接管的对象。

多个契约别名引用同一个 owner，不为同一实例建立多个释放所有者。重复接管必须拒绝，且不能因为拒绝新登记而释放已经属于旧 owner 的实例。

借用与长期捕获都连接实际释放权威。短寿命服务不能通过未经登记的裸字段捕获获得长期使用资格。服务绑定记录来源 ModuleId，但实例 owner 身份独立；一个模块可以拥有多个 scope 与实例。

## scope 与领域关联

scope 是通用拥有/工作分组，不默认提供父子容器继承解析。关联与借用应能表达共享实例：消费者关闭只归还自己的借用，原 owner 退休才关闭该实例的新准入。`Root` 是 Runtime 的根关闭边界，不是短期消费者 scope；它拒绝从较短寿命的子 scope 建立共享关联，避免子 scope 关闭升级为整个 Runtime 退休。

Core 将 Composition、Model、View、Attach 映射到这些分组，Foundation 不内置领域名称。两个消费者共享同一 owner 的合同见[受管生命周期](managed-lifetimes.md#scope-关联与外部所有者)。

## 动态重配边界

本节是 1.0.3 [模块动态重配事务](module-reconfiguration.md)的服务合同。已发布 1.0.2 的中立集合和受管 Runtime 冻结后均不接受登记变更；其 Replace/Remove、scope 退休和 `ShareFrom` 不提供候选服务代次协议。

重配在独立候选登记上收集服务声明并冻结校验，不解冻或修改当前代。每项候选绑定可追踪其模块来源、绑定身份和实际 owner；替换需显式声明，不能仅因服务类型相同就覆盖正在服务的实例。缺失、循环、寿命捕获及领域需求按实际可知阶段验证，无效静态模块图仍先于任何业务 factory 拒绝。

跨代复用必须同时保留合法 scope、原实例和唯一释放 owner。未受影响 Singleton 继续是该 Host/runtime 根下的原实例；Scoped 共享沿用原分组和借用关系。替换绑定的新旧实例可在退休期间并存，缓存须能区分绑定身份，不得把旧类型缓存误当作新代实例，也不按代次无差别重建全部 Singleton。

候选私有新增绑定不向当前请求公开；候选仅借用稳定旧服务，撤销不释放其原 owner，清理失败仍保留真实借用。发布后新准入解析新代，原代请求和结果 reader 继续经原 lease 使用旧对象。跨模块、服务和领域的一致读取使用共同视图，不能各自更新当前指针。

中立登记记录贡献模块来源，用于从旧冻结配置派生候选而不重放保留模块。不同贡献方被遮蔽的登记保持可恢复；同一贡献方、同一服务键已被覆盖的历史操作则压缩，避免新代继续保留已经被替换的调用方对象。Clear 只保留该贡献方最新的清除边界，不改变公开覆盖/移除语义。

中立 instance 的调用方所有权、裸 Getter 的既有语义、同代中立覆盖规则和受管重复登记拒绝规则继续有效。严格动态重配的外部容器接入须证明候选隔离、实例保留和逐 owner 退休；标准 provider/scope 的全量重建不能代替这些行为。

## 外部容器与严格资格

Microsoft DI 等容器可继续服务中立描述导出。若作为严格受管路径的构造内核，必须证明它不会同时自动捕获并释放同一实例，且满足逐 owner 清理和保留依赖闭包。包装 provider、延迟整个 provider 的 Dispose 或在外层 catch 一次，均不能代替这些证明。

严格路径使用真实 lease；裸 Getter 不自动具有这项资格。外部实例只有真实 owner 提供可验证的借用与退休接入后才可纳入。消费端已经把 raw reference 交给不遵守借用合同的代码时，Foundation 不声称能够拦截后续任意方法调用。

当前实现不增加适配包或新运行时包；受管服务 API 位于 `AtomUI.Modularity.Services`。具体公开签名如下。实施计划只安排工作，不另定义一套语义。

## 运行时公开接线

实现保留现有两个包，在运行时程序集增加 `AtomUI.Modularity.Services`。`ModuleServiceRuntime` 是显式运行时及 `IModuleHostLifetime` 适配器；`ModuleHostOptions.Lifetime` 或 builder 的 `UseLifetime` 选择它，默认值为空。`IModuleHostLifetime` 位于 AtomUI.Modularity，定义 `void Freeze()`、`void Stop()`、`ValueTask<ModuleHostResult> CompleteShutdownAsync()`；Host 在冻结服务时调用 Freeze，关闭模块回调前 Stop，全部回调后完成并等待收尾。

Runtime 提供 `Lifetimes`、`Root`、Freeze、Stop、CompleteShutdownAsync 与 IAsyncDisposable。三个独立登记方法为 `AddSingletonFactory<TService>`、`AddScopedFactory<TService>`、`AddTransientFactory<TService>`，参数固定为来源 ModuleId、`Func<ModuleServiceActivationContext,TService>`、`Func<TService,ValueTask> release`、可选 `Action<TService> stop`、`params Type[] dependencies`，TService 为 class。配置冻结前允许登记，冻结时校验缺失/循环与 Singleton 对 Scoped 的传递捕获。

`ModuleServiceActivationContext.GetRequiredService<T>()` 只在当前 factory 执行期间使用，且只允许已声明依赖；返回值由当前实例 owner 的真实 capture 保活。`ModuleServiceScope` 提供 `Borrow<T>()`、`CreateScope()`、`ShareFrom<T>(ModuleServiceScope source)`、`BeginRetirement()` 和 IAsyncDisposable。共享接入保留原实例，通过目标 scope 的真实关系持有原 owner，不转移释放权。

配置中每种受管服务类型只允许一个登记，重复 Add 明确拒绝；这不改变旧中立集合的覆盖语义。factory context 还限于当前同步 factory 线程，不能将它移交线程或在返回后继续解析。新运行时通过 `ModuleServiceValidationException.Diagnostics` 报告配置错误，通过 `ModuleServiceShutdownException.Diagnostics` 报告异步 Dispose 的最终清理错误。

## 动态绑定公开接线

动态重配实现增加 `ModuleServiceRuntime.Bindings` 和 `CreateCandidate(ModuleServiceBindings baseline, IEnumerable<ModuleId> removedModules)`。`ModuleServiceBindings` 提供与 Runtime 同签名的三个 typed factory 登记方法、`Freeze()`、`Root` 和 `BindScope(ModuleServiceScope)`；模块配置上下文的 `ManagedServices` 指向本次装配的绑定集。初始旧登记入口仍配置初始绑定；候选只登记新增或明确替换的模块，沿用未受影响登记的绑定身份。

`PrepareTransition(previous, next)` 返回 `ModuleServiceTransition`，其 `AffectedModules` 包括实际服务捕获与共享 scope 效果。未激活的保留服务若仍绑定被替换的依赖，也在提交前拒绝，不能等到首次请求才失败。factory 的依赖在 Freeze 时绑定到精确登记身份，不通过历史整代配置链或服务类型重新选择。

Transition 的 `Stop()`、`CompleteRetirement()` 与 `Dispose()` 分别执行不可逆通知、最终释放推进及提交前恢复；公开 `Retirement` 在 Stop 建立后返回真实票据。绑定集自身的同名 Stop/Complete 方法及 `BeginRetirement()` 用于撤销候选拥有的新对象和私有 scope；旧共享 owner 不被再次接管。候选新服务不能提前共享进当前 scope；成功发布后的共享则使用 Runtime 当前组合视图中的真实绑定判断。

已绑定 Runtime 的当前 `Bindings`/`Root` 从唯一 Host 组合视图取得，不维护第二个发布指针。旧、新绑定共用原 Runtime 的寿命域，cache 以登记身份区分；成功 Release 后清除相应缓存，保留失败对象及实际依赖。具体公开协议仍须通过包消费者验证后才取得交付资格。
