---
name: atomui-foundation-version-release
description: Use when preparing, validating, or carrying out an AtomUI.Foundation version release or NuGet publication.
---

# AtomUI.Foundation Version Release

Prepare releases from the repository's actual package and workflow configuration. Do not import release conventions from other AtomUI repositories unless this repository adopts them.

## Repository Contract

- `build/Version.props` is the single source of truth for `AtomUIFoundationVersion`.
- The release contains exactly two packages:
  - `AtomUI.Foundation`: `net8.0` and `net10.0` in Release.
  - `AtomUI.Foundation.Generator`: `netstandard2.0`, shipped under `analyzers/dotnet/cs`.
- Release artifacts belong under `.artifacts`; NuGet packages belong under `.artifacts/Nuget/Release` unless the user requests an isolated validation directory.
- Both packages must carry the repository README, LICENSE, logo, concise description, repository metadata, and the same version.
- The repository currently has no mandatory changelog or migration-document convention. Update an existing release-notes artifact when one is present or requested; do not invent one to satisfy a borrowed workflow.

## Release Workflow

### 1. Establish the release boundary

Before editing, determine:

- requested version and whether it is stable or prerelease;
- current branch, HEAD, upstream, latest relevant tag, and comparison range;
- staged, unstaged, untracked, and ignored release outputs;
- whether each dirty change belongs to this release;
- which actions the user authorized: prepare, commit, tag, push, and/or publish.

Read at least:

```bash
git status --short --branch
git tag --sort=-version:refname
git log --oneline --decorate -n 20
git diff --stat
git diff --cached --stat
git ls-remote --tags origin
```

Also inventory ignored packages under `.artifacts/Nuget/Release`; never infer the target artifacts from a directory-wide wildcard. Query the official NuGet registry for both package IDs when publication is in scope.

The target version must be greater than the latest released version and must not reuse a local tag, remote tag, or published version of either package. If the requested version moves backwards, collides, or does not match the intended compatibility level, stop and report the conflict.

### 2. Audit compatibility before changing release metadata

Inspect the release range, not only commit messages. Classify consumer-visible changes across all Foundation surfaces:

| Surface | Inspect for |
| --- | --- |
| Runtime | Public types and members, behavior, nullability, dependencies, and runtime requirements |
| Generator | Diagnostics, generated source shape, attributes, analyzer loading, Roslyn compatibility, and deterministic output |
| Build | MSBuild properties and targets, SDK requirements, target frameworks, project references, and output paths |
| Package | Package IDs, dependencies, asset paths, README, LICENSE, icon, descriptions, and repository metadata |

Do not claim "no breaking changes" merely because no `.cs` file changed. State the evidence used and mark uncertain surfaces explicitly. Do not introduce ApiCompat, migration documents, signing, SBOM, or other release machinery unless the repository already configures it or the user asks for it.

### 3. Synchronize only real version consumers

Update `AtomUIFoundationVersion` in `build/Version.props`. Then search the repository for the old version and update user-facing installation examples in both `README.md` and `README.zh-CN.md` when applicable.

Keep package descriptions brief and package-specific. Do not duplicate version properties in project files. Do not create a changelog solely because another repository has one.

Before proceeding, review the exact release diff and make sure unrelated working-tree changes are neither discarded nor silently included.

### 4. Validate the release build

Use Release configuration because it is the package contract:

```bash
git diff --check
dotnet build AtomUI.Foundation.slnx --configuration Release
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0 --configuration Release --no-build
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0 --configuration Release --no-build
dotnet pack src/AtomUI.Foundation/AtomUI.Foundation.csproj --configuration Release --no-build
dotnet pack src/AtomUI.Foundation.Generator/AtomUI.Foundation.Generator.csproj --configuration Release --no-build
```

Release builds require the .NET 8 targeting pack as well as the SDK selected by `global.json`. If the build reports a missing `net8.0` reference pack, inspect the installed SDKs and reference packs, then use the environment-approved .NET 8 SDK or targeting-pack installer. Do not assume an ordinary NuGet restore installs a missing framework reference pack. Rerun restore/build after installation; if installation is unavailable or unauthorized, report `Blocked`. Never remove `net8.0` or change the Release target frameworks to make validation pass.

If a command fails, diagnose the failure and report it. Do not publish packages built before the final version or release diff was established.

### 5. Inspect the packages themselves

Do not treat a successful `dotnet pack` as sufficient. Inspect the exact-version `.nupkg` files and require:

- exactly one runtime package and one generator package for the target version;
- runtime assemblies under both `lib/net8.0` and `lib/net10.0`;
- `AtomUI.Foundation.Generator.dll` under `analyzers/dotnet/cs` and no unintended `lib` asset for the generator;
- target version and expected identity in both nuspec files;
- expected dependencies only;
- `README.md`, `LICENSE`, and `logo.png` in both packages;
- the package icon is referenced by metadata and the descriptions remain concise.

Use exact expected filenames so stale packages in `.artifacts/Nuget/Release` cannot be mistaken for the release. Inspect archive entries and nuspec contents with an available ZIP tool.

### 6. Rebuild from the release commit

When the authorized scope includes a release commit that will be tagged or published, the working-tree validation above is preliminary. After creating the commit:

1. Confirm `HEAD` is the intended release commit and validate from a clean tracked worktree. If unrelated changes must remain in the current checkout, use an isolated clean checkout at that exact commit rather than discarding or building through them.
2. Remove only the two exact target-version generated `.nupkg` files, if present; never clear the whole artifact root.
3. Run a non-incremental Release build, both test commands, both pack commands, and the complete package inspection again from that commit.
4. Require each nuspec repository commit to equal `git rev-parse HEAD`.
5. Tag and publish only these post-commit packages. Never reuse packages created from a dirty or earlier tree.

Immediately before tagging or publishing, refresh remote tags with `git ls-remote --tags origin` and query the official NuGet registry again for both package IDs. A stale clone or a collision on only one package is a hard stop.

### 7. Apply authorization boundaries

- Preparing or validating a release does not authorize a commit, tag, push, workflow dispatch, or NuGet publication.
- If the user explicitly requests an end-to-end release, perform the authorized stages without asking for redundant confirmation, but stop on a hard failure.
- Never move, overwrite, or force-push an existing release tag. Create a new annotated tag only for the requested unused version.
- Before pushing or publishing, confirm the release commit is the post-commit validated commit, the tag points to it, the remote has not advanced unexpectedly, and the target version is still unused for both packages.
- Publish both packages as one release unit. A partial publication is a failed release that must be reported explicitly; do not hide it with `--skip-duplicate`.
- Inspect the repository workflow before publication. If it uses `--skip-duplicate`, it is not a strict publication path and must not be dispatched with publication enabled until corrected. Do not bypass the workflow with direct NuGet pushes unless the user explicitly authorizes that publication path.
- Never expose NuGet credentials or other secrets in commands, logs, or the report.

## Hard Stops

Report `Blocked` instead of improvising when:

- version intent or included dirty changes are ambiguous;
- the target version or tag already exists;
- compatibility evidence contradicts the chosen version;
- the .NET 8 targeting pack is unavailable;
- build, tests, package inspection, or final diff checks fail;
- the branch, upstream, or release commit changes during validation;
- only one of the two packages can be published.

## Required Report

Finish with a concise release-readiness report:

```markdown
## Release readiness: Ready | Blocked

- Version and baseline: ...
- Included scope: ...
- Compatibility: Runtime ...; Generator ...; Build/package ...
- Validation: commands actually run and their results
- Packages: exact filenames and inspected contract
- Completed actions: edits/commit/tag/push/publication actually performed
- Remaining actions or blockers: ...
```

Never list an unrun check as passed, and never describe a prepared package as published.
