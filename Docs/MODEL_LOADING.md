# 3D model loading

V12 imports model files in the engine, not in a renderer. The same importer
feeds every host, and the resulting geometry goes through the ordinary mesh
path (`IMeshRenderable` → render packet → draws), so a model behaves like any
other scene element.

NovaV2 is the reference renderer. Pulsar (MonoGame) and Rockstar (Raylib) are
discontinued but kept in the repo.

## Two components, two shapes

| Component | Result | Use when |
|---|---|---|
| `ModelInstance` | The file's node hierarchy is rebuilt as elements; every mesh part becomes a child element with `StaticMesh` + `MaterialComponent` | The model should stay editable part by part, with its own transforms and per-part materials |
| `MeshAsset` | The whole file is flattened into one triangle soup with the hierarchy baked into the vertices; the dominant material is used | One element should be a single mesh (simple props, import tools) |

Both live in `V12-Engine/Components/Renderables` and accept `v12://`, `res://`
or filesystem paths.

## Quick start

`ModelInstance` (recommended for authored models):

```csharp
var desk = new Element("Desk");
desk.SetPosition(3.0f, 0.575f, -3.6f);
desk.SetRotation(0f, -28f, 0f);
desk.AddComponent(new ScaleComponent(0.5f));
desk.AddComponent(new ModelInstance("v12://samples/Desk.glb"));
world.AddElement(desk);
```

`MeshAsset` (single mesh):

```csharp
var rock = new Element("Rock");
rock.AddComponent(new MeshAsset("v12://Models/rock.glb"));
world.AddElement(rock);
```

That is the whole API surface for loading. The rest of this document is what
the importer does with the file.

The NovaV2 demo world loads `v12://samples/Desk.glb` this way. For desktop
testing launch the host with `--xr-mode off` (see the sample READMEs, e.g.
[`V12-Samples/UiShowcase`](../V12-Samples/UiShowcase/README.md)).

## Asset paths and mounts

`ModelLoader.ResolvePath` resolves a path in this order:

1. `v12://mount/path` through the registered `V12AssetResolver`.
2. `res://path` through the Godot project (NovaV2 only).
3. A path that already exists as a file.
4. The resolver's default mount (`ResolveRelative`).
5. Relative to the process working directory.

Mounts are how a game or tool exposes content:

```csharp
var resolver = new V12AssetResolver();
resolver.Mount("samples", @"D:\git\V12-Monorepo\V12-Samples");
root.Registry.RegisterOrReplace("AssetResolver", resolver);
// now v12://samples/Desk.glb resolves to D:\...\V12-Samples\Desk.glb
```

The first segment of a `v12://` URI is a mount name. If it is not a registered
mount, the whole remaining path is resolved against the default mount, so
`v12://Models/rock.glb` works from the pack root. `SetDefaultMount` chooses that
root explicitly; otherwise the first registered mount wins.

For `.v12pak` content, models ship under `assets/meshes/` and are addressed
through the pack's manifest. See
[`../V12-Engine/Docs/v12pak-spec.md`](../V12-Engine/Docs/v12pak-spec.md).

## `ModelInstance`

`ModelInstance` keeps the model's own structure:

- every file node becomes an `Element` with its local transform;
- every mesh part becomes a child element with a `StaticMesh` and, when the
  file authored one, a `MaterialComponent`;
- node names are preserved, so parts stay findable in the hierarchy;
- the generated root carries `NonPersistentComponent`, which covers the whole
  generated subtree, so parts are never saved to WorldML or replicated. Only
  the asset path is persisted; the tree is rebuilt on load.

### Options

| Member | Default | Purpose |
|---|---|---|
| `AssetPath` | — | `v12://`, `res://` or file path. Changing it discards and rebuilds the generated elements |
| `BuildHierarchy` | `true` | `false` flattens the file into one mesh under the owner, using the dominant material |
| `TriplanarFallback` | `true` | Projects textures on parts that carry no UV set, instead of sampling one texel |
| `Materials` | read-only | Materials discovered in the file, in file order |
| `GeneratedElements` | read-only | The elements built for the current asset |
| `Rebuild()` | — | Rebuilds from the current path (safe while the owner is in a world) |

The owner's transform is the model's placement. Node transforms come from the
file. If a node matrix cannot be decomposed into translation/rotation/scale
(shear or mirror), the importer keeps the translation so the part still lands
in the right place.

### Editing a loaded model

Each part is an ordinary element, so you can replace its material, reparent it,
or toggle it:

```csharp
var part = desk.Children.First(child => child.Name == "Desk_top");
part.AddComponent(new MaterialComponent { ShaderUri = "shader://game/wood" });
```

Rebuilding (`AssetPath` setter or `Rebuild()`) discards generated elements, so
edits to parts are not preserved across a rebuild. Apply runtime overrides from
a system if they need to survive asset swaps.

Scale advice: glTF is authored in metres. Check the model's raw bounds (the
`Desk.glb` sample is about 4 × 2.3 × 2.1 units) and use `ScaleComponent` rather
than editing the file.

## `MeshAsset`

`MeshAsset` is a single `IMeshRenderable`:

- `MeshPoints`, `MeshUvs`, `MeshNormals`, `MeshTangents`, `Indices` are the
  flattened file, every node transform applied;
- the dominant material (the one used by the most triangles) supplies
  `AlbedoColor`, `Metallic`, `Roughness` and the texture slots through
  `IModelMaterialSource`;
- an element's own `MaterialComponent` still wins; `MeshAsset` only fills the
  gap when the element has none.

Use it when parts do not matter. A flattened model loses per-part materials and
per-node transforms, but it is one draw instead of many.

## Materials authored in the file

`MaterialComponent.FromModelMaterial` copies the file's PBR material:

| File value | Component field |
|---|---|
| Base colour / diffuse × opacity | `R`, `G`, `B`, `A` |
| Metallic / roughness factors | `Metallic`, `Roughness` |
| Emissive colour + strength | `EmissionR/G/B`, `EmissionEnergy` |
| Normal scale | `NormalScale` |
| Alpha cutoff / blend | `AlphaCutoff`, alpha |
| Two-sided / no shading | `DoubleSided`, `Unlit` |
| Base-colour texture UV set | `UvSet` |
| Base colour, normal, metallic, roughness, emission, occlusion | texture slot paths |

`MaterialName` records the authored material name for inspectors and debugging.

The importer picks the UV channel the material's textures actually address
(glTF often uses `TEXCOORD_1`), falling back to channel 0 and then scanning the
mesh's channels. A mesh with texturing but no UV set gets triplanar projection
when `TriplanarFallback` is on; otherwise it samples a single texel and reads as
flat colour.

glTF packs metallic (blue) and roughness (green) into one image. The importer
binds that file to both slots and the backend reads the right channel per slot;
NovaV2 documents this in its material path.

## Textures

- **Embedded images** are extracted once into
  `%TEMP%/v12/modeltextures/<sha256>` with a guessed extension, so the same
  image shared by two models is written once. Only compressed embedded payloads
  are extracted; an uncompressed embedded image yields no texture slot.
- **Loose images** resolve relative to the model file and are stored as
  absolute paths. Embedded and loose paths both go through the normal texture
  loader later, so `v12://`/`res://` resolution still applies where relevant.
- A texture that fails to load is warned once per path by the host and the slot
  is left empty; the material still draws.

NovaV2's loader decodes PNG, JPEG, WebP, BMP, TGA, KTX, DDS, EXR and SVG.
Compressed container formats such as KTX2 are not handled by the embedded
extractor (it writes PNG/JPEG/WebP/BMP/TGA/DDS/EXR-style payloads); export
PNG or JPEG for portable embedded textures.

## Import conventions

`ModelLoader` imports with Assimp (`AssimpNetter`) and normalises the result:

- `Triangulate` + `JoinIdenticalVertices`; non-triangle faces cannot survive.
- No global UV flip. OBJ and DAE author V bottom-up, so their V is flipped;
  glTF and FBX stay top-down, matching Godot/Vulkan.
- Normals are normalised; meshes without authored normals have them derived by
  the renderer from winding.
- Tangents are computed from the chosen UV set (Lengyel), Gram-Schmidt against
  the normal, with the binormal sign packed into `w`.
- Node matrices are transposed from Assimp into the row-vector convention V12
  uses everywhere (`local × parent`).
- An `AssimpContext` is created and disposed per import, so no native memory is
  retained.

Supported extensions: `glb`, `gltf`, `obj`, `fbx`, `dae`, `ply`, `stl`, `3ds`,
`x`, `off`, `abc`, `lwo`, `ctm`. glTF/GLB is the recommended interchange format.

## Animated and skinned models

`ModelInstance` and `MeshAsset` load **static geometry**. They ignore animation
clips, skins and morph targets in the file. For characters and animated props,
use the `V12.Skeletal` module, which imports through Prowl.Clay and drives a
skeleton at runtime:

```csharp
character.AddComponent(new SkeletalManagerComponent
{
    AssetPath = "v12://Assets/CesiumMan.glb",
    SampleRate = 30f,
});

character.AddComponent(new SkeletalPlayerComponent
{
    PlayOnLoad = "Walk",
    LoopOnLoad = true,
});

player.Time = 0.75f;   // seek
```

See [`ANIMATION.md`](ANIMATION.md) for clips, bone attachments and the full
component list. The runnable sample is
[`V12-Samples/SkinnedCharacter`](../V12-Samples/SkinnedCharacter/README.md).

## Editor and WorldML

Both components expose an `AssetPath` inspector field restricted to the model
extensions above, so project pickers can offer model assets. `ModelInstance`
also lists the file's materials read-only in the inspector.

WorldML instantiates components by type name, so a model is just a component:

```xml
<Element name="Desk">
  <LocalTransform>
    <Position>3,0.575,-3.6</Position>
    <Rotation>0,-28,0</Rotation>
    <Scale>0.5,0.5,0.5</Scale>
  </LocalTransform>
  <Component type="ModelInstance" AssetPath="v12://samples/Desk.glb" />
</Element>
```

Only the component and its asset path are saved. The generated part elements
are marked non-persistent and are rebuilt every load.

## Lifecycle and performance

- Import is synchronous and happens on first access (attach or first render
  read). There is no background streaming.
- Each `ModelInstance` caches its `ModelDocument` per component; `MeshAsset`
  caches its flattened model. Disposing the component drops the cache.
- The asset resolver caches `GetModelMaterials` scans per resolved path, so
  editors can list materials without re-importing.
- `ModelInstance` produces one element (and usually one draw) per mesh part.
  Use `BuildHierarchy = false` or `MeshAsset` for background props with many
  parts.
- Generated geometry is derived data: do not sync it, save it, or hold live
  part references across a rebuild.

## Limitations

- Static loading only: clips, skins and morph targets are ignored; use
  `V12.Skeletal`.
- No LOD generation, instancing or mesh compression. Draco/meshopt-compressed
  glTF can fail to import depending on the Assimp build; export uncompressed if
  a file loads empty or throws.
- Uncompressed embedded textures are skipped (compressed embedded images are
  extracted).
- Node transforms that cannot decompose to TRS lose rotation/scale.
- Flat mode keeps only the dominant material.
- Import cost scales with file size and part count; large scenes should share
  models by reusing a single imported element, not by importing per element.

## Troubleshooting

| Symptom | Check |
|---|---|
| `FileNotFoundException` | Resolution order above; the mount name is the first `v12://` segment; the pak manifest maps the file |
| Model draws black/flat | Missing texture path (check the host warning), no UVs and triplanar off, or a material override with no textures |
| Textures look wrong on OBJ/DAE | V flip is format-specific; do not pre-flip on export |
| Inside-out faces | Enable `DoubleSided` on the material or fix winding in the source |
| Model invisible | Scale/position, camera near/far, or the host did not load a renderer that draws the mesh layer |
| Animation does not play | `ModelInstance` is static; use `SkeletalManagerComponent` + `SkeletalPlayerComponent` |
| Very slow first frame | Import is synchronous; preload before the first visible frame or flatten large props |

## Related documents

- [`ANIMATION.md`](ANIMATION.md) — skeletal animation, clips, bone attachments
- [`BUILDING_GAMES.md`](BUILDING_GAMES.md) — scene and gameplay cheatsheet
- [`SHADERS.md`](SHADERS.md) — custom materials on imported parts
- [`../V12-Engine/Docs/v12pak-spec.md`](../V12-Engine/Docs/v12pak-spec.md) — asset layout inside a pak
