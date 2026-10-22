use sha2::{Digest, Sha256};
use std::fs::{self, File};
use std::io;
use std::path::Path;
use std::time::Duration;

pub const ADDON_FILE: &str = "renodx-dlss.addon64";

const DLSS_FILES: [(&str, &str, &str); 2] = [
    (
        "nvngx_dlssnr.dll",
        "https://github.com/OMouta/Unishade/releases/download/dlss5-assets/nvngx_dlssnr.dll",
        "e67dee209320cdafe0e93e45675d7aa34323a53acc57a72b2e40a181581c989a",
    ),
    (
        ADDON_FILE,
        "https://github.com/OMouta/Unishade/releases/download/dlss5-assets/renodx-dlss.addon64",
        "1d855cf226857dce890cffbf7206ba9b6497ce1d471b217c1c8b44b6cd5d27e9",
    ),
];

const DLSS_LIMIT: usize = 256 << 20;

pub fn installed(root: &Path) -> bool {
    DLSS_FILES
        .iter()
        .all(|(name, _, _)| fs::symlink_metadata(root.join(name)).is_ok_and(|metadata| metadata.is_file()))
}

pub fn install(root: &Path) -> io::Result<()> {
    let root = crate::effect_setup::prepare_root(root)?;
    let agent = ureq::Agent::new_with_config(
        ureq::Agent::config_builder()
            .https_only(true)
            .tls_config(
                ureq::tls::TlsConfig::builder()
                    .provider(ureq::tls::TlsProvider::NativeTls)
                    .build(),
            )
            .timeout_global(Some(Duration::from_secs(900)))
            .build(),
    );
    for (name, url, expected) in DLSS_FILES {
        let destination = root.join(name);
        if matches_hash(&destination, expected)? {
            continue;
        }
        let bytes = crate::effect_setup::download(&agent, url, DLSS_LIMIT, crate::effect_setup::is_github_host)?;
        if format!("{:x}", Sha256::digest(&bytes)) != expected {
            return Err(crate::effect_setup::invalid_data(format!(
                "{name} failed its pinned SHA-256 check"
            )));
        }
        let staged = root.join(format!("{name}.download"));
        fs::write(&staged, &bytes)?;
        if let Err(error) = fs::rename(&staged, &destination) {
            let _ = fs::remove_file(&staged);
            return Err(error);
        }
    }
    Ok(())
}

pub fn remove(root: &Path) -> io::Result<()> {
    for (name, _, expected) in DLSS_FILES {
        let path = root.join(name);
        if matches_hash(&path, expected)? {
            fs::remove_file(path)?;
        }
    }
    Ok(())
}

fn matches_hash(path: &Path, expected: &str) -> io::Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.is_file() => {
            let mut hasher = Sha256::new();
            io::copy(&mut File::open(path)?, &mut hasher)?;
            Ok(format!("{:x}", hasher.finalize()) == expected)
        }
        Ok(_) => Err(crate::effect_setup::invalid_data(
            "a DLSS file path is not a regular file",
        )),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(error),
    }
}

#[cfg(windows)]
pub fn has_rtx_gpu() -> bool {
    use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, IDXGIFactory1};
    const NVIDIA: u32 = 0x10DE;
    let Ok(factory) = (unsafe { CreateDXGIFactory1::<IDXGIFactory1>() }) else {
        return false;
    };
    let mut index = 0;
    while let Ok(adapter) = unsafe { factory.EnumAdapters1(index) } {
        index += 1;
        let Ok(desc) = (unsafe { adapter.GetDesc1() }) else {
            continue;
        };
        if desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 || desc.VendorId != NVIDIA {
            continue;
        }
        let length = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
        if String::from_utf16_lossy(&desc.Description[..length]).contains("RTX") {
            return true;
        }
    }
    false
}

#[cfg(not(windows))]
pub fn has_rtx_gpu() -> bool {
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn remove_keeps_files_that_are_not_the_pinned_ones() {
        let root = tempfile::tempdir().unwrap();
        fs::write(root.path().join(ADDON_FILE), b"someone else's add-on").unwrap();
        remove(root.path()).unwrap();
        assert!(root.path().join(ADDON_FILE).exists());
        assert!(!installed(root.path()));
    }

    #[test]
    fn pins_github_release_sources() {
        for (_, url, hash) in DLSS_FILES {
            assert!(url.starts_with("https://github.com/OMouta/Unishade/releases/download/"));
            assert_eq!(hash.len(), 64);
        }
    }
}
