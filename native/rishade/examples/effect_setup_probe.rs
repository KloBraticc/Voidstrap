use std::path::PathBuf;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let directory = std::env::args_os()
        .nth(1)
        .map(PathBuf::from)
        .ok_or("pass an isolated output directory")?;
    let summary = rishade_core::install_effects_and_presets(&directory)?;
    println!("{}", summary.describe());
    Ok(())
}
