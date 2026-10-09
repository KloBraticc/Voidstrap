use std::{
    env,
    fs::File,
    io::{self, Read, Seek, SeekFrom},
};

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args_os().collect();
    if args.len() != 3 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "expected an ISO input path and a disk image output path",
        )
        .into());
    }

    let mut input = File::open(&args[1])?;
    let input_length = input.metadata()?.len();
    let mut signature = [0; 6];
    if input_length < 32774 || input_length % 2048 != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "input is not a complete ISO 9660 image",
        )
        .into());
    }
    input.seek(SeekFrom::Start(32768))?;
    input.read_exact(&mut signature)?;
    if &signature != b"\x01CD001" {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "input is not a complete ISO 9660 image",
        )
        .into());
    }

    udif::writer::create_from_file(&args[2], &args[1], "Voidstrap")?;
    let mut output = File::open(&args[2])?;
    let output_length = output.metadata()?.len();
    let mut trailer_signature = [0; 4];
    if output_length < 512 {
        return Err(
            io::Error::new(io::ErrorKind::InvalidData, "disk image verification failed").into(),
        );
    }
    output.seek(SeekFrom::Start(output_length - 512))?;
    output.read_exact(&mut trailer_signature)?;
    if &trailer_signature != b"koly" {
        return Err(
            io::Error::new(io::ErrorKind::InvalidData, "disk image verification failed").into(),
        );
    }

    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
