//! Cooperative cross-process file lock (persistent lock file, never swept).
use crate::error::{Error, Result};
use std::fs::{File, OpenOptions};
use std::path::Path;
use std::time::{Duration, Instant};

pub struct FileLock {
    file: File,
}

impl FileLock {
    pub fn acquire(path: &Path, timeout: Duration) -> Result<Self> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let file = OpenOptions::new().read(true).write(true).create(true).truncate(false).open(path)?;
        let started = Instant::now();
        loop {
            if try_lock(&file) {
                return Ok(Self { file });
            }
            if started.elapsed() > timeout {
                return Err(Error::msg("Another court operation is preparing stock data. Try again shortly."));
            }
            std::thread::sleep(Duration::from_millis(100));
        }
    }
}

#[cfg(windows)]
fn try_lock(file: &File) -> bool {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::Storage::FileSystem::{LOCKFILE_EXCLUSIVE_LOCK, LOCKFILE_FAIL_IMMEDIATELY, LockFileEx};
    use windows::Win32::System::IO::OVERLAPPED;
    let mut overlapped = OVERLAPPED::default();
    unsafe { LockFileEx(HANDLE(file.as_raw_handle()), LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY, None, 1, 0, &mut overlapped) }.is_ok()
}
#[cfg(not(windows))]
fn try_lock(_file: &File) -> bool {
    true
}

impl Drop for FileLock {
    fn drop(&mut self) {
        #[cfg(windows)]
        {
            use std::os::windows::io::AsRawHandle;
            use windows::Win32::Foundation::HANDLE;
            use windows::Win32::Storage::FileSystem::UnlockFileEx;
            use windows::Win32::System::IO::OVERLAPPED;
            let mut overlapped = OVERLAPPED::default();
            let _ = unsafe { UnlockFileEx(HANDLE(self.file.as_raw_handle()), None, 1, 0, &mut overlapped) };
        }
    }
}
