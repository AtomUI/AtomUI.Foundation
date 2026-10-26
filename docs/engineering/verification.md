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
| 受管服务 | 每根 Singleton、scope 隔离、Transient 归属、并发首次激活、静态工厂匹配及失败交接 |
| 借用 | 退休后新借用为零；方法返回后结果 reader 保活；重复归还不多减计数 |
| 退休关系 | 无 Module Required 边但有真实借用；A.Release 归还 B；长期捕获环拒绝；不在释放前全局等待归零 |
| 失败保留 | 独立 A/C 仍清理；失败 B 不重试；工厂失败 prerequisite 和失败阻塞后新增的 Capture/WaitForRelease 都扩展 RetainedByFailedOwner 闭包；与真实 Pending 分开 |
| scope 共享 | 两消费者 factory=1；关闭其一不退休原 owner；根 scope 拒绝关联更短寿命子 scope；最后合法退出后 Release=1 |
| 诊断 | 接收器抛错不遮盖原错误；来源/路径/阶段完整；目标 Warning/Info 成功语义 |

生命周期并发测试使用可控制的 barrier 固定交错，不靠增加 sleep 或超时碰运气。非合作 reader 未退出时必须保持资源有效，取消等待不改变真实收尾；最终失败报告不得把保留资源标为 Released。

## 生成器与配置矩阵

覆盖单个/多个 partial、其他 partial 特性、别名、可访问嵌套类、泛型外层、file-local、required、无模块输入、命名遮蔽和精确错误位置。每个合法场景检查更新后编译，无效场景检查有针对性的诊断及无错误工厂输出。

真实 SDK/包测试验证 analyzer 装载、CompilerVisibleProperty 桥接、实际 editorconfig 和生成命名；ProjectReference 与 PackageReference 均需覆盖。双程序集组合必须实际创建外部依赖注册并运行 Host，不能只检查生成字符串包含外部类型。

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

## 本地命令与变更范围

```bash
dotnet build AtomUI.Foundation.slnx
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
git diff --check
```

以上是当前项目入口，不代表已经配置所有目标框架、OS、包消费与 AOT 测试。扩展资格须增加对应执行入口并核对非零发现数。

纯文档变更检查本地链接与锚点、代码围栏、空白、示例与当前公开 API 的一致性，以及正式合同与实现状态的区分。没有修改产品/项目/构建文件时，不以无关的全量构建代替文档检查。
