# Shaders

V12 shader support: what the contract already holds, which of Godot's three doors we use, and the order of work.

## The decision

**Author in GDShader, transpile GDShader to GLSL for native renderers.**

The reverse direction (arbitrary GLSL to GDShader) was considered and rejected. Godot's shading language is a small, documented grammar with a *closed* set of built-ins, so GDShader to GLSL has a known input and a known output. GLSL to GDShader does not: arbitrary GLSL arrives with versions, extensions, `layout(...)` qualifiers, uniform blocks and custom vertex attributes, and has to be mapped onto Godot's fixed built-in set (`VERTEX`, `NORMAL`, `UV`, `ALBEDO`, ...). The failure modes are silent: a wrong `gl_Position` decomposition or a missing uniform hint produces plausible-looking output that renders wrong, with no error.

V12 is on the renderer where GDShader is native today (NovaV2), so the expensive direction would be paid for nothing. The cheap direction is only needed by a renderer that does not exist yet.

## Three doors

Godot offers three separate mechanisms, and conflating them is what makes this confusing.

| Door | What it is | Consumer | Status |
| --- | --- | --- | --- |
| 0. `ShaderMaterial` | GDShader source on `Shader`, engine does the lighting | NovaV2, per-material lit shaders | needs Phase 0 |
| 1. `CompositorEffect` | GLSL compute on the rendered frame (post-process) | NovaV2 screen-space effects | needs Phase 2 |
| 2. `RenderingDevice` | raw GLSL/SPIR-V, you own buffers, pipelines, lighting | NovaV2 procedural (fur shells, wind, noise) | needs Phase 2 |

Per-material toon is door 0. Raw GLSL in doors 1 and 2 is for procedural and screen-space work, where the result crosses back into an ordinary material through `Texture2DRD`. Doors 1 and 2 do not give you a custom lit material on a mesh.

## Why not SPIR-V for scene materials

Verified from Godot 4.7's class reference:

- `RDShaderFile` and `RDShaderSource` both state they are "only meant to be used with the `RenderingDevice` API" and are "not to be confused with Godot's own `Shader` resource, which is what Godot's various nodes use for high-level shader programming."
- `ShaderMaterial.shader` is typed `Shader`. There is no `ShaderRD` resource in the 4.7 class reference, and no documented bridge from `RDShaderSPIRV` to `Shader`.
- `Texture2DRD` is a **one-way** bridge: a raw `RenderingDevice` texture can be fed into `material.albedo_texture`. Shaders do not cross the other way.

So the `RenderingDevice` route means owning vertex/index buffers, uniform sets, pipelines, render passes, depth and blend state, per-frame command lists, **and lighting**. Godot's lights, shadows, tonemapping and PBR are all bypassed. That is writing a renderer inside Godot, not abusing an API.

Third-party work confirms the boundary rather than crossing it:

- `teamclouday/SPIRV-Importer` imports `.spv` as `RDShaderSPIRV`, and its own example scene feeds a **compute** shader into a `TextureRect`. Same door 2 pattern, with the import step automated.
- `shaderbridge` converts Unity ShaderLab/HLSL to GDShader. `glslify-godot` did GLSL to GDShader for Godot 3.x and is abandoned. `crshader` transpiles its own Crystal DSL to GDShader. There is no maintained general GLSL to GDShader tool.

## Door 0: per-material lit shaders

`light()` is what makes engine-style lit materials affordable. Godot lights the material; the shader overrides the lighting response:

```glsl
shader_type spatial;

uniform sampler2D albedoTex : source_color, repeat_enable;

void fragment() {
    ALBEDO = texture(albedoTex, UV).rgb;
    METALLIC = 0.0;
    ROUGHNESS = 1.0;
}

void light() {
    float ndl = dot(NORMAL, LIGHT_DIRECTION);
    float ramp = smoothstep(0.35, 0.5, ndl);   // toon step
    LIGHT_COLOR = LIGHT_COLOR * ramp;
    LIGHT_ENERGY = ramp;
}
```

`light()` exposes `LIGHT_DIRECTION`, `LIGHT_COLOR`, `LIGHT_ENERGY`, `LIGHT_IS_DIRECTIONAL`, `ATTENUATION`, `SHADOW`, `NORMAL`, `VIEW`, and writes to `LIGHT`, `LIGHT_COLOR`, `LIGHT_ENERGY`. Shadows, tonemapping and the rest of the scene lighting stay intact.

Getting `render_mode` wrong gives unlit or double-lit output with **no error**, which is why the manifest carries the framing (below) instead of asking the shader to infer it.

## The asset: `.v12shader` and the `shader://` virtual file

One URI, three backing stores, resolved in order:

```
shader://<mount>/<name>
  ├─ 1. ShaderAssetRegistry (in-process)   built-ins: Paper's paper/text
  ├─ 2. Pak manifest                        vcsdk pack output
  └─ 3. On-disk .v12shader next to source  editor / loose dev run
```

A game writes `shader://game/wood` on a `MaterialComponent`. It never names a `.gdshader` file, because that is a derived artifact:

```
shaders/wood.gdshader     source of truth (committed)
shaders/wood.json         framing: shader_type, render_mode, hints (committed)
        │  gdshader2glsl / v12shaderc
        ▼
shaders/wood.v12shader    manifest + compiled source + reflection (generated)
        │  shader://game/wood
        ▼
Shader { Code = "shader_type spatial; ..." }
```

`.v12shader` is JSON. The framing field is what the language cannot express:

```json
{
  "name": "wood",
  "version": 1,
  "godot": {
    "shader_type": "spatial",
    "render_mode": ["cull_back", "depth_draw_opaque"],
    "hints": { "albedoTex": "source_color", "normalTex": "hint_normal" }
  },
  "backends": {
    "gdshader": "shader_type spatial;\n...",
    "glsl":     { "vertex": "wood.vert", "fragment": "wood.frag" },
    "spirv":    { "vertex": "wood.vert.spv", "fragment": "wood.frag.spv" }
  },
  "parameters": [ { "name": "tint", "index": 0, "kind": "Vector4" } ],
  "textures":   [ { "name": "albedoTex", "slot": 0 } ]
}
```

- `godot` is mandatory framing: `shader_type` (default `spatial`), `render_mode` (default empty), `hints` maps uniform name to Godot hint. Texture intent (normal map versus albedo) cannot be inferred, so it is declared.
- `parameters` and `textures` are **auto-derived** by the compiler from the parsed uniforms, not hand-written. Hand-maintained reflection is what goes stale.
- `backends.glsl` and `backends.spirv` are reserved slots. A hand-written entry takes precedence over a generated one.

Reload: `ShaderAssetRegistry.Register(uri, program, version)` takes a version. `RendererResourceCache` documents that NovaV2 has no shader *update* entry point, so a version change destroys and recreates the shader. A watch mode that bumps the version gives hot reload.

## Native GLSL contract (Phase 3, deferred)

The contract a native renderer implements. `ShaderProgram` already carries `VertexSpirV` / `FragmentSpirV` and `ShaderSourceLanguage` already has `Slang`, `Glsl`, `Hlsl`. Nothing populates them today because no consumer exists.

```glsl
// vertex inputs
layout(location = 0) in vec3 V12_Position;
layout(location = 1) in vec3 V12_Normal;
layout(location = 2) in vec2 V12_Uv0;
layout(location = 3) in vec4 V12_Color;

// uniforms: indices match BuiltInMaterialParameters
uniform mat4 V12_Mvp;
uniform float u_Metallic;    // 1
uniform float u_Roughness;   // 2

// textures: slots match BuiltInTextureSlots
uniform sampler2D t_Albedo;  // 0

// fragment outputs
layout(location = 0) out vec4 o_Albedo;
layout(location = 1) out vec4 o_Emission;
```

`V12_Mvp` is required because GDShader's `VERTEX` is model-space and Godot applies the transform *after* `vertex()`; a native backend has to apply it itself.

`render_mode` has no GLSL equivalent, but on a native renderer cull, depth and blend are **pipeline state**, not shader state, so `render_mode unshaded, cull_front` maps to pipeline flags. That mapping needs a documented table.

## Backend targets

| Backend | Consumer | Input | Notes |
| --- | --- | --- | --- |
| `gdshader` | NovaV2 | `Shader { Code }` | native, zero cost |
| `glsl` | Rockstar, any OpenGL renderer | `Raylib.LoadShaderFromMemory(vertex, fragment)` | raylib takes GLSL source, not SPIR-V |
| `spirv` | a future Vulkan/D3D V12 renderer | `ShaderProgram.VertexSpirV` / `FragmentSpirV` | fields already exist, unused |

Rockstar is OpenGL through raylib, so it wants GLSL source. SPIR-V is for a future Vulkan or D3D backend. Authoring stays GDShader; one transpiler feeds both native slots.

## Phases

| # | Deliverable | Consumer |
| --- | --- | --- |
| 0 | `MaterialComponent.ShaderUri` → snapshot → `RenderFrameProducer` → `ShaderAssetRegistry` → `MaterialDesc`. Ends with a hand-written lit toon `.gdshader` rendering under the scene's directional light. | NovaV2, now |
| 1 | `shader://` resolution across all three stores, `godot` framing field, auto-derived `parameters`/`textures`, `Shaders` list in `V12PakManifest`, `vcsdk pack` step, watch mode, optional `EditorImportPlugin` for `.v12shader`. | NovaV2, now |
| 2 | Door 1 and door 2: `RDShaderSource` + `shader_compile_spirv_from_source` with GLSL, `Texture2DRD` into a material uniform, per-stage error reporting, `CompositorEffect` for post-process. | NovaV2 effects |
| 3 | `gdshader2glsl` producing `backends.glsl` (and `backends.spirv`), plus the `render_mode` to pipeline-state table. Optional Slang front-end via `slangc -target glsl`. | Rockstar, deferred |

Phase 0 first: it is small, and it unblocks hand-written `.gdshader` while the rest is built.

## Godot's rendering constraints

Verified against the 4.7 documentation:

- **Compositor effects are Mobile and Forward+ only.** NovaV2 runs Forward+.
- **Runtime compilation versus import.** `shader_compile_spirv_from_source` is documented as great for prototyping, with the warning that it "prevents precompiling and caching this shader which may be an issue on some platforms such as consoles." The tutorial's alternative is a `.glsl` file containing the whole shader, which Godot precompiles and caches. So: runtime compile for hot reload and error reporting, `.glsl` import for shipped content.
- **The compositor callback runs on the rendering thread.** Guard shared state with a `Mutex`; `free_rid` is deferred safely by the engine.
- **Uniform sets must be built with `UniformSetCacheRD.get_cache`**, which invalidates automatically when buffers are freed. Building them by hand each frame leaks or goes stale.
- **Push constants need 16-byte alignment**: the `PackedFloat32Array` length must be a multiple of 4 floats.
- **`RenderSceneBuffersRD` differs per renderer**, hence the cast in the tutorial.
- **`RenderingDevice` needs a renderer that exposes it.** Headless runs must be assumed not to have it: confirm before relying on RD in a headless test, and use the windowed screenshot path in `Main.cs` otherwise.

## Verification

- **Phase 0**: one hand-written lit toon shader on a mesh, screenshot through the Godot binary showing the toon ramp under the scene's directional light. The `V12_SCREENSHOT=<path>` hook in `NovaV2.Host/Main.cs` captures a frame and quits.
- **Phase 1**: generated manifest round-trips through `ShaderAssetRegistry`; `shader://` resolves from all three stores; existing `ShaderAssetRegistryTests` and the engine test suite pass.
- **Phase 2**: needs a windowed run, not headless. Assert the compile error strings are empty and a `Texture2DRD` reaches the material. Use the existing probe harness style: synthetic events, assertions, no human at the window.
- **Phase 3**: transpiler output must pass Godot's own GLSL importer with no errors: Godot becomes both compiler and test oracle, so no external toolchain is needed in CI. Plus negative tests: each rejected construct yields a diagnostic with a line number.

## Risks

- **Two artifacts to keep in sync** (GDShader source plus generated GLSL). The corpus test is what keeps that honest.
- **`render_mode` to pipeline mapping** needs a documented table. `skip_vertex_transform` and `world_vertex_coords` change how the MVP is emitted, and getting them wrong is silent.
- **Phase 1 ships a reserved `backends.glsl` slot that nothing compiles yet.** Add a test that fails loudly when a manifest declares it and nothing validates it, rather than let it rot.
- **Phase 2 is GPU-bound**, so it cannot be covered by the headless test runs used everywhere else.

## Checklist

- [ ] `MaterialComponent.ShaderUri` reaches `MaterialDesc` and renders in NovaV2.
- [ ] A lit toon `.gdshader` renders under the scene's directional light.
- [ ] `shader://` resolves from registry, pak, and disk in that order.
- [ ] `parameters` and `textures` are generated, never hand-written.
- [ ] Framing (`shader_type`, `render_mode`, `hints`) lives in the manifest, not inferred.
- [ ] Unsupported constructs fail with a file and line, never silently.
- [ ] `ShaderAssetRegistryTests` and `dotnet test V12.Architecture.Tests` pass.
- [ ] No `RDShaderFile` / `RDShaderSource` value is ever assigned to `ShaderMaterial.shader`.
