use std::env;
use std::error::Error;
use std::fs;
use udif::{CompressionMethod, DmgArchive, DmgWriter};

fn run() -> Result<(), Box<dyn Error>> {
    let args: Vec<String> = env::args().collect();
    if args.len() != 3 {
        return Err("input and output paths are required".into());
    }

    let input = fs::read(&args[1])?;
    let mut writer = DmgWriter::create(&args[2])?.compression(CompressionMethod::Zlib);
    writer.add_partition("CD_ROM_XA", &input)?;
    writer.finish()?;

    let mut archive = DmgArchive::open(&args[2])?;
    if archive.extract_main_partition()? != input {
        return Err("the disk image verification failed".into());
    }

    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
