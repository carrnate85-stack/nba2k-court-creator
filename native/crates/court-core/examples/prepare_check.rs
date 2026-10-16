//! Development check for first-run floor preparation (never shipped).
//!   prepare_check prepare <library_root> <game_root> [cancel_after_progress]
//!   prepare_check ready <library_root>
//!   prepare_check candidates <game_root>            (id, name, category per candidate)
//!   prepare_check thumbnail <in.png> <out.jpg>      (Pillow-style thumbnail of a PNG)
use court_core::prepare::{self, Event};
use court_core::{game, raster};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Instant;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("prepare") if args.len() >= 3 => run(&args[1], &args[2], args.get(3).and_then(|s| s.parse().ok())),
        Some("ready") if args.len() == 2 => {
            println!("ready={}", prepare::ready(Path::new(&args[1])));
            Ok(())
        }
        Some("candidates") if args.len() == 2 => candidates(&args[1]),
        Some("thumbnail") if args.len() == 3 => thumbnail(&args[1], &args[2]),
        _ => {
            eprintln!("usage: see source");
            std::process::exit(2)
        }
    };
    if let Err(error) = result {
        eprintln!("error: {error}");
        std::process::exit(1);
    }
}

fn run(library: &str, game_root: &str, cancel_after: Option<usize>) -> court_core::Result<()> {
    let started = Instant::now();
    let progress = AtomicUsize::new(0);
    let cancel = AtomicBool::new(false);
    let report = |event: Event| {
        let t = started.elapsed().as_secs_f64();
        if let Event::Progress { done, .. } = &event {
            progress.store(*done, Ordering::SeqCst);
            if cancel_after.is_some_and(|n| *done >= n) {
                cancel.store(true, Ordering::SeqCst);
            }
        }
        println!("[{t:8.2}s] {event:?}");
    };
    let cancelled = || cancel.load(Ordering::SeqCst);
    let outcome = prepare::prepare(Path::new(library), Path::new(game_root), &report, &cancelled);
    println!("[{:8.2}s] result: {:?}", started.elapsed().as_secs_f64(), outcome.as_ref().map_err(|e| e.to_string()));
    println!("ready={}", prepare::ready(Path::new(library)));
    outcome.map(|_| ()).or_else(|e| if e.is_cancelled() { Ok(()) } else { Err(e) })
}

fn candidates(game_root: &str) -> court_core::Result<()> {
    let root = Path::new(game_root);
    let started = Instant::now();
    let manifest = game::read_manifest(root)?;
    let list = prepare::find_candidates(root, &manifest)?;
    eprintln!("{} candidates in {:?}", list.len(), started.elapsed());
    for c in list {
        let name = prepare::friendly_name(&c.name);
        println!("{}\t{}\t{}\t{}", prepare::template_id_for(&c.name, "2k27"), name, prepare::category_for_name(&name), c.name);
    }
    Ok(())
}

fn thumbnail(input: &str, output: &str) -> court_core::Result<()> {
    let mut image = raster::decode(&std::fs::read(input)?, Some("png"))?;
    image.set_alpha(255);
    let small = prepare::pillow_thumbnail(&image, 360, 180);
    std::fs::write(output, raster::encode_png(&small, false)?)?;
    Ok(())
}

