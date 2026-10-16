//! Bounded reads, content revisions and atomic publication that never overwrite a source.
use crate::error::{Error, Result};
use sha2::{Digest, Sha256};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

pub const MAX_ASSET_BYTES: u64 = 512 * 1024 * 1024;

pub fn read_bounded(path: &Path, max_bytes: u64) -> Result<Vec<u8>> {
    let mut file = File::open(path).map_err(|error| Error::msg(format!("{}: {error}", path.display())))?;
    let length = file.metadata()?.len();
    if length > max_bytes {
        return Err(Error::msg(format!("{} exceeds its {} size limit.", path.display(), crate::pyjson::human_bytes(max_bytes as usize))));
    }
    let mut data = Vec::with_capacity(length as usize);
    Read::by_ref(&mut file).take(max_bytes + 1).read_to_end(&mut data)?;
    if data.len() as u64 > max_bytes {
        return Err(Error::msg(format!("{} grew beyond its size limit while reading.", path.display())));
    }
    Ok(data)
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    hex(&Sha256::digest(bytes))
}

pub fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        out.push(DIGITS[(byte >> 4) as usize] as char);
        out.push(DIGITS[(byte & 15) as usize] as char);
    }
    out
}

/// SHA-256 of a complete file, streamed with the 512 MiB asset budget.
pub fn file_revision(path: &Path) -> Result<String> {
    let mut file = File::open(path).map_err(|error| Error::msg(format!("{}: {error}", path.display())))?;
    if file.metadata()?.len() > MAX_ASSET_BYTES {
        return Err(Error::msg("Artwork exceeds the 512 MB file size limit."));
    }
    let mut digest = Sha256::new();
    let mut buffer = vec![0u8; 256 * 1024];
    let mut total = 0u64;
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        total += read as u64;
        if total > MAX_ASSET_BYTES {
            return Err(Error::msg("Artwork exceeds the 512 MB file size limit."));
        }
        digest.update(&buffer[..read]);
    }
    Ok(hex(&digest.finalize()))
}

pub fn is_revision(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

/// Reads an asset and verifies its pinned revision (when one is recorded) against the same bytes.
pub fn read_pinned(path: &Path, revision: Option<&str>) -> Result<Vec<u8>> {
    let bytes = read_bounded(path, MAX_ASSET_BYTES).map_err(|error| {
        if revision.is_some() && !path.exists() {
            Error::msg("Artwork used in the preview is missing. Reimport the logo before exporting.")
        } else {
            error
        }
    })?;
    if let Some(expected) = revision {
        if !is_revision(expected) {
            return Err(Error::msg("Invalid artwork source revision."));
        }
        if sha256_hex(&bytes) != expected {
            return Err(Error::msg("Artwork changed after its preview was loaded. Refresh the court or reimport the logo before exporting."));
        }
    }
    Ok(bytes)
}

/// Absolute, normalized path text used for case-insensitive Windows comparisons.
pub fn normalized(path: &Path) -> PathBuf {
    let absolute = if path.is_absolute() { path.to_path_buf() } else { std::env::current_dir().unwrap_or_default().join(path) };
    if let Ok(canonical) = absolute.canonicalize() {
        return strip_verbatim(canonical);
    }
    // Missing destination: canonicalize its parent and keep the file name.
    if let (Some(parent), Some(name)) = (absolute.parent(), absolute.file_name()) {
        if let Ok(parent) = parent.canonicalize() {
            return strip_verbatim(parent).join(name);
        }
    }
    absolute
}

fn strip_verbatim(path: PathBuf) -> PathBuf {
    let text = path.to_string_lossy();
    if let Some(rest) = text.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{rest}"));
    }
    if let Some(rest) = text.strip_prefix(r"\\?\") {
        return PathBuf::from(rest);
    }
    path
}

pub fn same_path(a: &Path, b: &Path) -> bool {
    normalized(a).to_string_lossy().eq_ignore_ascii_case(&normalized(b).to_string_lossy())
}

#[cfg(windows)]
fn file_identity(path: &Path) -> Option<(u32, u64, u32)> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::Storage::FileSystem::{BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle};
    let file = OpenOptions::new().read(false).access_mode(0).share_mode(7).open(path).ok()?;
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    unsafe { GetFileInformationByHandle(windows::Win32::Foundation::HANDLE(file.as_raw_handle()), &mut info) }.ok()?;
    Some((info.dwVolumeSerialNumber, ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64, info.nNumberOfLinks))
}
#[cfg(not(windows))]
fn file_identity(_path: &Path) -> Option<(u32, u64, u32)> {
    None
}

/// Rejects destinations that would replace (or hard-link alias) any protected source.
pub fn ensure_new_export(destination: &Path, sources: &[&Path]) -> Result<()> {
    if destination.as_os_str().is_empty() {
        return Err(Error::msg("Choose an output file."));
    }
    let target_identity = if destination.exists() { file_identity(destination) } else { None };
    for source in sources {
        if source.as_os_str().is_empty() {
            continue;
        }
        if same_path(destination, source) {
            return Err(Error::msg("Choose a different output file. Court Creator never overwrites its source artwork, project or game files."));
        }
        if let (Some(target), Some(identity)) = (target_identity, if source.exists() { file_identity(source) } else { None }) {
            if target.0 == identity.0 && target.1 == identity.1 {
                return Err(Error::msg("The output file is linked to a source file. Choose a different output file."));
            }
        }
    }
    if destination.is_dir() {
        return Err(Error::msg("The output path is a folder."));
    }
    Ok(())
}

fn retryable(error: &std::io::Error) -> bool {
    matches!(error.raw_os_error(), Some(5 | 32 | 33))
}

/// Retries Windows access/sharing/lock violations up to four attempts (25/50/100 ms waits).
pub fn retry<T>(mut operation: impl FnMut() -> std::io::Result<T>) -> std::io::Result<T> {
    let mut attempt = 0;
    loop {
        match operation() {
            Ok(value) => return Ok(value),
            Err(error) if attempt < 3 && retryable(&error) => {
                std::thread::sleep(std::time::Duration::from_millis(25 << attempt));
                attempt += 1;
            }
            Err(error) => return Err(error),
        }
    }
}

/// A uniquely named file beside its destination, removed unless published.
pub struct Staged {
    pub path: PathBuf,
    destination: PathBuf,
    published: bool,
}

impl Staged {
    pub fn new(destination: &Path) -> Result<(Self, File)> {
        let parent = destination.parent().filter(|p| !p.as_os_str().is_empty()).unwrap_or(Path::new("."));
        fs::create_dir_all(parent)?;
        let name = destination.file_name().ok_or_else(|| Error::msg("Choose an output file."))?.to_string_lossy();
        let path = parent.join(format!(".{name}.{}.tmp", uuid::Uuid::new_v4().simple()));
        let file = OpenOptions::new().write(true).read(true).create_new(true).open(&path)?;
        Ok((Self { path, destination: destination.to_path_buf(), published: false }, file))
    }

    /// Atomically replaces the destination, optionally retaining the previous file as a backup.
    pub fn publish(mut self, backup: Option<&Path>) -> Result<()> {
        let staged = self.path.clone();
        let destination = self.destination.clone();
        retry(|| replace(&staged, &destination, backup))?;
        self.published = true;
        Ok(())
    }
}

impl Drop for Staged {
    fn drop(&mut self) {
        if !self.published {
            let _ = retry(|| fs::remove_file(&self.path).or_else(|e| if e.kind() == std::io::ErrorKind::NotFound { Ok(()) } else { Err(e) }));
        }
    }
}

#[cfg(windows)]
fn replace(staged: &Path, destination: &Path, backup: Option<&Path>) -> std::io::Result<()> {
    use windows::Win32::Storage::FileSystem::{MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW, REPLACE_FILE_FLAGS, ReplaceFileW};
    use windows::core::HSTRING;
    // Extended-length form so deep data roots and long staging names work past MAX_PATH.
    let long = |path: &Path| -> HSTRING {
        let absolute = if path.is_absolute() { path.to_path_buf() } else { std::env::current_dir().unwrap_or_default().join(path) };
        let text = absolute.to_string_lossy();
        if text.starts_with(r"\\?\") {
            HSTRING::from(text.as_ref())
        } else if let Some(unc) = text.strip_prefix(r"\\") {
            HSTRING::from(format!(r"\\?\UNC\{unc}"))
        } else {
            HSTRING::from(format!(r"\\?\{text}"))
        }
    };
    let exists = destination.exists();
    let (staged, destination) = (&long(staged), &long(destination));
    if let Some(backup) = backup {
        if exists {
            return unsafe { ReplaceFileW(destination, staged, &long(backup), REPLACE_FILE_FLAGS(2), None, None) }
            .map_err(|error| std::io::Error::from_raw_os_error((error.code().0 & 0xFFFF) as i32));
        }
    }
    unsafe { MoveFileExW(staged, destination, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) }
        .map_err(|error| std::io::Error::from_raw_os_error((error.code().0 & 0xFFFF) as i32))
}
#[cfg(not(windows))]
fn replace(staged: &Path, destination: &Path, backup: Option<&Path>) -> std::io::Result<()> {
    if let Some(backup) = backup {
        if destination.exists() {
            fs::copy(destination, backup)?;
        }
    }
    fs::rename(staged, destination)
}

/// Writes complete bytes through a flushed staging file and atomic replacement.
pub fn write_atomic(destination: &Path, bytes: &[u8], sources: &[&Path]) -> Result<()> {
    ensure_new_export(destination, sources)?;
    let (staged, mut file) = Staged::new(destination)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    ensure_new_export(destination, sources)?;
    staged.publish(None)
}

/// Streams a writer into a staged file, then publishes it only when `write` succeeds.
pub fn write_atomic_with(destination: &Path, sources: &[&Path], write: impl FnOnce(&mut File) -> Result<()>) -> Result<()> {
    write_atomic_checked(destination, sources, write, |_| Ok(()))
}

/// Like [`write_atomic_with`], but `validate` inspects the complete staged file before publication,
/// so a failed check never replaces the previous output.
pub fn write_atomic_checked(destination: &Path, sources: &[&Path], write: impl FnOnce(&mut File) -> Result<()>, validate: impl FnOnce(&Path) -> Result<()>) -> Result<()> {
    ensure_new_export(destination, sources)?;
    let (staged, mut file) = Staged::new(destination)?;
    write(&mut file)?;
    file.flush()?;
    file.sync_all()?;
    drop(file);
    validate(&staged.path)?;
    ensure_new_export(destination, sources)?;
    staged.publish(None)
}

/// Local application data for the separate Rust preview (never the existing app's private cache).
pub fn data_root() -> PathBuf {
    if let Ok(custom) = std::env::var("COURT_CREATOR_NATIVE_DATA") {
        if !custom.trim().is_empty() {
            return PathBuf::from(custom);
        }
    }
    let local = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    local.join("2K Studio").join("Court Creator Native")
}

/// The existing Court Creator settings folder, read only for one-time preference seeding.
pub fn legacy_settings_root() -> PathBuf {
    let local = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    local.join("2K Studio").join("Court Creator")
}
