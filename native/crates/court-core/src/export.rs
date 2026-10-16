//! Full-resolution PNG and NBA 2K27 IFF exports from an immutable project snapshot.
use crate::error::{Result, check};
use crate::geometry::Geometry;
use crate::iff;
use crate::raster;
use crate::render::{self, RenderRequest};
use crate::scene::{self, SCENE_ENTRY};
use crate::ziparc::Archive;
use std::path::{Path, PathBuf};

pub struct Progress<'a> {
    pub report: &'a dyn Fn(&str),
    pub cancelled: &'a dyn Fn() -> bool,
}

fn protected(request: &RenderRequest, extra: &[PathBuf]) -> Vec<PathBuf> {
    let mut sources = request.sources();
    sources.extend(extra.iter().cloned());
    sources
}

/// Renders the 8192 x 4096 court texture and publishes it atomically as PNG.
pub fn export_png(geometry: &Geometry, request: &RenderRequest, output: &Path, progress: &Progress) -> Result<()> {
    let sources = protected(request, &[]);
    let refs: Vec<&Path> = sources.iter().map(PathBuf::as_path).collect();
    crate::files::ensure_new_export(output, &refs)?;
    (progress.report)("Rendering the original assets at 8192 × 4096...");
    let image = render::render(geometry, request, 1.0, &mut Default::default(), progress.cancelled)?;
    check(progress.cancelled)?;
    (progress.report)("Writing the PNG texture...");
    let png = raster::encode_png(&image, false)?;
    crate::files::write_atomic(output, &png, &refs)
}

/// Builds the single-texture NBA 2K27 IFF from the regular base.
pub fn export_iff(geometry: &Geometry, request: &RenderRequest, output: &Path, game_root: Option<&Path>, progress: &Progress) -> Result<()> {
    let base_path = iff::base_path();
    let sources = protected(request, &[base_path.clone()]);
    let refs: Vec<&Path> = sources.iter().map(PathBuf::as_path).collect();
    crate::files::ensure_new_export(output, &refs)?;
    (progress.report)("Preparing the regular NBA 2K27 export base...");
    let base = iff::prepare_base(game_root, progress.cancelled)?;
    let archive = Archive::open(&base_path, iff::MAX_BASE_ARCHIVE_BYTES)?;
    let texture = base.textures.iter().find(|t| t.name == base.selected).cloned().ok_or_else(|| crate::Error::msg("The selected target DDS is not a supported full-court texture."))?;
    let scene_override = scene::native_export_scene(&archive.read(SCENE_ENTRY, scene::MAX_SCENE_BYTES as u64)?)?;
    check(progress.cancelled)?;
    (progress.report)("Rendering the court texture at 8192 × 4096...");
    let image = render::render(geometry, request, 1.0, &mut Default::default(), progress.cancelled)?;
    check(progress.cancelled)?;
    (progress.report)(&format!("Encoding {} with {} mips...", texture.format, texture.mipmaps.max(1)));
    let profile = iff::base_texture_profile(&archive, &texture.name)?;
    let dds = iff::encode_texture(&image.data, &profile, progress.cancelled)?;
    drop(image);
    check(progress.cancelled)?;
    (progress.report)("Packaging the NBA 2K27 IFF...");
    let comment = iff::layout_comment(&texture.name, iff::target_court_bounds(geometry)?);
    iff::package_texture(&archive, &texture, &dds, Some(&scene_override), &comment, output, &refs)
}
