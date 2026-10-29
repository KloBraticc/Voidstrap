use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

fn main() {
    println!("cargo:rerun-if-env-changed=VOIDSTRAP_LAUNCHER_PAYLOAD");
    println!("cargo:rerun-if-env-changed=VOIDSTRAP_VERSION");
    println!("cargo:rerun-if-env-changed=VOIDSTRAP_DOTNET_MAJOR");
    println!("cargo:rerun-if-env-changed=VOIDSTRAP_RC");
    println!("cargo:rerun-if-changed=voidstrap.manifest");

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let payload = PathBuf::from(env::var("VOIDSTRAP_LAUNCHER_PAYLOAD").expect("VOIDSTRAP_LAUNCHER_PAYLOAD must point at the published Voidstrap.exe"));
    println!("cargo:rerun-if-changed={}", payload.display());
    let version = env::var("VOIDSTRAP_VERSION").unwrap_or_else(|_| "0.0.0.0".into());
    let major = env::var("VOIDSTRAP_DOTNET_MAJOR").unwrap_or_else(|_| "10".into());

    let payload_bytes = fs::read(&payload).expect("the launcher payload could not be read");
    let uncompressed_len = payload_bytes.len();
    let hash = fnv1a(&payload_bytes);
    println!("cargo:rustc-env=VOIDSTRAP_PAYLOAD_DIR={}-{:016x}", version, hash);
    println!("cargo:rustc-env=VOIDSTRAP_DOTNET_MAJOR={}", major);
    println!("cargo:rustc-env=VOIDSTRAP_PAYLOAD_LEN={}", uncompressed_len);

    let compressed = miniz_oxide::deflate::compress_to_vec_zlib(&payload_bytes, 9);
    let compressed_payload = out_dir.join("payload.compressed");
    fs::write(&compressed_payload, &compressed).expect("could not write compressed payload");

    let icon = manifest_dir.join("..").join("..").join("src").join("Voidstrap.App").join("favicon (1).ico");
    let manifest = manifest_dir.join("voidstrap.manifest");
    let mut parts: Vec<String> = version.split('.').map(|part| part.parse::<u16>().unwrap_or(0).to_string()).collect();
    parts.resize(4, "0".into());
    let numeric = parts.join(",");

    let script = format!(
        "1 ICON \"{icon}\"\n\
1 24 \"{manifest}\"\n\
101 RCDATA \"{payload}\"\n\
1 VERSIONINFO\n\
FILEVERSION {numeric}\n\
PRODUCTVERSION {numeric}\n\
FILEFLAGSMASK 0x3fL\n\
FILEFLAGS 0x0L\n\
FILEOS 0x40004L\n\
FILETYPE 0x1L\n\
FILESUBTYPE 0x0L\n\
BEGIN\n\
  BLOCK \"StringFileInfo\"\n\
  BEGIN\n\
    BLOCK \"040904b0\"\n\
    BEGIN\n\
      VALUE \"CompanyName\", \"Voidstrap\"\n\
      VALUE \"FileDescription\", \"Voidstrap\"\n\
      VALUE \"FileVersion\", \"{version}\"\n\
      VALUE \"InternalName\", \"Voidstrap\"\n\
      VALUE \"OriginalFilename\", \"Voidstrap.exe\"\n\
      VALUE \"ProductName\", \"Voidstrap\"\n\
      VALUE \"ProductVersion\", \"{version}\"\n\
    END\n\
  END\n\
  BLOCK \"VarFileInfo\"\n\
  BEGIN\n\
    VALUE \"Translation\", 0x409, 1200\n\
  END\n\
END\n",
        icon = rc_path(&icon),
        manifest = rc_path(&manifest),
        payload = rc_path(&compressed_payload),
    );

    let rc_file = out_dir.join("voidlauncher.rc");
    let res_file = out_dir.join("voidlauncher.res");
    fs::write(&rc_file, script).unwrap();
    let status = Command::new(find_rc())
        .arg("/nologo")
        .arg("/fo")
        .arg(&res_file)
        .arg(&rc_file)
        .status()
        .expect("rc.exe could not be started, install the Windows SDK or set VOIDSTRAP_RC");
    assert!(status.success(), "rc.exe failed to compile the launcher resources");
    println!("cargo:rustc-link-arg-bins={}", res_file.display());
}

fn rc_path(path: &Path) -> String {
    fs::canonicalize(path)
        .unwrap_or_else(|_| path.to_path_buf())
        .display()
        .to_string()
        .trim_start_matches(r"\\?\")
        .replace('\\', "\\\\")
}

fn fnv1a(buffer: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf29ce484222325;
    for byte in buffer {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(0x100000001b3);
    }
    hash
}

fn find_rc() -> PathBuf {
    if let Ok(rc) = env::var("VOIDSTRAP_RC") {
        return PathBuf::from(rc);
    }
    let program_files = env::var("ProgramFiles(x86)").unwrap_or_else(|_| r"C:\Program Files (x86)".into());
    let bin = Path::new(&program_files).join("Windows Kits").join("10").join("bin");
    let mut kits: Vec<PathBuf> = fs::read_dir(&bin)
        .map(|entries| entries.filter_map(|entry| entry.ok().map(|entry| entry.path())).collect())
        .unwrap_or_default();
    kits.sort();
    kits.into_iter()
        .rev()
        .map(|kit| kit.join("x64").join("rc.exe"))
        .find(|rc| rc.exists())
        .unwrap_or_else(|| PathBuf::from("rc.exe"))
}
