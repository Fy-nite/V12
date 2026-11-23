@tool
extends RefCounted
## Puts Workbench on the editor's 3D viewports and takes it off again.
##
## While active, each editor camera draws with a neutral environment (a flat
## background, no fog, glow, ambient or tonemapping), neutral camera
## attributes (no exposure or auto-exposure) and a compositor carrying the
## effect, and each viewport draws unshaded when the texture colour is
## wanted. Those are only on the camera and viewport for the duration of a
## draw: [method before_draw] puts them on from
## [signal RenderingServer.frame_pre_draw] and [method after_draw] puts the
## editor's own values back from [signal RenderingServer.frame_post_draw].
## Between frames the editor sees its own state, which matters because it
## stores its View Environment toggle as "does the camera have an
## environment" in the per-scene edit state, and would otherwise save and
## replay this addon's environment as the user's choice.
##
## MSAA is the exception: changing it rebuilds the render buffers, so it stays
## off for as long as the mode is on, and is re-asserted before every draw
## because the editor puts the project's value back on a settings change.
##
## X-ray needs what the opaque pass throws away, the geometry behind the
## front surface. For it, each editor viewport gets a hidden child
## SubViewport rendering the same world from a copy of its camera in the
## editor's Overdraw debug draw, which draws every surface, regardless of
## depth, as a fixed amount of green on black. A child viewport renders before
## its parent in the same frame, so the count of surfaces under each pixel is
## current when the effect blends it in.
##
## Wireframe needs the mesh edges, which no screen-space buffer knows. It
## gets a hidden viewport of the same kind in the editor's Wireframe debug
## draw, which rasterizes every mesh as lines with real depth, and a
## depth-exporting effect on that viewport's camera writes the edge pixels and
## their depth into its colour buffer, for the main effect to depth-test
## against the solid surfaces.

const WorkbenchEffect := preload("workbench_effect.gd")
const VIEWPORT_COUNT := 4
## Blender's default viewport grey, as a display colour.
const BACKGROUND := Color(0.24, 0.24, 0.24)
## The editor cameras see the game's 20 render layers plus their own gizmo,
## grid and tool layers above; the x-ray camera only wants the game's.
const GAME_LAYERS := (1 << 20) - 1
const XRAY_VIEWPORT_NAME := "WorkbenchXray"
const WIREFRAME_VIEWPORT_NAME := "WorkbenchWireframe"

## The options, read by every viewport's own effect before each draw.
var effect: WorkbenchEffect
var environment: Environment
var attributes: CameraAttributesPractical
var active: bool = false:
	set(value):
		if value != active:
			active = value
			if active:
				_take()
			else:
				_restore()
## Shade with the albedo from the colour buffer instead of a flat colour.
var texture_color: bool = false:
	set(value):
		texture_color = value
		effect.texture_color = value
## See through the front surfaces to the geometry behind them.
var xray: bool = false
## How opaque the front surface is in x-ray.
var xray_alpha: float = 0.5
## Line every mesh edge, as Blender's Wireframe overlay does.
var wireframe: bool = false
## How dark the wireframe lines are.
var wireframe_opacity: float = 1.0
## Only line the edges where the surfaces either side differ by at least this
## many degrees; 0 lines every edge, as Blender's threshold of 1 does.
var wireframe_threshold: float = 0.0

## Per viewport taken over: its camera, its own effect and compositor, the
## MSAA it had, during a draw the environment, attributes, compositor and
## debug draw it had before the draw, and in x-ray and wireframe their hidden
## viewports and cameras, under "xray_viewport", "xray_camera",
## "wire_viewport" and "wire_camera".
var _entries: Array[Dictionary] = []
## True between [method before_draw] and [method after_draw].
var _drawing: bool = false


func _init() -> void:
	effect = WorkbenchEffect.new()
	effect.background_color = BACKGROUND
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = BACKGROUND
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_DISABLED
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	attributes = CameraAttributesPractical.new()


func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE and active:
		_restore()


## Puts the workbench state on every editor viewport for the draw that
## follows. Connected to [signal RenderingServer.frame_pre_draw] while active.
func before_draw() -> void:
	if not active or _drawing:
		return
	_drawing = true
	for entry: Dictionary in _entries:
		var camera := entry["camera"] as Camera3D
		var viewport := entry["viewport"] as SubViewport
		if not is_instance_valid(camera) or not is_instance_valid(viewport):
			continue
		var own_effect := entry["effect"] as WorkbenchEffect
		_copy_options(own_effect)
		if xray:
			_ensure_ghost(entry, "xray", XRAY_VIEWPORT_NAME, Viewport.DEBUG_DRAW_OVERDRAW, false)
			_sync_ghost(entry, "xray")
			own_effect.xray = xray_alpha
			own_effect.xray_texture = RenderingServer.viewport_get_texture((entry["xray_viewport"] as SubViewport).get_viewport_rid())
		else:
			_free_ghost(entry, "xray")
			own_effect.xray = 0.0
			own_effect.xray_texture = RID()
		if wireframe:
			_ensure_ghost(entry, "wire", WIREFRAME_VIEWPORT_NAME, Viewport.DEBUG_DRAW_WIREFRAME, true)
			_sync_ghost(entry, "wire")
			own_effect.wireframe = wireframe_opacity
			own_effect.wireframe_threshold = wireframe_threshold
			own_effect.wireframe_texture = RenderingServer.viewport_get_texture((entry["wire_viewport"] as SubViewport).get_viewport_rid())
		else:
			_free_ghost(entry, "wire")
			own_effect.wireframe = 0.0
			own_effect.wireframe_texture = RID()
		entry["environment"] = camera.environment
		entry["attributes"] = camera.attributes
		entry["compositor"] = camera.compositor
		entry["debug_draw"] = viewport.debug_draw
		camera.environment = environment
		camera.attributes = attributes
		camera.compositor = entry["own_compositor"] as Compositor
		if viewport.msaa_3d != Viewport.MSAA_DISABLED:
			viewport.msaa_3d = Viewport.MSAA_DISABLED
		viewport.debug_draw = Viewport.DEBUG_DRAW_UNSHADED if texture_color else Viewport.DEBUG_DRAW_DISABLED


## Puts the editor's own camera and viewport state back after a draw.
## Connected to [signal RenderingServer.frame_post_draw] while active.
func after_draw() -> void:
	if not _drawing:
		return
	_drawing = false
	for entry: Dictionary in _entries:
		var camera := entry["camera"] as Camera3D
		var viewport := entry["viewport"] as SubViewport
		if is_instance_valid(camera):
			if camera.environment == environment:
				camera.environment = entry.get("environment") as Environment
			if camera.attributes == attributes:
				camera.attributes = entry.get("attributes") as CameraAttributes
			if camera.compositor == entry["own_compositor"]:
				camera.compositor = entry.get("compositor") as Compositor
		if is_instance_valid(viewport) and entry.has("debug_draw"):
			viewport.debug_draw = entry["debug_draw"] as Viewport.DebugDraw


func _take() -> void:
	_entries.clear()
	for index: int in VIEWPORT_COUNT:
		var viewport := EditorInterface.get_editor_viewport_3d(index)
		if viewport == null:
			continue
		var camera := viewport.get_camera_3d()
		if camera == null:
			continue
		var own_effect := WorkbenchEffect.new()
		var own_compositor := Compositor.new()
		own_compositor.compositor_effects = [own_effect]
		_entries.append({
			"viewport": viewport,
			"camera": camera,
			"msaa_3d": viewport.msaa_3d,
			"effect": own_effect,
			"own_compositor": own_compositor,
		})
		viewport.msaa_3d = Viewport.MSAA_DISABLED
	if not RenderingServer.frame_pre_draw.is_connected(before_draw):
		RenderingServer.frame_pre_draw.connect(before_draw)
	if not RenderingServer.frame_post_draw.is_connected(after_draw):
		RenderingServer.frame_post_draw.connect(after_draw)


func _restore() -> void:
	if RenderingServer.frame_pre_draw.is_connected(before_draw):
		RenderingServer.frame_pre_draw.disconnect(before_draw)
	if RenderingServer.frame_post_draw.is_connected(after_draw):
		RenderingServer.frame_post_draw.disconnect(after_draw)
	after_draw()
	for entry: Dictionary in _entries:
		_free_ghost(entry, "xray")
		_free_ghost(entry, "wire")
		var viewport := entry["viewport"] as SubViewport
		if is_instance_valid(viewport) and viewport.msaa_3d == Viewport.MSAA_DISABLED:
			viewport.msaa_3d = entry["msaa_3d"] as Viewport.MSAA
	_entries.clear()


func _copy_options(own_effect: WorkbenchEffect) -> void:
	own_effect.shading = effect.shading
	own_effect.texture_color = effect.texture_color
	own_effect.base_color = effect.base_color
	own_effect.background_color = effect.background_color
	own_effect.cavity = effect.cavity
	own_effect.outline = effect.outline
	own_effect.outline_width = effect.outline_width
	own_effect.crease = effect.crease
	own_effect.matcap = effect.matcap


## Adds a hidden viewport in the given debug draw under this editor viewport,
## if it isn't there yet. As a child, it shares the editor viewport's world
## and renders before it. With [param export_depth], its camera gets a
## compositor whose effect writes the depth of what it drew into its colour.
func _ensure_ghost(entry: Dictionary, key: String, ghost_name: String, debug_draw: Viewport.DebugDraw, export_depth: bool) -> void:
	var viewport_key := key + "_viewport"
	if entry.has(viewport_key) and is_instance_valid(entry[viewport_key]):
		return
	var viewport := entry["viewport"] as SubViewport
	var ghost := SubViewport.new()
	ghost.name = ghost_name
	ghost.debug_draw = debug_draw
	ghost.msaa_3d = Viewport.MSAA_DISABLED
	ghost.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED
	ghost.use_taa = false
	ghost.use_debanding = false
	# Keeps the render linear, so the shader reads the overdraw's green, or
	# the exported depth, as is.
	ghost.use_hdr_2d = true
	ghost.positional_shadow_atlas_size = 0
	ghost.gui_disable_input = true
	ghost.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	ghost.physics_interpolation_mode = Node.PHYSICS_INTERPOLATION_MODE_OFF
	var ghost_camera := Camera3D.new()
	ghost_camera.environment = environment
	ghost_camera.attributes = attributes
	ghost_camera.physics_interpolation_mode = Node.PHYSICS_INTERPOLATION_MODE_OFF
	if export_depth:
		var export_effect := WorkbenchEffect.new()
		export_effect.depth_export = true
		var export_compositor := Compositor.new()
		export_compositor.compositor_effects = [export_effect]
		ghost_camera.compositor = export_compositor
	ghost.add_child(ghost_camera)
	viewport.add_child(ghost)
	entry[viewport_key] = ghost
	entry[key + "_camera"] = ghost_camera


## Matches the hidden viewport and its camera to the editor's for this draw.
func _sync_ghost(entry: Dictionary, key: String) -> void:
	var viewport := entry["viewport"] as SubViewport
	var camera := entry["camera"] as Camera3D
	var ghost := entry[key + "_viewport"] as SubViewport
	var ghost_camera := entry[key + "_camera"] as Camera3D
	if ghost.size != viewport.size:
		ghost.size = viewport.size
	ghost_camera.global_transform = camera.global_transform
	ghost_camera.projection = camera.projection
	ghost_camera.keep_aspect = camera.keep_aspect
	ghost_camera.fov = camera.fov
	ghost_camera.size = camera.size
	ghost_camera.near = camera.near
	ghost_camera.far = camera.far
	ghost_camera.h_offset = camera.h_offset
	ghost_camera.v_offset = camera.v_offset
	ghost_camera.frustum_offset = camera.frustum_offset
	ghost_camera.cull_mask = camera.cull_mask & GAME_LAYERS


func _free_ghost(entry: Dictionary, key: String) -> void:
	var viewport_key := key + "_viewport"
	if not entry.has(viewport_key):
		return
	var ghost := entry[viewport_key] as SubViewport
	if is_instance_valid(ghost):
		ghost.queue_free()
	entry.erase(viewport_key)
	entry.erase(key + "_camera")
