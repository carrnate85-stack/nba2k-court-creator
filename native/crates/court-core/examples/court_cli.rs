//! Development and verification CLI (never shipped): exercises the engine off-screen.
use court_core::{geometry::Geometry, project, raster, render};
use std::path::{Path, PathBuf};

fn usage() -> ! {
    eprintln!("court_cli revision <geometry.json>\ncourt_cli render <geometry.json> <project.court.json> <out.png> [scale]\ncourt_cli compare <a.png> <b.png>");
    std::process::exit(2)
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("revision") if args.len() == 2 => Geometry::load(Path::new(&args[1])).map(|g| println!("{}", g.revision)),
        Some("render") if args.len() >= 4 => render_project(&args[1], &args[2], &args[3], args.get(4).and_then(|s| s.parse().ok()).unwrap_or(1.0)),
        Some("compare") if args.len() == 3 => compare(&args[1], &args[2]),
        Some("prepare-base") if args.len() == 2 => prepare_base(&args[1]),
        Some("verify-base") if args.len() == 3 => verify_base(&args[1], &args[2]),
        Some("export-iff") if args.len() == 4 => export_iff(&args[1], &args[2], &args[3]),
        Some("export-png") if args.len() == 4 => export_png(&args[1], &args[2], &args[3]),
        _ => usage(),
    };
    if let Err(error) = result {
        eprintln!("error: {error}");
        std::process::exit(1);
    }
}

fn render_project(geometry: &str, project_path: &str, out: &str, scale: f64) -> court_core::Result<()> {
    let geometry = Geometry::load(Path::new(geometry))?;
    let project = project::read(Path::new(project_path))?;
    let base = Path::new(project_path).parent().unwrap().to_path_buf();
    let resolve = |p: &str| -> PathBuf { project::absolute(p, &base) };
    let request = render::RenderRequest::from_project(&project, &resolve)?;
    let started = std::time::Instant::now();
    let image = render::render(&geometry, &request, scale, &mut Default::default(), &|| false)?;
    eprintln!("rendered {}x{} in {:?}", image.width, image.height, started.elapsed());
    std::fs::write(out, raster::encode_png(&image, true)?)?;
    Ok(())
}

fn compare(a: &str, b: &str) -> court_core::Result<()> {
    let a = raster::decode_file(Path::new(a), None)?;
    let b = raster::decode_file(Path::new(b), None)?;
    if (a.width, a.height) != (b.width, b.height) {
        println!("size mismatch {}x{} vs {}x{}", a.width, a.height, b.width, b.height);
        return Ok(());
    }
    let mut differing = 0u64;
    let mut max = 0u8;
    let mut total = 0u64;
    let mut over8 = 0u64;
    for (p, q) in a.data.chunks_exact(4).zip(b.data.chunks_exact(4)) {
        let d = (0..4).map(|c| p[c].abs_diff(q[c])).max().unwrap();
        if d > 0 {
            differing += 1;
        }
        if d > 8 {
            over8 += 1;
        }
        max = max.max(d);
        total += d as u64;
    }
    let pixels = (a.width * a.height) as f64;
    println!("{{\"pixels\":{},\"differing\":{},\"over8\":{},\"maxChannelDelta\":{},\"meanDelta\":{:.5},\"differingFraction\":{:.6},\"over8Fraction\":{:.6}}}", pixels, differing, over8, max, total as f64 / pixels, differing as f64 / pixels, over8 as f64 / pixels);
    Ok(())
}

fn prepare_base(game: &str) -> court_core::Result<()> {
    let started = std::time::Instant::now();
    let result = court_core::iff::prepare_base(Some(Path::new(game)), &|| false)?;
    eprintln!("prepared {} in {:?}", court_core::iff::base_path().display(), started.elapsed());
    println!("{}", serde_json::to_string_pretty(&result).unwrap());
    Ok(())
}

/// Compares a prepared base with a reference base: scene, geometry, UV1 fit and other members.
fn verify_base(candidate: &str, reference: &str) -> court_core::Result<()> {
    use court_core::{scene::Scene, ziparc::Archive};
    let a = Archive::open(Path::new(candidate), 1 << 29)?;
    let b = Archive::open(Path::new(reference), 1 << 29)?;
    a.test()?;
    let scene_a = a.read("level_floor.SCNE", 1 << 24)?;
    let scene_b = b.read("level_floor.SCNE", 1 << 24)?;
    println!("comment: {:?}", String::from_utf8_lossy(&a.comment));
    println!("scene bytes identical: {} ({} vs {})", scene_a == scene_b, scene_a.len(), scene_b.len());
    let names_a: Vec<&str> = a.names().collect();
    let names_b: Vec<&str> = b.names().collect();
    println!("member names identical: {}", names_a == names_b);
    let mut different = Vec::new();
    for name in &names_a {
        if !b.contains(name) { different.push(format!("{name} (missing in reference)")); continue; }
        if a.read(name, 1 << 29)? != b.read(name, 1 << 29)? { different.push(name.to_string()); }
    }
    println!("members with different contents: {different:?}");
    let scene = Scene::parse(&scene_a)?;
    let model = scene.models().find(|m| m.get("VertexStream").is_some()).unwrap();
    let vertex_name = model["VertexStream"][0]["Binary"].as_str().unwrap();
    let raw_a = a.read(vertex_name, 1 << 24)?;
    let raw_b = b.read(vertex_name, 1 << 24)?;
    let mut uv_diff = 0; let mut other_diff = 0;
    for i in (0..raw_a.len().min(raw_b.len())).step_by(28) {
        if raw_a[i..i + 16] != raw_b[i..i + 16] || raw_a[i + 20..i + 28] != raw_b[i + 20..i + 28] { other_diff += 1; }
        if raw_a[i + 16..i + 20] != raw_b[i + 16..i + 20] { uv_diff += 1; }
    }
    println!("vertex buffer {vertex_name}: {} bytes, {} vertices, non-UV1 field differences {other_diff}, UV1 differences {uv_diff}", raw_a.len(), raw_a.len() / 28);
    let (min, max) = (model["Min"].clone(), model["Max"].clone());
    println!("bounds Min {min} Max {max}");
    let mut xs = (f32::MAX, f32::MIN); let mut zs = (f32::MAX, f32::MIN); let mut ys = 0f32;
    for i in (0..raw_a.len()).step_by(28) {
        let f = |k: usize| f32::from_le_bytes(raw_a[i + k..i + k + 4].try_into().unwrap());
        xs = (xs.0.min(f(0)), xs.1.max(f(0))); zs = (zs.0.min(f(8)), zs.1.max(f(8))); ys = ys.max(f(4).abs());
    }
    println!("decoded X {:?} Z {:?} |Y|max {}", xs, zs, ys);
    Ok(())
}

fn request_for(project_path: &str) -> court_core::Result<render::RenderRequest> {
    let project = project::read(Path::new(project_path))?;
    let base = Path::new(project_path).parent().unwrap().to_path_buf();
    render::RenderRequest::from_project(&project, &|p: &str| project::absolute(p, &base))
}

fn export_iff(geometry: &str, project_path: &str, out: &str) -> court_core::Result<()> {
    let geometry = Geometry::load(Path::new(geometry))?;
    let request = request_for(project_path)?;
    let started = std::time::Instant::now();
    court_core::export::export_iff(&geometry, &request, Path::new(out), None, &court_core::export::Progress { report: &|m| eprintln!("{:?} {m}", started.elapsed()), cancelled: &|| false })?;
    eprintln!("IFF written in {:?}", started.elapsed());
    Ok(())
}

fn export_png(geometry: &str, project_path: &str, out: &str) -> court_core::Result<()> {
    let geometry = Geometry::load(Path::new(geometry))?;
    let request = request_for(project_path)?;
    let started = std::time::Instant::now();
    court_core::export::export_png(&geometry, &request, Path::new(out), &court_core::export::Progress { report: &|m| eprintln!("{:?} {m}", started.elapsed()), cancelled: &|| false })?;
    eprintln!("PNG written in {:?}", started.elapsed());
    Ok(())
}
