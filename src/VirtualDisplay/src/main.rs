mod config;
#[cfg(target_os = "macos")]
mod macos;

fn main() -> std::process::ExitCode {
    let result = match config::Config::parse(std::env::args().skip(1)) {
        Ok(None) => {
            println!(
                "Usage: voidstrap-virtualdisplay [--width pixels --height pixels] [--refresh-rate hz] [--watch-stdin] [--probe]"
            );
            return std::process::ExitCode::SUCCESS;
        }
        Ok(Some(config)) => run(config),
        Err(error) => Err(error),
    };
    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("Virtual display: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}

#[cfg(target_os = "macos")]
fn run(config: config::Config) -> Result<(), String> {
    macos::run(config)
}

#[cfg(not(target_os = "macos"))]
fn run(_: config::Config) -> Result<(), String> {
    Err("macOS is required".into())
}
