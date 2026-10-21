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
const DEPTH_ASSETS: [(&str, &str, &str); 3] = [
    (
        "onnxruntime.dll",
        "https://github.com/OMouta/Unishade/releases/download/depth-assets/onnxruntime.dll",
        "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
    ),
    (
        "DirectML.dll",
        "https://github.com/OMouta/Unishade/releases/download/depth-assets/DirectML.dll",
        "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1",
    ),
    (
        "depth-anything-v2-small.onnx",
        "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/4472b7362082ad9968fee890ca0f1e5aca36b93d/onnx/model_fp16.onnx",
        "2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04",
    ),
];
const RETIRED_ASSETS: [&str; 2] = ["renodx-dlss.addon64", "nvngx_dlssnr.dll"];
const DEPTH_VERIFIED_MARKER: &str = ".rishade-depth-verified";
const DEPTH_ASSET_LIMIT: usize = 512 << 20;

const PRESET_CATALOG: &str = include_str!("../presets/downloads.ini");
const BUNDLED_PRESETS: &[(&str, &[u8])] = &[
    (
        "GenericPreset1.ini",
        include_bytes!("../presets/GenericPreset1.ini"),
    ),
    (
        "GenericPreset2.ini",
        include_bytes!("../presets/GenericPreset2.ini"),
    ),
    (
        "RiShadeReflections.ini",
        include_bytes!("../presets/RiShadeReflections.ini"),
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

pub fn install_depth_assets(root: &Path) -> io::Result<()> {
    let root = prepare_root(root)?;
    let cache_path = root.join(DEPTH_VERIFIED_MARKER);
    let previous_cache = fs::read_to_string(&cache_path).unwrap_or_default();
    let mut cache = String::new();
    let mut work = None;
    let mut agent = None;

    for (name, url, expected_hash) in DEPTH_ASSETS.iter().copied() {
        let destination = root.join(name);
        match fs::symlink_metadata(&destination) {
            Ok(metadata) => {
                let stamp = depth_asset_stamp(name, &metadata, expected_hash);
                if !metadata.is_file() || !previous_cache.lines().any(|line| line == stamp) {
                    verify_depth_asset(&destination, expected_hash)?;
                }
                cache.push_str(&stamp);
                cache.push('\n');
                continue;
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }

        if work.is_none() {
            work = Some(
                tempfile::Builder::new()
                    .prefix("rishade-depth-setup-")
                    .tempdir()?,
            );
        }
        let agent = agent.get_or_insert_with(|| {
            ureq::Agent::new_with_config(
                ureq::Agent::config_builder()
                    .https_only(true)
                    .timeout_global(Some(Duration::from_secs(300)))
                    .build(),
            )
        });
        let bytes = download(agent, url, DEPTH_ASSET_LIMIT, is_depth_asset_host)?;
        verify_hash(&bytes, expected_hash)?;
        let staged = work
            .as_ref()
            .map_or_else(|| root.join(name), |dir| dir.path().join(name));
        fs::write(&staged, &bytes)?;
        copy_file_if_missing(&staged, &destination)?;
        verify_depth_asset(&destination, expected_hash)?;
        cache.push_str(&depth_asset_stamp(
            name,
            &fs::symlink_metadata(&destination)?,
            expected_hash,
        ));
        cache.push('\n');
    }
    if cache != previous_cache {
        let _ = fs::write(&cache_path, cache);
    }
    Ok(())
}

fn depth_asset_stamp(name: &str, metadata: &fs::Metadata, expected_hash: &str) -> String {
    let modified = metadata
        .modified()
        .ok()
        .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
        .map_or(0, |duration| duration.as_nanos());
    format!("{name}\t{}\t{modified}\t{expected_hash}", metadata.len())
}

fn verify_depth_asset(path: &Path, expected_hash: &str) -> io::Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid_data("depth asset path is not a regular file"));
    }
    let mut hasher = Sha256::new();
    io::copy(&mut File::open(path)?, &mut hasher)?;
    verify_digest(&format!("{:x}", hasher.finalize()), expected_hash)
}

pub fn remove_retired_assets(root: &Path) {
    for name in RETIRED_ASSETS {
        let path = root.join(name);
        if fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.is_file()) {
            let _ = fs::remove_file(path);
        }
    }
}

fn verify_hash(bytes: &[u8], expected_hash: &str) -> io::Result<()> {
    verify_digest(&format!("{:x}", Sha256::digest(bytes)), expected_hash)
}

fn verify_digest(actual: &str, expected_hash: &str) -> io::Result<()> {
    if actual.eq_ignore_ascii_case(expected_hash) {
        Ok(())
    } else {
        Err(invalid_data("depth asset failed its pinned SHA-256 check"))
    }
}

fn is_depth_asset_host(host: &str) -> bool {
    is_github_host(host)
        || host.eq_ignore_ascii_case("huggingface.co")
        || host.to_ascii_lowercase().ends_with(".huggingface.co")
        || host.eq_ignore_ascii_case("xethub.hf.co")
        || host.to_ascii_lowercase().ends_with(".xethub.hf.co")
        || host.eq_ignore_ascii_case("hf.co")
        || host.to_ascii_lowercase().ends_with(".hf.co")
        || host.eq_ignore_ascii_case("cloudfront.net")
        || host.to_ascii_lowercase().ends_with(".cloudfront.net")
}

fn prepare_root(root: &Path) -> io::Result<PathBuf> {
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
            summary.skipped_presets.push(name.to_owned());
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

fn download<F>(agent: &ureq::Agent, url: &str, limit: usize, allowed_host: F) -> io::Result<Vec<u8>>
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

fn is_github_host(host: &str) -> bool {
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

fn invalid_data(message: impl Into<String>) -> io::Error {
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
        for effect in ["qUINT_ssr.fx", "AdaptiveTonemapper.fx"] {
            fs::write(shader_dir.join(effect), b"effect").unwrap();
        }
        fs::write(root.join(ASSET_MARKER), b"already done").unwrap();
        let presets = root.join("presets");
        fs::create_dir_all(&presets).unwrap();
        fs::write(presets.join("GenericPreset2.ini"), b"user edited").unwrap();

        let result = install_effects_and_presets(&root).unwrap();

        assert!(!result.assets_already_provisioned);
        assert!(presets.join("RiShadeReflections.ini").is_file());
        assert_eq!(
            fs::read(presets.join("GenericPreset2.ini")).unwrap(),
            b"user edited"
        );
        fs::remove_dir_all(root).unwrap();
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

    #[test]
    fn depth_download_hosts_and_hashes_are_pinned() {
        assert!(is_depth_asset_host("github.com"));
        assert!(is_depth_asset_host("cdn-lfs.huggingface.co"));
        assert!(is_depth_asset_host("cas-bridge.xethub.hf.co"));
        assert!(!is_depth_asset_host("huggingface.co.attacker.test"));
        assert!(!is_depth_asset_host("attacker.test"));
        verify_hash(
            b"abc",
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        )
        .unwrap();
        assert!(
            verify_hash(
                b"abc",
                "0000000000000000000000000000000000000000000000000000000000000000"
            )
            .is_err()
        );
    }

    #[test]
    fn depth_asset_verification_rejects_conflicts_without_replacing_them() {
        let directory = tempfile::tempdir().unwrap();
        let asset = directory.path().join("onnxruntime.dll");
        fs::write(&asset, b"user-installed file").unwrap();

        assert!(
            verify_depth_asset(
                &asset,
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            )
            .is_err()
        );
        assert_eq!(fs::read(asset).unwrap(), b"user-installed file");
    }
}
