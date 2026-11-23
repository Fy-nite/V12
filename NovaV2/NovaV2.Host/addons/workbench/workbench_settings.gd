@tool
extends RefCounted
## The workbench options, kept in Editor Settings under workbench/ so they
## follow the user between projects. Whether the view is on is not saved: it
## starts off, like a debug draw mode.

const PREFIX := "workbench/"
const MATCAP_DIR := "res://addons/workbench/matcaps/"
const DEFAULT_MATCAP := MATCAP_DIR + "clay.png"
## The matcaps that ship with the addon, by menu label, in menu order.
const BUNDLED_MATCAPS: Dictionary[String, String] = {
	"Clay": MATCAP_DIR + "clay.png",
	"Normal": MATCAP_DIR + "normal.png",
	"Contrast": MATCAP_DIR + "contrast.png",
	"Toon": MATCAP_DIR + "toon.png",
	"Zebra": MATCAP_DIR + "zebra.png",
	"Wax": MATCAP_DIR + "wax.png",
	"Chrome": MATCAP_DIR + "chrome.png",
}

var shading: int = 0 # WorkbenchEffect.Shading
var texture_color: bool = false
var cavity: bool = true
var outline: bool = true
var outline_width: float = 1.0
var crease: bool = true
var xray: bool = false
var xray_alpha: float = 0.5
var wireframe: bool = false
var wireframe_opacity: float = 1.0
## Degrees; 0 lines every edge.
var wireframe_threshold: float = 0.0
var matcap_path: String = DEFAULT_MATCAP
## Look up the latest GitHub release when the editor starts, and show a dot
## on the options menu if it is newer.
var check_for_updates: bool = true


func load() -> void:
	var settings := EditorInterface.get_editor_settings()
	shading = _read(settings, "shading", shading, TYPE_INT, PROPERTY_HINT_ENUM, "Studio Lighting,MatCap")
	texture_color = _read(settings, "texture_color", texture_color, TYPE_BOOL)
	cavity = _read(settings, "cavity", cavity, TYPE_BOOL)
	outline = _read(settings, "outline", outline, TYPE_BOOL)
	outline_width = _read(settings, "outline_width", outline_width, TYPE_FLOAT, PROPERTY_HINT_RANGE, "0.5,4,0.5")
	crease = _read(settings, "crease", crease, TYPE_BOOL)
	xray = _read(settings, "xray", xray, TYPE_BOOL)
	xray_alpha = _read(settings, "xray_alpha", xray_alpha, TYPE_FLOAT, PROPERTY_HINT_RANGE, "0.05,0.95,0.05")
	wireframe = _read(settings, "wireframe", wireframe, TYPE_BOOL)
	wireframe_opacity = _read(settings, "wireframe_opacity", wireframe_opacity, TYPE_FLOAT, PROPERTY_HINT_RANGE, "0.1,1,0.05")
	wireframe_threshold = _read(settings, "wireframe_threshold", wireframe_threshold, TYPE_FLOAT, PROPERTY_HINT_RANGE, "0,90,1")
	matcap_path = _read(settings, "matcap", matcap_path, TYPE_STRING, PROPERTY_HINT_FILE, "*.png,*.jpg,*.jpeg,*.exr,*.webp")
	check_for_updates = _read(settings, "check_for_updates", check_for_updates, TYPE_BOOL)


func save() -> void:
	var settings := EditorInterface.get_editor_settings()
	settings.set_setting(PREFIX + "shading", shading)
	settings.set_setting(PREFIX + "texture_color", texture_color)
	settings.set_setting(PREFIX + "cavity", cavity)
	settings.set_setting(PREFIX + "outline", outline)
	settings.set_setting(PREFIX + "outline_width", outline_width)
	settings.set_setting(PREFIX + "crease", crease)
	settings.set_setting(PREFIX + "xray", xray)
	settings.set_setting(PREFIX + "xray_alpha", xray_alpha)
	settings.set_setting(PREFIX + "wireframe", wireframe)
	settings.set_setting(PREFIX + "wireframe_opacity", wireframe_opacity)
	settings.set_setting(PREFIX + "wireframe_threshold", wireframe_threshold)
	settings.set_setting(PREFIX + "matcap", matcap_path)
	settings.set_setting(PREFIX + "check_for_updates", check_for_updates)


## The matcap texture the settings name, or the bundled one if it won't load.
func load_matcap() -> Texture2D:
	var texture: Texture2D = null
	if ResourceLoader.exists(matcap_path):
		texture = ResourceLoader.load(matcap_path, "Texture2D") as Texture2D
	if texture == null and matcap_path != DEFAULT_MATCAP:
		push_warning("Workbench: can't load the matcap at %s; using the bundled one." % matcap_path)
		texture = ResourceLoader.load(DEFAULT_MATCAP, "Texture2D") as Texture2D
	return texture


static func _read(settings: EditorSettings, name: String, default: Variant, type: int, hint: int = PROPERTY_HINT_NONE, hint_string: String = "") -> Variant:
	var path := PREFIX + name
	if not settings.has_setting(path):
		settings.set_setting(path, default)
	settings.set_initial_value(path, default, false)
	settings.add_property_info({
		"name": path,
		"type": type,
		"hint": hint,
		"hint_string": hint_string,
	})
	return settings.get_setting(path)
