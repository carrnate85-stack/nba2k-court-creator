//! Oodle decompression through the installed game's `oo2core_9_win64.dll` (never redistributed).
use crate::error::{Error, Result};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

type Decompress = unsafe extern "system" fn(
    *const u8, isize, *mut u8, isize, i32, i32, i32, *mut u8, isize, *mut u8, *mut u8, *mut u8, isize, i32,
) -> isize;

static LOADED: Mutex<Option<HashMap<PathBuf, usize>>> = Mutex::new(None);

pub fn dll_path(game_root: &Path) -> PathBuf {
    game_root.join("data").join("oodle").join("oo2core_9_win64.dll")
}

#[cfg(windows)]
fn function(game_root: &Path) -> Result<Decompress> {
    use windows::Win32::System::LibraryLoader::{GetProcAddress, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW};
    use windows::core::{HSTRING, s};
    let path = dll_path(game_root);
    if !path.is_file() {
        return Err(Error::msg("Select the NBA 2K27 installation to decode its floor geometry and textures (Oodle support was not found)."));
    }
    let mut loaded = LOADED.lock().map_err(|_| Error::msg("Oodle state is unavailable."))?;
    let table = loaded.get_or_insert_with(HashMap::new);
    if let Some(&address) = table.get(&path) {
        return Ok(unsafe { std::mem::transmute::<usize, Decompress>(address) });
    }
    // Load only from the game's own folder and System32; the module stays loaded for reuse.
    let module = unsafe { LoadLibraryExW(&HSTRING::from(path.as_os_str()), None, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32) }
        .map_err(|error| Error::msg(format!("The installed game's Oodle support could not be loaded: {error}")))?;
    let address = unsafe { GetProcAddress(module, s!("OodleLZ_Decompress")) }.ok_or_else(|| Error::msg("The installed Oodle library has no decompressor."))?;
    table.insert(path, address as usize);
    Ok(unsafe { std::mem::transmute::<usize, Decompress>(address as usize) })
}

#[cfg(not(windows))]
fn function(_game_root: &Path) -> Result<Decompress> {
    Err(Error::msg("Oodle decompression requires Windows."))
}

pub fn decompress(game_root: &Path, compressed: &[u8], raw_size: usize) -> Result<Vec<u8>> {
    if raw_size == 0 || raw_size > 256 * 1024 * 1024 {
        return Err(Error::msg("Invalid Oodle output size."));
    }
    let decompress = function(game_root)?;
    let mut output = vec![0u8; raw_size];
    let decoded = unsafe {
        decompress(
            compressed.as_ptr(), compressed.len() as isize, output.as_mut_ptr(), raw_size as isize,
            1, 0, 0, std::ptr::null_mut(), 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut(), 0, 3,
        )
    };
    if decoded != raw_size as isize {
        return Err(Error::msg(format!("Oodle decompressed {decoded} bytes, expected {raw_size}.")));
    }
    Ok(output)
}
