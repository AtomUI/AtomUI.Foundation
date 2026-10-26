#!/usr/bin/env python3
"""Build real generator consumers. No package is published and no checkout is copied.

Run from any directory. --dotnet-runner accepts a Python wrapper when builds must
be serialized. Results, editorconfigs, generated sources and packages are retained
under .artifacts/generator-consumer; its NuGet cache is recreated on every run.
External ProjectReference consumers explicitly import the shared source props:
an analyzer ProjectReference cannot propagate CompilerVisibleProperty items into
its parent. NuGet consumers receive that import through buildTransitive.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
from xml.sax.saxutils import escape
import zipfile

ROOT = Path(__file__).resolve().parents[3]
VERSION = "1.0.1-generator-consumer"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet-runner", type=Path)
    parser.add_argument("--work-dir", type=Path, default=Path(".artifacts/generator-consumer"),
                        help="artifact directory to recreate; defaults to .artifacts/generator-consumer")
    parser.add_argument("--mode", choices=("all", "project", "package"), default="all")
    parser.add_argument("--case", choices=("all", "default", "configured", "invalid"), default="all")
    parser.add_argument("--aot", action="store_true", help="publish the managed lifetime consumer as local Native AOT")
    args = parser.parse_args()
    work = args.work_dir if args.work_dir.is_absolute() else ROOT / args.work_dir
    work = work.resolve()
    if work == ROOT or not work.is_relative_to(ROOT):
        raise ValueError("The generator consumer work directory must be inside the repository.")
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    # These files deliberately stop discovery of the repository's build defaults.
    (work / "Directory.Build.props").write_text("<Project />\n")
    (work / "Directory.Build.targets").write_text("<Project />\n")
    (work / "Directory.Packages.props").write_text("<Project />\n")
    packages = work / "packages"
    packages.mkdir()
    (work / "NuGet.Config").write_text(
        '<configuration><packageSources><clear/><add key="fixture" value="'
        + escape(str(packages)) + '"/><add key="nuget.org" '
        'value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>\n')
    command = [sys.executable, str(args.dotnet_runner.resolve())] if args.dotnet_runner else ["dotnet"]
    transcript = work / "commands.log"

    def dotnet(*arguments, expected_success=True, env=None):
        invocation = command + [str(argument) for argument in arguments]
        print("+ " + shlex.join(invocation), flush=True)
        result = subprocess.run(invocation, cwd=ROOT, text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, env=env)
        with transcript.open("a") as log:
            log.write("+ " + shlex.join(invocation) + "\n" + result.stdout + "\n")
        print(result.stdout, end="", flush=True)
        if expected_success != (result.returncode == 0):
            raise RuntimeError(f"Unexpected exit code {result.returncode}; see {transcript}")
        return result.stdout

    modes = ("project", "package") if args.mode == "all" else (args.mode,)
    cases = ("default", "configured", "invalid") if args.case == "all" else (args.case,)
    report = {"version": VERSION, "cases": [], "runtimeConsumers": [], "packages": {}}
    if "package" in modes:
        for name in ("AtomUI.Foundation.Generator", "AtomUI.Foundation"):
            dotnet("pack", ROOT / "src" / name / f"{name}.csproj", "-c", "Release",
                   f"-p:PackageVersion={VERSION}", "-o", packages)
        for package in sorted(packages.glob("*.nupkg")):
            report["packages"][package.name] = hashlib.sha256(package.read_bytes()).hexdigest()

    for mode in modes:
        for case in cases:
            directory = work / f"{mode}-{case}"
            directory.mkdir()
            custom = case == "configured"
            invalid = case == "invalid"
            namespace = "Consumer.Generated" if custom else ""
            type_name = "ConfiguredCatalog" if custom else "ModuleGeneratedCatalog"
            settings = ""
            if custom or invalid:
                settings = f"""
    <AtomUIModularityCatalogNamespace>{namespace if custom else 'invalid-namespace'}</AtomUIModularityCatalogNamespace>
    <AtomUIModularityCatalogTypeName>{type_name if custom else 'invalid-type'}</AtomUIModularityCatalogTypeName>"""
            if mode == "project":
                references = f"""
  <Import Project="{escape(str(ROOT / 'src/AtomUI.Foundation.Generator/buildTransitive/AtomUI.Foundation.Generator.props'))}" />
  <ItemGroup>
    <ProjectReference Include="{escape(str(ROOT / 'src/AtomUI.Foundation/AtomUI.Foundation.csproj'))}" />
    <ProjectReference Include="{escape(str(ROOT / 'src/AtomUI.Foundation.Generator/AtomUI.Foundation.Generator.csproj'))}"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>"""
            else:
                references = f"""
  <ItemGroup>
    <PackageReference Include="AtomUI.Foundation" Version="{VERSION}" />
    <PackageReference Include="AtomUI.Foundation.Generator" Version="{VERSION}" />
  </ItemGroup>"""
            (directory / "Consumer.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestorePackagesPath>{escape(str(work / 'nuget-cache'))}</RestorePackagesPath>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath>
    <WarningsAsErrors>$(WarningsAsErrors);ATOMUIMODGEN009</WarningsAsErrors>{settings}
  </PropertyGroup>{references}
</Project>
""")
            catalog = (namespace + "." if namespace else "") + type_name
            (directory / "Program.cs").write_text(f"""using AtomUI.Modularity;
using System;
namespace Consumer {{
    [Module] public sealed class FeatureModule : ModuleBase {{ }}
    public static class Program {{
        public static void Main() {{
            var registrations = global::{catalog}.CreateRegistrations();
            if (registrations.Count != 1 || registrations[0].Factory() is not FeatureModule ||
                registrations[0].Descriptor.Id != ModuleId.FromModuleType<FeatureModule>())
                throw new InvalidOperationException("Generated registration/factory contract failed");
            Console.WriteLine("GENERATOR_CONSUMER_OK {mode} {case}");
        }}
    }}
}}
""")
            output = dotnet("build", directory / "Consumer.csproj", "-c", "Debug", expected_success=not invalid)
            editorconfig = next((directory / "obj").rglob("*.GeneratedMSBuildEditorConfig.editorconfig"))
            config = editorconfig.read_text()
            wanted_namespace = namespace if not invalid else "invalid-namespace"
            wanted_type = type_name if custom else ("invalid-type" if invalid else "")
            for key, value in (("AtomUIModularityCatalogNamespace", wanted_namespace),
                               ("AtomUIModularityCatalogTypeName", wanted_type)):
                expected_line = f"build_property.{key} = {value}"
                if expected_line not in config.splitlines():
                    raise AssertionError(f"Missing actual editorconfig entry {expected_line!r} in {editorconfig}")
            if invalid:
                if "error ATOMUIMODGEN009" not in output or output.count("invalid-namespace") < 1 or output.count("invalid-type") < 1:
                    raise AssertionError("Explicit invalid properties were not diagnosed")
            else:
                if "ATOMUIMODGEN009" in output:
                    raise AssertionError("Valid/default configuration produced a generator warning")
                generated = next((directory / "obj/generated").rglob(f"{type_name}.g.cs"))
                source = generated.read_text()
                if "new global::Consumer.FeatureModule()" not in source:
                    raise AssertionError("Generated factory is missing its global type reference")
                if custom and "namespace Consumer.Generated;" not in source:
                    raise AssertionError("Configured catalog namespace was not used")
                execution = dotnet("run", "--project", directory / "Consumer.csproj", "-c", "Debug", "--no-build")
                if f"GENERATOR_CONSUMER_OK {mode} {case}" not in execution:
                    raise AssertionError("Real generated factory did not execute")
            report["cases"].append({"mode": mode, "case": case, "editorconfig": str(editorconfig.relative_to(ROOT))})

    if "package" in modes:
        generator_package = packages / f"AtomUI.Foundation.Generator.{VERSION}.nupkg"
        with zipfile.ZipFile(generator_package) as package:
            expected = ("analyzers/dotnet/cs/AtomUI.Foundation.Generator.dll",
                        "buildTransitive/AtomUI.Foundation.Generator.props")
            for name in expected:
                if name not in package.namelist():
                    raise AssertionError(f"Missing generator package asset {name}")
        managed_project = ROOT / "tests/fixtures/managed-runtime-consumer/Consumer.csproj"
        package_config = work / "NuGet.Config"
        package_cache = work / "runtime-nuget-cache"
        dotnet("restore", managed_project, "--configfile", package_config,
               f"-p:FoundationConsumerVersion={VERSION}", f"-p:RestorePackagesPath={package_cache}")
        for framework in ("net8.0", "net10.0"):
            output = dotnet("run", "--project", managed_project, "-c", "Debug",
                            "-f", framework, "--no-restore", f"-p:FoundationConsumerVersion={VERSION}",
                            f"-p:RestorePackagesPath={package_cache}")
            if f"MANAGED_RUNTIME_CONSUMER_OK {framework}" not in output:
                raise AssertionError(f"Managed runtime consumer did not complete for {framework}")
            report["runtimeConsumers"].append({"targetFramework": framework, "mode": "JIT"})
        if args.aot:
            aot_config = work / "AotNuGet.Config"
            aot_config.write_text(
                '<configuration><packageSources><clear/>'
                '<add key="foundation" value="' + escape(str(packages)) + '"/>'
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>'
                '</packageSources></configuration>\n')
            rid = "osx-arm64" if sys.platform == "darwin" else "linux-x64"
            dotnet("restore", managed_project, "--configfile", aot_config,
                   "--runtime", rid, "-p:TargetFramework=net10.0", "-p:PublishTrimmed=true",
                   f"-p:FoundationConsumerVersion={VERSION}",
                   f"-p:RestorePackagesPath={package_cache}")
            trimmed = work / "trimmed-publish"
            dotnet("publish", managed_project, "-c", "Release", "-f", "net10.0", "-r", rid,
                   "--no-restore", "-p:PublishTrimmed=true", "--self-contained",
                   f"-p:FoundationConsumerVersion={VERSION}",
                   f"-p:RestorePackagesPath={package_cache}", "-o", trimmed)
            trimmed_app = trimmed / ("Consumer" if sys.platform != "win32" else "Consumer.exe")
            trimmed_execution = subprocess.run([str(trimmed_app)], cwd=ROOT, text=True,
                                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            with transcript.open("a") as log:
                log.write("+ " + shlex.join([str(trimmed_app)]) + "\n" + trimmed_execution.stdout + "\n")
            print(trimmed_execution.stdout, end="", flush=True)
            if trimmed_execution.returncode != 0 or "MANAGED_RUNTIME_CONSUMER_OK net10.0" not in trimmed_execution.stdout:
                raise RuntimeError(f"Trimmed consumer failed; see {transcript}")
            report["runtimeConsumers"].append({"targetFramework": "net10.0", "mode": "trimmed", "rid": rid})

            dotnet("restore", managed_project, "--configfile", aot_config,
                   "--runtime", rid, "-p:TargetFramework=net10.0", "-p:PublishAot=true",
                   f"-p:FoundationConsumerVersion={VERSION}",
                   f"-p:RestorePackagesPath={package_cache}")
            native_env = os.environ.copy()
            if sys.platform == "darwin":
                native_library_directories = []
                for formula in ("openssl@3", "brotli"):
                    for root in (Path("/opt/homebrew/opt"), Path("/usr/local/opt")):
                        library = root / formula / "lib"
                        if library.is_dir():
                            native_library_directories.append(str(library))
                current_library_path = native_env.get("LIBRARY_PATH")
                native_env["LIBRARY_PATH"] = os.pathsep.join(
                    native_library_directories + ([current_library_path] if current_library_path else []))
                native_env["DYLD_LIBRARY_PATH"] = native_env["LIBRARY_PATH"]
            publish = work / "aot-publish"
            output = dotnet("publish", managed_project, "-c", "Release", "-f", "net10.0",
                            "-r", rid, "--no-restore", "-p:PublishAot=true", "--self-contained",
                            f"-p:FoundationConsumerVersion={VERSION}",
                            f"-p:RestorePackagesPath={package_cache}", "-o", publish, env=native_env)
            app = publish / ("Consumer" if sys.platform != "win32" else "Consumer.exe")
            execution = subprocess.run([str(app)], cwd=ROOT, text=True, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, env=native_env)
            with transcript.open("a") as log:
                log.write("+ " + shlex.join([str(app)]) + "\n" + execution.stdout + "\n")
            print(execution.stdout, end="", flush=True)
            if execution.returncode != 0 or "MANAGED_RUNTIME_CONSUMER_OK net10.0" not in execution.stdout:
                raise RuntimeError(f"Native AOT consumer failed; see {transcript}\n{output}")
            report["runtimeConsumers"].append({"targetFramework": "net10.0", "mode": "native-aot", "rid": rid})
    (work / "verification.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"Consumer verification passed: {len(report['cases'])} generator cases, "
          f"{len(report['runtimeConsumers'])} runtime cases; {work / 'verification.json'}")


if __name__ == "__main__":
    main()
