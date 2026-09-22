use crate::java;

const BLOCKED: &[&str] = &[
    "dex", "so", "jar", "apk", "apks", "xapk", "aab", "odex", "vdex", "oat", "lua", "luau", "rbxm", "rbxmx", "js", "sh", "bat", "cmd", "exe", "dll", "ps1", "py",
];

pub const ASSET_EXTENSIONS: &[&str] = &[
    "png", "jpg", "jpeg", "bmp", "tga", "dds", "ktx", "webp", "tex", "ogg", "mp3", "wav", "flac", "mesh", "rbxm", "rbxmx", "rbxl", "rbxlx", "ttf", "otf", "woff",
    "woff2", "font", "fontfamily", "json", "xml", "txt", "csv", "md", "dat", "bin", "ini", "cfg", "hlsl", "glsl", "fx", "pack", "idx", "mp4", "webm", "gif",
];

pub const DANGEROUS: &[&str] = &[
    "exe", "dll", "sys", "drv", "com", "scr", "cpl", "msi", "msix", "msp", "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta",
    "reg", "lnk", "url", "scf", "inf", "jar", "py", "pyc", "sh", "apk", "app", "so", "dylib", "ocx", "ax", "efi", "iso", "img", "vhd", "vhdx", "lua", "luau",
    "rbxs", "dmp", "pif", "gadget", "application", "dex",
];

const BLOCKED_ROOTS: &[&str] = &["ssl", "clientsettings", "webview2", "webview2runtimeinstaller"];
const RESERVED: &[&str] = &[
    "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7",
    "lpt8", "lpt9",
];
const VERSION_LOCKED: &[&str] = &["content/configs/", "extracontent/models/", "extracontent/translations/"];
const PACKAGE_ART: &[&str] = &[".png", ".jpg", ".jpeg", ".dds", ".ktx", ".webp", ".ttf", ".otf"];

pub fn extension(name: &str) -> String {
    match name.rfind('.') {
        Some(dot) => java::lower(&name[dot + 1..]),
        None => String::new(),
    }
}

pub fn blocked(name: &str) -> bool {
    BLOCKED.contains(&extension(name).as_str())
}

pub fn arch_normalize(name: &str) -> String {
    let mut n = name.replace('\\', "/");
    while let Some(rest) = n.strip_prefix("./") {
        n = rest.to_string();
    }
    n
}

pub fn inspect_path(relative: &str, client_copy: &mut dyn FnMut(&str) -> bool) -> Option<String> {
    let n = arch_normalize(relative);
    if java::trim(&n).is_empty() {
        return Some("The package contains an unnamed file.".into());
    }
    if n.contains("..") {
        return Some("The package tries to escape its folder with a relative path.".into());
    }
    if n.starts_with('/') || n.contains(':') {
        return Some("The package contains an absolute path.".into());
    }
    for seg in n.split('/') {
        if seg.is_empty() {
            continue;
        }
        let trimmed = seg.trim_end_matches([' ', '.']);
        if trimmed.is_empty() || trimmed.len() != seg.len() {
            return Some("The package contains an invalid file name.".into());
        }
        if seg.chars().any(|c| (c as u32) < 32 || "<>\"|?*".contains(c)) {
            return Some("The package contains an invalid file name.".into());
        }
        let stem = seg.split('.').next().unwrap_or(seg);
        if RESERVED.contains(&java::lower(stem).as_str()) {
            return Some(format!("The package uses the reserved name {seg}."));
        }
    }
    let root = match n.find('/') {
        Some(slash) if slash > 0 => java::lower(&n[..slash]),
        _ => String::new(),
    };
    if BLOCKED_ROOTS.contains(&root.as_str()) {
        return Some(format!("The package writes into the protected {root} folder."));
    }
    let ext = extension(&n);
    if DANGEROUS.contains(&ext.as_str()) && !client_copy(&n) {
        return Some(format!("The package contains a .{ext} file, which a Roblox mod never needs."));
    }
    None
}

pub fn installable(relative: &str) -> bool {
    ASSET_EXTENSIONS.contains(&extension(relative).as_str())
}

pub fn dangerous(ext: &str) -> bool {
    DANGEROUS.contains(&ext)
}

pub fn ignored(relative: &str) -> bool {
    let n = relative.replace('\\', "/");
    let lower = java::lower(&n);
    if lower.ends_with(".lua") || lower.ends_with(".luau") || lower.ends_with(".lock") {
        return true;
    }
    if lower.starts_with("extracontent/luapackages/") {
        if PACKAGE_ART.iter().any(|e| lower.ends_with(e)) {
            return duplicate(&n);
        }
        return true;
    }
    if VERSION_LOCKED.iter().any(|f| lower.starts_with(f)) {
        return true;
    }
    duplicate(&n)
}

pub fn duplicate(relative: &str) -> bool {
    let mut name = &relative[relative.rfind('/').map_or(0, |i| i + 1)..];
    if let Some(dot) = name.rfind('.')
        && dot > 0
    {
        name = &name[..dot];
    }
    let Some(open) = name.rfind(" (") else {
        return false;
    };
    if open == 0 || !name.ends_with(')') {
        return false;
    }
    let digits = &name[open + 2..name.len() - 1];
    if digits.chars().count() < 1 {
        return false;
    }
    digits.chars().all(|c| java::char_digit(c).is_some())
}

pub fn asset_path(relative: &str) -> Option<String> {
    let n = relative.replace('\\', "/");
    let n = n.trim_start_matches('/');
    let slash = n.find('/')?;
    if slash == 0 {
        return None;
    }
    let (head, rest) = (&n[..slash], &n[slash + 1..]);
    if rest.is_empty() {
        return None;
    }
    if java::eq_ignore_case(head, "content") {
        return Some(format!("assets/content/{rest}"));
    }
    if java::eq_ignore_case(head, "ExtraContent") {
        return Some(format!("assets/ExtraContent/{rest}"));
    }
    if java::eq_ignore_case(head, "PlatformContent") {
        let s2 = rest.find('/')?;
        if s2 == 0 {
            return None;
        }
        let platform = &rest[..s2];
        if !java::eq_ignore_case(platform, "pc") && !java::eq_ignore_case(platform, "android") {
            return None;
        }
        let tail = &rest[s2 + 1..];
        return if tail.is_empty() { None } else { Some(format!("assets/android/{tail}")) };
    }
    None
}

pub fn desktop_path(apk_entry: &str) -> Option<String> {
    if let Some(r) = apk_entry.strip_prefix("assets/content/") {
        return Some(format!("content/{r}"));
    }
    if let Some(r) = apk_entry.strip_prefix("assets/ExtraContent/") {
        return Some(format!("ExtraContent/{r}"));
    }
    apk_entry.strip_prefix("assets/android/").map(|r| format!("PlatformContent/pc/{r}"))
}

pub fn normalize_name(name: Option<&str>, fallback: Option<&str>) -> Option<String> {
    let t = java::trim(name.unwrap_or(""));
    let mut collapsed = String::new();
    let mut in_space = false;
    for c in t.chars() {
        if java::regex_space(c) {
            if !in_space {
                collapsed.push(' ');
            }
            in_space = true;
        } else {
            collapsed.push(c);
            in_space = false;
        }
    }
    let mut sb = String::new();
    let mut units = 0;
    for c in collapsed.chars() {
        if units >= 80 {
            break;
        }
        if !c.is_control() {
            sb.push(c);
            units += c.len_utf16();
        }
    }
    let out = java::trim(&sb);
    if out.is_empty() { fallback.map(str::to_string) } else { Some(out.to_string()) }
}

pub fn valid_id(id: &str) -> bool {
    id.len() == 32 && id.bytes().all(|b| b.is_ascii_hexdigit())
}

pub fn lower_hex32(s: &str) -> bool {
    s.len() == 32 && s.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

pub fn hash_of(file_name: &str) -> Option<String> {
    let mut name = &file_name[file_name.rfind('/').map_or(0, |i| i + 1)..];
    if let Some(dot) = name.rfind('.')
        && dot > 0
    {
        name = &name[..dot];
    }
    let chars: Vec<char> = name.chars().collect();
    if chars.len() < 32 {
        return None;
    }
    for start in (0..=chars.len() - 32).rev() {
        let cand = java::lower(&chars[start..start + 32].iter().collect::<String>());
        if lower_hex32(&cand) {
            return Some(cand);
        }
    }
    None
}

pub fn cache_entry(data: &[u8]) -> bool {
    const HEADER: usize = 37;
    const LENGTH_OFFSET: usize = 25;
    let total = data.len() as u64;
    if data.len() < HEADER || total <= HEADER as u64 || total > 64 * 1024 * 1024 {
        return false;
    }
    if &data[..4] != b"RBXH" {
        return false;
    }
    let declared = u32::from_le_bytes([data[LENGTH_OFFSET], data[LENGTH_OFFSET + 1], data[LENGTH_OFFSET + 2], data[LENGTH_OFFSET + 3]]) as u64;
    declared == total - HEADER as u64
}

pub fn valid_set_name(requested: Option<&str>) -> Option<String> {
    let mut n = java::trim(requested.unwrap_or("")).to_string();
    while let Some(rest) = n.strip_suffix('.') {
        n = java::trim(rest).to_string();
    }
    if n.is_empty() || java::utf16_len(&n) > 64 {
        return None;
    }
    if n.chars().any(|c| c.is_control() || "/\\<>:\"|?*".contains(c)) {
        return None;
    }
    Some(n)
}

pub fn safe_sky_name(n: &str) -> bool {
    !java::trim(n).is_empty() && java::utf16_len(n) <= 128 && n != "." && n != ".." && !n.contains('/') && !n.contains('\\')
}
