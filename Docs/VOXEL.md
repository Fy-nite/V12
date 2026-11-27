# V12.Voxel

`V12.Voxel` adds deformable, Astroneer-style planets to V12. The planet is a
signed-distance field, not a mesh: brushes edit density samples, and only the
chunks a brush touched are re-meshed into ordinary `IMeshRenderable` geometry.

The package depends on `V12-Engine` only. It has no renderer, physics or
gameplay references, so it can simulate a planet on a headless server and let a
client rebuild the same surface from the same seed and edit log.

```
V12.Voxel/
  VoxelPlanetSettings.cs      radius, voxel size, chunk size, noise, palette
  VoxelField.cs               analytic sphere + seeded value noise
  VoxelTerrain.cs             sparse edits, cached chunk grids, raycast, brushes
  VoxelSurfaceNets.cs         chunk mesher
  VoxelBrush.cs               brush modes, results, hit
  VoxelChunkKey.cs            chunk addressing and voxel key packing
  Components/
    VoxelPlanetComponent.cs   ECS glue: chunk elements, budgeted remesh, tool API
    VoxelChunkMesh.cs         one chunk as IMeshRenderable
```

## How it works

**Base field plus sparse edits.** `VoxelField` returns
`|p| - (Radius + noise)`: negative is solid, positive is air. Nothing about the
base sphere is stored. A brush writes only the voxels it changes into a
`Dictionary<long, float>` of overrides, so memory grows with edits, not with the
planet volume.

**Chunked surface nets.** A chunk owns `ChunkCells³` cells and samples a
`(ChunkCells + 2)³` density grid, including a one-cell border. Each active cell
becomes one vertex (the average of its edge crossings), and each sign-changing
grid edge becomes one quad. Vertex normals come from the density gradient, which
is what makes a dug wall shade smoothly instead of showing the voxel grid.

Seam ownership is explicit: a quad is emitted by exactly the chunk that owns the
edge's minimum cell, and vertex positions are derived from global voxel
coordinates, so the two chunks sharing a cell compute identical positions. A
headless test (`V12.Voxel.Tests`) asserts that the assembled surface has no open
edges, no duplicated seam triangles, and no inward-facing triangles.

**Cached grids.** After a chunk is meshed, its density grid stays in memory. A
brush updates only the affected entries in every cached chunk that contains the
edited voxel (up to eight, because of the border), and meshing then runs without
noise or dictionary lookups. That cache is the difference between a ~20 ms brush
hitch and a sub-millisecond one.

**Budgeted remesh.** A brush marks chunks dirty but keeps the previous mesh on
screen. `VoxelPlanetComponent` rebuilds dirty chunks nearest the brush first,
under a 4 ms per-tick budget. Remeshing never happens inside frame capture.

**Vertex colour strata.** `VoxelChunkMesh` publishes `MeshColors`, so the
palette is baked per vertex with no texture and no UV unwrap. Depth is measured
against the noisy base surface, not the nominal radius, so a pristine dip stays
topsoil and only cut material exposes lower bands.

## Use it

World file:

```xml
<Element name="Planet">
  <Component type="VoxelPlanetComponent"
             Radius="28" VoxelSize="1" ChunkCells="8"
             Seed="1207" NoiseAmplitude="2.9" NoiseFrequency="0.075"
             CrustThickness="4" />
</Element>
```

Code:

```csharp
// Ray from the camera (world space) -> carve where it hits.
if (planet.TryUseTool(origin, direction, radius: 2.4f,
                      VoxelBrushMode.Dig, strength: 0.5f, out VoxelHit hit))
{
    // hit.Point / hit.Normal are world space.
}

// Brush at a world position, no raycast (explosions, scripted craters).
planet.ApplyWorldBrush(worldCenter, radius: 5f, strength: 3.6f, VoxelBrushMode.Dig);

// Server-side simulation: planet-local brushes, no ECS owner required.
terrain.Apply(VoxelBrush.Fill(localCenter, 3f, 1.2f));
```

Brush modes:

| Mode | Effect | Strength |
|---|---|---|
| `Dig` | adds density, carving air | metres of density |
| `Fill` | removes density, adding ground | metres of density |
| `Smooth` | blends samples toward their neighbourhood | 0..1 blend |

`VoxelBrushResult` reports the changed voxel count and the exact chunk bounds
that must be remeshed.

## Engine contract additions

Per-vertex terrain colour needs a mesh channel the contract previously lacked:

- `IMeshRenderable.MeshColors` (flat RGBA doubles, optional, null means white).
- `RenderableSnapshot.MeshColors` and the capture/producer plumbing.
- `MaterialDesc.VertexColors`, which the producer sets automatically when a mesh
  publishes colours. NovaV2 maps it to `VertexColorUseAsAlbedo`; a custom shader
  reads `COLOR` as usual.

This is additive. Existing meshes keep the opaque-white colour stream and render
exactly as before.

## Performance

Measured in the `VoxelPlanet` sample on a 28 m planet, 1 m voxels, 8 m chunks:

| Operation | Cost |
|---|---|
| Initial build, 376 chunks | ~200-240 ms, one-time |
| Brush pass (2.4 m radius) | 8 chunks, 0.6-0.9 ms |
| Remesh budget | 4 ms per fixed tick |

The sample applies the brush every fixed tick (60 Hz) at roughly
`VoxelSize * 0.14` strength per tick. Raise the strength for a faster tool or
lower it for finer control; the remesh budget keeps the frame cost bounded
either way.

Tuning knobs:

- `VoxelSize` is the detail/remesh trade. Bigger voxels mean fewer chunks and
  cheaper remeshes, but a blobbier brush.
- `ChunkCells` trades chunk count against per-chunk remesh cost. Large chunks
  lower draw calls but make each edit more expensive.
- `CrustThickness` only affects the eager startup set. Deeper chunks are created
  lazily when a brush reaches them.

## Limits and next steps

- **No persistence yet.** Terrain is rebuilt from settings on load; edits do not
  round-trip through WorldML. Saving should store the sparse override map, not
  the mesh.
- **No replication yet.** Do not sync chunk vertices. Replicate brush operations
  (center, radius, strength, mode) and replay them deterministically on peers.
  The field and noise are seed-stable for this reason.
- **No collision backend.** `VoxelTerrain.SampleDensity`, `IsSolidAt` and
  `Raycast` are enough for an SDF character motor or for rebuilding a per-chunk
  `MeshShape.Custom` Bepu body. A chunk body must be recreated when its mesh
  changes; `V12.Physics` has no in-place shape swap yet.
- **No LOD.** A whole planet is one fixed voxel size. Transvoxel or chunk LOD is
  the next step if planets get much larger than tens of metres.
- **No baked AO.** The vertex colour stream is ready for it; a corner-solidity
  pass is the natural next addition.

## Development notes

- The mesher and field are deterministic: same settings and edit sequence, same
  mesh. Do not introduce wall-clock or `System.Random` into either.
- Run the geometry invariants after mesher changes:

  ```powershell
  dotnet run --project V12.Voxel.Tests
  ```

- A gamepak does **not** carry its V12 package assemblies. They come from the
  installed renderer host, so a stale renderer slot shows up as a missing
  component at parse time (for example `V12.Voxel.dll` absent = no planet).
  One command packs every discovered `Finite.V12.*` module and installs any
  assembly a renderer slot is missing:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-v12.ps1
  ```

  `vcsdk sync` already auto-discovers root-level `Finite.V12.*` projects, so a
  new package needs no SDK edit. Add `-DryRun` to preview, `-Force` to overwrite
  existing assemblies, or `-Renderer novav2` to target one slot.

  Verify a slot against the build that needs it:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-v12-deps.ps1 -Renderer novav2 -IncludePackages
  ```

  The audit lists every expected `V12.*.dll`, flags missing/stale entries, and
  can print the newest `Finite.V12.*` packages with `-Packages`. Refresh the
  whole renderer (host assemblies included) after an engine change:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-novav2-renderer.ps1
  ```

  The three steps are distinct: `vcsdk sync` updates the NuGet feed,
  `tools/sync-v12.ps1` installs missing packages into renderer slots, and
  `tools/install-novav2-renderer.ps1` re-exports the renderer host itself.
  `vcsdk build -r novav2` only copies the already-installed renderer.

- NovaV2 resolves optional packages from its **own** output directory. After
  changing `V12.Voxel`, rebuild the host, not just the sample:

  ```powershell
  dotnet build NovaV2/NovaV2.Host/NovaV2.Host.csproj
  ```

  Otherwise the game keeps running the previous `V12.Voxel.dll` and the change
  appears to do nothing.
