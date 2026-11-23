@tool
extends CompositorEffect
## Re-shades every opaque pixel from the depth and normal buffers, ignoring
## the scene's lights and materials. Forward+ only: it needs the normal and
## roughness buffer the depth prepass writes there. Attached to the editor
## cameras by the controller; the viewport must draw with MSAA off, because
## the opaque colour is only resolved after the transparent pass and a
## post-opaque write to the resolved buffer would be lost.
##
## With [member depth_export] on, the same shader instead copies the depth of
## whatever was drawn into the colour buffer. The controller puts one such
## effect on the hidden wireframe viewport's camera, so the main effect can
## depth-test the mesh edges against the solid surfaces.

enum Shading { STUDIO, MATCAP }

const SHADER_FILE_NAME := "workbench.glsl"
const WORKGROUP := 8
const PUSH_CONSTANT_FLOATS := 24

var shading: Shading = Shading.STUDIO
## Multiply the shading by the albedo in the colour buffer. Only meaningful
## while the viewport draws unshaded, which the controller arranges.
var texture_color: bool = false
var base_color: Color = Color(0.8, 0.8, 0.8)
## What the sky is cleared to; what x-ray shows through the geometry.
var background_color: Color = Color(0.24, 0.24, 0.24)
var cavity: float = 1.0
var outline: float = 1.0
## Line width in pixels of the viewport's output; scaled to the internal
## render size before use, and never below one pixel there.
var outline_width: float = 1.0
## Also line the sharp creases within a surface, not only its silhouette.
var crease: bool = false
var matcap: Texture2D
## Opacity of the front surface in x-ray; 0 is off.
var xray: float = 0.0
## The overdraw render of the same view, as a RenderingServer texture: each
## surface under a pixel adds 0.08 of green. Only read while [member xray] is on.
var xray_texture: RID
## Opacity of the mesh edge lines; 0 is off.
var wireframe: float = 0.0
## Only line the mesh edges where the surfaces either side differ by at least
## this many degrees; 0 lines every edge.
var wireframe_threshold: float = 0.0
## The wireframe render of the same view, as a RenderingServer texture, after
## a depth-exporting effect marked the edge pixels and stored their depth.
## Only read while [member wireframe] is on.
var wireframe_texture: RID
## Export the depth instead of shading. The normal buffer isn't needed then.
var depth_export: bool = false:
	set(value):
		depth_export = value
		needs_normal_roughness = not value

var _rd: RenderingDevice
var _shader_file: RDShaderFile
var _shader: RID
var _pipeline: RID
var _nearest_sampler: RID
var _linear_sampler: RID
var _failed: bool = false


func _init() -> void:
	effect_callback_type = EFFECT_CALLBACK_TYPE_POST_OPAQUE
	needs_normal_roughness = true
	access_resolved_depth = true
	access_resolved_color = true
	# Null when the renderer is a dummy (headless) or not RenderingDevice-based.
	_rd = RenderingServer.get_rendering_device()
	if _rd == null:
		return
	# Loaded here rather than preloaded: on a project's first scan the .glsl
	# isn't imported yet when the scripts compile.
	var path := (get_script() as Script).resource_path.get_base_dir().path_join(SHADER_FILE_NAME)
	_shader_file = ResourceLoader.load(path, "RDShaderFile") as RDShaderFile
	if _shader_file == null:
		push_error("Workbench: can't load the compute shader at %s." % path)
		_failed = true


func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE and _rd != null:
		# Freeing the shader frees the pipeline with it.
		if _shader.is_valid():
			_rd.free_rid(_shader)
		if _nearest_sampler.is_valid():
			_rd.free_rid(_nearest_sampler)
		if _linear_sampler.is_valid():
			_rd.free_rid(_linear_sampler)


func _render_callback(p_effect_callback_type: int, p_render_data: RenderData) -> void:
	if _rd == null or p_effect_callback_type != EFFECT_CALLBACK_TYPE_POST_OPAQUE:
		return
	if not _ensure_pipeline():
		return
	var buffers := p_render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	if buffers == null:
		return
	var size := buffers.get_internal_size()
	if size.x == 0 or size.y == 0:
		return
	var has_normals := buffers.has_texture(&"forward_clustered", &"normal_roughness")
	if not has_normals and not depth_export:
		return

	var projection := p_render_data.get_render_scene_data().get_cam_projection()
	var push_constant := PackedFloat32Array()
	push_constant.resize(PUSH_CONSTANT_FLOATS)
	push_constant[0] = size.x
	push_constant[1] = size.y
	# The reversed-Z render projection; its planes can't be read back with
	# get_z_near() / get_z_far(), so the shader inverts these entries itself.
	push_constant[2] = projection.z.z
	push_constant[3] = projection.w.z
	push_constant[4] = base_color.r
	push_constant[5] = base_color.g
	push_constant[6] = base_color.b
	push_constant[7] = 1.0
	push_constant[8] = 1.0 if shading == Shading.MATCAP else 0.0
	push_constant[9] = 1.0 if texture_color else 0.0
	push_constant[10] = cavity
	push_constant[11] = outline
	push_constant[12] = 1.0 if projection.is_orthogonal() else 0.0
	push_constant[13] = xray
	push_constant[14] = outline_width * _internal_scale(buffers)
	push_constant[15] = 1.0 if crease else 0.0
	push_constant[16] = background_color.r
	push_constant[17] = background_color.g
	push_constant[18] = background_color.b
	push_constant[19] = 1.0
	push_constant[20] = wireframe
	push_constant[21] = 1.0 if depth_export else 0.0
	# Above 1 no dot product is below it, so every edge is lined.
	push_constant[22] = cos(deg_to_rad(wireframe_threshold)) if wireframe_threshold > 0.0 else 2.0
	push_constant[23] = 0.0

	var matcap_rid := _matcap_rd_texture()
	var xray_rid := _xray_rd_texture()
	var wireframe_rid := _wireframe_rd_texture()
	var groups_x := (size.x - 1) / WORKGROUP + 1
	var groups_y := (size.y - 1) / WORKGROUP + 1

	for view: int in buffers.get_view_count():
		var color_set := UniformSetCacheRD.get_cache(_shader, 0, [_image_uniform(buffers.get_color_layer(view))])
		var depth_set := UniformSetCacheRD.get_cache(_shader, 1, [_sampler_uniform(_nearest_sampler, buffers.get_depth_layer(view))])
		# The depth export never reads normals; the depth stands in for the
		# buffer the wireframe viewport doesn't have.
		var normal_rid := buffers.get_texture(&"forward_clustered", &"normal_roughness") if has_normals else buffers.get_depth_layer(view)
		var normal_set := UniformSetCacheRD.get_cache(_shader, 2, [_sampler_uniform(_nearest_sampler, normal_rid)])
		var matcap_set := UniformSetCacheRD.get_cache(_shader, 3, [_sampler_uniform(_linear_sampler, matcap_rid)])
		var xray_set := UniformSetCacheRD.get_cache(_shader, 4, [_sampler_uniform(_linear_sampler, xray_rid)])
		var wireframe_set := UniformSetCacheRD.get_cache(_shader, 5, [_sampler_uniform(_nearest_sampler, wireframe_rid)])

		var compute_list := _rd.compute_list_begin()
		_rd.compute_list_bind_compute_pipeline(compute_list, _pipeline)
		_rd.compute_list_bind_uniform_set(compute_list, color_set, 0)
		_rd.compute_list_bind_uniform_set(compute_list, depth_set, 1)
		_rd.compute_list_bind_uniform_set(compute_list, normal_set, 2)
		_rd.compute_list_bind_uniform_set(compute_list, matcap_set, 3)
		_rd.compute_list_bind_uniform_set(compute_list, xray_set, 4)
		_rd.compute_list_bind_uniform_set(compute_list, wireframe_set, 5)
		_rd.compute_list_set_push_constant(compute_list, push_constant.to_byte_array(), PUSH_CONSTANT_FLOATS * 4)
		_rd.compute_list_dispatch(compute_list, groups_x, groups_y, 1)
		_rd.compute_list_end()


## Compiles the shader the first time the effect renders. Runs on the
## rendering thread, where RenderingDevice resources must be made.
func _ensure_pipeline() -> bool:
	if _pipeline.is_valid():
		return true
	if _failed:
		return false
	var spirv := _shader_file.get_spirv()
	if spirv == null or spirv.compile_error_compute != "":
		push_error("Workbench: the compute shader failed to compile: %s" % (spirv.compile_error_compute if spirv != null else "no SPIR-V"))
		_failed = true
		return false
	_shader = _rd.shader_create_from_spirv(spirv)
	if not _shader.is_valid():
		push_error("Workbench: could not create the compute shader.")
		_failed = true
		return false
	_pipeline = _rd.compute_pipeline_create(_shader)
	if not _pipeline.is_valid():
		push_error("Workbench: could not create the compute pipeline.")
		_failed = true
		return false

	var nearest := RDSamplerState.new()
	nearest.min_filter = RenderingDevice.SAMPLER_FILTER_NEAREST
	nearest.mag_filter = RenderingDevice.SAMPLER_FILTER_NEAREST
	nearest.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	nearest.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	_nearest_sampler = _rd.sampler_create(nearest)
	var linear := RDSamplerState.new()
	linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear.mip_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	linear.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
	_linear_sampler = _rd.sampler_create(linear)
	return true


## The matcap as a RenderingDevice texture, through its sRGB view so the
## shader reads linear values. A white pixel stands in when there is none.
func _matcap_rd_texture() -> RID:
	var texture_rid := RenderingServer.get_white_texture()
	if matcap != null and matcap.get_rid().is_valid():
		texture_rid = matcap.get_rid()
	var rd_texture := RenderingServer.texture_get_rd_texture(texture_rid, true)
	if not rd_texture.is_valid():
		rd_texture = RenderingServer.texture_get_rd_texture(texture_rid, false)
	return rd_texture


## The overdraw render as a RenderingDevice texture, read as stored: the
## hidden viewport renders in HDR, so its values are linear. A white pixel
## stands in while x-ray is off, when the shader never reads it.
func _xray_rd_texture() -> RID:
	if xray > 0.0 and xray_texture.is_valid():
		var rd_texture := RenderingServer.texture_get_rd_texture(xray_texture, false)
		if rd_texture.is_valid():
			return rd_texture
	return RenderingServer.texture_get_rd_texture(RenderingServer.get_white_texture(), false)


## The wireframe render as a RenderingDevice texture, read as stored: the
## hidden viewport renders in HDR, so the depth it carries is linear. A white
## pixel stands in while wireframe is off, when the shader never reads it.
func _wireframe_rd_texture() -> RID:
	if wireframe > 0.0 and wireframe_texture.is_valid():
		var rd_texture := RenderingServer.texture_get_rd_texture(wireframe_texture, false)
		if rd_texture.is_valid():
			return rd_texture
	return RenderingServer.texture_get_rd_texture(RenderingServer.get_white_texture(), false)


## Internal render pixels per output pixel, under 1 when the 3D scale is.
static func _internal_scale(buffers: RenderSceneBuffersRD) -> float:
	var internal := buffers.get_internal_size()
	var target := buffers.get_target_size()
	if target.x <= 0:
		return 1.0
	return float(internal.x) / float(target.x)


static func _image_uniform(image: RID) -> RDUniform:
	var uniform := RDUniform.new()
	uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	uniform.binding = 0
	uniform.add_id(image)
	return uniform


static func _sampler_uniform(sampler: RID, texture: RID) -> RDUniform:
	var uniform := RDUniform.new()
	uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
	uniform.binding = 0
	uniform.add_id(sampler)
	uniform.add_id(texture)
	return uniform
