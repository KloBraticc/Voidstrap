use std::env;
use std::error::Error;
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
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

    repair_koly_trailer(&args[2])?;

    Ok(())
}

fn repair_koly_trailer(path: &str) -> Result<(), Box<dyn Error>> {
    let mut file = OpenOptions::new().read(true).write(true).open(path)?;
    file.seek(SeekFrom::End(-512))?;
    let mut trailer = [0u8; 512];
    file.read_exact(&mut trailer)?;
    if &trailer[..4] != b"koly" {
        return Err("the disk image trailer is invalid".into());
    }

    let mut corrected = [0u8; 512];
    corrected[..232].copy_from_slice(&trailer[..232]);
    corrected[352..500].copy_from_slice(&trailer[296..444]);
    file.seek(SeekFrom::End(-512))?;
    file.write_all(&corrected)?;
    file.flush()?;

    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
