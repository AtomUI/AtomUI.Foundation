# Foundation 生命周期扩展评估

日期：2026-10-26。状态：评估记录，已据此更新[正式受管生命周期设计](managed-lifetimes.md)及[服务接线设计](service-registration.md)，不作为第二份权威合同或实现完成证明。依据为 Foundation `0041863`、LuminEditor 本地工作 checkout `05cce8a` 的正式合同与实际代码，以及已核对的其他消费端。

结论：外部评估提出的四项能力应纳入 Foundation 的显式受管生命周期扩展：真实借用 lease、全部 Stop 后的统一退出屏障、逐 owner 清理与异常聚合、共享服务的唯一 owner 与 scope 关联。它们是通用服务寿命问题，不能用“编辑器专用”排除。保留旧的中立描述模式，扩展必须显式启用；不把 Model、View、命令结果和预算账本迁入 Foundation。

统一方案见 [Foundation 统一重构方案](2026-10-26-foundation-refactor-proposal.md)。审计结论修正及消费端证据见[完整性审计](../engineering/2026-10-26-foundation-completeness-audit.md)。

## 1. 事实与推论分开

Foundation 当前的类型服务登记确实不创建实例，但旧模块测试明确把它规定为中立描述；CLI 把描述映射到自己的 DI Host。这是原有分层，不宜追认为意外占位。Charts 与 SyntaxViewer 使用模块宿主并自行完成业务生命周期；它们可用不能证明 LuminEditor 要求的 Foundation-owned 服务分支已满足。

LuminEditor 的 [Foundation 模块化合同](../../../AtomUILuminEditor/docs/architecture/foundation-modularity.md) 第 226–254 行要求跨 owner 借用、全部 Stop、退休票据和真实 FinalCompletion；[命令合同](../../../AtomUILuminEditor/docs/modules/core/command-keybinding-catalog.md) 第 160–190 行要求调用与未退休结果保有原实例 lease，并明确容器借用分支尚未取得资格。这是明确的新消费需求，不依赖截图中的接口命名推断。

代码对应关系如下：

- [EditorCommandResult](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Commands/EditorCommandResult.cs) 第 19–44、49–67 行：结果有独立所有权与读者，方法返回不等于原 binding 可以释放。
- [EditorModuleBindings](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Modules/EditorModuleBindings.cs)：Shutdown 只调用 RequestSharedStop 并返回；最终退出另由票据表达。
- [CoreResourceScope](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Resources/CoreResourceScope.cs) 与 [Owned](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Resources/Owned.cs)：已有真实领域 owner/reader/保留资源处理，但明确不是 Foundation 服务容器。

因此不能说“当前 Foundation provider 已经提前 Dispose”：当前根本没有这个内建 provider 路径。准确结论是上一版拟议运行时若只在模块 Shutdown 返回后 Dispose provider，就不能履行这些新合同。

## 2. 对四项意见的判定

| 评估意见 | 判定与归属 | 不能被替代的保证 |
| --- | --- | --- |
| 缺少真实服务借用 lease | 采纳；Foundation 受管实例提供，Core 持有并移交 | 原 owner 知道真实活跃借用，并在最后借用退出前禁止最终 Release；外部裸引用包装不算 |
| 关闭前缺少退出屏障 | 采纳；Foundation 编排通用阶段，Core 提供真实工作/结果票据 | 全体准入先关闭、全体 Stop 已发出，才等待调用与 lease；模块回调返回、取消发出都不是完成依据 |
| 清理异常后保证不足 | 采纳；Foundation 对其管理的每个 owner 独立尝试并聚合 | B 清理失败后，独立且可释放的 A 仍获得清理机会；失败对象及仍可能使用的依赖保留，不报 Released |
| 共享 scope 与唯一 owner 未定义 | 采纳目标，限定实现；Foundation 定义通用关联与借用，Core 映射领域 | 两个 View 借用同一 Model owner；关闭一个 View 不释放 Model。无需引入父子 DI provider 或自动继承解析 |

第四项的双 View 示例本身可以通过一个 Model scope 加显式关联解决，不足以单独证明必须自研完整层级容器。需要新增的是可验证的共享所有权与受限访问，而不是任意 child-container 功能。

## 3. 为什么 Microsoft DI 包装不足

标准 scope 没有父子继承关系，外部实例的所有权与容器创建对象的所有权也不同，见 [Microsoft DI 指南](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines)。这些常规机制可以用于普通消费端，但不会自动产生跨 owner 的真实借用或结果退出屏障。

所核对的 [.NET 10 scope 源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ServiceProviderEngineScope.cs)中，Dispose/DisposeAsync 的逐项释放遇到异常可能中断余下循环；对外再 catch 一次只能报告失败，不能恢复“其他 owner 仍各自尝试”的保证。

更关键的是不能让同一个 disposable 同时由 Microsoft DI 和 Foundation owner 自动释放。严格受管路径推荐使用显式 typed factory 与唯一 Release 委托，由 Foundation owner 作为释放权威；Microsoft DI 继续适用于中立描述导出与普通宿主。若将它作为严格路径的构造内核，必须先证明没有双重释放、隐式捕获及不可逐 owner 清理的问题，否则不能取得这一资格。

只延迟整个 provider 的 Dispose 可以粗粒度保活，但会延长所有服务寿命，也仍不能证明逐 owner 清理与共享生命周期隔离，不作为四项需求已满足的依据。

## 4. Foundation 最小新增合同

### 4.1 owner、借用与捕获

每个受管实例只有一个释放 owner；多个服务别名引用同一 owner，重复接管同一实例必须拒绝。ModuleId 表示来源模块，不代替实例 owner 身份；同一模块可以产生多个 scope 和多个 owner。借用只持有能力，不获得 Dispose/转移权。

借用准入和退休转换必须原子有序：退休后新借用为零，已准入借用保持原对象有效。调用和结果各有真实保活关系；方法返回后结果仍被读取时，原实例不能释放。lease 的重复归还及句柄复制不能导致重复减计数。

跨 scope 共享必须引用原 owner，不重复创建、不转移所有权。长期捕获显式登记依赖；拒绝形成不能自行归还的保留依赖环。暂时调用 lease 与长期捕获区分，不能把任意运行中调用都误判为永久依赖。

### 4.2 全部 Stop 后统一等待

固定影响集 → 关闭全体准入 → 向全体 owner/参与方发出非阻塞 Stop/退休请求 → 收齐退出票据 → 在调度锁外按 owner 就绪条件交错等待退出与释放 → 汇总全部最终票据。

一个模块的 Shutdown 不能等待另一个尚未获得 Stop 的模块；收集阶段不 await 某个票据的最终释放。Core 的既有领域票据可通过适配接入，不能另造一个立即完成的假票据。

这里有两个屏障：全体 Stop 已分发，以及全部实际退休最终结束。两者之间必须能推进 ready owner 的 Release，不能先等待“所有 lease 都归零”。例如 A 持有 B 的长期借用，且只在 A.Release 归还：A 的调用/结果读者退出后先释放 A、归还 B，随后才能释放 B。把 B 的 FinalCompletion 放进释放 A 之前的全局等待会死锁。短期读者保活与这种长期捕获边必须在模型中区分。

领域票据若要等待 Foundation owner 释放，必须将其依赖接入同一退休关系；不能把一个同时持有该 owner lease 的黑盒 FinalCompletion 当作前置屏障。无法表示或形成闭环的依赖在准入时拒绝，不能靠超时或空票据通过。

### 4.3 清理失败与非合作读者

每个 ready owner 的 Release 独立捕获异常，继续处理独立的其他 owner；失败 owner 不自动重试，不丢失异常和保留资源记录。若失败对象仍可能访问依赖，该依赖也不能为了“全部归零”而先行释放。

失败保留有明确的终态：若 A.Release 已失败，且 A 仍保留 B 的长期依赖，A 的受管依赖闭包标记为 RetainedByFailedOwner/Blocked，不调用 B.Release，也不把 B 当成永远必须归零的未知读者。外部真实调用/结果 reader 未退出仍为 Pending；这些读者退出后，可以汇总带保留闭包的失败最终报告。失败报告完成不意味着 B 已释放。独立 C 继续清理，根 runtime/provider 不得在 finally 中绕过该保留闭包强制 Dispose。

关闭句柄区分首次快照与 FinalCompletion。首次快照可报告仍在使用的隔离 owner；FinalCompletion 等真实读者退出和最终清理尝试，结果可以是 CleanupFailed，不能把任务完成等同于全部资源释放成功。永久不退出的读者使最终等待持续；调用方取消或关闭预算耗尽只停止等待、报告保留，不强制销毁。

### 4.4 与 scope/provider 的关系

Foundation 可以提供通用 scope 分组、显式关联和受限借用；Core 决定哪个分组代表 Composition、Model、View 或 Attach。关闭 View 只停止其工作并归还 Model lease，Model 的最终退休仍由 Model owner 决定。

容器拥有的服务、Core 拥有的结果、调用方传入的实例必须分清释放权威。外部实例只有在其真实 owner 提供租约/退休接入时，才能进入严格借用分支；不能由裸引用推断其寿命。

## 5. 不下沉的职责与尚未覆盖的需求

命令路由、schema、Undo、Model/View 身份、修订与提交校验、Token/Frame/数组、领域预算及隔离政策保留在 LuminEditor。Foundation 不复制这些类型，也不建立第二预算根。Core 结果只需持有并归还真实的 Foundation 服务 lease，不把结果对象变成 Foundation 数据模型。

这四项还不等于完整动态模块启停。LuminEditor 文档另要求候选准备、原子提交代次和未受影响实例保持身份；它们需要独立的模块变更事务设计与验收，不能靠新增 lease 宣称完成。本次先使服务借用与退休路径可资格验收，不用重建整个 Host 冒充增量变更。

## 6. 必须新增的验收

| 场景 | 必须观察到的结果 |
| --- | --- |
| handler 返回后，结果 reader 继续持有服务 | 退休请求关闭新借用；原服务 Release=0，最后结果 lease 退出后 Release=1 |
| A 无 Module Required B，但保留 B 的真实服务 lease | B 先收到 Stop 时不阻塞 A 的通知；A 归还后 B 才 Release，无回调锁内自依赖 |
| A 只在 Release 回调归还长期 B lease | A 的外部使用者退出后先 Release A，再使 B 就绪；不在 A.Release 前等待 B 的 FinalCompletion |
| 两个 View 共享一个 Model 服务 | 工厂次数为 1；关闭 View1 不释放 Model；Model 退休并等待 View2/结果退出后只释放一次 |
| C/B/A 中 B 的 Release 抛异常 | 独立 A、C 仍被尝试；B 保留为 CleanupFailed，不重复调用成功项，不把失败改成 Success |
| A.Release 失败，仍持有 B 的长期借用 | A 与 B 的受管依赖闭包明确保留；无外部在途读者后最终报告失败，B 不被误释放，也不因该已知保留边永久 Pending |
| 借用与关闭并发 | 只有明确在准入关闭前成功的 lease 能进入；关闭后的新借用为零，无早释放 |
| 重复关闭、取消等待、非合作调用 | 同一关闭句柄、同一结果；取消不终止清理，非合作调用保活并被如实报告 |
| 严格路径混用普通 provider 自动释放 | 在资格验证中拒绝双 owner；不得仅通过最终 catch 或空 lease 通过测试 |

先用无编辑器依赖的确定性 barrier 消费者验证通用协议，再用 LuminEditor 真正生成的模块桥、handler、结果 reader 和退休票据运行同样断言。两者都要有独立包消费的 JIT、trim、Native AOT 证据。未接真实 Foundation-owned 服务前，不解除该分支的交付前置。

旧消费者还要证明：Charts 的串行协调与 rollbackOnFailure=false 不变；SyntaxViewer 的领域激活/释放不被接管；CLI 不出现第二 provider；当前 DataGrid 使用 AtomUI.Base，须先确认迁移关系，不能直接当作 Foundation 新包的兼容测试。
