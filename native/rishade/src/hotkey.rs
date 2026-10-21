use std::fmt;

pub const MOD_ALT: u32 = 0x0001;
pub const MOD_CONTROL: u32 = 0x0002;
pub const MOD_SHIFT: u32 = 0x0004;
pub const MOD_WIN: u32 = 0x0008;
pub const MOD_NOREPEAT: u32 = 0x4000;

#[allow(dead_code)]
pub const VK_TAB: u32 = 0x09;
#[allow(dead_code)]
pub const VK_PAUSE: u32 = 0x13;
#[allow(dead_code)]
pub const VK_SCROLL: u32 = 0x91;
#[allow(dead_code)]
pub const VK_ESCAPE: u32 = 0x1B;
#[allow(dead_code)]
pub const VK_SPACE: u32 = 0x20;
#[allow(dead_code)]
pub const VK_PRIOR: u32 = 0x21;
#[allow(dead_code)]
pub const VK_NEXT: u32 = 0x22;
#[allow(dead_code)]
pub const VK_END: u32 = 0x23;
#[allow(dead_code)]
pub const VK_HOME: u32 = 0x24;
#[allow(dead_code)]
pub const VK_INSERT: u32 = 0x2D;
#[allow(dead_code)]
pub const VK_DELETE: u32 = 0x2E;
#[allow(dead_code)]
pub const VK_NUMPAD0: u32 = 0x60;
#[allow(dead_code)]
pub const VK_NUMPAD1: u32 = 0x61;
#[allow(dead_code)]
pub const VK_NUMPAD2: u32 = 0x62;
#[allow(dead_code)]
pub const VK_NUMPAD3: u32 = 0x63;
#[allow(dead_code)]
pub const VK_NUMPAD4: u32 = 0x64;
#[allow(dead_code)]
pub const VK_NUMPAD5: u32 = 0x65;
#[allow(dead_code)]
pub const VK_NUMPAD6: u32 = 0x66;
#[allow(dead_code)]
pub const VK_NUMPAD7: u32 = 0x67;
#[allow(dead_code)]
pub const VK_NUMPAD8: u32 = 0x68;
#[allow(dead_code)]
pub const VK_NUMPAD9: u32 = 0x69;
#[allow(dead_code)]
pub const VK_MULTIPLY: u32 = 0x6A;
#[allow(dead_code)]
pub const VK_ADD: u32 = 0x6B;
#[allow(dead_code)]
pub const VK_SUBTRACT: u32 = 0x6D;
#[allow(dead_code)]
pub const VK_DECIMAL: u32 = 0x6E;
#[allow(dead_code)]
pub const VK_DIVIDE: u32 = 0x6F;
#[allow(dead_code)]
pub const VK_F1: u32 = 0x70;
#[allow(dead_code)]
pub const VK_F8: u32 = 0x77;
#[allow(dead_code)]
pub const VK_F12: u32 = 0x7B;
#[allow(dead_code)]
pub const VK_F24: u32 = 0x87;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Hotkey {
    pub modifiers: u32,
    pub key: u32,
}

impl Default for Hotkey {
    fn default() -> Self {
        Self {
            modifiers: MOD_NOREPEAT,
            key: 0,
        }
    }
}

pub struct NamedKey {
    pub name: &'static str,
    pub key: u32,
}

pub const NAMED_KEYS: &[NamedKey] = &[
    NamedKey {
        name: "Home",
        key: VK_HOME,
    },
    NamedKey {
        name: "End",
        key: VK_END,
    },
    NamedKey {
        name: "Insert",
        key: VK_INSERT,
    },
    NamedKey {
        name: "Delete",
        key: VK_DELETE,
    },
    NamedKey {
        name: "PageUp",
        key: VK_PRIOR,
    },
    NamedKey {
        name: "PageDown",
        key: VK_NEXT,
    },
    NamedKey {
        name: "Pause",
        key: VK_PAUSE,
    },
    NamedKey {
        name: "ScrollLock",
        key: VK_SCROLL,
    },
    NamedKey {
        name: "Space",
        key: VK_SPACE,
    },
    NamedKey {
        name: "Tab",
        key: VK_TAB,
    },
    NamedKey {
        name: "Escape",
        key: VK_ESCAPE,
    },
    NamedKey {
        name: "Numpad0",
        key: VK_NUMPAD0,
    },
    NamedKey {
        name: "Numpad1",
        key: VK_NUMPAD1,
    },
    NamedKey {
        name: "Numpad2",
        key: VK_NUMPAD2,
    },
    NamedKey {
        name: "Numpad3",
        key: VK_NUMPAD3,
    },
    NamedKey {
        name: "Numpad4",
        key: VK_NUMPAD4,
    },
    NamedKey {
        name: "Numpad5",
        key: VK_NUMPAD5,
    },
    NamedKey {
        name: "Numpad6",
        key: VK_NUMPAD6,
    },
    NamedKey {
        name: "Numpad7",
        key: VK_NUMPAD7,
    },
    NamedKey {
        name: "Numpad8",
        key: VK_NUMPAD8,
    },
    NamedKey {
        name: "Numpad9",
        key: VK_NUMPAD9,
    },
    NamedKey {
        name: "NumpadMultiply",
        key: VK_MULTIPLY,
    },
    NamedKey {
        name: "NumpadAdd",
        key: VK_ADD,
    },
    NamedKey {
        name: "NumpadSubtract",
        key: VK_SUBTRACT,
    },
    NamedKey {
        name: "NumpadDecimal",
        key: VK_DECIMAL,
    },
    NamedKey {
        name: "NumpadDivide",
        key: VK_DIVIDE,
    },
];

fn modifier_bit(name: &str) -> u32 {
    if name.eq_ignore_ascii_case("Ctrl") {
        MOD_CONTROL
    } else if name.eq_ignore_ascii_case("Alt") {
        MOD_ALT
    } else if name.eq_ignore_ascii_case("Shift") {
        MOD_SHIFT
    } else if name.eq_ignore_ascii_case("Win") {
        MOD_WIN
    } else {
        0
    }
}

fn split_hotkey_text(text: &str) -> Option<(u32, &str)> {
    let mut bits = 0;
    let mut key_name = None;
    let mut remaining = text;

    while !remaining.is_empty() {
        let (token, rest) = match remaining.find('+') {
            Some(idx) => (&remaining[..idx], &remaining[idx + 1..]),
            None => (remaining, ""),
        };
        let token = token.trim();
        if token.is_empty() {
            return None;
        }

        let bit = modifier_bit(token);
        if bit != 0 {
            if key_name.is_some() || (bits & bit) != 0 {
                return None;
            }
            bits |= bit;
        } else {
            if key_name.is_some() {
                return None;
            }
            key_name = Some(token);
        }

        remaining = rest;
        if remaining.is_empty() && rest.is_empty() && text.ends_with('+') {
            return None;
        }
    }

    let key = key_name?;
    Some((bits, key))
}

fn parse_function_key(name: &str) -> Option<u32> {
    if name.len() < 2 || name.len() > 3 {
        return None;
    }
    let bytes = name.as_bytes();
    if bytes[0] != b'F' && bytes[0] != b'f' {
        return None;
    }
    let num_str = &name[1..];
    if !num_str.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    let number: u32 = num_str.parse().ok()?;
    if (1..=24).contains(&number) {
        Some(number)
    } else {
        None
    }
}

fn parse_char_key(name: &str) -> Option<u32> {
    if name.len() != 1 {
        return None;
    }
    let c = name.chars().next()?;
    if c.is_ascii_alphanumeric() {
        Some(c.to_ascii_uppercase() as u32)
    } else {
        None
    }
}

pub fn parse_hotkey(text: &str) -> Option<Hotkey> {
    if !text.is_ascii() {
        return None;
    }

    let (modifiers, name) = split_hotkey_text(text)?;

    let mut key = 0;
    if let Some(c) = parse_char_key(name) {
        key = c;
    } else if let Some(number) = parse_function_key(name) {
        if number == 12 {
            return None;
        }
        key = VK_F1 + number - 1;
    } else {
        for named in NAMED_KEYS {
            if name.eq_ignore_ascii_case(named.name) {
                key = named.key;
                break;
            }
        }
    }

    if key == 0 {
        return None;
    }

    Some(Hotkey {
        modifiers: MOD_NOREPEAT | modifiers,
        key,
    })
}

pub fn format_hotkey(hotkey: &Hotkey) -> String {
    let key_str = if (hotkey.key >= b'A' as u32 && hotkey.key <= b'Z' as u32)
        || (hotkey.key >= b'0' as u32 && hotkey.key <= b'9' as u32)
    {
        (hotkey.key as u8 as char).to_string()
    } else if hotkey.key >= VK_F1 && hotkey.key <= VK_F24 && hotkey.key != VK_F12 {
        format!("F{}", hotkey.key - VK_F1 + 1)
    } else {
        match NAMED_KEYS.iter().find(|named| named.key == hotkey.key) {
            Some(named) => named.name.to_string(),
            None => return String::new(),
        }
    };

    let mut result = String::new();
    if (hotkey.modifiers & MOD_CONTROL) != 0 {
        result.push_str("Ctrl+");
    }
    if (hotkey.modifiers & MOD_ALT) != 0 {
        result.push_str("Alt+");
    }
    if (hotkey.modifiers & MOD_SHIFT) != 0 {
        result.push_str("Shift+");
    }
    if (hotkey.modifiers & MOD_WIN) != 0 {
        result.push_str("Win+");
    }
    result.push_str(&key_str);
    result
}

impl fmt::Display for Hotkey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", format_hotkey(self))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_valid_unishade_shortcuts() {
        let valid = [
            ("Ctrl+Home", MOD_CONTROL, VK_HOME),
            ("  cTrL + sHiFt + f8  ", MOD_CONTROL | MOD_SHIFT, VK_F8),
            ("F1", 0, VK_F1),
            ("F24", 0, VK_F24),
            ("Alt+0", MOD_ALT, b'0' as u32),
            ("Win+Shift+P", MOD_WIN | MOD_SHIFT, b'P' as u32),
            ("PageDown", 0, VK_NEXT),
            ("ScrollLock", 0, VK_SCROLL),
            ("Numpad0", 0, VK_NUMPAD0),
            ("Ctrl+numpad9", MOD_CONTROL, VK_NUMPAD9),
            ("NumpadMultiply", 0, VK_MULTIPLY),
            ("Alt+NumpadAdd", MOD_ALT, VK_ADD),
            ("NumpadSubtract", 0, VK_SUBTRACT),
            ("NumpadDecimal", 0, VK_DECIMAL),
            ("Shift+NumpadDivide", MOD_SHIFT, VK_DIVIDE),
        ];

        for (text, mods, key) in valid {
            let res = parse_hotkey(text).unwrap_or_else(|| panic!("failed to parse: {text}"));
            assert_eq!(
                res.modifiers,
                mods | MOD_NOREPEAT,
                "mismatched modifiers for: {text}"
            );
            assert_eq!(res.key, key, "mismatched key for: {text}");
        }
    }

    #[test]
    fn rejects_invalid_shortcuts() {
        let invalid = [
            "",
            " ",
            "Ctrl",
            "Ctrl+",
            "+Home",
            "Ctrl++Home",
            "Ctrl+Ctrl+Home",
            "Home+Ctrl",
            "Home+End",
            "F0",
            "F25",
            "F12",
            "F-1",
            "F1x",
            "Unknown",
            "Ctrl+Mouse1",
            "Numpad",
            "Numpad10",
            "NumpadEnter",
            "Numpad+5",
        ];

        for text in invalid {
            assert!(
                parse_hotkey(text).is_none(),
                "expected rejection for: {text}"
            );
        }
    }

    #[test]
    fn round_trips_unishade_formatted_shortcuts() {
        let round_trip = [
            ("home", "Home"),
            ("shift+ctrl+f8", "Ctrl+Shift+F8"),
            ("Win+Alt+pagedown", "Alt+Win+PageDown"),
            ("F24", "F24"),
            ("Ctrl+7", "Ctrl+7"),
            ("q", "Q"),
            ("numpad5", "Numpad5"),
            ("shift+NUMPADDECIMAL", "Shift+NumpadDecimal"),
            ("Ctrl+NumpadDivide", "Ctrl+NumpadDivide"),
        ];

        for (input, expected) in round_trip {
            let parsed = parse_hotkey(input).unwrap();
            assert_eq!(format_hotkey(&parsed), expected);
        }
    }

    #[test]
    fn every_named_key_round_trips() {
        for named in NAMED_KEYS {
            let hotkey = Hotkey {
                modifiers: MOD_NOREPEAT,
                key: named.key,
            };
            assert_eq!(format_hotkey(&hotkey), named.name);
            let parsed = parse_hotkey(named.name).unwrap();
            assert_eq!(parsed.key, named.key);
        }
    }

    #[test]
    fn rejects_unsupported_formats() {
        assert!(
            format_hotkey(&Hotkey {
                modifiers: MOD_CONTROL,
                key: VK_F12
            })
            .is_empty()
        );
        assert!(
            format_hotkey(&Hotkey {
                modifiers: MOD_CONTROL,
                key: 0x01
            })
            .is_empty()
        );
        assert!(format_hotkey(&Hotkey::default()).is_empty());
    }
}
