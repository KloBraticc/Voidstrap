use crate::transport::Stream;

pub fn connect(pipe: i32) -> Option<(Stream, i32)> {
    for (path, index) in candidates(pipe) {
        if let Some(stream) = Stream::connect(&path) {
            return Some((stream, index));
        }
    }
    None
}

pub fn find_pipe() -> i32 {
    for (path, index) in candidates(-1) {
        if Stream::probe(&path) {
            return index;
        }
    }
    -1
}

#[cfg(windows)]
fn candidates(pipe: i32) -> Vec<(String, i32)> {
    (0..10)
        .filter(|index| pipe < 0 || *index == pipe)
        .map(|index| (format!(r"\\.\pipe\discord-ipc-{index}"), index))
        .collect()
}

#[cfg(unix)]
fn candidates(pipe: i32) -> Vec<(String, i32)> {
    let mut result = Vec::new();
    for directory in directories() {
        for index in 0..10 {
            if pipe >= 0 && index != pipe {
                continue;
            }
            let path = directory.join(format!("discord-ipc-{index}"));
            if path.exists() {
                result.push((path.to_string_lossy().into_owned(), index));
            }
        }
    }
    result
}

#[cfg(unix)]
fn directories() -> Vec<std::path::PathBuf> {
    use std::path::PathBuf;
    let mut roots: Vec<PathBuf> = ["XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"]
        .iter()
        .filter_map(std::env::var_os)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .collect();
    roots.push(std::env::temp_dir());
    roots.push(PathBuf::from("/tmp"));
    let mut directories: Vec<PathBuf> = Vec::new();
    let add = |directory: PathBuf, directories: &mut Vec<PathBuf>| {
        if directory.is_dir() && !directories.contains(&directory) {
            directories.push(directory);
        }
    };
    for root in &roots {
        add(root.clone(), &mut directories);
        add(root.join("app/com.discordapp.Discord"), &mut directories);
        add(root.join("snap.discord"), &mut directories);
    }
    for root in &roots {
        for (parent, suffix) in [("app", ""), (".flatpak", "xdg-run")] {
            for child in sorted_children(&root.join(parent), |_| true) {
                add(if suffix.is_empty() { child } else { child.join(suffix) }, &mut directories);
            }
        }
        for child in sorted_children(root, |name| name.starts_with("snap.")) {
            add(child, &mut directories);
        }
    }
    directories
}

#[cfg(unix)]
fn sorted_children(directory: &std::path::Path, keep: impl Fn(&str) -> bool) -> Vec<std::path::PathBuf> {
    let mut children: Vec<std::path::PathBuf> = match std::fs::read_dir(directory) {
        Ok(entries) => entries
            .flatten()
            .filter(|entry| entry.file_type().map(|kind| kind.is_dir()).unwrap_or(false))
            .filter(|entry| keep(&entry.file_name().to_string_lossy()))
            .map(|entry| entry.path())
            .collect(),
        Err(_) => Vec::new(),
    };
    children.sort();
    children
}
