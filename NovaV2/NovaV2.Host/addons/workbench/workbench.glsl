#[compute]
#version 450

// Workbench: after the opaque pass, every pixel that has geometry is
// re-shaded from the depth and normal buffers alone, the way Blender's Solid
// mode does. The scene's lights, materials, fog and post-processing never
// reach it. Runs before the sky and the transparent pass, so the editor's
// grid, gizmos and any see-through objects still draw on top.
//
// The same shader, with depth_export set, runs on the hidden wireframe
// viewport instead, where it only copies the depth of the drawn mesh edges
// into the colour buffer for the main pass to read.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;
layout(set = 1, binding = 0) uniform sampler2D depth_texture;
layout(set = 2, binding = 0) uniform sampler2D normal_roughness_texture;
layout(set = 3, binding = 0) uniform sampler2D matcap_texture;
layout(set = 4, binding = 0) uniform sampler2D xray_texture;
layout(set = 5, binding = 0) uniform sampler2D wireframe_texture;

layout(push_constant, std430) uniform Params {
	vec2 raster_size;
	float proj_z_z; // The projection matrix's z.z entry.
	float proj_w_z; // Its w.z entry.
	vec4 base_color; // Linear rgb; a unused.
	float shading; // 0 studio lighting, 1 matcap.
	float texture_color; // 1 multiplies by the colour buffer (the albedo, when the viewport draws unshaded).
	float cavity; // Ridge and valley strength, 0 off.
	float outline; // Silhouette darkening, 0 off.
	float orthographic;
	float xray; // Opacity of the front surface in x-ray, 0 off.
	float outline_width; // In pixels of this buffer, 1 or more.
	float crease; // 1 also outlines sharp creases within a surface.
	vec4 background_color; // Linear rgb; a unused.
	float wireframe; // Opacity of the mesh edge lines, 0 off.
	float depth_export; // 1 writes the depth into the colour buffer instead of shading: the wireframe helper viewport.
	float wireframe_cos; // An edge is lined where the normals beside it differ by more than this cosine; 2 lines every edge.
	float pad;
} params;

const vec3 LINE_COLOR = vec3(0.015);
// Neighbouring normals further apart than this are a crease: 45 degrees.
const float CREASE_COS = 0.7071;

ivec2 size = ivec2(params.raster_size);

ivec2 clamp_uv(ivec2 uv) {
	return clamp(uv, ivec2(0), size - ivec2(1));
}

float raw_depth(ivec2 uv) {
	return texelFetch(depth_texture, clamp_uv(uv), 0).r;
}

// Distance along the view axis, by inverting the projection's own depth row.
// Godot's RenderingDevice projections are reversed-Z, 1.0 at the near plane
// and 0.0 at the far plane, and Projection.get_z_near() / get_z_far() don't
// recover the planes from one, so the two matrix entries are passed instead:
// perspective depth = w.z / d - z.z, orthographic depth = w.z - z.z * d.
float view_distance(float depth) {
	if (params.orthographic > 0.5) {
		return (params.proj_w_z - depth) / params.proj_z_z;
	}
	return params.proj_w_z / (depth + params.proj_z_z);
}

// The depth buffer is cleared to 0 where nothing was drawn: sky, or a
// transparent object that draws later.
bool has_geometry(float depth) {
	return depth > 0.0;
}

// View-space normal: +X right, +Y up, +Z towards the viewer.
vec3 load_normal(ivec2 uv) {
	vec4 nr = texelFetch(normal_roughness_texture, clamp_uv(uv), 0);
	return normalize(nr.xyz * 2.0 - 1.0);
}

float wrap_lambert(float ndl, float wrap) {
	return clamp((ndl + wrap) / (1.0 + wrap), 0.0, 1.0);
}

// Three fixed view-space lights and a little ambient, like Blender's default
// studio light: a warm key from the upper left, a cool fill from the right
// and a faint rim from below and behind.
vec3 shade_studio(vec3 n, vec3 base) {
	vec3 view = vec3(0.0, 0.0, 1.0);
	vec3 key_dir = normalize(vec3(-0.45, 0.55, 0.70));
	vec3 fill_dir = normalize(vec3(0.75, 0.10, 0.45));
	vec3 rim_dir = normalize(vec3(0.25, -0.65, -0.35));
	vec3 key_col = vec3(0.60, 0.59, 0.56);
	vec3 fill_col = vec3(0.20, 0.22, 0.26);
	vec3 rim_col = vec3(0.14, 0.13, 0.12);
	vec3 ambient = vec3(0.07);

	vec3 light = ambient
			+ key_col * wrap_lambert(dot(n, key_dir), 0.25)
			+ fill_col * wrap_lambert(dot(n, fill_dir), 0.40)
			+ rim_col * wrap_lambert(dot(n, rim_dir), 0.0);
	vec3 half_dir = normalize(key_dir + view);
	float spec = pow(max(dot(n, half_dir), 0.0), 24.0) * 0.08;
	return base * light + spec;
}

vec3 shade_matcap(vec3 n, vec3 base) {
	vec2 uv = vec2(n.x, -n.y) * 0.495 + 0.5;
	return texture(matcap_texture, uv).rgb * base;
}

// Screen-space curvature from the normal buffer, Blender's "screen" cavity:
// normals that spread apart across a pixel are a ridge, normals that converge
// are a valley. Neighbours on another surface (a depth jump) are ignored.
float curvature(ivec2 uv, float dist) {
	vec3 n_c = load_normal(uv);
	float limit = max(dist * 0.02, 0.005);
	vec3 n_l = abs(view_distance(raw_depth(uv + ivec2(-1, 0))) - dist) < limit ? load_normal(uv + ivec2(-1, 0)) : n_c;
	vec3 n_r = abs(view_distance(raw_depth(uv + ivec2(1, 0))) - dist) < limit ? load_normal(uv + ivec2(1, 0)) : n_c;
	vec3 n_u = abs(view_distance(raw_depth(uv + ivec2(0, -1))) - dist) < limit ? load_normal(uv + ivec2(0, -1)) : n_c;
	vec3 n_d = abs(view_distance(raw_depth(uv + ivec2(0, 1))) - dist) < limit ? load_normal(uv + ivec2(0, 1)) : n_c;
	float diff = (n_r.x - n_l.x) + (n_u.y - n_d.y);
	// Soft clamp so sharp creases don't blow out.
	float strength = params.cavity;
	if (diff < 0.0) {
		return -clamp(-diff * strength, 0.0, 0.6);
	}
	return clamp(diff * strength, 0.0, 0.6);
}

int outline_width() {
	return max(int(round(params.outline_width)), 1);
}

// A depth measure that is linear across a flat surface in screen space:
// 1/distance in perspective (so a plane at any angle has zero second
// difference), distance itself in orthographic. Pixels with no geometry
// count as infinitely far.
float depth_measure(float depth) {
	bool ortho = params.orthographic > 0.5;
	if (!has_geometry(depth)) {
		return ortho ? -(params.proj_w_z / params.proj_z_z) : 0.0;
	}
	float dist = view_distance(depth);
	return ortho ? -dist : 1.0 / dist;
}

// How big a change in the depth measure counts as a jump to another surface.
float jump_threshold(float q_c, float dist) {
	return params.orthographic > 0.5 ? 0.005 * max(dist, 0.1) : 0.03 * q_c;
}

// How strongly this pixel sits on the near side of a depth discontinuity,
// within outline_width pixels of it. Only a jump to something farther (or to
// the sky) counts: the second difference of the depth measure is zero across
// any flat surface, whatever its slope.
float silhouette(ivec2 uv, float dist) {
	float q_c = depth_measure(raw_depth(uv));
	float threshold = jump_threshold(q_c, dist);
	float edge = 0.0;
	ivec2 dirs[2] = ivec2[2](ivec2(1, 0), ivec2(0, 1));
	int width = outline_width();
	for (int k = 1; k <= width; k++) {
		for (int i = 0; i < 2; i++) {
			ivec2 step = dirs[i] * k;
			float q_a = depth_measure(raw_depth(uv + step));
			float q_b = depth_measure(raw_depth(uv - step));
			// Positive only where a neighbour drops away farther than the
			// slope through the opposite neighbour predicts.
			edge = max(edge, (q_c - q_a) - (q_b - q_c));
			edge = max(edge, (q_c - q_b) - (q_a - q_c));
		}
	}
	return clamp((edge - threshold) / threshold, 0.0, 1.0);
}

// A line on one side of a sharp crease: where a neighbour to the right or
// below faces more than CREASE_COS away. Checked in two directions only so
// the line is outline_width wide, like the silhouette, instead of straddling
// the crease at twice that. A neighbour across a depth jump is the
// silhouette's job: the jump is scored the way the silhouette scores it,
// against the slope through the opposite neighbour, and handed over only
// once the silhouette line is fully on, so the two never leave a gap
// between them where an edge turns from a contact into an overhang.
float crease(ivec2 uv, vec3 n_c, float dist) {
	float q_c = depth_measure(raw_depth(uv));
	float threshold = jump_threshold(q_c, dist);
	ivec2 dirs[2] = ivec2[2](ivec2(1, 0), ivec2(0, 1));
	int width = outline_width();
	for (int k = 1; k <= width; k++) {
		for (int i = 0; i < 2; i++) {
			ivec2 step = dirs[i] * k;
			float depth = raw_depth(uv + step);
			if (!has_geometry(depth)) {
				continue;
			}
			float q_n = depth_measure(depth);
			float q_b = depth_measure(raw_depth(uv - step));
			float residual = (q_n - q_c) - (q_c - q_b);
			if (abs(residual) > 2.0 * threshold) {
				continue;
			}
			if (dot(n_c, load_normal(uv + step)) < CREASE_COS) {
				return 1.0;
			}
		}
	}
	return 0.0;
}

// Surfaces under this pixel, the front one included, from the overdraw
// render of the same view: 0.08 of green per surface on black.
float xray_layers(vec2 uv_norm) {
	return texture(xray_texture, uv_norm).g / 0.08;
}

// Blender's X-ray: the front surface at params.xray opacity over whatever is
// behind it, over the background. Only the count of hidden surfaces is
// known, so they are drawn as a flat ghost, each at the same opacity, and
// with the outline on, a line where that count changes marks their
// silhouettes.
vec3 xray_blend(ivec2 uv, vec3 front) {
	vec2 texel = 1.0 / params.raster_size;
	vec2 uv_norm = (vec2(uv) + 0.5) * texel;
	float layers = max(xray_layers(uv_norm), 1.0);
	float hidden = layers - 1.0;
	float a = params.xray;
	float through = pow(1.0 - a, hidden);
	vec3 ghost = params.base_color.rgb * 0.55;
	vec3 behind = mix(ghost, params.background_color.rgb, through);
	vec3 color = mix(behind, front, a);
	if (params.outline > 0.0) {
		float edge = 0.0;
		int width = outline_width();
		for (int k = 1; k <= width; k++) {
			vec2 dx = vec2(texel.x * float(k), 0.0);
			vec2 dy = vec2(0.0, texel.y * float(k));
			edge = max(edge, abs(xray_layers(uv_norm + dx) - layers));
			edge = max(edge, abs(xray_layers(uv_norm - dx) - layers));
			edge = max(edge, abs(xray_layers(uv_norm + dy) - layers));
			edge = max(edge, abs(xray_layers(uv_norm - dy) - layers));
		}
		color = mix(color, LINE_COLOR, smoothstep(0.3, 0.8, edge) * 0.5 * params.outline);
	}
	return color;
}

// How much the surface distance changes over one pixel, taking the smaller
// change of each axis's two sides so a silhouette on one side doesn't count.
// A neighbour without geometry counts as a large change.
float depth_slope(ivec2 uv, float dist) {
	float side[4];
	ivec2 offsets[4] = ivec2[4](ivec2(-1, 0), ivec2(1, 0), ivec2(0, -1), ivec2(0, 1));
	for (int i = 0; i < 4; i++) {
		float depth = raw_depth(uv + offsets[i]);
		side[i] = has_geometry(depth) ? abs(view_distance(depth) - dist) : 1.0e9;
	}
	return max(min(side[0], side[1]), min(side[2], side[3]));
}

// The wireframe render of the same view, as the depth export wrote it: r is
// 1 where a mesh edge was drawn, g and b carry the edge's depth in two halves.
bool wire_at(ivec2 uv, out float depth) {
	vec4 w = texture(wireframe_texture, (vec2(uv) + 0.5) / params.raster_size);
	depth = w.g + w.b / 1024.0;
	return w.r > 0.5;
}

// Whether the surfaces meeting at a mesh edge pixel differ by at least the
// threshold angle, from the largest normal difference between the pixel and
// its neighbours on the same surface. A neighbour on another surface, or on
// none, makes it a boundary edge, which always counts. Flat-shaded faces jump
// across an edge; smooth-shaded ones barely change between pixels, so their
// edges go first as the threshold rises.
bool wire_sharp(ivec2 uv, float dist) {
	if (params.wireframe_cos > 1.0) {
		return true;
	}
	vec3 n_c = load_normal(uv);
	float limit = max(dist * 0.02, 0.005);
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			if (x == 0 && y == 0) {
				continue;
			}
			ivec2 at = uv + ivec2(x, y);
			float depth = raw_depth(at);
			if (!has_geometry(depth) || abs(view_distance(depth) - dist) > limit) {
				return true;
			}
			if (dot(n_c, load_normal(at)) < params.wireframe_cos) {
				return true;
			}
		}
	}
	return false;
}

// Mesh edges from the wireframe render, outline_width pixels wide and, like
// the other lines, landing on the -x -y side of the edge. An edge is visible
// where it is no farther than the surface under it, give or take the
// surface's own slope across a pixel and a little depth precision; an edge
// behind the surface is hidden, and only x-ray shows it. The angle threshold
// applies to the visible edges, whose surfaces are in the normal buffer.
void wireframe(ivec2 uv, out float visible, out float hidden) {
	visible = 0.0;
	hidden = 0.0;
	int width = outline_width();
	for (int y = 0; y < width; y++) {
		for (int x = 0; x < width; x++) {
			ivec2 at = clamp_uv(uv + ivec2(x, y));
			float wire_depth;
			if (!wire_at(at, wire_depth)) {
				continue;
			}
			float depth = raw_depth(at);
			if (!has_geometry(depth)) {
				visible = 1.0;
				continue;
			}
			float dist = view_distance(depth);
			float tolerance = 0.002 * dist + 2.0 * depth_slope(at, dist);
			if (view_distance(wire_depth) <= dist + tolerance) {
				if (wire_sharp(at, dist)) {
					visible = 1.0;
				}
			} else {
				hidden = 1.0;
			}
		}
	}
}

void main() {
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
	if (uv.x >= size.x || uv.y >= size.y) {
		return;
	}

	float depth = raw_depth(uv);
	if (!has_geometry(depth)) {
		return;
	}

	if (params.depth_export > 0.5) {
		// The wireframe helper viewport: mark every pixel a mesh edge was
		// drawn on and keep its depth, split into two halves so the 16-bit
		// float colour buffer holds about 21 bits of it. Pixels without an
		// edge keep the clear colour, and the sky draws over them.
		float coarse = floor(depth * 1024.0) / 1024.0;
		imageStore(color_image, uv, vec4(1.0, coarse, (depth - coarse) * 1024.0, 1.0));
		return;
	}

	vec3 n = load_normal(uv);
	vec3 base = params.base_color.rgb;
	if (params.texture_color > 0.5) {
		base = imageLoad(color_image, uv).rgb;
	}

	// A matcap carries its own brightness; the flat base only matters for
	// the studio light, and the albedo for texture colour.
	vec3 color = params.shading > 0.5
			? shade_matcap(n, params.texture_color > 0.5 ? base : vec3(1.0))
			: shade_studio(n, base);

	float dist = view_distance(depth);
	if (params.cavity > 0.0) {
		color *= 1.0 + curvature(uv, dist);
	}
	if (params.outline > 0.0) {
		float line = silhouette(uv, dist);
		if (params.crease > 0.5) {
			line = max(line, crease(uv, n, dist));
		}
		color = mix(color, LINE_COLOR, line * params.outline);
	}
	if (params.xray > 0.0) {
		color = xray_blend(uv, color);
	}
	if (params.wireframe > 0.0) {
		float visible;
		float hidden;
		wireframe(uv, visible, hidden);
		float line = visible;
		if (params.xray > 0.0) {
			// Hidden edges show through the front surface as much as it lets through.
			line = max(line, hidden * (1.0 - params.xray));
		}
		color = mix(color, LINE_COLOR, line * params.wireframe);
	}

	imageStore(color_image, uv, vec4(color, 1.0));
}
