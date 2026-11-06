# Foundation 验证合同

本文规定正式设计的行为、兼容与包消费验收。设计文档完成或历史测试通过，不等于新增受管生命周期已经交付。阶段安排保留在实施计划，稳定合同以[架构总览](../architecture/overview.md)及其分册为准。

## 验证原则

修复用例必须保留原触发条件并在修复前暴露相应缺陷；新增能力测试必须执行实际 owner/lease/factory/Release。正式测试不得依赖本机忽略目录的探针，也不通过 Skip、零匹配或只断言描述对象来取得运行资格。

描述、执行和所有权分开验收。中立类型注册只产生描述是应保留的旧合同，不能把它改为“自动解析成功”来制造修复。外部 owner 的空 lease、立即完成的假票据与 Core-owned 替身不能证明 Foundation-owned 服务可用。

## 运行时矩阵

| 领域 | 必须执行的场景与断言 |
| --- | --- |
| 模块图 | 重复、缺失、环、结构化路径、显式启用与闭包；预检失败时工厂为零 |
| 阶段 | 全体配置屏障、正逆序、手动阶段继续推进、重复初始化不重建 |
| 失败 | 各创建/配置/初始化阶段异常与取消；null 工厂；已创建实例不丢失，部分初始化可清理 |
| 并发 | 初始化与关闭重叠、相同在途操作共享、回调重入拒绝、终态不被覆盖 |
| 取消 | 原令牌身份、加入者只取消等待、清理不因原 token 取消跳过、空模块集的预取消不推进阶段 |
| 关闭 | 已尝试模块不重放；重复等待保留原失败；同步上下文风险有对应证据 |
| 中立服务 | type/instance 二选一、最后登记覆盖、冻结、移除、值类型/委托实例、调用方所有权与导出 |
| 受管服务 | 每根 Singleton、scope 隔离、Transient 请求 owner 在最后 lease 归还后恰释放一次、被 Singleton 捕获的 Transient 随 Singleton 释放并产生 Info 诊断、已准入工厂在 scope 同时退休时仍完成且退休成功、并发首次激活、静态工厂匹配及失败交接 |
| 借用 | 退休后新借用为零；方法返回后结果 reader 保活；重复归还不多减计数 |
| 退休关系 | 无 Module Required 边但有真实借用；A.Release 归还 B；长期捕获环拒绝；不在释放前全局等待归零 |
| 失败保留 | 独立 A/C 仍清理；失败 B 不重试；工厂失败 prerequisite 和失败阻塞后新增的 Capture/WaitForRelease 都扩展 RetainedByFailedOwner 闭包；与真实 Pending 分开 |
| scope 共享 | 两消费者 factory=1；关闭其一不退休原 owner；根 scope 拒绝关联更短寿命子 scope；最后合法退出后 Release=1 |
| 诊断 | 接收器抛错不遮盖原错误；来源/路径/阶段完整；目标 Warning/Info 成功语义；变更拒绝保留候选阶段的结构化诊断 |
| 事务统一 | 启动即空组合到 S 的变更，提交结果 Generation 为 1；手动阶段 API 与一次性 Initialize 得到相同实例与工厂次数；最终关闭经同一退休调度器退休当前代，当前状态的发布者依次收到 `StopCurrent` 与 `CompleteCurrentAsync`，持有模块 lease 的领域状态在后者中归还后关闭完成；被取代的发布者不再收到 `StopCurrent`；领域失败被报告且不跳过模块关闭 |
| 并发压力 | 固定种子随机交错 Borrow/Dispose/CreateScope 与 BeginRetirement、ChangeAsync 与 AcquireView；每个 owner 恰释放一次，无 lease 在 Released 后可读，FinalCompletion 不伪报成功 |
| 推进复杂度 | 单次 lease 归还的分配与工作量不随域内待退休 batch 数增长（以 64 与 4096 个待退休 batch 对照）；服务层与宿主内部路径无同步等待；公开同步 Stop 的并发通知屏障保留；Stop 回调转交到同一逻辑流其他线程的 Stop 被识别为重入 |
| 簿记 ledger | 成功候选退休后 ledger 为空；失败与 Pending 票据保留到最终关闭；重复关闭不重试 Release |

生命周期并发测试使用可控制的 barrier 固定交错，不靠增加 sleep 或超时碰运气。非合作 reader 未退出时必须保持资源有效，取消等待不改变真实收尾；最终失败报告不得把保留资源标为 Released。

2026-11-04 审计修复增加固定回归：原 4000 个成功 scope 及跨绑定包装退休不累积；真实 lease 和失败回执继续保留；失败 scope 的原异常仍被候选汇总；局部/整体退休并发不死锁且 Release 一次；异步 Release 完成前后明确验证索引收尾顺序。失败闭包在合法 capture 归还后收缩，已满足的 WaitForRelease 双向关系和成功候选票据不累积；单项失败不保留已成功关闭的独立模块载荷。

## 动态重配事务验收

本节验证[模块动态重配合同](../architecture/module-reconfiguration.md)，不将 1.0.2 已有静态 Host 或退休用例改记为动态变更通过。当前本地 1.0.3 验证见[唯一证据记录](evidence/module-reconfiguration-20261103-local-1.0.3.json)，不等于真实 Core 联合接入或公开发布资格。原始缺口场景保留已初始化 Host、A 实例、原 registrations/enable 输入及 Resolve/Initialize 调用顺序，作为静态快照与幂等兼容事实；能力用例在同一 Host 上调用新版本真实公开变更入口，不能继续用无操作调用或重建 Host 代替。

| 场景 | 必须观察的结果 |
| --- | --- |
| 新增 B，另有未选 D | 当前 A 增量变为 A/B；A 引用不变，A/B 各构造一次，D=0；无变化请求不重放阶段 |
| 局部停用 B | 新准入不再选择 B，独立 A 与稳定 Infrastructure 保持身份；B 最后使用者退出后 Release 一次 |
| 图拒绝 | 缺失、重复、环及 C Required B 时显式停用 B 均在候选业务 factory 前拒绝；级联仅在显式请求时按实际闭包执行 |
| 候选初始化失败 | barrier 后失败只清理候选实际成功项；旧 A 持续服务，不触发其全局 Shutdown |
| 领域拒绝与候选清理失败 | Foundation 准备后领域拒绝，两侧均不发布；失败候选保留真实资源/借用，不关闭旧代合法共享服务准入 |
| 发布一致性 | 准备、封准入及提交处暂停，并发捕获视图只得到合法 G 或 G+1；模块、服务及领域载荷无混代组合 |
| 请求与结果 reader | 必须排空的旧工作真实退出；允许存活的旧结果持有原视图和 lease，退出前不得释放或复用其资源 |
| 领域持有模块 lease | 先实际发布持有 B 的 ModuleHostLease 的领域 State，再停用 B；CompletePreviousAsync 在模块最终等待期间进入并归还该 lease，无需手动解除循环；全部 Stop 已分发，服务 Release 不早于模块回调结束 |
| 实际借用影响集 | A 无 Required B 而实际捕获 B；完整影响集先封准入并收到停止通知，不在 B 回调等待未调度 A；长期捕获按合法重绑或释放合同处理 |
| 取消与基线 | 封准入后、提交前取消可恢复旧配置并独立清理；取消与决定竞争有唯一结果，过期或外来 Host 基线不能覆盖当前代 |
| 提交后清理异常 | 新代保留，旧失败与保留闭包可诊断；其他独立 owner 继续释放，超预算和调用方取消等待不伪报完成 |
| 最终关闭竞争 | 分别固定决定前后交错；关闭覆盖候选、当前代和仍被持有的旧资源，重复关闭复用原票据及失败 |

暂停点用 barrier 固定，计数来自真实 module/service factory、Stop、Release 和结果 owner。替换用例还须证明新旧绑定并存时不误命中旧缓存、未受影响 Singleton/Scoped 不被重建，且只有一个实际释放权威。

恢复失败回归必须保留已暂停的真实旧状态：Resume 抛错后受影响 AcquireView/服务准入仍拒绝、独立模块可读、再次变更拒绝且最终关闭可执行。State/AffectedModules getter 及自定义集合枚举中的重入均须立即拒绝，不能启动真实关闭或等待当前事务。

资格按三层取得：先用仅依赖候选 Foundation 包的独立消费者，再运行 LuminEditor Core 联合消费者，最后对相同创建、请求、变更与关闭分支取得适用 JIT、trim 和 Native AOT 运行证据。Core 消费者包括独立 ProbeFeature 包、稳定 Infrastructure、闭合 Provider、真实 CompositionSingleton/ModelScoped 服务及共享 Model 的双 View，并验证领域拒绝、单一提交和文档身份保持。禁止源码链接、InternalsVisibleTo 或另建 Host 绕过公开边界。

记录候选包版本与内容哈希、消费者源码提交、依赖和完整调用、生成指纹、命令/TFM/RID/机器、构造/停止/释放计数、代次及实例身份、旧租约状态、退出码与日志。详细证据保存到一个权威批次记录，状态表仅链接；publish-only、局部静态 AOT 冒烟和本机结果均不能扩写成其他路径或平台资格。无反射执行路径与 JIT 消费者关闭默认反射 JSON 的要求也须按真实调用验证。

## 生成器与配置矩阵

覆盖单个/多个 partial、其他 partial 特性、别名、可访问嵌套类、泛型外层、file-local、required、无模块输入、命名遮蔽和精确错误位置。每个合法场景检查更新后编译，无效场景检查有针对性的诊断及无错误工厂输出。

增加依赖 null 数组、null 元素及数组类型目标；增加仅 extern alias 外部引用以及 alias/global 同全名不同程序集对照。后者除检查输入/输出编译外，还应检查生成 Required 的实际绑定程序集与原属性一致。

增加增量缓存用例：修改不含模块声明的文件后，目录输出步骤状态为 Cached；extern alias、嵌套类型、关键字转义输出不变。显示名推断不再剥除产品前缀的行为变化有对应用例。

真实 SDK/包测试验证 analyzer 装载、CompilerVisibleProperty 桥接、实际 editorconfig 和生成命名；ProjectReference 与 PackageReference 均需覆盖，并包含运行时所支持的每个 SDK 大版本至少一条 analyzer 装载用例。.NET 8 SDK 用例由验证脚本的 `--sdk8-loader` 选项提供：它在单独目录锁定 8.0 SDK，构建并运行 net8.0 包消费者，且先核对实际选中的 SDK 为 8.x；验证工作流启用该选项。双程序集组合必须实际创建外部依赖注册并运行 Host，不能只检查生成字符串包含外部类型。

## 独立消费者与兼容

先运行不依赖任何产品的通用包消费者，再执行对应产品接入。消费项目只引用允许的公开包，不链接内部源码或使用本机已构建程序集冒充 NuGet 消费。

| 消费路径 | 必须保留或证明的行为 |
| --- | --- |
| Charts | 原显式 instance 路径、串行协调、rollbackOnFailure=false、真实工作退出先于 owned 释放 |
| SyntaxViewer | 回调令牌身份、领域 Activation/Session 继续由自身管理，模块关闭不接管领域资源 |
| CLI | 类型描述导出到选择的容器，只有一个实际 provider/实例权威 |
| LuminEditor | 真实生成模块桥、handler、结果 reader、Foundation 服务 lease 与退休票据共同闭环 |
| DataGrid Commercial | 先核对实际包身份；当前审计基线使用 AtomUI.Base，不能直接作为 Foundation 新包验收 |

LuminEditor 的 Foundation-owned 命令分支是独立门禁。Core-owned 结果或纯描述验证通过，不能解除该门禁，也不能推断完整动态模块启停已完成。

## 平台、性能与发布

运行时目标在 net8.0 与 net10.0 Release 下实际执行，不以双框架编译代替运行。桌面 Windows/macOS/Linux 的适用常规用例及支持 RID 上的 trim/Native AOT 消费程序分别取得发布证据。AOT 可运行和全路径无反射分开验收；Generator 自身不要求作为 AOT 应用发布。

未启用受管 Runtime 时不额外创建 provider、owner 或退休状态。已绑定热路径不遍历 Host/Module 图，不按字符串重新发现服务；配置与退休时才处理关系图。性能测试记录实际操作数、分配与测量边界，不能把初始化开销藏入测量或用空工作证明成本。

打包消费使用隔离的 NuGet 缓存或其他可证明不命中旧同版本包的方式，记录包/二进制身份；不因此创建研发 checkout 或新分支。普通 PR 验证与发布流程分开，源代码变化须触发适用验证，发布仍需要明确发布操作。

发布门禁：发布工作流在打包前运行消费者验证脚本（含 .NET 8 SDK 装载用例），发布到 nuget.org 时要求目标提交的验证工作流已成功；PublicAPI 基线无未声明变更，AOT/trim 分析器 0 警告；包资产清单含 Generator `buildTransitive` 属性桥。

## 本地命令与变更范围

```bash
dotnet build AtomUI.Foundation.slnx
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
git diff --check
```

以上是当前项目入口，不代表已经配置所有目标框架、OS、包消费与 AOT 测试。扩展资格须增加对应执行入口并核对非零发现数。

纯文档变更检查本地链接与锚点、代码围栏、空白、示例与当前公开 API 的一致性，以及正式合同与实现状态的区分。没有修改产品/项目/构建文件时，不以无关的全量构建代替文档检查。
