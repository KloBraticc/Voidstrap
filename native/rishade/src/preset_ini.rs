use std::fmt;

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PresetIni {
    sections: Vec<Section>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct Section {
    name: String,
    values: Vec<(String, String)>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PresetIniError {
    UnsafeValue,
}

impl fmt::Display for PresetIniError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::UnsafeValue => {
                formatter.write_str("preset values cannot contain control characters")
            }
        }
    }
}

impl std::error::Error for PresetIniError {}

impl PresetIni {
    pub fn parse(text: &str) -> Self {
        let mut preset = Self {
            sections: vec![Section::default()],
        };
        let mut current = 0;
        for line in text.trim_start_matches('\u{feff}').lines() {
            let line = line.trim_matches(['\r', ' ', '\t']);
            let Some(first) = line.find(|c| c != ' ' && c != '\t') else {
                continue;
            };
            let line = &line[first..];
            if line.starts_with(';') || line.starts_with('#') {
                continue;
            }
            if line.starts_with('[') && line.ends_with(']') {
                let name = &line[1..line.len() - 1];
                current = preset.find_section(name).unwrap_or_else(|| {
                    preset.sections.push(Section {
                        name: name.to_owned(),
                        values: Vec::new(),
                    });
                    preset.sections.len() - 1
                });
                continue;
            }
            let Some((key, value)) = line.split_once('=') else {
                continue;
            };
            let key = key.trim_end_matches([' ', '\t']);
            let value = value.trim_start_matches([' ', '\t']);
            if key.is_empty() {
                continue;
            }
            preset.set_at(current, key, value);
        }
        preset
    }

    pub fn get(&self, section: &str, key: &str) -> Option<&str> {
        let section = self.find_section(section)?;
        self.sections[section]
            .values
            .iter()
            .find(|(name, _)| same(name, key))
            .map(|(_, value)| value.as_str())
    }

    pub fn has_section(&self, section: &str) -> bool {
        self.find_section(section).is_some()
    }

    pub fn section_names(&self) -> impl Iterator<Item = &str> {
        self.sections
            .iter()
            .filter(|section| !section.name.is_empty())
            .map(|section| section.name.as_str())
    }

    pub fn set(&mut self, section: &str, key: &str, value: &str) -> Result<(), PresetIniError> {
        if [section, key, value]
            .iter()
            .any(|part| part.chars().any(char::is_control))
        {
            return Err(PresetIniError::UnsafeValue);
        }
        let index = self.find_section(section).unwrap_or_else(|| {
            self.sections.push(Section {
                name: section.to_owned(),
                values: Vec::new(),
            });
            self.sections.len() - 1
        });
        self.set_at(index, key, value);
        Ok(())
    }

    pub fn remove(&mut self, section: &str, key: &str) {
        if let Some(index) = self.find_section(section) {
            self.sections[index]
                .values
                .retain(|(name, _)| !same(name, key));
        }
    }

    pub fn text(&self) -> String {
        let mut text = String::new();
        for section in &self.sections {
            if !section.name.is_empty() {
                if section.values.is_empty() {
                    continue;
                }
                if !text.is_empty() {
                    text.push('\n');
                }
                text.push('[');
                text.push_str(&section.name);
                text.push_str("]\n");
            }
            for (key, value) in &section.values {
                text.push_str(key);
                text.push('=');
                text.push_str(value);
                text.push('\n');
            }
        }
        text
    }

    pub fn split(value: &str) -> impl Iterator<Item = &str> {
        value.split(',').filter(|item| !item.is_empty())
    }

    fn find_section(&self, name: &str) -> Option<usize> {
        self.sections
            .iter()
            .position(|section| same(&section.name, name))
    }

    fn set_at(&mut self, section: usize, key: &str, value: &str) {
        if let Some((_, current)) = self.sections[section]
            .values
            .iter_mut()
            .find(|(name, _)| same(name, key))
        {
            *current = value.to_owned();
        } else {
            self.sections[section]
                .values
                .push((key.to_owned(), value.to_owned()));
        }
    }
}

pub fn preset_effect_files(preset: &PresetIni) -> Vec<String> {
    let mut files = Vec::new();
    for technique in PresetIni::split(preset.get("", "Techniques").unwrap_or_default()) {
        let Some((_, file)) = technique.split_once('@') else {
            continue;
        };
        let file = file.trim_end_matches([' ', '\t']);
        if !file.is_empty() && !files.iter().any(|known: &String| same(known, file)) {
            files.push(file.to_owned());
        }
    }
    files
}

fn same(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_and_saves_ordered_sections_and_keys() {
        let mut preset = PresetIni::parse(
            "\u{feff}Techniques=A@first.fx, B@second.fx\r\n[Second.fx]\r\nEnabled=1\r\n[first.FX]\r\nEnabled=0\r\n",
        );
        assert_eq!(
            preset.get("", "techniques"),
            Some("A@first.fx, B@second.fx")
        );
        assert_eq!(
            preset.section_names().collect::<Vec<_>>(),
            ["Second.fx", "first.FX"]
        );
        preset.set("FIRST.fx", "enabled", "1").unwrap();
        assert_eq!(
            preset.text(),
            "Techniques=A@first.fx, B@second.fx\n\n[Second.fx]\nEnabled=1\n\n[first.FX]\nEnabled=1\n"
        );
    }

    #[test]
    fn set_updates_without_reordering_and_remove_is_case_insensitive() {
        let mut preset = PresetIni::parse("A=1\nB=2\n[Effects]\nX=1\nY=2\n");
        preset.set("", "a", "3").unwrap();
        preset.remove("effects", "x");
        assert_eq!(preset.text(), "A=3\nB=2\n\n[Effects]\nY=2\n");
    }

    #[test]
    fn rejects_control_characters_and_extracts_unique_effect_files() {
        let mut preset = PresetIni::default();
        assert_eq!(
            preset.set("", "Techniques", "one\n[Injected]"),
            Err(PresetIniError::UnsafeValue)
        );
        preset
            .set("", "Techniques", "A@a.fx,B@B.fx,C@A.FX,Invalid")
            .unwrap();
        assert_eq!(preset_effect_files(&preset), ["a.fx", "B.fx"]);
    }
}
