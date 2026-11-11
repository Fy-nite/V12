# V12 Game Paks (.NET)

How a C#/.NET game pak is structured, how the renderer discovers it, which
MSBuild keys to use, and what goes in `manifest.json`.

This is the reference for `vcsdk new net`, `vcsdk pack`, and hand-written
`.csproj` game paks. For the CLI itself see `V12-Sdk/README.md`.

---

## 1. What a game pak is

A game pak is a class library packaged as a `.v12pak` archive. The generic
renderer (for example `Pulsar.GL`) looks for `<executable name>.v12pak` next to
its own executable, loads it, and runs it as content plus gameplay code. You do
not write a `Main`, and you do not need an `IV12Gamepack` entry point.

`vcsdk pack` stages the published assembly and packs the archive. `vcsdk build`
on a directory containing a `.csproj` delegates to `pack`.

---

## 2. Discovery and load order

`V12PakLoader.Load` runs in this order:

| Step | Source | What it does |
|---|---|---|
| 1 | `manifest.Dependencies` | Loads dependency paks first |
| 2 | `manifest.Paks` | Extracts `paks/*.dll` and loads every assembly |
| 3 | `manifest.CSharpScripts` | Compiles `scripts/*.cs` at load time (live mode) |
| 4 | `manifest.Worlds` | Parses `world.xml` and templates |
| 5 | `manifest.Scripts` | Registers `.ct` / `.orbt` script entrypoints |

DLLs and C# sources load before worlds, so a pak's own WorldML can attach the
components it ships. After loading, the MonoGame host selects the pak's first
world automatically.

---

## 3. Components are the plugin contract

Any concrete type in a pak assembly that implements `IComponent` (directly, via
`ComponentBase`, or via `ScriptableComponent`) with a public parameterless
constructor is picked up. `CSharpComponentRegistry.RegisterAssembly` registers
them for the editor and Contract code, and `WorldMLParser` resolves them from the
loaded assemblies.

Attach a component by its type name in `world.xml`:

```xml
<Element name="Spinner">
  <Component type="TransformComponent" x="0" y="1" z="0" />
  <Component type="MeshComponent" Shape="Box" Width="1" Height="1" Depth="1" />
  <Component type="MeshRenderer" MeshTargetName="Mesh" />
  <Component type="MaterialComponent" R="0.82" G="0.52" B="0.24" A="1" Roughness="0.55" />
  <Component type="MyGameScript" Speed="45" />
</Element>
```

`WorldMLParser` matches the simple name or the fully qualified name,
case-insensitively. Direct attributes are assigned to public properties, so
`Speed="45"` sets `MyGameScript.Speed`.

### Requirements and rules

- Concrete class, not abstract, not an interface, not a generic definition.
- Public parameterless constructor (instantiation uses `Activator.CreateInstance`).
- A unique **simple** name across everything loaded in one session. The
  registry is keyed by simple name, so two paks that both ship
  `DefaultScript` will overwrite each other and WorldML will bind whichever
  loaded last. Name components after the pak, for example `MyGameScript`.
- `ScriptableComponent` is the small default: it sets `Name` from the type name,
  and you override `Update(float deltaTime)`.
- Stamp `[ComponentEntry("GAMEPLAY", "My Script")]` (namespace `V12.Components`)
  to give the editor a category and display name. Optional.

### When you need `IV12Gamepack`

`IV12Gamepack` is optional and is about lifecycle, not content:

| Method | When | Use for |
|---|---|---|
| `Initialize()` | After platform services exist, before the loop | Register services, resolve dependencies |
| `OnStart()` | After `GameRoot.Initialize()` | Networking, UI, game-loop setup |

Do **not** use `OnStart` to create a world the pak already ships under
`worlds/`. `CreateWorld` returns the existing world when the name matches
(selecting it again) and creates and selects a second one when it does not, so
the shipped scene is silently replaced. If the entire world is built in code,
that is the case where `OnStart` should create it, and the pak should not also
ship a `worlds/` entry.

Content-only paks should omit the entry point entirely. The template does.

---

## 4. Engine references (Purr packages)

`vcsdk new net` and `vcsdk new renderer` reference the engine from the Purr
install, not from a monorepo checkout:

```
~/.purr/
  packages/        Finite.V12.* and Contract.* nupkgs
  sdk/V12.props    V12Version + package feed, written by `vcsdk sync`
  renderers/<id>/<rid>/  installed renderer distribution + renderer.json
  templates/       user template overrides
  bin/             tools
```

Every scaffolded project gets a `Directory.Build.props` that imports
`~/.purr/sdk/V12.props`:

```xml
<Project>
  <PropertyGroup>
    <PurrHome Condition="'$(PurrHome)' == '' and '$(PURR_HOME)' != ''">$(PURR_HOME)</PurrHome>
    <PurrHome Condition="'$(PurrHome)' == '' and '$(USERPROFILE)' != ''">$(USERPROFILE)/.purr</PurrHome>
    <PurrHome Condition="'$(PurrHome)' == '' and '$(HOME)' != ''">$(HOME)/.purr</PurrHome>
  </PropertyGroup>

  <Import Project="$(PurrHome)/sdk/V12.props"
          Condition="'$(PurrHome)' != '' and Exists('$(PurrHome)/sdk/V12.props')" />

  <Target Name="V12VerifyPurr" BeforeTargets="Restore;CollectPackageReferences;PrepareForBuild">
    <Error Condition="'$(V12Version)' == ''"
           Code="V12SDK003"
           Text="V12 is not installed in Purr. Run 'vcsdk sync' to populate $(PurrHome)/packages and $(PurrHome)/sdk/V12.props." />
  </Target>
</Project>
```

`~/.purr/sdk/V12.props` carries the version and feed:

```xml
<Project>
  <PropertyGroup>
    <V12Version>1.0.0</V12Version>
    <V12PackageFeed>$(PurrHome)/packages</V12PackageFeed>
    <RestoreAdditionalProjectSources>$(RestoreAdditionalProjectSources);$(V12PackageFeed)</RestoreAdditionalProjectSources>
  </PropertyGroup>
</Project>
```

### Package references

A content pak:

```xml
<PackageReference Include="Finite.V12.Basic" Version="$(V12Version)" />
```

`Finite.V12.Basic` depends on `Finite.V12`, which brings `Finite.V12.Audio` and
`Finite.V12.Rendering`. `Finite.V12` depends on `Contract.Runtime`, so
`vcsdk sync` also packs the Contract runtime chain (`Contract.Runtime`,
`Contract.Compiler`, `ObjectRT.*`, `ObjektRT.*`). Add `Finite.V12.Paper`,
`.Editor`, `.Pak` or `.Rendering` only when the code uses them.

A renderer library:

```xml
<PackageReference Include="Finite.V12.Rendering" Version="$(V12Version)" />
<PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.3" PrivateAssets="All" />
```

or `Godot.NET.Sdk/4.7.0` as the project SDK for a Godot renderer. Renderer
libraries must not reference `V12.Components.*`.

### Engine development

Inside the monorepo, the root `Directory.Build.props` still defines
`$(V12EngineProject)`, `$(V12BasicProject)` and the rest of the path table for
engine and sample builds. Generated projects do not use those properties.

If you hand-write an out-of-tree project against a checkout instead of the Purr
feed, the silent empty-reference trap still applies: MSBuild imports only the
nearest `Directory.Build.props`, and `<ProjectReference Include="" />` produces
no item and no warning, which shows up as `CS0246` for every V12 type. Diagnose
with:

```powershell
dotnet msbuild MyGame.csproj -getProperty:V12EngineProject -getItem:ProjectReference
```

The fix is a `Directory.Build.props` that finds the checkout and imports its
root props, or simply use the Purr feed as above.

---

## 5. Pak layout and `manifest.json`

`vcsdk pack` stages the archive in this shape:

```
stage/
  manifest.json              optional; generated when absent
  worlds/{World}/world.xml
  worlds/{World}/templates/*.xml
  assets/**                  textures, meshes, audio, ...
  paks/*.dll                 game pak assemblies
  bin/**                     third-party dependency DLLs
  scripts/*.cs               C# sources (live mode)
  scripts/*.ct               Contract entrypoints
  dependencies/*.v12pak      dependency paks
```

Without a `manifest.json`, the packer scans the directory:

| Folder | Auto-detected as |
|---|---|
| `worlds/<dir>/world.xml` or `main.xml` | `Worlds` entry (name = directory name) |
| `worlds/<dir>/templates/` | `TemplatesDir` |
| `assets/**` | `Assets` map (key = path relative to `assets/`) |
| `paks/*.dll` | `Paks` |
| `scripts/*.ct`, `*.orbt`, `*.oil` | `Scripts` |
| `scripts/*.cs` | `CSharpScripts` (live mode) |
| `dependencies/*.v12pak` | `Dependencies` |

`vcsdk pack` adds the built assembly to `Paks` and first-party
`ProjectReference` DLLs to `paks/`; third-party NuGet DLLs go to `bin/`. Engine
assemblies (`V12*.dll`) are never packed, so the renderer's copy stays
authoritative.

### Manifest keys

| Key | Type | Meaning |
|---|---|---|
| `Version` | int | Manifest format version, currently `1` |
| `Engine` | string | Always `"v12"` |
| `Worlds` | array | `{ "Name", "Entry", "TemplatesDir" }` per world |
| `Assets` | object | Virtual path -> archive path |
| `Paks` | array | Archive paths to component DLLs |
| `Scripts` | array | Archive paths to `.ct` / `.orbt` / `.oil` entrypoints |
| `CSharpScripts` | array | Archive paths to `.cs` sources compiled at load |
| `Xr` | bool | The pak needs an XR runtime |
| `Dependencies` | array | Other `.v12pak` files loaded first |

Example:

```json
{
  "Version": 1,
  "Engine": "v12",
  "Worlds": [
    {
      "Name": "MyGame",
      "Entry": "worlds/MyGame/world.xml",
      "TemplatesDir": "worlds/MyGame/templates/"
    }
  ],
  "Assets": {
    "textures/crate.png": "assets/textures/crate.png"
  },
  "Paks": ["paks/MyGame.dll"],
  "Scripts": [],
  "CSharpScripts": [],
  "Xr": false,
  "Dependencies": []
}
```

Manifest paths are validated against the staged directory before packing, so a
typo fails the build instead of producing a pak with an empty world.

---

## 6. Build and run

```powershell
# one-time: scaffold
vcsdk new net --name MyGame -o D:\git\MyGame

# build the project and pack bin/MyGame.v12pak, copying the renderer beside it
vcsdk pack D:\git\MyGame

# equivalent for a project directory
vcsdk build D:\git\MyGame

# watch .cs/.csproj/content and repack on change
vcsdk serve D:\git\MyGame

# fail when the pak's engine version differs from the renderer's
vcsdk pack D:\git\MyGame --strict

# build for several platforms in one pass
vcsdk pack D:\git\MyGame --runtime win-x64,linux-x64,osx-arm64 -o dist
```

The default target RID is the host RID, and the matching renderer build is
copied from `renderers/<id>/<rid>/`. The result is `bin/<name>.v12pak` plus the
renderer executable renamed to `<name>` (`<name>.exe` on Windows). Multi-RID
builds produce `dist/<name>-<rid>.v12pak` and the matching host for each RID.

For `osx-*` targets, a Godot/Nova `.app` is deployed as `<name>.app` with its
inner `Contents/MacOS` executable renamed, `Info.plist` `CFBundleExecutable`
updated, an external `.pck` renamed when present, and the pak placed in
`Contents/MacOS` beside the executable. The SDK re-signs the modified bundle
ad-hoc on macOS; re-sign it with your own identity before distributing it.

Set `PURR_HOME` if Purr is not at `~/.purr`. Run `vcsdk sync` once before the
first build so `~/.purr/packages` and `~/.purr/sdk/V12.props` exist.

---

## 7. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `CS0246: V12 / GameRoot / ScriptableComponent not found` | Hand-written project with an empty `$(V12...Project)` | Use the Purr package refs from section 4, or add the checkout props import |
| `error V12SDK003` | `~/.purr/sdk/V12.props` is missing | Run `vcsdk sync` |
| `error V12SDK001` (hand-written checkout builds) | The props could not find a checkout | Set `V12_MONOREPO_ROOT`, or switch to the Purr packages |
| `WorldMLParser: Unknown component type 'X'` | The assembly was not loaded, or the type name does not match | Confirm `paks/` contains your DLL, the class is concrete with a parameterless constructor, and `type=` matches the class name |
| Component silently replaced by another pak's | Two paks ship the same simple type name | Give every component a pak-specific name |
| Pak loads but the wrong/none world shows | An `IV12Gamepack.OnStart` called `CreateWorld` with a different name, creating and selecting a second world | Remove the world creation, or stop shipping `worlds/` |
| `NU1101` / restore cannot find `Finite.V12.*` | The Purr feed is missing packages | Run `vcsdk sync` |
| Version mismatch warning | Pak and renderer built from different engine versions | Rebuild the pak or set `--strict` to make it an error |
