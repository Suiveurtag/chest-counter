fn main() {
    // The overlay page is served by the engine (http://127.0.0.1:<port>/overlay.html), a remote origin for Tauri:
    // its commands must be declared so that capabilities/overlay.json can allow exactly these.
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&["overlay_layout", "overlay_scale", "overlay_menu"]),
    ))
    .expect("failed to run tauri-build");
}
