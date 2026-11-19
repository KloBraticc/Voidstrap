use std::{env, path::PathBuf, process::Command};

const SOURCE: &str = "virtualdisplay/src/virtualdisplay.c";

fn run(command: &mut Command) {
    let status = command
        .status()
        .expect("xcrun is required to build the virtual display");
    assert!(status.success(), "{command:?} failed with {status}");
}

fn main() {
    println!("cargo:rerun-if-changed={SOURCE}");
    println!("cargo:rerun-if-changed=virtualdisplay/src/virtualdisplay.h");
    println!("cargo:rerun-if-env-changed=MACOSX_DEPLOYMENT_TARGET");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("macos") {
        return;
    }
    let output = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR"));
    let root = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR"));
    let arch = match env::var("CARGO_CFG_TARGET_ARCH").as_deref() {
        Ok("aarch64") => "arm64",
        _ => "x86_64",
    };
    let object = output.join("virtualdisplay.o");
    let library = output.join("libvirtualdisplay.a");
    let mut clang = Command::new("xcrun");
    clang
        .args([
            "clang", "-c", "-std=c11", "-O3", "-fblocks", "-Wall", "-Wextra", "-Werror", "-arch",
            arch,
        ])
        .arg(format!("-ffile-prefix-map={}=/src", root.display()));
    if let Ok(target) = env::var("MACOSX_DEPLOYMENT_TARGET") {
        clang.arg(format!("-mmacosx-version-min={target}"));
    }
    run(clang.arg(root.join(SOURCE)).arg("-o").arg(&object));
    let _ = std::fs::remove_file(&library);
    run(Command::new("xcrun")
        .args(["libtool", "-static", "-o"])
        .arg(&library)
        .arg(&object));
    println!("cargo:rustc-link-search=native={}", output.display());
    println!("cargo:rustc-link-lib=static=virtualdisplay");
    println!("cargo:rustc-link-lib=framework=CoreGraphics");
    println!("cargo:rustc-link-lib=framework=Foundation");
    println!("cargo:rustc-link-lib=objc");
}
