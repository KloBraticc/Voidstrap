#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Config {
    pub size: Option<(u32, u32)>,
    pub refresh: u32,
    pub watch_stdin: bool,
    pub probe: bool,
}

impl Config {
    pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Option<Self>, String> {
        let mut result = Self {
            size: None,
            refresh: 240,
            watch_stdin: false,
            probe: false,
        };
        let mut width = None;
        let mut height = None;
        let mut refresh_seen = false;
        let mut args = args.into_iter();
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--help" if args.next().is_none() => return Ok(None),
                "--no-menu" => {}
                "--watch-stdin" if !result.watch_stdin => result.watch_stdin = true,
                "--probe" if !result.probe => result.probe = true,
                "--width" | "--height" | "--refresh-rate" => {
                    let value = args
                        .next()
                        .ok_or_else(|| format!("Missing value for {arg}"))?
                        .parse::<u32>()
                        .map_err(|_| format!("Invalid value for {arg}"))?;
                    match arg.as_str() {
                        "--width" if width.is_none() && (320..=8192).contains(&value) => {
                            width = Some(value)
                        }
                        "--height" if height.is_none() && (200..=8192).contains(&value) => {
                            height = Some(value)
                        }
                        "--refresh-rate" if !refresh_seen && (30..=480).contains(&value) => {
                            result.refresh = value;
                            refresh_seen = true;
                        }
                        _ => return Err(format!("Invalid or repeated option: {arg}")),
                    }
                }
                _ => return Err(format!("Unknown or repeated option: {arg}")),
            }
        }
        result.size = match (width, height) {
            (None, None) => None,
            (Some(w), Some(h)) => Some((w, h)),
            _ => return Err("Width and height must be supplied together".into()),
        };
        if result.probe && (result.size.is_some() || refresh_seen || result.watch_stdin) {
            return Err("Probe cannot be combined with display options".into());
        }
        Ok(Some(result))
    }
}
