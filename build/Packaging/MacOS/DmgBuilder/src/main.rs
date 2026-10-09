use std::env;
use std::error::Error;
use std::fs;
use std::io::Cursor;
use udif::{CompressionMethod, DmgArchive, DmgWriter};

fn run() -> Result<(), Box<dyn Error>> {
    let args: Vec<String> = env::args().collect();
    if args.len() != 3 {
        return Err("input and output paths are required".into());
    }

    let input = fs::read(&args[1])?;
    let mut writer = DmgWriter::create(&args[2])?
        .compression(CompressionMethod::Raw)
        .chunk_size(input.len());
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
    let image = fs::read(path)?;
    let trailer_start = image
        .len()
        .checked_sub(512)
        .ok_or("the disk image is too short")?;
    let trailer = &image[trailer_start..];
    if &trailer[..4] != b"koly" {
        return Err("the disk image trailer is invalid".into());
    }

    let plist_offset = u64::from_be_bytes(trailer[216..224].try_into()?) as usize;
    let plist_length = u64::from_be_bytes(trailer[224..232].try_into()?) as usize;
    let plist_end = plist_offset
        .checked_add(plist_length)
        .ok_or("the disk image property list is invalid")?;
    if plist_end > trailer_start {
        return Err("the disk image property list is invalid".into());
    }

    let mut property_list =
        plist::Value::from_reader_xml(Cursor::new(&image[plist_offset..plist_end]))?;
    let resource_fork = property_list
        .as_dictionary_mut()
        .and_then(|root| root.get_mut("resource-fork"))
        .and_then(plist::Value::as_dictionary_mut)
        .ok_or("the disk image block map is invalid")?;
    let block_maps = resource_fork
        .get_mut("blkx")
        .and_then(plist::Value::as_array_mut)
        .ok_or("the disk image block map is invalid")?;
    for block_map in block_maps {
        let data = block_map
            .as_dictionary_mut()
            .and_then(|entry| entry.get_mut("Data"))
            .and_then(|value| match value {
                plist::Value::Data(data) => Some(data),
                _ => None,
            })
            .ok_or("the disk image block map is invalid")?;
        if data.len() < 40 {
            return Err("the disk image block map is truncated".into());
        }
        data[32..36].copy_from_slice(&2056u32.to_be_bytes());
        data[36..40].copy_from_slice(&0u32.to_be_bytes());
    }
    resource_fork.insert("plst".to_string(), plist::Value::Array(Vec::new()));

    let mut serialized_property_list = Vec::new();
    plist::to_writer_xml(&mut serialized_property_list, &property_list)?;
    let mut corrected = [0u8; 512];
    corrected[..232].copy_from_slice(&trailer[..232]);
    corrected[56..64].fill(0);
    corrected[224..232].copy_from_slice(&(serialized_property_list.len() as u64).to_be_bytes());
    corrected[352..500].copy_from_slice(&trailer[296..444]);
    let mut output = Vec::with_capacity(plist_offset + serialized_property_list.len() + 512);
    output.extend_from_slice(&image[..plist_offset]);
    output.extend_from_slice(&serialized_property_list);
    output.extend_from_slice(&corrected);
    fs::write(path, output)?;

    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
