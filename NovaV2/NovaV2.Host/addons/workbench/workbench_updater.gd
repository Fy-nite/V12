@tool
extends Node
## Looks up the addon's latest GitHub release and installs it over this
## folder. Housekeeping for the addon, no part of the engine proposal.
##
## [method check] asks GitHub for the latest release and emits
## [signal checked] with [member latest_version] set, or [signal failed].
## [method install] downloads that release's zip, writes its
## addons/workbench/ folder over this one, removes the files it no longer
## ships, emits [signal installed], then disables and re-enables the plugin
## so the editor runs the new code.

signal checked
signal installed(version: String)
signal failed(message: String)

const REPO := "reidhannaford/godot-workbench"
const REPO_URL := "https://github.com/" + REPO
const RELEASES_URL := REPO_URL + "/releases"
const LATEST_URL := "https://api.github.com/repos/" + REPO + "/releases/latest"
const HEADERS: PackedStringArray = ["Accept: application/vnd.github+json", "User-Agent: godot-workbench"]
## Where the addon's files sit inside a release zip, under whatever top
## folder the zip has.
const ZIP_MARKER := "addons/workbench/"

enum Stage { IDLE, CHECKING, DOWNLOADING }

## The version in plugin.cfg beside this script.
var current_version: String = ""
## The newest release's version, once [method check] has succeeded.
var latest_version: String = ""
## Where that release's zip is: the asset attached to it, or GitHub's own
## archive of the tag.
var download_url: String = ""
var release_url: String = RELEASES_URL

var _addon_dir: String
var _request: HTTPRequest
var _stage: Stage = Stage.IDLE


func _init() -> void:
	name = "WorkbenchUpdater"
	_addon_dir = (get_script() as Script).resource_path.get_base_dir()
	var config := ConfigFile.new()
	if config.load(_addon_dir.path_join("plugin.cfg")) == OK:
		current_version = str(config.get_value("plugin", "version", ""))
	_request = HTTPRequest.new()
	_request.request_completed.connect(_on_request_completed)
	add_child(_request)


func is_busy() -> bool:
	return _stage != Stage.IDLE


## True once a check has found a release newer than the installed one.
func has_update() -> bool:
	return latest_version != "" and compare_versions(latest_version, current_version) > 0


func check() -> void:
	if is_busy():
		return
	_stage = Stage.CHECKING
	_request.timeout = 20.0
	_request.download_file = ""
	var error := _request.request(LATEST_URL, HEADERS)
	if error != OK:
		_fail("Couldn't start the request: %s." % error_string(error))


func install() -> void:
	if is_busy() or download_url == "":
		return
	_stage = Stage.DOWNLOADING
	_request.timeout = 120.0
	_request.download_file = EditorInterface.get_editor_paths().get_cache_dir().path_join("workbench_update.zip")
	var error := _request.request(download_url, HEADERS)
	if error != OK:
		_fail("Couldn't start the download: %s." % error_string(error))


## Negative, zero or positive as [param a] is older than, the same as or
## newer than [param b]; "v1.2" and "1.2.0" are the same.
static func compare_versions(a: String, b: String) -> int:
	var parts_a := a.trim_prefix("v").split(".")
	var parts_b := b.trim_prefix("v").split(".")
	for index: int in maxi(parts_a.size(), parts_b.size()):
		var number_a := parts_a[index].to_int() if index < parts_a.size() else 0
		var number_b := parts_b[index].to_int() if index < parts_b.size() else 0
		if number_a != number_b:
			return number_a - number_b
	return 0


func _fail(message: String) -> void:
	_stage = Stage.IDLE
	failed.emit(message)


func _on_request_completed(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	var stage := _stage
	_stage = Stage.IDLE
	if result != HTTPRequest.RESULT_SUCCESS:
		_fail("The request didn't go through (%s)." % _result_text(result))
		return
	if response_code != 200:
		_fail("GitHub answered with status %d." % response_code)
		return
	match stage:
		Stage.CHECKING:
			_read_release(body)
		Stage.DOWNLOADING:
			_install_zip(_request.download_file)


func _read_release(body: PackedByteArray) -> void:
	var json: Variant = JSON.parse_string(body.get_string_from_utf8())
	if typeof(json) != TYPE_DICTIONARY or not (json as Dictionary).has("tag_name"):
		_fail("GitHub's answer wasn't a release.")
		return
	var release := json as Dictionary
	latest_version = str(release["tag_name"]).trim_prefix("v")
	release_url = str(release.get("html_url", RELEASES_URL))
	download_url = str(release.get("zipball_url", ""))
	for asset: Variant in release.get("assets", []) as Array:
		if typeof(asset) == TYPE_DICTIONARY and str((asset as Dictionary).get("name", "")).ends_with(".zip"):
			download_url = str((asset as Dictionary).get("browser_download_url", download_url))
			break
	checked.emit()


## Writes the zip's addons/workbench/ files over this folder, removes the
## rest of the folder, and reloads the plugin.
func _install_zip(zip_path: String) -> void:
	var reader := ZIPReader.new()
	if reader.open(zip_path) != OK:
		_fail("The download isn't a zip.")
		return
	var written: Dictionary[String, bool] = {}
	for entry: String in reader.get_files():
		var at := entry.find(ZIP_MARKER)
		if at < 0 or entry.ends_with("/"):
			continue
		var relative := entry.substr(at + ZIP_MARKER.length())
		var target := _addon_dir.path_join(relative)
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(target.get_base_dir()))
		var file := FileAccess.open(target, FileAccess.WRITE)
		if file == null:
			reader.close()
			_fail("Couldn't write %s: %s." % [target, error_string(FileAccess.get_open_error())])
			return
		file.store_buffer(reader.read_file(entry))
		file.close()
		written[relative] = true
	reader.close()
	DirAccess.remove_absolute(ProjectSettings.globalize_path(zip_path))
	if written.is_empty():
		_fail("The zip has no %s folder." % ZIP_MARKER)
		return
	_remove_stale(_addon_dir, "", written)
	var version := latest_version
	current_version = version
	installed.emit(version)
	_reload()


## Deletes the files under [param dir] that the new release doesn't ship.
func _remove_stale(dir: String, prefix: String, keep: Dictionary[String, bool]) -> void:
	for file: String in DirAccess.get_files_at(dir):
		var relative := prefix + file
		if not keep.has(relative):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(dir.path_join(file)))
	for sub: String in DirAccess.get_directories_at(dir):
		_remove_stale(dir.path_join(sub), prefix + sub + "/", keep)


## Has the editor pick up the changed files, then turns the plugin off and
## on so it runs the new code. The plugin frees this node on the way out, but
## only at the end of the frame, so the re-enable still happens.
func _reload() -> void:
	var filesystem := EditorInterface.get_resource_filesystem()
	filesystem.scan()
	while filesystem.is_scanning():
		await get_tree().process_frame
	await get_tree().process_frame
	var plugin_name := _addon_dir.get_file()
	EditorInterface.set_plugin_enabled(plugin_name, false)
	EditorInterface.set_plugin_enabled(plugin_name, true)
	queue_free()


static func _result_text(result: int) -> String:
	match result:
		HTTPRequest.RESULT_CANT_CONNECT, HTTPRequest.RESULT_CANT_RESOLVE, HTTPRequest.RESULT_CONNECTION_ERROR:
			return "no connection"
		HTTPRequest.RESULT_TLS_HANDSHAKE_ERROR:
			return "TLS handshake failed"
		HTTPRequest.RESULT_TIMEOUT:
			return "timed out"
		HTTPRequest.RESULT_DOWNLOAD_FILE_CANT_OPEN, HTTPRequest.RESULT_DOWNLOAD_FILE_WRITE_ERROR:
			return "couldn't write the download"
		HTTPRequest.RESULT_REDIRECT_LIMIT_REACHED:
			return "too many redirects"
	return "error %d" % result
