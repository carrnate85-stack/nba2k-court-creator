//! Minimal ZIP container support for NBA 2K `.iff` archives.
//!
//! Unchanged members are copied as their exact local-header and compressed bytes; replaced members
//! keep their name, method, timestamps and attributes and are written with fast deflate (level 1,
//! like the 1.7.17 exporter). Directory reads are bounded and every member's CRC can be verified.
use crate::error::{Error, Result};
use flate2::Compression;
use flate2::read::DeflateDecoder;
use flate2::write::DeflateEncoder;
use std::collections::{HashMap, HashSet};
use std::io::{Read, Write};
use std::path::Path;

pub const MAX_DIRECTORY_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_ENTRIES: usize = 20_000;

#[derive(Clone, Debug)]
pub struct Entry {
    pub name: String,
    pub version_made_by: u16,
    pub version_needed: u16,
    pub flags: u16,
    pub method: u16,
    pub time: u16,
    pub date: u16,
    pub crc: u32,
    pub compressed_size: u64,
    pub size: u64,
    pub name_raw: Vec<u8>,
    pub extra: Vec<u8>,
    pub comment: Vec<u8>,
    pub internal_attributes: u16,
    pub external_attributes: u32,
    pub header_offset: u64,
}

pub struct Archive {
    pub data: Vec<u8>,
    pub entries: Vec<Entry>,
    pub comment: Vec<u8>,
    index: HashMap<String, usize>,
}

fn u16_at(data: &[u8], offset: usize) -> Result<u16> {
    data.get(offset..offset + 2).map(|b| u16::from_le_bytes([b[0], b[1]])).ok_or_else(|| Error::msg("The IFF archive is truncated."))
}
fn u32_at(data: &[u8], offset: usize) -> Result<u32> {
    data.get(offset..offset + 4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])).ok_or_else(|| Error::msg("The IFF archive is truncated."))
}
fn u64_at(data: &[u8], offset: usize) -> Result<u64> {
    data.get(offset..offset + 8).map(|b| u64::from_le_bytes(b.try_into().unwrap())).ok_or_else(|| Error::msg("The IFF archive is truncated."))
}

fn bad() -> Error {
    Error::msg("Choose a readable ZIP-style NBA 2K court .iff file.")
}

impl Archive {
    pub fn open(path: &Path, max_bytes: u64) -> Result<Self> {
        let data = crate::files::read_bounded(path, max_bytes)?;
        Self::parse(data)
    }

    pub fn parse(data: Vec<u8>) -> Result<Self> {
        if data.len() < 22 {
            return Err(bad());
        }
        let search_start = data.len().saturating_sub(22 + 65535);
        let mut eocd = None;
        for offset in (search_start..=data.len() - 22).rev() {
            if data[offset..offset + 4] == [0x50, 0x4B, 0x05, 0x06] {
                let comment_length = u16_at(&data, offset + 20)? as usize;
                if offset + 22 + comment_length <= data.len() {
                    eocd = Some(offset);
                    break;
                }
            }
        }
        let eocd = eocd.ok_or_else(bad)?;
        let comment_length = u16_at(&data, eocd + 20)? as usize;
        let comment = data[eocd + 22..eocd + 22 + comment_length].to_vec();
        let mut directory_size = u32_at(&data, eocd + 12)? as u64;
        let mut directory_offset = u32_at(&data, eocd + 16)? as u64;
        let mut base_offset = 0u64;
        if (directory_offset == 0xFFFF_FFFF || directory_size == 0xFFFF_FFFF) && eocd >= 20 && data[eocd - 20..eocd - 16] == [0x50, 0x4B, 0x06, 0x07] {
            let record = u64_at(&data, eocd - 12)? as usize;
            if data.get(record..record + 4) != Some(&[0x50, 0x4B, 0x06, 0x06]) {
                return Err(bad());
            }
            directory_size = u64_at(&data, record + 40)?;
            directory_offset = u64_at(&data, record + 48)?;
        } else {
            // Archives with a prefix (self-extracting stubs) shift every recorded offset.
            let expected = eocd as u64 - directory_size;
            if expected > directory_offset {
                base_offset = expected - directory_offset;
            }
        }
        if directory_size as usize > MAX_DIRECTORY_BYTES {
            return Err(Error::msg("This IFF's ZIP directory exceeds the 8 MB inspection limit."));
        }
        let start = (directory_offset + base_offset) as usize;
        let end = start.checked_add(directory_size as usize).filter(|&e| e <= data.len()).ok_or_else(bad)?;
        let mut cursor = start;
        let mut entries = Vec::new();
        while cursor + 46 <= end && data[cursor..cursor + 4] == [0x50, 0x4B, 0x01, 0x02] {
            if entries.len() >= MAX_ENTRIES {
                return Err(Error::msg("This IFF has too many entries to inspect safely."));
            }
            let name_length = u16_at(&data, cursor + 28)? as usize;
            let extra_length = u16_at(&data, cursor + 30)? as usize;
            let comment_length = u16_at(&data, cursor + 32)? as usize;
            let fixed = cursor + 46;
            let name_raw = data.get(fixed..fixed + name_length).ok_or_else(bad)?.to_vec();
            let extra = data.get(fixed + name_length..fixed + name_length + extra_length).ok_or_else(bad)?.to_vec();
            let entry_comment = data.get(fixed + name_length + extra_length..fixed + name_length + extra_length + comment_length).ok_or_else(bad)?.to_vec();
            let flags = u16_at(&data, cursor + 8)?;
            let name = if flags & 0x800 != 0 { String::from_utf8(name_raw.clone()).map_err(|_| bad())? } else { name_raw.iter().map(|&b| cp437(b)).collect() };
            let mut entry = Entry {
                name,
                version_made_by: u16_at(&data, cursor + 4)?,
                version_needed: u16_at(&data, cursor + 6)?,
                flags,
                method: u16_at(&data, cursor + 10)?,
                time: u16_at(&data, cursor + 12)?,
                date: u16_at(&data, cursor + 14)?,
                crc: u32_at(&data, cursor + 16)?,
                compressed_size: u32_at(&data, cursor + 20)? as u64,
                size: u32_at(&data, cursor + 24)? as u64,
                name_raw,
                extra,
                comment: entry_comment,
                internal_attributes: u16_at(&data, cursor + 36)?,
                external_attributes: u32_at(&data, cursor + 38)?,
                header_offset: u32_at(&data, cursor + 42)? as u64 + base_offset,
            };
            apply_zip64(&mut entry, base_offset)?;
            entries.push(entry);
            cursor = fixed + name_length + extra_length + comment_length;
        }
        let mut index = HashMap::new();
        for (i, entry) in entries.iter().enumerate() {
            if index.insert(entry.name.clone(), i).is_some() {
                return Err(Error::msg("This IFF contains duplicate entry names. Use an archive with unambiguous texture names."));
            }
        }
        Ok(Self { data, entries, comment, index })
    }

    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.entries.iter().map(|e| e.name.as_str())
    }
    pub fn entry(&self, name: &str) -> Option<&Entry> {
        self.index.get(name).map(|&i| &self.entries[i])
    }
    pub fn contains(&self, name: &str) -> bool {
        self.index.contains_key(name)
    }
    pub fn total_size(&self) -> u64 {
        self.entries.iter().map(|e| e.size).sum()
    }

    fn data_range(&self, entry: &Entry) -> Result<(usize, usize)> {
        let offset = entry.header_offset as usize;
        if self.data.get(offset..offset + 4) != Some(&[0x50, 0x4B, 0x03, 0x04]) {
            return Err(Error::msg(format!("The IFF member {} has an invalid local header.", entry.name)));
        }
        let name_length = u16_at(&self.data, offset + 26)? as usize;
        let extra_length = u16_at(&self.data, offset + 28)? as usize;
        let start = offset + 30 + name_length + extra_length;
        let end = start.checked_add(entry.compressed_size as usize).filter(|&e| e <= self.data.len()).ok_or_else(|| Error::msg(format!("The IFF member {} is truncated.", entry.name)))?;
        Ok((start, end))
    }

    /// Exact bytes of a member's local record (header, data and any data descriptor).
    fn raw_record(&self, entry: &Entry) -> Result<&[u8]> {
        let (_, mut end) = self.data_range(entry)?;
        if entry.flags & 0x08 != 0 {
            let zip64 = entry.size >= 0xFFFF_FFFF || entry.compressed_size >= 0xFFFF_FFFF;
            let signed = self.data.get(end..end + 4) == Some(&[0x50, 0x4B, 0x07, 0x08]);
            end += if signed { 4 } else { 0 } + if zip64 { 20 } else { 12 };
            if end > self.data.len() {
                return Err(Error::msg(format!("The IFF member {} is truncated.", entry.name)));
            }
        }
        Ok(&self.data[entry.header_offset as usize..end])
    }

    /// Inflated, CRC-checked member contents (limited to `max_bytes`).
    pub fn read(&self, name: &str, max_bytes: u64) -> Result<Vec<u8>> {
        let entry = self.entry(name).ok_or_else(|| Error::msg(format!("The IFF has no member named {name}.")))?;
        if entry.size > max_bytes {
            return Err(Error::msg(format!("The IFF member {name} exceeds its size limit.")));
        }
        let (start, end) = self.data_range(entry)?;
        let compressed = &self.data[start..end];
        let mut out = Vec::with_capacity(entry.size as usize);
        match entry.method {
            0 => out.extend_from_slice(compressed),
            8 => {
                DeflateDecoder::new(compressed).take(max_bytes.saturating_add(1)).read_to_end(&mut out).map_err(|_| Error::msg(format!("The IFF member {name} is damaged.")))?;
            }
            _ => return Err(Error::msg(format!("The IFF member {name} uses an unsupported compression method."))),
        }
        if out.len() as u64 != entry.size || crc32fast::hash(&out) != entry.crc {
            return Err(Error::msg(format!("The IFF member {name} failed its CRC check.")));
        }
        Ok(out)
    }

    /// First `count` inflated bytes of a member (used for DDS headers).
    pub fn read_prefix(&self, name: &str, count: usize) -> Result<Vec<u8>> {
        let entry = self.entry(name).ok_or_else(|| Error::msg(format!("The IFF has no member named {name}.")))?;
        let (start, end) = self.data_range(entry)?;
        let compressed = &self.data[start..end];
        let mut out = Vec::with_capacity(count);
        match entry.method {
            0 => out.extend_from_slice(&compressed[..compressed.len().min(count)]),
            8 => {
                DeflateDecoder::new(compressed).take(count as u64).read_to_end(&mut out).map_err(|_| Error::msg(format!("The IFF member {name} is damaged.")))?;
            }
            _ => return Err(Error::msg(format!("The IFF member {name} uses an unsupported compression method."))),
        }
        Ok(out)
    }

    /// Verifies every member's CRC (Python `testzip`).
    pub fn test(&self) -> Result<()> {
        for entry in &self.entries {
            self.read(&entry.name, u64::MAX)?;
        }
        Ok(())
    }
}

fn apply_zip64(entry: &mut Entry, base_offset: u64) -> Result<()> {
    if entry.size != 0xFFFF_FFFF && entry.compressed_size != 0xFFFF_FFFF && entry.header_offset - base_offset != 0xFFFF_FFFF {
        return Ok(());
    }
    let mut cursor = 0;
    while cursor + 4 <= entry.extra.len() {
        let id = u16::from_le_bytes([entry.extra[cursor], entry.extra[cursor + 1]]);
        let length = u16::from_le_bytes([entry.extra[cursor + 2], entry.extra[cursor + 3]]) as usize;
        if id == 1 {
            let mut field = cursor + 4;
            if entry.size == 0xFFFF_FFFF {
                entry.size = u64_at(&entry.extra, field)?;
                field += 8;
            }
            if entry.compressed_size == 0xFFFF_FFFF {
                entry.compressed_size = u64_at(&entry.extra, field)?;
                field += 8;
            }
            if entry.header_offset - base_offset == 0xFFFF_FFFF {
                entry.header_offset = u64_at(&entry.extra, field)? + base_offset;
            }
            return Ok(());
        }
        cursor += 4 + length;
    }
    Err(bad())
}

fn cp437(byte: u8) -> char {
    if byte < 0x80 { byte as char } else { char::from_u32(0x80 + byte as u32).unwrap_or('?') }
}

/// Writes a new archive from `source`, replacing selected members' contents.
pub fn rewrite(source: &Archive, replacements: &HashMap<String, Vec<u8>>, comment: &[u8], out: &mut impl Write) -> Result<u64> {
    let mut offset = 0u64;
    let mut central = Vec::new();
    let mut written = HashSet::new();
    for entry in &source.entries {
        let local_offset = offset;
        let mut record = entry.clone();
        if let Some(contents) = replacements.get(&entry.name) {
            let (method, data) = if entry.method == 0 {
                (0u16, contents.clone())
            } else {
                let mut encoder = DeflateEncoder::new(Vec::with_capacity(contents.len() / 2), Compression::fast());
                encoder.write_all(contents)?;
                (8u16, encoder.finish()?)
            };
            if data.len() as u64 >= 0xFFFF_FFFF || contents.len() as u64 >= 0xFFFF_FFFF {
                return Err(Error::msg("The converted IFF member exceeds the ZIP size limit."));
            }
            record.method = method;
            record.flags &= !(0x08 | 0x01);
            record.crc = crc32fast::hash(contents);
            record.compressed_size = data.len() as u64;
            record.size = contents.len() as u64;
            record.extra = strip_zip64(&entry.extra);
            record.version_needed = record.version_needed.max(20);
            let mut header = Vec::with_capacity(30 + record.name_raw.len() + record.extra.len());
            header.extend_from_slice(&[0x50, 0x4B, 0x03, 0x04]);
            header.extend_from_slice(&record.version_needed.to_le_bytes());
            header.extend_from_slice(&record.flags.to_le_bytes());
            header.extend_from_slice(&record.method.to_le_bytes());
            header.extend_from_slice(&record.time.to_le_bytes());
            header.extend_from_slice(&record.date.to_le_bytes());
            header.extend_from_slice(&record.crc.to_le_bytes());
            header.extend_from_slice(&(record.compressed_size as u32).to_le_bytes());
            header.extend_from_slice(&(record.size as u32).to_le_bytes());
            header.extend_from_slice(&(record.name_raw.len() as u16).to_le_bytes());
            header.extend_from_slice(&(record.extra.len() as u16).to_le_bytes());
            header.extend_from_slice(&record.name_raw);
            header.extend_from_slice(&record.extra);
            out.write_all(&header)?;
            out.write_all(&data)?;
            offset += (header.len() + data.len()) as u64;
            written.insert(entry.name.clone());
        } else {
            let raw = source.raw_record(entry)?;
            if entry.size >= 0xFFFF_FFFF || entry.compressed_size >= 0xFFFF_FFFF {
                return Err(Error::msg("ZIP64 members are not supported in court exports."));
            }
            out.write_all(raw)?;
            offset += raw.len() as u64;
        }
        if local_offset >= 0xFFFF_FFFF {
            return Err(Error::msg("The converted IFF exceeds the ZIP size limit."));
        }
        record.header_offset = local_offset;
        let extra = strip_zip64(&record.extra);
        central.extend_from_slice(&[0x50, 0x4B, 0x01, 0x02]);
        central.extend_from_slice(&record.version_made_by.to_le_bytes());
        central.extend_from_slice(&record.version_needed.to_le_bytes());
        central.extend_from_slice(&record.flags.to_le_bytes());
        central.extend_from_slice(&record.method.to_le_bytes());
        central.extend_from_slice(&record.time.to_le_bytes());
        central.extend_from_slice(&record.date.to_le_bytes());
        central.extend_from_slice(&record.crc.to_le_bytes());
        central.extend_from_slice(&(record.compressed_size as u32).to_le_bytes());
        central.extend_from_slice(&(record.size as u32).to_le_bytes());
        central.extend_from_slice(&(record.name_raw.len() as u16).to_le_bytes());
        central.extend_from_slice(&(extra.len() as u16).to_le_bytes());
        central.extend_from_slice(&(record.comment.len() as u16).to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&record.internal_attributes.to_le_bytes());
        central.extend_from_slice(&record.external_attributes.to_le_bytes());
        central.extend_from_slice(&(local_offset as u32).to_le_bytes());
        central.extend_from_slice(&record.name_raw);
        central.extend_from_slice(&extra);
        central.extend_from_slice(&record.comment);
    }
    if let Some(missing) = replacements.keys().find(|k| !written.contains(*k)) {
        return Err(Error::msg(format!("The export base has no member named {missing} to replace.")));
    }
    if comment.len() > 65535 || source.entries.len() > 65535 {
        return Err(Error::msg("The converted IFF exceeds the ZIP directory limits."));
    }
    let directory_offset = offset;
    out.write_all(&central)?;
    offset += central.len() as u64;
    let mut end = Vec::with_capacity(22 + comment.len());
    end.extend_from_slice(&[0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0]);
    end.extend_from_slice(&(source.entries.len() as u16).to_le_bytes());
    end.extend_from_slice(&(source.entries.len() as u16).to_le_bytes());
    end.extend_from_slice(&(central.len() as u32).to_le_bytes());
    end.extend_from_slice(&(directory_offset as u32).to_le_bytes());
    end.extend_from_slice(&(comment.len() as u16).to_le_bytes());
    end.extend_from_slice(comment);
    out.write_all(&end)?;
    offset += end.len() as u64;
    Ok(offset)
}

fn strip_zip64(extra: &[u8]) -> Vec<u8> {
    let mut out = Vec::new();
    let mut cursor = 0;
    while cursor + 4 <= extra.len() {
        let id = u16::from_le_bytes([extra[cursor], extra[cursor + 1]]);
        let length = u16::from_le_bytes([extra[cursor + 2], extra[cursor + 3]]) as usize;
        let end = (cursor + 4 + length).min(extra.len());
        if id != 1 {
            out.extend_from_slice(&extra[cursor..end]);
        }
        cursor = end;
    }
    out
}

/// Byte-for-byte member comparison with expected contents.
pub fn matches(archive: &Archive, name: &str, expected: &[u8]) -> bool {
    archive.read(name, expected.len() as u64).is_ok_and(|data| data == expected)
}
