//! Bounded JSON input plus a serializer that reproduces Python's `json.dumps` text.
//!
//! Floor scenes, geometry revisions and catalogs were produced by the Python backend. Matching
//! its float `repr`, separators, key order and `ensure_ascii` escaping keeps prepared scenes and
//! cache revisions comparable byte-for-byte with the verified 1.7.17 outputs.
use crate::error::{Error, Result};
use serde_json::{Map, Value};

pub const MAX_DEPTH: usize = 32;

/// Decodes UTF-8 (optionally BOM-prefixed) or BOM-marked UTF-16 text.
pub fn decode_text(bytes: &[u8]) -> Result<String> {
    if let Some(rest) = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
        return String::from_utf8(rest.to_vec()).map_err(|_| Error::msg("The file contains invalid UTF-8 text."));
    }
    let utf16 = |little: bool, rest: &[u8]| -> Result<String> {
        if rest.len() % 2 != 0 {
            return Err(Error::msg("The file contains truncated UTF-16 text."));
        }
        let units: Vec<u16> = rest
            .chunks_exact(2)
            .map(|pair| if little { u16::from_le_bytes([pair[0], pair[1]]) } else { u16::from_be_bytes([pair[0], pair[1]]) })
            .collect();
        String::from_utf16(&units).map_err(|_| Error::msg("The file contains invalid UTF-16 text."))
    };
    if let Some(rest) = bytes.strip_prefix(&[0xFF, 0xFE]) {
        return utf16(true, rest);
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFE, 0xFF]) {
        return utf16(false, rest);
    }
    String::from_utf8(bytes.to_vec()).map_err(|_| Error::msg("The file contains invalid UTF-8 text."))
}

/// Rejects documents nested deeper than `limit` containers before serde allocates them.
pub fn check_depth(text: &str, limit: usize) -> Result<()> {
    let mut depth = 0usize;
    let mut in_string = false;
    let mut escaped = false;
    for byte in text.bytes() {
        if in_string {
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == b'"' {
                in_string = false;
            }
            continue;
        }
        match byte {
            b'"' => in_string = true,
            b'[' | b'{' => {
                depth += 1;
                if depth > limit {
                    return Err(Error::msg(format!("JSON nesting exceeds the {limit}-level limit.")));
                }
            }
            b']' | b'}' => depth = depth.saturating_sub(1),
            _ => {}
        }
    }
    Ok(())
}

/// Parses bounded JSON with finite numbers only (serde rejects NaN/Infinity and overflow).
pub fn parse(bytes: &[u8], max_bytes: usize, max_depth: usize) -> Result<Value> {
    if bytes.len() > max_bytes {
        return Err(Error::msg(format!("The JSON document exceeds the {} size limit.", human_bytes(max_bytes))));
    }
    let text = decode_text(bytes)?;
    check_depth(&text, max_depth)?;
    serde_json::from_str(&text).map_err(|error| Error::msg(format!("Invalid JSON: {error}")))
}

pub fn read_file(path: &std::path::Path, max_bytes: usize, max_depth: usize) -> Result<Value> {
    let bytes = crate::files::read_bounded(path, max_bytes as u64)?;
    parse(&bytes, max_bytes, max_depth)
}

pub fn human_bytes(bytes: usize) -> String {
    if bytes >= 1024 * 1024 { format!("{} MiB", bytes / (1024 * 1024)) } else { format!("{} KiB", bytes / 1024) }
}

/// Python `float.__repr__`: shortest round-trip digits; scientific below 1e-4 or from 1e16.
pub fn py_float(value: f64) -> String {
    if value == 0.0 {
        return if value.is_sign_negative() { "-0.0".into() } else { "0.0".into() };
    }
    let mut buffer = ryu::Buffer::new();
    let text = buffer.format_finite(value);
    let (negative, text) = match text.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, text),
    };
    let (mantissa, exponent) = match text.find('e') {
        Some(index) => (&text[..index], text[index + 1..].parse::<i32>().unwrap_or(0)),
        None => (text, 0),
    };
    let (integer, fraction) = match mantissa.find('.') {
        Some(index) => (&mantissa[..index], &mantissa[index + 1..]),
        None => (mantissa, ""),
    };
    let mut digits = format!("{integer}{fraction}");
    let mut point = integer.len() as i32 + exponent;
    let leading = digits.len() - digits.trim_start_matches('0').len();
    digits.drain(..leading);
    point -= leading as i32;
    let digits = digits.trim_end_matches('0');
    let digits = if digits.is_empty() { "0" } else { digits };
    let scientific = point - 1;
    let body = if scientific < -4 || scientific >= 16 {
        let (head, tail) = digits.split_at(1);
        let mantissa = if tail.is_empty() { head.to_string() } else { format!("{head}.{tail}") };
        format!("{mantissa}e{}{:02}", if scientific < 0 { '-' } else { '+' }, scientific.abs())
    } else if point <= 0 {
        format!("0.{}{}", "0".repeat((-point) as usize), digits)
    } else if point as usize >= digits.len() {
        format!("{}{}.0", digits, "0".repeat(point as usize - digits.len()))
    } else {
        format!("{}.{}", &digits[..point as usize], &digits[point as usize..])
    };
    if negative { format!("-{body}") } else { body }
}

#[derive(Clone, Copy)]
pub struct Style {
    pub indent: Option<usize>,
    pub ensure_ascii: bool,
    pub sort_keys: bool,
    /// `(",", ":")` when true, Python's defaults otherwise.
    pub compact: bool,
}

impl Style {
    /// `json.dumps(value, ensure_ascii=True, indent=2)`, used for floor scenes.
    pub const SCENE: Style = Style { indent: Some(2), ensure_ascii: true, sort_keys: false, compact: false };
    /// `json.dumps(value, sort_keys=True, separators=(",", ":"))`, used for geometry revisions.
    pub const REVISION: Style = Style { indent: None, ensure_ascii: true, sort_keys: true, compact: true };
    /// `json.dumps(value, separators=(",", ":"))`.
    pub const COMPACT: Style = Style { indent: None, ensure_ascii: true, sort_keys: false, compact: true };
    /// `json.dumps(value, indent=2, ensure_ascii=False)`.
    pub const PRETTY_UNICODE: Style = Style { indent: Some(2), ensure_ascii: false, sort_keys: false, compact: false };
}

pub fn dumps(value: &Value, style: Style) -> String {
    let mut out = String::new();
    write_value(&mut out, value, style, 0);
    out
}

fn write_string(out: &mut String, text: &str, ensure_ascii: bool) {
    out.push('"');
    for character in text.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{08}' => out.push_str("\\b"),
            '\u{0C}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c if ensure_ascii && !(' '..='~').contains(&c) => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    out.push_str(&format!("\\u{:04x}", unit));
                }
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

fn newline(out: &mut String, style: Style, level: usize) {
    if let Some(indent) = style.indent {
        out.push('\n');
        out.push_str(&" ".repeat(indent * level));
    }
}

fn write_value(out: &mut String, value: &Value, style: Style, level: usize) {
    let item_separator = if style.compact || style.indent.is_some() { "," } else { ", " };
    let key_separator = if style.compact { ":" } else { ": " };
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(flag) => out.push_str(if *flag { "true" } else { "false" }),
        Value::Number(number) => {
            if let Some(integer) = number.as_i64() {
                out.push_str(&integer.to_string());
            } else if let Some(integer) = number.as_u64() {
                out.push_str(&integer.to_string());
            } else {
                out.push_str(&py_float(number.as_f64().unwrap_or(0.0)));
            }
        }
        Value::String(text) => write_string(out, text, style.ensure_ascii),
        Value::Array(items) => {
            if items.is_empty() {
                out.push_str("[]");
                return;
            }
            out.push('[');
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push_str(item_separator);
                }
                newline(out, style, level + 1);
                write_value(out, item, style, level + 1);
            }
            newline(out, style, level);
            out.push(']');
        }
        Value::Object(map) => {
            if map.is_empty() {
                out.push_str("{}");
                return;
            }
            out.push('{');
            let mut entries: Vec<(&String, &Value)> = map.iter().collect();
            if style.sort_keys {
                entries.sort_by(|a, b| a.0.cmp(b.0));
            }
            for (index, (key, item)) in entries.into_iter().enumerate() {
                if index > 0 {
                    out.push_str(item_separator);
                }
                newline(out, style, level + 1);
                write_string(out, key, style.ensure_ascii);
                out.push_str(key_separator);
                write_value(out, item, style, level + 1);
            }
            newline(out, style, level);
            out.push('}');
        }
    }
}

/// Convenience accessors that mirror the defensive reads in the original code.
pub fn obj<'a>(value: &'a Value, key: &str) -> Option<&'a Map<String, Value>> {
    value.get(key).and_then(Value::as_object)
}
pub fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(Value::as_str)
}
pub fn number(value: Option<&Value>) -> Option<f64> {
    value.and_then(Value::as_f64).filter(|v| v.is_finite())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn python_float_repr() {
        for (value, expected) in [
            (1.0, "1.0"), (0.1, "0.1"), (100.0, "100.0"), (4.76837158e-07, "4.76837158e-07"), (1e16, "1e+16"),
            (1.5e-5, "1.5e-05"), (0.0001, "0.0001"), (123456789.123, "123456789.123"), (-975.360352, "-975.360352"),
            (1e22, "1e+22"), (5.00000000069889e-07, "5.00000000069889e-07"), (0.015500000000000014, "0.015500000000000014"),
            (1234567890123456.0, "1234567890123456.0"), (12345678901234567.0, "1.2345678901234568e+16"),
        ] {
            assert_eq!(py_float(value), expected, "{value}");
        }
    }
    #[test]
    fn python_layout() {
        let value: Value = serde_json::from_str(r#"{"b":[1,2.5,{}],"a":"é\u0001","c":[]}"#).unwrap();
        assert_eq!(dumps(&value, Style::SCENE), "{\n  \"b\": [\n    1,\n    2.5,\n    {}\n  ],\n  \"a\": \"\\u00e9\\u0001\",\n  \"c\": []\n}");
        assert_eq!(dumps(&value, Style::REVISION), "{\"a\":\"\\u00e9\\u0001\",\"b\":[1,2.5,{}],\"c\":[]}");
    }
}
