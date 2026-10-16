//! Parity harness for `court_core::import` (off-screen; writes only to the given output paths).
//!
//! import_check inspect <iff> [selected]
//! import_check render <geometry.json> <background.png> <out-dir> <iff>...
//! import_check iff <geometry.json> <background.png> <source.iff> <out.iff>
use court_core::geometry::Geometry;
use court_core::import::{self, ImportRequest};
use std::path::{Path, PathBuf};
use std::time::Instant;

fn request(source: &Path, background: &Path) -> court_core::Result<(ImportRequest, court_core::iff::Inspection)> {
    let inspection = import::inspect(source, None)?;
    let request = ImportRequest {
        source_path: source.to_path_buf(),
        texture_name: inspection.selected.clone(),
        bounds: inspection.source_bounds.unwrap(),
        background_path: background.to_path_buf(),
        background_revision: None,
        source_revision: inspection.source_revision.clone(),
    };
    Ok((request, inspection))
}

fn main() -> court_core::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("inspect") => {
            let started = Instant::now();
            let inspection = import::inspect(Path::new(&args[1]), args.get(2).map(String::as_str))?;
            println!("{}", serde_json::to_string(&inspection).unwrap());
            eprintln!("inspect {:.2}s", started.elapsed().as_secs_f64());
        }
        Some("render") => {
            let geometry = Geometry::load(Path::new(&args[1]))?;
            let background = PathBuf::from(&args[2]);
            let out = PathBuf::from(&args[3]);
            for source in &args[4..] {
                let source = Path::new(source);
                let stem = source.file_stem().unwrap().to_string_lossy().into_owned();
                let started = Instant::now();
                let (request, inspection) = request(source, &background)?;
                println!("{}", serde_json::to_string(&inspection).unwrap());
                let t_inspect = started.elapsed().as_secs_f64();
                let preview = import::preview(&request, &geometry)?;
                std::fs::write(out.join(format!("{stem}-preview.png")), import::encode_png_rgb(&preview)?)?;
                let t_preview = started.elapsed().as_secs_f64() - t_inspect;
                let started_full = Instant::now();
                import::export_png(&request, &geometry, &out.join(format!("{stem}-full.png")))?;
                eprintln!("{stem}: inspect {t_inspect:.2}s preview {t_preview:.2}s full png {:.2}s", started_full.elapsed().as_secs_f64());
                // Detection internals for the Pillow comparison.
                let archive = court_core::ziparc::Archive::open(source, 1 << 30)?;
                let decoded = court_core::raster::decode_dds(&archive.read(&request.texture_name, 1 << 28)?)?;
                let (sample, blurred) = import::detection_debug(&decoded);
                std::fs::write(out.join(format!("{stem}-sample.png")), import::encode_png_rgb(&sample)?)?;
                std::fs::write(out.join(format!("{stem}-blur.png")), import::encode_png_rgb(&blurred)?)?;
                std::fs::write(out.join(format!("{stem}-decoded.png")), court_core::raster::encode_png(&decoded, true)?)?;
            }
        }
        Some("iff") => {
            let geometry = Geometry::load(Path::new(&args[1]))?;
            let (request, _) = request(Path::new(&args[3]), Path::new(&args[2]))?;
            let started = Instant::now();
            import::export_iff(&request, &geometry, Path::new(&args[4]), None, &|| false)?;
            eprintln!("export iff {:.2}s", started.elapsed().as_secs_f64());
        }
        _ => eprintln!("usage: import_check inspect|render|iff ..."),
    }
    Ok(())
}
