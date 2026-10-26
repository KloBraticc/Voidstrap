use crate::{PresetIni, preset_effect_files};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::time::Duration;
use ureq::ResponseExt;
use zip::ZipArchive;

const EFFECT_CATALOG_URL: &str =
    "https://raw.githubusercontent.com/crosire/reshade-shaders/list/EffectPackages.ini";
const CATALOG_LIMIT: usize = 4 << 20;
const PACKAGE_LIMIT: usize = 512 << 20;
const ZIP_ENTRY_LIMIT: usize = 20_000;
const ZIP_ENTRY_SIZE_LIMIT: u64 = 128 << 20;
const ZIP_TOTAL_SIZE_LIMIT: u64 = 1 << 30;
const ASSET_MARKER: &str = ".rishade-effect-packages-v1";
const RETIRED_ASSETS: [&str; 4] = [
    ".rishade-depth-verified",
    "reshade-shaders/Shaders/RiShade/RiShade_SSR.fx",
    "reshade-shaders/Shaders/RiShade/RiShade_SSR.fx.update",
    "reshade-shaders/Shaders/qUINT/qUINT_ssr.fx.patch",
];
pub const DEFAULT_PRESET: &str = "RiShade Natural.ini";
pub const RENAMED_PRESETS: [(&str, &str, &str); 3] = [
    (
        "GenericPreset1.ini",
        "RiShade Enhanced.ini",
        "cc8899f300270e391adc6106c06673d0e2012cb78f8baadf42e45fb62b2a6b56",
    ),
    (
        "GenericPreset2.ini",
        "RiShade Glow.ini",
        "608218ab87a2ca5643caabd45c538310be0a5205ec052d37c88cbeb4acecbf6a",
    ),
    (
        "RiShadeReflections.ini",
        "RiShade Reflections.ini",
        "afdcf21fa758ef1a229d9a350738b5f7a76b8f90044fa2fafb3d8df67ad4c68a",
    ),
];

const PRESET_CATALOG: &str = include_str!("../presets/downloads.ini");
const BUNDLED_PRESETS: &[(&str, &[u8])] = &[
    (
        "RiShade Natural.ini",
        include_bytes!("../presets/RiShade Natural.ini"),
    ),
    (
        "RiShade Vivid.ini",
        include_bytes!("../presets/RiShade Vivid.ini"),
    ),
    (
        "RiShade Cinematic.ini",
        include_bytes!("../presets/RiShade Cinematic.ini"),
    ),
    (
        "RiShade Competitive.ini",
        include_bytes!("../presets/RiShade Competitive.ini"),
    ),
    (
        "RiShade Performance.ini",
        include_bytes!("../presets/RiShade Performance.ini"),
    ),
    (
        "RiShade Enhanced.ini",
        include_bytes!("../presets/RiShade Enhanced.ini"),
    ),
    (
        "RiShade Glow.ini",
        include_bytes!("../presets/RiShade Glow.ini"),
    ),
    (
        "RiShade Reflections.ini",
        include_bytes!("../presets/RiShade Reflections.ini"),
    ),
    (
        "RiShade Ambient Occlusion.ini",
        include_bytes!("../presets/RiShade Ambient Occlusion.ini"),
    ),
];

#[derive(Default)]
pub struct InstallSummary {
    assets_already_provisioned: bool,
    installed_packages: usize,
    skipped_packages: Vec<String>,
    installed_presets: usize,
    skipped_presets: Vec<String>,
}

impl InstallSummary {
    pub fn describe(&self) -> String {
        if self.assets_already_provisioned {
            return "ReShade effect packages and RiShade presets are already provisioned"
                .to_owned();
        }
        let mut summary = format!(
            "Installed {} effect packages and {} presets",
            self.installed_packages, self.installed_presets
        );
        if !self.skipped_packages.is_empty() {
            summary.push_str("; skipped effect packages: ");
            summary.push_str(&self.skipped_packages.join(", "));
        }
        if !self.skipped_presets.is_empty() {
            summary.push_str("; skipped presets: ");
            summary.push_str(&self.skipped_presets.join(", "));
        }
        summary
    }
}

pub fn install_effects_and_presets(root: &Path) -> io::Result<InstallSummary> {
    let root = prepare_root(root)?;
    let marker = root.join(ASSET_MARKER);
    if marker.is_file() && root.join("reshade-shaders/Shaders/ReShade.fxh").is_file() {
        let mut summary = InstallSummary {
            assets_already_provisioned: true,
            ..InstallSummary::default()
        };
        let missing_bundled = BUNDLED_PRESETS
            .iter()
            .any(|(name, _)| !root.join("presets").join(name).exists());
        if missing_bundled {
            let effects = collect_effect_names(&root, &root)?;
            install_bundled_presets(&root, &root, &effects, &mut summary)?;
            summary.assets_already_provisioned = summary.installed_presets == 0;
        }
        return Ok(summary);
    }

    let work = tempfile::Builder::new()
        .prefix("rishade-setup-")
        .tempdir()?;
    let staged_files = work.path().join("files");
    fs::create_dir(&staged_files)?;
    let agent = ureq::Agent::new_with_config(
        ureq::Agent::config_builder()
            .https_only(true)
            .tls_config(
                ureq::tls::TlsConfig::builder()
                    .provider(ureq::tls::TlsProvider::NativeTls)
                    .build(),
            )
            .timeout_global(Some(Duration::from_secs(300)))
            .build(),
    );
    let catalog = download(&agent, EFFECT_CATALOG_URL, CATALOG_LIMIT, is_github_host)?;
    let catalog_text = std::str::from_utf8(&catalog)
        .map_err(|_| invalid_data("the effect package catalog is not UTF-8"))?;
    let catalog_ini = PresetIni::parse(catalog_text);
    let mut summary = InstallSummary::default();

    for section in catalog_ini.section_names() {
        let required = catalog_ini.get(section, "Required") == Some("1");
        let package_name = catalog_ini
            .get(section, "PackageName")
            .unwrap_or(section)
            .trim();
        let result = install_package(
            &agent,
            &catalog_ini,
            section,
            package_name,
            &staged_files,
            work.path(),
        );
        match result {
            Ok(()) => summary.installed_packages += 1,
            Err(error) if required => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    format!("required effect package {package_name} failed: {error}"),
                ));
            }
            Err(_) => summary.skipped_packages.push(package_name.to_owned()),
        }
    }

    let effects = collect_effect_names(&staged_files, &root)?;
    install_bundled_presets(&staged_files, &root, &effects, &mut summary)?;
    let mask_path = staged_files.join("reshade-shaders/Textures/UIMask.png");
    if !mask_path.exists() {
        if let Some(parent) = mask_path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        let blank_png: &[u8] = &[
            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 16, 0, 0, 0, 16,
            8, 6, 0, 0, 0, 31, 243, 255, 97, 0, 0, 0, 38, 73, 68, 65, 84, 120, 156, 99, 252, 255,
            255, 255, 127, 6, 10, 0, 19, 37, 154, 71, 13, 128, 0, 38, 6, 10, 1, 211, 168, 1, 12,
            163, 97, 192, 64, 121, 24, 0, 0, 107, 202, 4, 28, 186, 185, 31, 90, 0, 0, 0, 0, 73, 69,
            78, 68, 174, 66, 96, 130,
        ];
        let _ = fs::write(&mask_path, &blank_png);
    }
    copy_missing_tree(&staged_files, &root)?;
    let mut marker_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&marker)?;
    marker_file.write_all(
        format!(
            "ReShade assets provisioned: {} packages, {} bundled presets, {} defaults skipped\n",
            summary.installed_packages,
            summary.installed_presets,
            summary.skipped_presets.len()
        )
        .as_bytes(),
    )?;
    marker_file.flush()?;
    Ok(summary)
}

const UPGRADED_PRESETS: [(&str, &str); 4] = [
    (
        "RiShade Reflections.ini",
        "afdcf21fa758ef1a229d9a350738b5f7a76b8f90044fa2fafb3d8df67ad4c68a",
    ),
    (
        "RiShade Reflections.ini",
        "39cf148e54f45e7119272d97bd7ac48dbb4be7119ad815b34a44c4ca809bc31c",
    ),
    (
        "RiShade Reflections.ini",
        "be961699e182e5040e4bbbce54e59fc312cb1214377c9122e6d73b0a2632dd88",
    ),
    (
        "RiShade Reflections.ini",
        "0b26c3d5cca60d4bb6074138df023c6a1e7efc9969b422f835e6a6f2f7249ee0",
    ),
];

const RETIRED_SSR_TECHNIQUES: [&str; 1] = ["RiShade_SSR@RiShade_SSR.fx"];
const REPLACEMENT_SSR_TECHNIQUE: &str = "SSR@qUINT_ssr.fx";
const QUINT_SSR_PATH: &str = "reshade-shaders/Shaders/qUINT/qUINT_ssr.fx";
const QUINT_SSR_PATCHED: &str = "#define RISHADE_SSR_PATCH 2";
const QUINT_SSR_LEGACY_PATCHED: &str = "SSR_FLOOR_ONLY";
const QUINT_SSR_PATCHES: [(&str, &str); 8] = [
    (
        "#define RESHADE_QUINT_COMMON_VERSION_REQUIRE    202",
        "#define RISHADE_SSR_PATCH 2\n\nuniform float SSR_FLOOR_ONLY <\n\tui_type = \"drag\";\n\tui_min = 0.0; ui_max = 1.0;\n\tui_label = \"Floor Only\";\n\tui_tooltip = \"Limits reflections to surfaces that face up, so walls and characters stay clean.\";\n> = 0.85;\n\nuniform float SSR_BASE_REFLECTIVITY <\n\tui_type = \"drag\";\n\tui_min = 0.0; ui_max = 1.0;\n\tui_label = \"Base Reflectivity\";\n\tui_tooltip = \"How much surfaces reflect when viewed head on. Grazing angles always reflect more.\";\n> = 0.3;\n\n#define RESHADE_QUINT_COMMON_VERSION_REQUIRE    202",
    ),
    (
        "float3 single_pixel_offset = float3(qUINT::PIXEL_SIZE, 0);",
        "float3 single_pixel_offset = float3(qUINT::PIXEL_SIZE * 2.0, 0);",
    ),
    (
        "uv_inside_screen = all(saturate(-trace.uv.y * trace.uv.y + trace.uv.y));",
        "uv_inside_screen = all(saturate(trace.uv - trace.uv * trace.uv));",
    ),
    (
        "trace.hit = k != 0;",
        "trace.hit = k != 0 && all(saturate(trace.uv - trace.uv * trace.uv)) && qUINT::linear_depth(trace.uv) < 0.98 && scene.depth < 0.98;",
    ),
    (
        "float SSR_FRESNEL_K = 0.04;",
        "float SSR_FRESNEL_K = SSR_BASE_REFLECTIVITY;",
    ),
    (
        "saturate(dot(scene.eyedir, ray.dir)) * saturate(1 - dot(-scene.eyedir, scene.normal));",
        "saturate(dot(scene.eyedir, ray.dir) * 2.0 + 0.25);",
    ),
    (
        "reflection.a   = trace.hit * schlick * fade;",
        "float2 edge = min(trace.uv, 1.0 - trace.uv);\n\tfloat edge_fade = saturate(min(edge.x, edge.y) * 10.0);\n\tfloat surface = lerp(1.0, smoothstep(0.25, 0.65, -blurbuffer.y), SSR_FLOOR_ONLY);\n\treflection.a   = trace.hit * schlick * fade * edge_fade * edge_fade * surface;",
    ),
    (
        "o.rgb = lerp(o.rgb, reflection.rgb, alpha * fade);",
        "o.rgb = lerp(o.rgb, reflection.rgb / max(alpha, 1e-4), saturate(alpha * fade));",
    ),
];
const QUINT_SSR_LEGACY_PATCHES: [(&str, &str); 6] = [
    (
        "#define RESHADE_QUINT_COMMON_VERSION_REQUIRE    202",
        "uniform float SSR_FLOOR_ONLY <\n\tui_type = \"drag\";\n\tui_min = 0.0; ui_max = 1.0;\n\tui_label = \"Floor Only\";\n\tui_tooltip = \"Limits reflections to surfaces that face up, so walls and characters stay clean.\";\n> = 0.85;\n\n#define RESHADE_QUINT_COMMON_VERSION_REQUIRE    202",
    ),
    (
        "float3 single_pixel_offset = float3(qUINT::PIXEL_SIZE, 0);",
        "float3 single_pixel_offset = float3(qUINT::PIXEL_SIZE * 2.0, 0);",
    ),
    (
        "uv_inside_screen = all(saturate(-trace.uv.y * trace.uv.y + trace.uv.y));",
        "uv_inside_screen = all(saturate(trace.uv - trace.uv * trace.uv));",
    ),
    (
        "trace.hit = k != 0;",
        "trace.hit = k != 0 && all(saturate(trace.uv - trace.uv * trace.uv)) && qUINT::linear_depth(trace.uv) < 0.98 && scene.depth < 0.98;",
    ),
    (
        "reflection.a   = trace.hit * schlick * fade;",
        "float2 edge = min(trace.uv, 1.0 - trace.uv);\n\tfloat edge_fade = saturate(min(edge.x, edge.y) * 10.0);\n\tfloat surface = lerp(1.0, smoothstep(0.25, 0.65, -blurbuffer.y), SSR_FLOOR_ONLY);\n\treflection.a   = trace.hit * schlick * fade * edge_fade * edge_fade * surface;",
    ),
    (
        "o.rgb = lerp(o.rgb, reflection.rgb, alpha * fade);",
        "o.rgb = lerp(o.rgb, reflection.rgb / max(alpha, 1e-4), saturate(alpha * fade));",
    ),
];

pub fn patch_quint_ssr(root: &Path) {
    let path = root.join(QUINT_SSR_PATH);
    if !fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.is_file()) {
        return;
    }
    let Ok(text) = fs::read_to_string(&path) else {
        return;
    };
    if let Some(patched) = patched_quint_ssr(&text) {
        let staged = path.with_extension("fx.patch");
        if fs::write(&staged, patched).is_ok() && fs::rename(&staged, &path).is_err() {
            let _ = fs::remove_file(&staged);
        }
    }
}

fn patched_quint_ssr(text: &str) -> Option<String> {
    if text.contains(QUINT_SSR_PATCHED) {
        return None;
    }
    let mut output = text.to_owned();
    if output.contains(QUINT_SSR_LEGACY_PATCHED) {
        for (find, replace) in QUINT_SSR_LEGACY_PATCHES.iter().rev() {
            if output.matches(replace).count() != 1 {
                return None;
            }
            output = output.replacen(replace, find, 1);
        }
    }
    for (find, replace) in QUINT_SSR_PATCHES {
        if output.matches(find).count() != 1 {
            return None;
        }
        output = output.replacen(find, replace, 1);
    }
    Some(output)
}

pub fn migrate_ssr_technique(root: &Path) {
    let Ok(entries) = fs::read_dir(root.join("presets")) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let is_preset = entry.file_type().is_ok_and(|kind| kind.is_file())
            && path
                .extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("ini"));
        if !is_preset {
            continue;
        }
        let Ok(text) = fs::read_to_string(&path) else {
            continue;
        };
        if let Some(updated) = replace_ssr_technique(&text) {
            let staged = path.with_extension("ini.migrate");
            if fs::write(&staged, updated).is_ok() && fs::rename(&staged, &path).is_err() {
                let _ = fs::remove_file(&staged);
            }
        }
    }
}

fn replace_ssr_technique(text: &str) -> Option<String> {
    let mut changed = false;
    let lines: Vec<String> = text
        .split_inclusive('\n')
        .map(|line| {
            let Some((head, value)) = line.split_once('=') else {
                return line.to_owned();
            };
            let key = head.trim();
            if !(key.eq_ignore_ascii_case("Techniques") || key.eq_ignore_ascii_case("TechniqueSorting")) {
                return line.to_owned();
            }
            let body = value.trim_end_matches(['\r', '\n']);
            let ending = &value[body.len()..];
            let items: Vec<&str> = body.split(',').map(str::trim).collect();
            let retired = |item: &str| RETIRED_SSR_TECHNIQUES.iter().any(|old| item.eq_ignore_ascii_case(old));
            if !items.iter().any(|item| retired(item)) {
                return line.to_owned();
            }
            let mut seen = items.iter().any(|item| item.eq_ignore_ascii_case(REPLACEMENT_SSR_TECHNIQUE));
            let mut output: Vec<&str> = Vec::with_capacity(items.len());
            for item in items {
                if retired(item) {
                    if !seen {
                        output.push(REPLACEMENT_SSR_TECHNIQUE);
                        seen = true;
                    }
                } else {
                    output.push(item);
                }
            }
            changed = true;
            format!("{head}={}{ending}", output.join(","))
        })
        .collect();
    changed.then(|| lines.concat())
}

pub fn upgrade_unmodified_presets(root: &Path) {
    let directory = root.join("presets");
    for (name, previous_hash) in UPGRADED_PRESETS {
        let path = directory.join(name);
        if !fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.is_file()) {
            continue;
        }
        let unmodified = fs::read(&path)
            .is_ok_and(|bytes| format!("{:x}", Sha256::digest(bytes)) == previous_hash);
        let Some((_, data)) = BUNDLED_PRESETS
            .iter()
            .find(|(bundled, _)| bundled.eq_ignore_ascii_case(name))
        else {
            continue;
        };
        if unmodified {
            let staged = directory.join(format!("{name}.upgrade"));
            if fs::write(&staged, data).is_ok() && fs::rename(&staged, &path).is_err() {
                let _ = fs::remove_file(&staged);
            }
        }
    }
}

pub fn migrate_renamed_presets(root: &Path) {
    let directory = root.join("presets");
    for (old, new, original_hash) in RENAMED_PRESETS {
        let old_path = directory.join(old);
        let new_path = directory.join(new);
        if new_path.exists()
            || !fs::symlink_metadata(&old_path).is_ok_and(|metadata| metadata.is_file())
        {
            continue;
        }
        let unmodified = fs::read(&old_path)
            .is_ok_and(|bytes| format!("{:x}", Sha256::digest(bytes)) == original_hash);
        if unmodified {
            let _ = fs::rename(&old_path, &new_path);
        }
    }
}

pub fn remove_retired_assets(root: &Path) {
    for name in RETIRED_ASSETS {
        let path = root.join(name);
        if fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.is_file()) {
            let _ = fs::remove_file(path);
        }
    }
}

pub(crate) fn prepare_root(root: &Path) -> io::Result<PathBuf> {
    if !root.is_absolute() {
        return Err(invalid_input(
            "RiShade's extension directory must be absolute",
        ));
    }
    match fs::symlink_metadata(root) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => {
            return Err(invalid_input(
                "RiShade's extension directory is not a regular folder",
            ));
        }
        Ok(_) => {}
        Err(error) if error.kind() == io::ErrorKind::NotFound => fs::create_dir_all(root)?,
        Err(error) => return Err(error),
    }
    root.canonicalize()
}

fn install_package(
    agent: &ureq::Agent,
    catalog: &PresetIni,
    section: &str,
    package_name: &str,
    staged_files: &Path,
    work: &Path,
) -> io::Result<()> {
    let url = catalog
        .get(section, "DownloadUrl")
        .ok_or_else(|| invalid_data("the package has no download address"))?;
    let install_path = package_destination(
        staged_files,
        catalog
            .get(section, "InstallPath")
            .ok_or_else(|| invalid_data("the package has no shader install folder"))?,
        "Shaders",
    )?;
    let texture_path = catalog
        .get(section, "TextureInstallPath")
        .map(|path| package_destination(staged_files, path, "Textures"))
        .transpose()?;
    let denied = catalog
        .get(section, "DenyEffectFiles")
        .unwrap_or_default()
        .split(',')
        .map(str::trim)
        .filter(|name| !name.is_empty())
        .map(str::to_ascii_lowercase)
        .collect::<HashSet<_>>();

    let archive = download(agent, url, PACKAGE_LIMIT, is_github_host)?;
    let extracted = work.join(format!("package-{}", safe_component(section)?));
    fs::create_dir(&extracted)?;
    extract_package(&archive, &extracted, package_name)?;
    let folders = find_package_folders(&extracted, package_name)?;
    copy_package_files(&folders.shaders, &install_path, &denied)?;
    if let (Some(source), Some(destination)) = (folders.textures, texture_path) {
        copy_package_files(&source, &destination, &HashSet::new())?;
    }
    Ok(())
}

struct PackageFolders {
    shaders: PathBuf,
    textures: Option<PathBuf>,
}

fn extract_package(bytes: &[u8], destination: &Path, package_name: &str) -> io::Result<()> {
    let mut archive = ZipArchive::new(std::io::Cursor::new(bytes))
        .map_err(|_| invalid_data(format!("{package_name} is not a valid ZIP archive")))?;
    let count = archive.len();
    if count > ZIP_ENTRY_LIMIT {
        return Err(invalid_data(format!(
            "{package_name} contains too many files"
        )));
    }
    let mut declared_total = 0u64;
    for index in 0..count {
        let file = archive
            .by_index(index)
            .map_err(|_| invalid_data(format!("could not inspect {package_name}")))?;
        let size = file.size();
        declared_total = declared_total.saturating_add(size);
        if size > ZIP_ENTRY_SIZE_LIMIT || declared_total > ZIP_TOTAL_SIZE_LIMIT {
            return Err(invalid_data(format!(
                "{package_name} expands beyond the accepted size limit"
            )));
        }
    }

    for index in 0..count {
        let mut file = archive
            .by_index(index)
            .map_err(|_| invalid_data(format!("could not read {package_name}")))?;
        if file.is_dir()
            || file
                .unix_mode()
                .is_some_and(|mode| mode & 0o170000 == 0o120000)
        {
            continue;
        }
        let relative = safe_archive_path(file.name())?;
        if !allowed_package_file(Path::new(relative.file_name().unwrap_or_default())) {
            continue;
        }
        let output = destination.join(relative);
        ensure_safe_directory(destination, output.parent().unwrap_or(destination))?;
        let mut output_file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&output)?;
        let copied = io::copy(
            &mut (&mut file).take(ZIP_ENTRY_SIZE_LIMIT + 1),
            &mut output_file,
        )?;
        if copied > ZIP_ENTRY_SIZE_LIMIT || copied != file.size() {
            return Err(invalid_data(format!(
                "{package_name} contains a file with an invalid expanded size"
            )));
        }
    }
    Ok(())
}

fn safe_archive_path(name: &str) -> io::Result<PathBuf> {
    let normalized = name.replace('\\', "/");
    if normalized.is_empty() || normalized.starts_with('/') || normalized.contains(':') {
        return Err(invalid_data("an effect archive contains an unsafe path"));
    }
    let mut path = PathBuf::new();
    for component in normalized.split('/') {
        match component {
            "" | "." => continue,
            ".." => {
                return Err(invalid_data(
                    "an effect archive contains a path outside its folder",
                ));
            }
            name if name.chars().any(|character| {
                character.is_control() || matches!(character, '<' | '>' | '"' | '|' | '?' | '*')
            }) || name.ends_with(['.', ' '])
                || windows_reserved_name(name) =>
            {
                return Err(invalid_data(
                    "an effect archive contains an invalid Windows path",
                ));
            }
            name => path.push(name),
        }
    }
    if path.as_os_str().is_empty() {
        return Err(invalid_data("an effect archive contains an empty path"));
    }
    Ok(path)
}

fn windows_reserved_name(component: &str) -> bool {
    let base = component
        .split('.')
        .next()
        .unwrap_or_default()
        .trim_end_matches(['.', ' '])
        .to_ascii_uppercase();
    matches!(base.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || ["COM", "LPT"].iter().any(|prefix| {
            base.strip_prefix(prefix).is_some_and(|suffix| {
                suffix.len() == 1 && suffix.as_bytes()[0].is_ascii_digit() && suffix != "0"
            })
        })
}

fn find_package_folders(extracted: &Path, package_name: &str) -> io::Result<PackageFolders> {
    let mut named_shaders = None;
    let mut named_textures = None;
    let mut fallback_shaders: Option<(usize, PathBuf)> = None;
    let mut fallback_textures: Option<(usize, PathBuf)> = None;
    scan_package_folders(
        extracted,
        0,
        &mut named_shaders,
        &mut named_textures,
        &mut fallback_shaders,
        &mut fallback_textures,
    )?;
    let shaders = named_shaders
        .or_else(|| fallback_shaders.map(|(_, path)| path))
        .ok_or_else(|| invalid_data(format!("{package_name} contains no effects")))?;
    Ok(PackageFolders {
        shaders,
        textures: named_textures.or_else(|| fallback_textures.map(|(_, path)| path)),
    })
}

fn scan_package_folders(
    directory: &Path,
    depth: usize,
    named_shaders: &mut Option<PathBuf>,
    named_textures: &mut Option<PathBuf>,
    fallback_shaders: &mut Option<(usize, PathBuf)>,
    fallback_textures: &mut Option<(usize, PathBuf)>,
) -> io::Result<()> {
    let mut has_effect = false;
    let mut has_image = false;
    let mut children = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let kind = entry.file_type()?;
        if kind.is_symlink() {
            continue;
        }
        if kind.is_dir() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
            if name == "shaders" && named_shaders.is_none() {
                *named_shaders = Some(path.clone());
            }
            if name == "textures" && named_textures.is_none() {
                *named_textures = Some(path.clone());
            }
            children.push(path);
        } else if kind.is_file() {
            let extension = entry
                .path()
                .extension()
                .unwrap_or_default()
                .to_string_lossy()
                .to_ascii_lowercase();
            has_effect |= extension == "fx";
            has_image |= matches!(extension.as_str(), "png" | "jpg" | "jpeg");
        }
    }
    if has_effect
        && fallback_shaders
            .as_ref()
            .is_none_or(|(known_depth, _)| depth < *known_depth)
    {
        *fallback_shaders = Some((depth, directory.to_path_buf()));
    }
    if has_image
        && fallback_textures
            .as_ref()
            .is_none_or(|(known_depth, _)| depth < *known_depth)
    {
        *fallback_textures = Some((depth, directory.to_path_buf()));
    }
    for child in children {
        scan_package_folders(
            &child,
            depth + 1,
            named_shaders,
            named_textures,
            fallback_shaders,
            fallback_textures,
        )?;
    }
    Ok(())
}

fn copy_package_files(
    source: &Path,
    destination: &Path,
    denied: &HashSet<String>,
) -> io::Result<()> {
    fs::create_dir_all(destination)?;
    for entry in fs::read_dir(source)? {
        let entry = entry?;
        let kind = entry.file_type()?;
        if kind.is_symlink() {
            continue;
        }
        let target = destination.join(entry.file_name());
        if kind.is_dir() {
            copy_package_files(&entry.path(), &target, denied)?;
        } else if kind.is_file() {
            let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
            if allowed_package_file(Path::new(&name)) && !denied.contains(&name) {
                ensure_safe_directory(destination, destination)?;
                copy_file_if_missing(&entry.path(), &target)?;
            }
        }
    }
    Ok(())
}

fn package_destination(root: &Path, relative: &str, kind: &str) -> io::Result<PathBuf> {
    let mut normalized = relative.trim().replace('\\', "/");
    while let Some(stripped) = normalized.strip_prefix("./") {
        normalized = stripped.to_owned();
    }
    let lowered = normalized.to_ascii_lowercase();
    let base = format!("reshade-shaders/{}", kind.to_ascii_lowercase());
    if (lowered != base && !lowered.starts_with(&(base + "/"))) || normalized.contains(':') {
        return Err(invalid_data(
            "an effect package has an invalid install folder",
        ));
    }
    let relative_path = safe_archive_path(&normalized)?;
    let destination = root.join(relative_path);
    if !destination.starts_with(root) {
        return Err(invalid_data(
            "an effect package install folder escaped its stage",
        ));
    }
    Ok(destination)
}

fn allowed_package_file(name: &Path) -> bool {
    let filename = name.to_string_lossy().to_ascii_lowercase();
    let extension = name
        .extension()
        .unwrap_or_default()
        .to_string_lossy()
        .to_ascii_lowercase();
    matches!(
        extension.as_str(),
        "fx" | "fxh"
            | "png"
            | "jpg"
            | "jpeg"
            | "bmp"
            | "tga"
            | "dds"
            | "hdr"
            | "cube"
            | "txt"
            | "md"
    ) || matches!(
        filename.as_str(),
        "license" | "licence" | "copying" | "notice" | "readme"
    )
}

fn install_bundled_presets(
    staged_files: &Path,
    root: &Path,
    effects: &HashSet<String>,
    summary: &mut InstallSummary,
) -> io::Result<()> {
    let catalog = PresetIni::parse(PRESET_CATALOG);
    let preset_dir = staged_files.join("presets");
    for name in catalog.section_names() {
        let Some((_, data)) = BUNDLED_PRESETS
            .iter()
            .find(|(bundled_name, _)| bundled_name.eq_ignore_ascii_case(name))
        else {
            summary.skipped_presets.push(name.to_owned());
            continue;
        };
        let expected = catalog.get(name, "sha256").unwrap_or_default();
        let actual = format!("{:x}", Sha256::digest(data));
        if !valid_hash(expected) || !actual.eq_ignore_ascii_case(expected) {
            summary.skipped_presets.push(name.to_owned());
            continue;
        }
        if root.join("presets").join(name).exists() {
            continue;
        }
        let text = match std::str::from_utf8(data) {
            Ok(text) => text,
            Err(_) => {
                summary.skipped_presets.push(name.to_owned());
                continue;
            }
        };
        let preset = PresetIni::parse(text);
        let referenced = preset_effect_files(&preset);
        let valid_references = !referenced.is_empty()
            && referenced.iter().all(|file| {
                !file.contains(['\\', '/', ':']) && effects.contains(&file.to_ascii_lowercase())
            });
        if !valid_references {
            summary.skipped_presets.push(name.to_owned());
            continue;
        }
        fs::create_dir_all(&preset_dir)?;
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(preset_dir.join(name))?;
        output.write_all(data)?;
        summary.installed_presets += 1;
    }
    Ok(())
}

fn collect_effect_names(staged_files: &Path, root: &Path) -> io::Result<HashSet<String>> {
    let mut effects = HashSet::new();
    collect_effect_names_from(&staged_files.join("reshade-shaders/Shaders"), &mut effects)?;
    collect_effect_names_from(&root.join("reshade-shaders/Shaders"), &mut effects)?;
    Ok(effects)
}

fn collect_effect_names_from(directory: &Path, effects: &mut HashSet<String>) -> io::Result<()> {
    let Ok(entries) = fs::read_dir(directory) else {
        return Ok(());
    };
    for entry in entries {
        let entry = entry?;
        let kind = entry.file_type()?;
        if kind.is_symlink() {
            continue;
        }
        if kind.is_dir() {
            collect_effect_names_from(&entry.path(), effects)?;
        } else if kind.is_file()
            && entry
                .path()
                .extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("fx"))
        {
            effects.insert(entry.file_name().to_string_lossy().to_ascii_lowercase());
        }
    }
    Ok(())
}

fn copy_missing_tree(source: &Path, root: &Path) -> io::Result<()> {
    copy_missing_tree_inner(source, source, root)
}

fn copy_missing_tree_inner(source: &Path, source_root: &Path, root: &Path) -> io::Result<()> {
    for entry in fs::read_dir(source)? {
        let entry = entry?;
        let kind = entry.file_type()?;
        if kind.is_symlink() {
            continue;
        }
        let relative = entry
            .path()
            .strip_prefix(source_root)
            .map_err(|_| invalid_input("staged asset path escaped its staging folder"))?
            .to_path_buf();
        let target = root.join(relative);
        if kind.is_dir() {
            ensure_safe_directory(root, &target)?;
            copy_missing_tree_inner(&entry.path(), source_root, root)?;
        } else if kind.is_file() {
            ensure_safe_directory(root, target.parent().unwrap_or(root))?;
            copy_file_if_missing(&entry.path(), &target)?;
        }
    }
    Ok(())
}

fn ensure_safe_directory(root: &Path, directory: &Path) -> io::Result<()> {
    let relative = directory
        .strip_prefix(root)
        .map_err(|_| invalid_input("installation path escaped RiShade's extension directory"))?;
    let mut current = root.to_path_buf();
    for component in relative.components() {
        let Component::Normal(part) = component else {
            return Err(invalid_input(
                "installation path contains an unsafe component",
            ));
        };
        current.push(part);
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => {
                return Err(invalid_input(
                    "installation path contains a reparse point or file",
                ));
            }
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => fs::create_dir(&current)?,
            Err(error) => return Err(error),
        }
    }
    Ok(())
}

fn copy_file_if_missing(source: &Path, target: &Path) -> io::Result<()> {
    if fs::symlink_metadata(target).is_ok() {
        return Ok(());
    }
    let mut input = File::open(source)?;
    let mut output = match OpenOptions::new().write(true).create_new(true).open(target) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => return Ok(()),
        Err(error) => return Err(error),
    };
    if let Err(error) = io::copy(&mut input, &mut output) {
        let _ = fs::remove_file(target);
        return Err(error);
    }
    output.flush()?;
    Ok(())
}

pub(crate) fn download<F>(agent: &ureq::Agent, url: &str, limit: usize, allowed_host: F) -> io::Result<Vec<u8>>
where
    F: Fn(&str) -> bool,
{
    let initial_host =
        https_host(url).ok_or_else(|| invalid_data("only HTTPS downloads are allowed"))?;
    if !allowed_host(initial_host) {
        return Err(invalid_data("download host is not in RiShade's allowlist"));
    }
    let response = agent
        .get(url)
        .call()
        .map_err(|error| io::Error::other(format!("download failed: {error}")))?;
    let final_host = response
        .get_uri()
        .host()
        .ok_or_else(|| invalid_data("download response has no host"))?;
    if response.get_uri().scheme_str() != Some("https") || !allowed_host(final_host) {
        return Err(invalid_data(
            "download redirected outside RiShade's HTTPS allowlist",
        ));
    }
    if response
        .headers()
        .get("content-length")
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<u64>().ok())
        .is_some_and(|length| length > limit as u64)
    {
        return Err(invalid_data("download exceeds RiShade's size limit"));
    }
    let mut reader = response.into_body().into_reader().take(limit as u64 + 1);
    let mut bytes = Vec::new();
    reader.read_to_end(&mut bytes)?;
    if bytes.len() > limit {
        return Err(invalid_data("download exceeds RiShade's size limit"));
    }
    Ok(bytes)
}

fn https_host(url: &str) -> Option<&str> {
    let rest = url.strip_prefix("https://")?;
    let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
    let authority = &rest[..end];
    if authority.is_empty() || authority.contains('@') {
        return None;
    }
    let host = authority.split(':').next()?;
    (!host.is_empty()).then_some(host)
}

pub(crate) fn is_github_host(host: &str) -> bool {
    host.eq_ignore_ascii_case("github.com")
        || host.eq_ignore_ascii_case("raw.githubusercontent.com")
        || host.eq_ignore_ascii_case("codeload.github.com")
        || host.to_ascii_lowercase().ends_with(".github.com")
        || host
            .to_ascii_lowercase()
            .ends_with(".githubusercontent.com")
}

fn valid_hash(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn safe_component(value: &str) -> io::Result<String> {
    let name = value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() {
                character
            } else {
                '_'
            }
        })
        .take(64)
        .collect::<String>();
    if name.is_empty() {
        return Err(invalid_data(
            "the effect package has an invalid section name",
        ));
    }
    Ok(name)
}

pub(crate) fn invalid_data(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

fn invalid_input(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message.into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;
    use zip::write::SimpleFileOptions;

    fn temp_path(label: &str) -> PathBuf {
        std::env::temp_dir().join(format!("rishade-{label}-{}", std::process::id()))
    }

    fn test_archive(entries: &[(&str, &[u8])]) -> Vec<u8> {
        let mut output = Cursor::new(Vec::new());
        {
            let mut writer = zip::ZipWriter::new(&mut output);
            for (name, content) in entries {
                writer
                    .start_file(*name, SimpleFileOptions::default())
                    .unwrap();
                writer.write_all(content).unwrap();
            }
            writer.finish().unwrap();
        }
        output.into_inner()
    }

    #[test]
    fn archive_extraction_filters_files_and_rejects_traversal() {
        let root = temp_path("effect-archive");
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).unwrap();
        let data = test_archive(&[
            ("pkg/Shaders/example.fx", b"shader"),
            ("pkg/Shaders/README.md", b"license notice"),
            ("pkg/Shaders/drop.exe", b"not allowed"),
        ]);
        extract_package(&data, &root, "test").unwrap();
        assert_eq!(
            fs::read(root.join("pkg/Shaders/example.fx")).unwrap(),
            b"shader"
        );
        assert!(!root.join("pkg/Shaders/drop.exe").exists());
        assert!(
            find_package_folders(&root, "test")
                .unwrap()
                .shaders
                .ends_with("Shaders")
        );

        let traversal = test_archive(&[("../escape.fx", b"no")]);
        assert!(extract_package(&traversal, &root, "test").is_err());
        assert!(!root.parent().unwrap().join("escape.fx").exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn package_install_paths_must_stay_in_expected_effect_tree() {
        let root = Path::new("C:/temp/stage");
        assert!(package_destination(root, ".\\reshade-shaders\\Shaders\\X", "Shaders").is_ok());
        assert!(package_destination(root, ".\\outside\\Shaders", "Shaders").is_err());
        assert!(
            package_destination(root, ".\\reshade-shaders\\Shaders\\..\\Textures", "Shaders")
                .is_err()
        );
        assert!(safe_archive_path("C:/outside.fx").is_err());
        assert!(safe_archive_path("../outside.fx").is_err());
        assert!(safe_archive_path("Shaders/CON.fx").is_err());
        assert!(safe_archive_path("Shaders/bad?.fx").is_err());
    }

    #[test]
    fn bundled_presets_match_their_hash_manifest_and_have_safe_names() {
        let catalog = PresetIni::parse(PRESET_CATALOG);
        for (name, data) in BUNDLED_PRESETS {
            let expected = catalog.get(name, "sha256").unwrap();
            assert_eq!(format!("{:x}", Sha256::digest(data)), expected);
            assert!(
                Path::new(name)
                    .file_name()
                    .is_some_and(|file| file == *name)
            );
        }
    }

    #[test]
    fn provisioned_marker_skips_network_and_does_not_replace_user_files() {
        let root = temp_path("effect-fast-path");
        let _ = fs::remove_dir_all(&root);
        let shader_dir = root.join("reshade-shaders/Shaders");
        fs::create_dir_all(&shader_dir).unwrap();
        fs::write(shader_dir.join("ReShade.fxh"), b"fxh").unwrap();
        fs::write(shader_dir.join("user.fx"), b"user shader").unwrap();
        fs::write(root.join(ASSET_MARKER), b"already done").unwrap();

        let result = install_effects_and_presets(&root).unwrap();

        assert!(result.assets_already_provisioned);
        assert_eq!(
            fs::read(shader_dir.join("user.fx")).unwrap(),
            b"user shader"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn provisioned_install_adds_new_bundled_presets_without_touching_existing_ones() {
        let root = temp_path("effect-new-presets");
        let _ = fs::remove_dir_all(&root);
        let shader_dir = root.join("reshade-shaders/Shaders");
        fs::create_dir_all(&shader_dir).unwrap();
        fs::write(shader_dir.join("ReShade.fxh"), b"fxh").unwrap();
        for effect in ["CAS.fx", "qUINT_ssr.fx"] {
            fs::write(shader_dir.join(effect), b"effect").unwrap();
        }
        fs::write(root.join(ASSET_MARKER), b"already done").unwrap();
        let presets = root.join("presets");
        fs::create_dir_all(&presets).unwrap();
        fs::write(presets.join("GenericPreset2.ini"), b"user edited").unwrap();

        let result = install_effects_and_presets(&root).unwrap();

        assert!(!result.assets_already_provisioned);
        assert!(presets.join("RiShade Reflections.ini").is_file());
        assert_eq!(
            fs::read(presets.join("GenericPreset2.ini")).unwrap(),
            b"user edited"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn renamed_presets_move_only_when_unmodified() {
        let root = temp_path("preset-renames");
        let _ = fs::remove_dir_all(&root);
        let presets = root.join("presets");
        fs::create_dir_all(&presets).unwrap();
        let original = BUNDLED_PRESETS
            .iter()
            .find(|(name, _)| *name == "RiShade Enhanced.ini")
            .unwrap()
            .1;
        fs::write(presets.join("GenericPreset1.ini"), original).unwrap();
        fs::write(presets.join("GenericPreset2.ini"), b"user edited").unwrap();

        migrate_renamed_presets(&root);

        assert!(!presets.join("GenericPreset1.ini").exists());
        assert_eq!(
            fs::read(presets.join("RiShade Enhanced.ini")).unwrap(),
            original
        );
        assert_eq!(
            fs::read(presets.join("GenericPreset2.ini")).unwrap(),
            b"user edited"
        );
        assert!(!presets.join("RiShade Glow.ini").exists());
        fs::remove_dir_all(root).unwrap();
    }


    #[test]
    fn quint_ssr_is_patched_once_and_left_alone_when_unknown() {
        let mut stock = String::new();
        for (find, _) in QUINT_SSR_PATCHES {
            stock.push_str(find);
            stock.push_str("\n");
        }
        let patched = patched_quint_ssr(&stock).unwrap();
        assert!(patched.contains(QUINT_SSR_PATCHED));
        assert!(patched.contains("reflection.rgb / max(alpha, 1e-4)"));
        assert!(patched_quint_ssr(&patched).is_none());
        let mut legacy = stock.clone();
        for (find, replace) in QUINT_SSR_LEGACY_PATCHES {
            legacy = legacy.replacen(find, replace, 1);
        }
        assert_eq!(patched_quint_ssr(&legacy).unwrap(), patched);
        assert!(patched_quint_ssr("technique SSR {}").is_none());
    }

    #[test]
    fn retired_ssr_technique_is_replaced_once() {
        let text = "Techniques=Vibrance@Vibrance.fx,RiShade_SSR@RiShade_SSR.fx\r\nTechniqueSorting=RiShade_SSR@RiShade_SSR.fx,Vibrance@Vibrance.fx\r\n[qUINT_ssr.fx]\r\nSSR_FADE_DIST=0.3\r\n";
        let updated = replace_ssr_technique(text).unwrap();
        assert_eq!(
            updated,
            "Techniques=Vibrance@Vibrance.fx,SSR@qUINT_ssr.fx\r\nTechniqueSorting=SSR@qUINT_ssr.fx,Vibrance@Vibrance.fx\r\n[qUINT_ssr.fx]\r\nSSR_FADE_DIST=0.3\r\n"
        );
        assert!(replace_ssr_technique(&updated).is_none());
    }
    #[test]
    fn upgrades_only_unmodified_presets() {
        let root = tempfile::tempdir().unwrap();
        let presets = root.path().join("presets");
        fs::create_dir_all(&presets).unwrap();
        let (_, current) = BUNDLED_PRESETS
            .iter()
            .find(|(name, _)| *name == "RiShade Reflections.ini")
            .unwrap();
        let previous = concat!(
            "Techniques=SSR@qUINT_ssr.fx,AdaptiveTonemapper@AdaptiveTonemapper.fx\r\n",
            "TechniqueSorting=SSR@qUINT_ssr.fx,AdaptiveTonemapper@AdaptiveTonemapper.fx\r\n",
            "\r\n",
            "[AdaptiveTonemapper.fx]\r\n",
            "AdaptFocalPoint=0.500000,0.500000\r\n",
            "AdaptPrecision=0\r\n",
            "AdaptRange=0.000000,1.000000\r\n",
            "AdaptSensitivity=1.000000\r\n",
            "AdaptTime=1.000000\r\n",
            "Amount=0.409000\r\n",
            "Exposure=-4.842000\r\n",
            "FixWhitePoint=1\r\n",
            "TonemapOperator=2\r\n",
            "\r\n",
            "[qUINT_ssr.fx]\r\n",
            "SSR_ACCEPT_RANGE=0.200000\r\n",
            "SSR_FADE_DIST=0.300000\r\n",
            "SSR_FIELD_OF_VIEW=70.000000\r\n",
            "SSR_FILTER_SIZE=2.500000\r\n",
            "SSR_FRESNEL_EXP=7.000000\r\n",
            "SSR_JITTER_AMOUNT=0.350000\r\n",
            "SSR_RAY_INC=1.600000\r\n",
            "SSR_REFLECTION_INTENSITY=0.400000\r\n",
            "SSR_RELIEF_AMOUNT=0.000000\r\n",
            "SSR_RELIEF_SCALE=0.350000\r\n",
        )
        .to_owned();
        assert_eq!(format!("{:x}", Sha256::digest(previous.as_bytes())), UPGRADED_PRESETS[1].1);
        fs::write(presets.join("RiShade Reflections.ini"), &previous).unwrap();
        upgrade_unmodified_presets(root.path());
        assert_eq!(fs::read(presets.join("RiShade Reflections.ini")).unwrap(), *current);

        fs::write(presets.join("RiShade Reflections.ini"), b"user edited").unwrap();
        upgrade_unmodified_presets(root.path());
        assert_eq!(fs::read(presets.join("RiShade Reflections.ini")).unwrap(), b"user edited");
    }

    #[test]
    fn only_https_github_package_hosts_are_allowed() {
        assert!(https_host("https://github.com/repo/archive.zip").is_some_and(is_github_host));
        assert!(!https_host("http://github.com/repo/archive.zip").is_some_and(is_github_host));
        assert!(
            !https_host("https://github.com.evil.test/archive.zip").is_some_and(is_github_host)
        );
        assert!(!https_host("https://user@github.com/archive.zip").is_some_and(is_github_host));
    }
}
