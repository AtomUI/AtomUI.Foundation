# Foundation Runtime Refactor Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for integration and superpowers:dispatching-parallel-agents for independent, explicitly owned implementation domains. Steps use checkbox syntax. Work in the current checkout; do not create branches, worktrees or clones. Do not commit or publish without a separate request.

**Goal:** Implement the canonical host, generator and opt-in managed lifetime contracts while preserving neutral service descriptors.

**Architecture:** Keep the existing runtime and generator projects. Add AtomUI.Lifetimes for ownership/retirement and AtomUI.Modularity.Services for explicit typed service activation. The existing host accepts an optional lifecycle adapter, without constructing a default provider.

**Tech Stack:** C#, net8.0/net10.0, Roslyn netstandard2.0, xUnit v3, existing SDK and package versions.

**Spec:** docs/architecture/overview.md, modularity.md, service-registration.md, managed-lifetimes.md, source-generation.md; docs/engineering/verification.md.

## Global Constraints

- Preserve legacy public signatures, instance ownership, delegate/value registration and neutral type descriptors.
- Use the current branch and directory; no branch/worktree/clone creation.
- No new runtime DI dependency, reflection construction or separate module dependency graph.
- Register resources immediately, preserve callback token identity, execute no user code under internal state locks.
- All Stop notifications precede Release; drain and release interleave per owner, failures retain dependent closures.
- No modifications to consumer repositories or automatic commit/publication.
- All builds share a serialized diagnostic runner while independent source edits are in progress.

## Review Focus

- A factory returns after scope retirement begins: the reserved owner still tracks and releases its result.
- A.Release returns a B capture then throws: B must remain retained, not race into Release.
- A lifetime adapter Stop fails: remaining Stop/module callbacks and ready owner cleanup still run.
- A source scope is shared with two consumers: neither link may become another release owner.
- Default generator options exposed by MSBuild remain warning-free while invalid explicit options are diagnosed.

## Task 1: Host coordination and diagnostics

**Files:** ModuleHost.cs and internal partial/coordinator files; ModuleHostOptions/Builder; IModuleHostLifetime.cs; result/diagnostic/graph types; tests/AtomUI.Foundation.Tests/Modularity/ModuleHost*Tests.cs and graph/neutral tests.

**Interfaces:** Preserve existing APIs. Add public ValueTask<ModuleHostResult> CreateModulesAsync(CancellationToken=default), ModuleHostValidationResult Validate(), ModuleHostOptions.Lifetime { get; init; }, builder UseLifetime(IModuleHostLifetime). Interface methods: void Freeze(); void Stop(); ValueTask<ModuleHostResult> CompleteShutdownAsync(). Stop runs before module shutdown; Complete runs after all callbacks, including on failures. Runtime adapter is optional.

- [x] Write deterministic regressions for partial creation cancellation/null, initialization cancellation, shared initialization, shutdown overlap/reentry, saved shutdown failure, observer exceptions, manual stages and rollbackOnFailure=false.
- [x] Run narrow tests and record their actual failures before fixes.
- [x] Implement one coordinator/ledger with no callbacks under locks; preserve original initiating token, cancel only joiner waits, save cleanup task/result, make all entry points obey terminal state.
- [x] Add Validate without factories; fill dependency paths; results succeed without Error; retain neutral descriptor tests.
- [x] Verify focused and runtime suites through the shared runner; report recorded in `.artifacts/foundation-refactor/host-report.md`.

## Task 2: Generator and real build configuration

**Files:** Generator/Modularity files; generator tests; new bundled catalog configuration props, runtime analyzer bridge imports as needed; generator project packaging metadata only.

**Interfaces:** Preserve generator class and catalog defaults. Deduplicate symbols, emit global:: type references; diagnose generic/file-local/inaccessible/unsatisfied-required types; identify serialized calls semantically. Expose both catalog properties to PackageReference and ProjectReference consumers.

- [x] Add failing tests for partial duplicate, unsupported shapes, namespace shadow, using static and empty/default options.
- [x] Observe RED; implement focused discovery/validation/emission helpers, retaining current public outputs.
- [x] Add actual MSBuild bridge and a real package consumer fixture/script; test configured and default catalog compilation.
- [x] Verify generator suite and report exact commands/results in `.artifacts/foundation-refactor/generator-report.md`.

## Task 3: Lifetimes ownership and retirement

**Files:** src/AtomUI.Foundation/Lifetimes/*.cs; tests/AtomUI.Foundation.Tests/Lifetimes/*.cs. No Modularity/service-runtime file edits.

**Interfaces:** Exact public contract is appended to docs/architecture/managed-lifetimes.md. Domain creates scopes, scopes Own/Create typed owners; owners AcquireLease/Capture/WaitForRelease; Stop defers releases until CompleteRetirement; BeginRetirement combines both. Close results expose immutable owner snapshots and failures. No user callback under domain gate.

- [x] Add RED tests for original-reader survival, Stop-all barrier, A.Release returning B, alias/double adoption, cycle rejection, failed closure, independent cleanup, repeated closes and factory/retirement races.
- [x] Implement guarded admission, construction reservation, separate external reader/capture/ordering edges, ready-owner scheduler and immutable close results.
- [x] Preserve capture pins until a releasing owner's outcome is known; on failure retain its dependency closure. Distinguish blocked failure from live external readers in final completion.
- [x] Verify focused lifetime suite via shared runner; report recorded in `.artifacts/foundation-refactor/lifetimes-report.md`.

## Task 4: Typed managed service runtime and host adapter

**Files:** src/AtomUI.Foundation/Modularity/Services/*.cs; runtime tests; canonical API sections.

**Interfaces:** ModuleServiceRuntime implements IModuleHostLifetime, exposes Lifetimes/Root and explicit AddSingletonFactory/AddScopedFactory/AddTransientFactory; each factory receives a short-lived ModuleServiceActivationContext. Context resolves declared dependencies as real captures. ModuleServiceScope supports Borrow<T>(), CreateScope(), ShareFrom<T>(source), BeginRetirement and DisposeAsync. Legacy descriptor classes are unchanged.

- [x] Add failing tests for lifetimes, parallel singleton construction, declared graph validation, captures, result leases, two scope links, construction failure/race and host managed shutdown.
- [x] Implement frozen typed registration and one resolution authority, no reflection fallback, context escapes rejected, legacy factory/delegate behavior untouched.
- [x] Use LifetimeScope.Create for in-flight construction reservations and attach real captures; do not create/release a second owner for shared instances.
- [x] Verify runtime and cross-layer tests; initial task reviews are resolved and final independent review is tracked under Task 5.

## Task 5: Packaged consumers and final qualification

**Files:** tests/fixtures/Foundation.Runtime.Consumer; build/scripts verification; CI validation workflow; formal status docs.

- [x] Create standalone PackageReference consumer using module lifecycle, neutral export, static factories, result lease, shared scopes and failed cleanup.
- [x] Pack both current packages without publishing; restore with a fresh package cache and execute net8/net10, trim and local Native AOT on the current macOS arm64 host.
- [x] Add source/PR verification workflow; preserve manual publication behavior and report unexecuted OS/RID/product qualifications accurately.
- [x] Run Release solution build, runtime and generator suites, whitespace and local Markdown-link checks.
- [x] Complete final independent code review and resolve all Important/Critical findings; reviewer rechecked the root-scope, empty-stage cancellation, and open-generic fixes and found no remaining issue in those changes.
- [x] Re-review the later `AddOrdering` failure-closure propagation fix; independent reviewer confirmed the edge direction and found no remaining issue.
- [x] Update canonical implementation status based on local evidence. Consumer repositories were not modified; Linux CI, other OS/RID qualification and real product adoption remain outstanding.

## Execution ledger

- Baseline: release/1.0, 0041863; prior documentation changes retained.
- Ruling: implement in the existing runtime assembly with independent namespaces; no speculative new packages or DI dependency.
- Ruling: independent known host/generator defects and the new lifetime kernel have disjoint file ownership; integration remains serial.
- Final local Release verification on 2026-10-26: `dotnet build AtomUI.Foundation.slnx --configuration Release --nologo --verbosity minimal` succeeded with 0 warnings/errors; runtime tests passed 90/90; generator tests passed 36/36.
- Package-consumer verification: `python3 tests/fixtures/generator-consumer/verify.py --mode all --case all --aot --work-dir .artifacts/foundation-refactor/verification-final-after-review-20261026` passed all six ProjectReference/PackageReference cases, net8.0/net10.0 runtime consumers, trimmed net10.0 osx-arm64, and Native AOT net10.0 osx-arm64. Packages were local test packages only and were not published. The AOT linker reported Homebrew libraries built for a newer macOS deployment target; the produced binary executed successfully. Linux CI is configured but was not run locally.
- Documentation verification: local relative links in all architecture/engineering Markdown files resolve; `git diff --check` passed. Product repositories were not changed. Native CI, other platform/RID combinations, cross-assembly Host composition, and product-level qualification remain open.
- Final review: an independent read-only code review identified root-scope sharing, empty-stage cancellation, and open-generic dependency gaps; all were reproduced with failing regressions, fixed, and re-reviewed clean. No branch, worktree, clone, commit, or publication was created.
- Post-review release blockers supplied on 2026-10-26: added regressions for the 20,000-owner synchronous capture chain, factory-failure ordering, a later capture extending a blocked failure closure, Domain failure persistence through Scope disposal, and an internal OCE with `CancellationToken.None`. The four non-stack cases failed before fixes; the user's preserved probe log records the 20,000-owner stack overflow before this regression was added.
- Follow-up fixes: LifetimeDomain progression now drains a deduplicated owner queue and tracks final ticket membership incrementally; factory failures propagate to release dependents; later captures extend a blocked owner's failure closure; scopes reuse the Domain retirement result if Domain shutdown happened first. Module callbacks/factories that throw OCE are reported as failures unless the initiating operation token is actually canceled.
- Final regression verification before the last reviewer finding: Release build succeeded with 0 warnings/errors; runtime tests passed 101/101, generator tests 37/37; the 20,000-owner chain completed in 156 ms. Fresh local package consumer verification at `.artifacts/foundation-refactor/verification-review-core-fixes-20261026/verification.json` passed all six generator cases, net8/net10 JIT, trim, and macOS arm64 Native AOT. Local packages only; no release or publication was performed. AOT linker emitted the previously recorded Homebrew deployment-target warnings; the binary executed successfully.
- Last P1 follow-up: a regression reproducing `A.Release` failure → B blocked → `C.WaitForRelease(B)` first timed out. `AddOrdering` now propagates and progresses the retained closure when its prerequisite is failure-blocked. The focused Release test passes; latest Release suite is 102/102 runtime and 37/37 generator. Latest fresh package-consumer evidence is `.artifacts/foundation-refactor/verification-final-ordering-20261026/verification.json`; six generator cases, net8/net10 JIT, trim, and local macOS arm64 AOT passed. Independent review confirmed the ordering direction and found no remaining issue. This local verification does not publish packages.
