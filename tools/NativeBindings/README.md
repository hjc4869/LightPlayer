# NativeBindings

NativeBindings generates C# declarations for the C API of native libraries at build time. A project names C headers with `[Include]` attributes; each build parses those headers for one exact target ABI and emits `DllImport` functions, constants, and blittable native types that are compatible with trimming and NativeAOT.

The toolkit has three parts:

- [NativeBindings.props](NativeBindings.props), [NativeBindings.Selection.targets](NativeBindings.Selection.targets), and [NativeBindings.targets](NativeBindings.targets) select the target of each project, separate its outputs, propagate the selection to referenced projects, and configure native linkage.
- The [parser](Parser/NativeBindings.Parser.csproj) is a framework-dependent build-host tool. It collects `[Include]` requests from the project's C# sources, selects headers and libraries, parses the headers through the libclang C API, inspects the libraries' exports, and writes a binding manifest.
- The [generator](Generator/NativeBindings.Generator.csproj) is a Roslyn incremental source generator that turns the manifest into C#.

The binding surface is exported C functions, value macros, enum constants, and the types needed to call them. C++ APIs are outside the surface; a library implemented in C++ is supported when it exposes a C API.

## Setup

Import the props file from `Directory.Build.props` and the targets file from `Directory.Build.targets`, so every project in the build participates in target selection:

```xml
<!-- Directory.Build.props -->
<Import Project="path/to/NativeBindings/NativeBindings.props" />

<!-- Directory.Build.targets -->
<Import Project="path/to/NativeBindings/NativeBindings.targets" />
```

Enable generation in the project that owns the declarations and configure a native provider:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <NativeBindingsEnabled>true</NativeBindingsEnabled>
  <NativeBindingsPkgConfigModules>zlib</NativeBindingsPkgConfigModules>
</PropertyGroup>
```

```csharp
using System.Reflection;
using LightStudio.NativeBindings;

[Include("zlib.h", Access = BindingFlags.Public)]
public static unsafe partial class Zlib;

[Include("errno.h", Macros = ["EAGAIN", "ENOMEM"])]
internal static partial class Errors;
```

`IncludeAttribute` is generated into each enabled project. Its arguments must be literals.

| Argument | Behavior |
| --- | --- |
| `Header` | Header path relative to the include roots of the selected provider, with `/` separators. |
| `Class` | Generated nested static partial class that receives the members. Members are added to the annotated type when omitted. |
| `Access` | `BindingFlags.Public` or `BindingFlags.NonPublic` (default, generates private members). Includes sharing a generated class must agree. |
| `ImportName` | Overrides the module name of dynamically linked imports from this header. The named library must provide every affected function. |
| `Macros` | Selects only these macros, evaluated after including the header, wherever they are defined (for example C library macros). No functions or types are generated for such an include. |

The annotated type and all containing types must be non-generic partial classes or structs. Several `[Include]` attributes can target the same type; identical declarations from different headers merge, while conflicting ones are errors.

## What is generated

- **Functions** declared in headers that belong to the selected libraries, with external linkage and a providing library. Each becomes a `DllImport` with the native entry point and calling convention; a function exported by several selected libraries is an error. Functions with `const char*` parameters or results also get a wrapper that converts .NET strings to and from UTF-8; the raw import is then named `__native_<name>`.
- **Constants** from enum members and from object-like macros that evaluate to an integer, floating-point, or string constant in the target compiler.
- **Types** in the namespace `NativeBindingsTypeNamespace`: named enums with their underlying type, structs and unions with the native size, alignment, and field offsets (sequential layout only for scalar-only structs whose managed layout reproduces them, explicit layout otherwise), fixed arrays (`NativeArray_<element>_<length>` with checked indexers), bitfield accessor properties, flexible array member pointers, and empty opaque structs for records that are incomplete or belong to other libraries. A project that references an assembly already containing a type with the same name reuses it, so types from one binding owner flow through its consumers.

Scalar types follow the target ABI: `size_t` and `uintptr_t` lower to `nuint`, `ptrdiff_t`, `intptr_t`, and `ssize_t` to `nint`, C `long` to the target's width, `char` and `_Bool` to `byte`, and function pointer fields to typed `delegate* unmanaged` pointers. Function pointer parameters lower to `void*`.

Unsupported declarations are not guessed. The manifest records each one as an `<excluded>` entry with its kind and reason. Excluded are: variadic functions, header-only inline functions, functions without prototypes or without a providing library, exported variables, function-like macros, macros that are not constant expressions, records passed by value without sequential layout, `va_list` passed by value, `long double` wider than 64 bits, and other types without a verified C# representation.

### Header ownership

Each request is parsed in its own translation unit. Declarations are generated from the headers that belong to the selected libraries, never from the C runtime or platform SDK:

- A requested header inside a provider include root makes that whole root owned, unless the compiler treats the directory as a system directory.
- In a system directory (for example `/usr/include`), a request owns only its own top-level directory, or the header file itself when it has none. Headers that owned headers include with quoted includes are owned as well, which covers umbrella headers that include their siblings.
- Types from other headers that appear in owned declarations are emitted as opaque types when referenced through pointers.

## Native providers

Every selection uses exactly one provider.

| Provider | Configuration | Targets |
| --- | --- | --- |
| Native package | `NativeBindingsPackageDirectory`, optionally `NativeBindingsProfile` | Any supported target, including cross-compilation. |
| pkg-config | `NativeBindingsPkgConfigModules` (semicolon-separated); `NativeBindingsPkgConfig` selects the executable, defaulting to `PKG_CONFIG` or `pkg-config` | The build host's own runtime. |
| Explicit directories | `NativeBindingsIncludeDirectory`, `NativeBindingsLibraryDirectory`, `NativeBindingsLibraries` (semicolon-separated library names without `lib` prefix or extension) | The build host's own runtime. |

With pkg-config, explicit include directories, library directories, or library names replace the corresponding pkg-config results. System providers resolve `lib<name>.so` (Linux) or `lib<name>.dylib` (macOS) for dynamic linkage, and `lib<name>.a` for static linkage.

### Package manifest

A native package provides `native-bindings/manifest.json` at its root. The parser reads these fields:

```json
{
  "profiles": {
    "android-arm64": {
      "abi": {
        "runtimeIdentifier": "android-arm64",
        "triple": "aarch64-unknown-linux-android35",
        "endianness": "little",
        "pointerSize": 8,
        "longSize": 8
      },
      "includeRoots": ["native-bindings/profiles/android-arm64/include"],
      "defines": [],
      "undefines": [],
      "headers": [{ "path": "native-bindings/profiles/android-arm64/include/example.h", "sha256": "..." }],
      "libraries": [
        {
          "path": "static/android-arm64/libexample.a",
          "sha256": "...",
          "role": "implementation-archive",
          "runtimeIdentity": null
        }
      ],
      "wasm": null
    }
  }
}
```

- A profile is selected automatically when exactly one profile matches the runtime and provides libraries of the needed role: `shared` for dynamic linkage, `implementation-archive` for static linkage. Otherwise set `NativeBindingsProfile`.
- `runtimeIdentity` is the identity a shared library is loaded by (ELF `DT_SONAME`, Mach-O install name, or DLL file name). It is checked against the binary.
- All paths are package-relative and must stay inside the package. Every listed header and library is verified against its SHA-256 hash before use.
- Browser profiles can set `"wasm": { "simd": "simd128", "threads": true }`, which adds `-msimd128` and `-pthread` to the compiler arguments.
- The package's own MSBuild targets remain responsible for deploying shared libraries and adding static link inputs.

## Target selection and output separation

`NativeBindingsRuntime` is the runtime identifier the bindings are generated for. It defaults to `RuntimeIdentifier`, then the first entry of `RuntimeIdentifiers`, then the host operating system combined with the `x64` or `ARM64` `Platform`/`PlatformTarget`, and finally the SDK's host runtime.

The selection of a project is its runtime, a profile when it differs from the runtime, `static` for static linkage, and `stubs` when imports are not emitted. NativeBindings.Selection.targets is imported through `BeforeMicrosoftNETSdkTargets`, after the project file and before the SDK computes output paths:

- With the SDK artifacts output layout, `ArtifactsPivots` becomes `<configuration>_<selection>` (plus the target framework in multi-targeted projects), for example `debug_linux-x64` or `release_android-arm64_static`.
- Otherwise the default `OutputPath` and `IntermediateOutputPath` gain a `<configuration>/<selection>/` directory.
- Explicit output paths or `ArtifactsPivots` are left unchanged, and `NativeBindingsSelectionPivots=false` disables separation. Such configurations must separate selections themselves.

NativeBindings.targets passes the selection to referenced projects as global properties, including SDK-added transitive references, so layout-dependent code compiles once per selection and never against another target's types or constants. Analyzer and other non-reference project references, including the toolkit itself, are built for the build host without the selection. Projects that do not use the .NET SDK, such as packaging projects, pass no selection; the projects they reference select their own. Multi-RID outer builds compile against their first runtime; each inner build uses its own.

`NativeBindingsPolicy` names an optional MSBuild file imported during selection, after the runtime is known and before defaults are applied. Repositories use it to choose providers, profiles, and linkage per runtime in one place.

## Linkage

| Mode | Imports | Linking |
| --- | --- | --- |
| Dynamic (default) | Module names are the runtime identity of the library exporting each function: ELF `DT_SONAME`, Mach-O install name, or PE DLL file name. | Libraries are loaded at run time; deploying them is the responsibility of the application or package. |
| Static NativeAOT (`NativeBindingsStatic=true`) | One import token, `__Internal` by default or `NativeBindingsImportName`. | Executables register the token as `DirectPInvoke` and must use `PublishAot=true`. Package providers supply link inputs through their own targets. For system providers, the archives and the dependencies from `pkg-config --static --libs` are added as `NativeLibrary` link inputs. |
| Browser (`browser-wasm`, always static) | The archive name without `lib` prefix and extension, as registered by the WebAssembly SDK's native linking. | Archives are linked through `NativeFileReference` by the package. |

`NativeBindingsEmitImports=false` generates the same API with members that throw `PlatformNotSupportedException` instead of calling native code, for builds that must compile shared code without linking the libraries.

## Properties

| Property | Default | Purpose |
| --- | --- | --- |
| `NativeBindingsEnabled` | `false` | Generates bindings for the project's `[Include]` attributes. |
| `NativeBindingsRuntime` | Inferred | Target runtime identifier. |
| `NativeBindingsProfile` | Automatic | Package profile name. |
| `NativeBindingsStatic` | `false`; `true` for `browser-wasm` | Static linkage. |
| `NativeBindingsImportName` | `__Internal` when statically linked | Import token of static NativeAOT linkage. |
| `NativeBindingsEmitImports` | `true` | `false` generates throwing stubs. |
| `NativeBindingsTypeNamespace` | `LightStudio.NativeBindings.Types` | Namespace of generated types. |
| `NativeBindingsPackageDirectory` | | Native package root. |
| `NativeBindingsPkgConfigModules`, `NativeBindingsPkgConfig` | | pkg-config provider. |
| `NativeBindingsIncludeDirectory`, `NativeBindingsLibraryDirectory`, `NativeBindingsLibraries` | | Explicit system provider, or overrides of pkg-config results. |
| `NativeBindingsCompilerArgument` items | | Additional compiler arguments, such as `-D` definitions. |
| `NativeBindingsClang`, `NativeBindingsLibClang`, `NativeBindingsSymbolTool`, `NativeBindingsSysroot` | Discovered | Target compiler, libclang library, `nm`-compatible symbol tool, and sysroot. |
| `NativeBindingsNdkDirectory` | Discovered | Android NDK; also read from `AndroidNdkDirectory`, `ANDROID_NDK_ROOT`, and `ANDROID_NDK_HOME`. |
| `NativeBindingsDeploymentTarget` | iOS: `SupportedOSPlatformVersion` | Apple deployment version; otherwise 11.0 for macOS and 15.0 for iOS. |
| `NativeBindingsPolicy` | | MSBuild file imported during selection. |
| `NativeBindingsSelectionPivots` | `true` | `false` disables output separation. |

## Build prerequisites

All build hosts need:

- A .NET SDK that can run .NET 10 applications; the parser runs on the SDK's `dotnet` host, and the generator runs in the compiler.
- A libclang shared library for the architecture of that `dotnet` process. The parser loads `NativeBindingsLibClang` when set, then the libclang beside the target toolchain's Clang, then the build host's installations. It passes the libclang's builtin header directory explicitly.
- The .NET workload of the target platform (`android`, `ios`, or `wasm-tools`) and the target's usual .NET native build prerequisites, such as the NativeAOT linker toolchain.

| Build host | Target | Additional requirements |
| --- | --- | --- |
| Linux | Linux (host architecture), dynamic | `pkg-config` and the development packages of the libraries: headers, `.pc` files, and `lib<name>.so` links. libclang from the distribution, for example `libclang-dev` on Debian and Ubuntu or `clang-devel` on Fedora. |
| Linux | Linux, static NativeAOT | Additionally the static archives of the libraries and of every dependency listed by `pkg-config --static --libs`, and `nm` or `llvm-nm`. |
| Linux | Flatpak | Build inside the Flatpak SDK with its development files, and add an LLVM SDK extension (for example `org.freedesktop.Sdk.Extension.llvm22`) whose `bin` directory is on `PATH` to provide libclang. |
| Linux | Android | Android SDK with an NDK; the NDK provides libclang, `llvm-nm`, and the sysroot. The NDK ships x86_64 host binaries only: on other hosts, also install a host libclang and set `NativeBindingsSymbolTool` to a host `llvm-nm`. |
| Linux | Browser | `wasm-tools` workload (Emscripten Clang, `llvm-nm`, and sysroot) and a host libclang, since the workload has none. |
| macOS | macOS, iOS, iOS simulator | Xcode with its command-line tools; `xcrun` provides libclang, Clang, `nm`, `otool`, and the SDKs. The `ios` workload must match the Xcode version. |
| macOS | Android | Android SDK with an NDK. |
| macOS | Browser | `wasm-tools` workload; Xcode provides libclang. |
| Windows x64 or ARM64 | Windows x64 or ARM64 | Visual Studio or Build Tools with the "Desktop development with C++" workload (MSVC and a Windows SDK), plus the MSVC build tools of each target architecture for NativeAOT linking. LLVM for the host architecture with `clang.exe`, `libclang.dll`, and `llvm-nm.exe` (static profiles), for example from `winget install --id LLVM.LLVM --exact`. The parser searches `PATH`, `%ProgramFiles%\LLVM`, and the Visual Studio LLVM toolset. |

Windows targets require a Windows build host and Apple targets a macOS build host. Targets other than the build host's own runtime require a native package profile. System providers on macOS, such as Homebrew libraries found through pkg-config, serve only the host runtime.

## Outputs and diagnostics

The parser writes to `$(IntermediateOutputPath)nativebindings/`:

- `bindings.nativebindings.xml`: the manifest consumed by the generator. It records the target triple, pointer width, provider, linkage, generated declarations, and exclusions.
- `bindings.nativebindings.xml.fingerprint`: the inputs of the last generation. The parser skips work when options, requests, the parser itself, relevant environment variables, libclang, and every header, library, and package file it used are unchanged.
- `*.c` and `*.c.diagnostics.txt`: the translation unit of each request and the complete libclang diagnostics.

Design-time builds do not run the parser; they use the existing manifest. Parser errors are reported as `NB1001` with details from the failing tool or exception. Generator errors (invalid attribute usage, manifests from another runtime, unavailable requested macros, conflicting declarations) are reported as `NB2001` at the annotated type.
