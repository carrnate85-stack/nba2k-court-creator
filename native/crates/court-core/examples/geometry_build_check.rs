//! Verification CLI for the stock geometry builder (never shipped).
//! geometry_build_check build <game_root> <out.json>
//! geometry_build_check prepare [game_root]
//! geometry_build_check compare <built.json> <reference.json>
use court_core::{geometry, geometry_build, pyjson, raster};
use serde_json::Value;
use std::path::Path;

const TRIANGULATED: [&str; 4] = ["two-point-left", "two-point-right", "college-three", "high-school-three"];

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("build") if args.len() == 3 => build(&args[1], &args[2]),
        Some("prepare") if args.len() <= 2 => prepare(args.get(1).map(String::as_str)),
        Some("compare") if args.len() == 3 => compare(&args[1], &args[2]),
        _ => {
            eprintln!("geometry_build_check build <game_root> <out.json> | prepare [game_root] | compare <built.json> <reference.json>");
            std::process::exit(2)
        }
    };
    if let Err(error) = result {
        eprintln!("error: {error}");
        std::process::exit(1);
    }
}

fn build(game: &str, out: &str) -> court_core::Result<()> {
    let started = std::time::Instant::now();
    let document = geometry_build::build(Path::new(game), &|| false)?;
    eprintln!("built in {:?}; valid={}", started.elapsed(), geometry::validate(&document));
    std::fs::write(out, pyjson::dumps(&document, pyjson::Style::COMPACT))?;
    println!("revision {}", geometry::revision(&document));
    Ok(())
}

fn prepare(game: Option<&str>) -> court_core::Result<()> {
    let started = std::time::Instant::now();
    let result = geometry_build::prepare(game.map(Path::new), &|| false)?;
    eprintln!("prepared {} in {:?}", geometry_build::cache_path().display(), started.elapsed());
    println!("revision {}", result.revision);
    Ok(())
}

#[derive(Default)]
struct Diff {
    numbers: usize,
    differing: usize,
    max_delta: f64,
    structural: Vec<String>,
}

fn walk(a: &Value, b: &Value, path: &str, diff: &mut Diff) {
    match (a, b) {
        (Value::Object(x), Value::Object(y)) => {
            let (kx, ky): (Vec<_>, Vec<_>) = (x.keys().collect(), y.keys().collect());
            if kx != ky {
                diff.structural.push(format!("{path}: keys {kx:?} vs {ky:?}"));
            }
            for (key, value) in x {
                if let Some(other) = y.get(key) {
                    walk(value, other, &format!("{path}.{key}"), diff);
                }
            }
        }
        (Value::Array(x), Value::Array(y)) => {
            if x.len() != y.len() {
                diff.structural.push(format!("{path}: length {} vs {}", x.len(), y.len()));
                return;
            }
            for (i, (p, q)) in x.iter().zip(y).enumerate() {
                walk(p, q, &format!("{path}[{i}]"), diff);
            }
        }
        (Value::Number(p), Value::Number(q)) => {
            diff.numbers += 1;
            if p.is_f64() != q.is_f64() {
                diff.structural.push(format!("{path}: number type {p} vs {q}"));
            }
            let (p, q) = (p.as_f64().unwrap(), q.as_f64().unwrap());
            if p.to_bits() != q.to_bits() {
                diff.differing += 1;
                diff.max_delta = diff.max_delta.max((p - q).abs());
                if diff.differing <= 10 {
                    eprintln!("  {path}: {p:?} vs {q:?}");
                }
            }
        }
        _ => {
            if a != b {
                diff.structural.push(format!("{path}: {a} vs {b}"));
            }
        }
    }
}

fn report(label: &str, a: &Value, b: &Value) {
    let mut diff = Diff::default();
    walk(a, b, label, &mut diff);
    println!("{label}: numbers={} differing={} maxDelta={:e} structural={}", diff.numbers, diff.differing, diff.max_delta, diff.structural.len());
    for line in diff.structural.iter().take(10) {
        println!("    {line}");
    }
}

fn polygons(value: &Value) -> Vec<Vec<[f64; 2]>> {
    value.as_array().unwrap().iter().map(|p| p.as_array().unwrap().iter().map(|q| [q[0].as_f64().unwrap(), q[1].as_f64().unwrap()]).collect()).collect()
}

fn coverage_mask(polygons: &[Vec<[f64; 2]>]) -> raster::Mask {
    let mut mask = raster::Mask::new(8192, 4096);
    for polygon in polygons {
        raster::fill_mask(&mut mask, polygon, 255);
    }
    mask
}

fn compare(built: &str, reference: &str) -> court_core::Result<()> {
    let a = pyjson::read_file(Path::new(built), 64 << 20, 64)?;
    let b = pyjson::read_file(Path::new(reference), 64 << 20, 64)?;
    println!("validate built={} reference={}", geometry::validate(&a), geometry::validate(&b));
    let keys = |v: &Value| v.as_object().unwrap().keys().cloned().collect::<Vec<_>>();
    println!("top-level keys equal: {}", keys(&a) == keys(&b));
    for key in ["version", "source", "size", "alignment", "guides", "regulationCalibration"] {
        report(key, &a[key], &b[key]);
    }
    for key in ["texcoord", "matrix", "surfaceTriangles", "courtSurfacePolygons", "hardwoodBounds", "materialParameters", "shaderTransform", "inGameVerified"] {
        report(&format!("gameUv.{key}"), &a["gameUv"][key], &b["gameUv"][key]);
    }
    for group in ["layers", "paints"] {
        let (x, y) = (a[group].as_array().unwrap(), b[group].as_array().unwrap());
        let ids = |list: &Vec<Value>| list.iter().map(|l| l["id"].as_str().unwrap().to_string()).collect::<Vec<_>>();
        println!("{group}: order equal {}", ids(x) == ids(y));
        for layer in x {
            let id = layer["id"].as_str().unwrap();
            let Some(other) = y.iter().find(|l| l["id"] == layer["id"]) else {
                println!("{id}: missing from reference");
                continue;
            };
            if TRIANGULATED.contains(&id) {
                let mut meta_a = layer.clone();
                let mut meta_b = other.clone();
                for m in [&mut meta_a, &mut meta_b] {
                    let o = m.as_object_mut().unwrap();
                    o.insert("polygons".into(), Value::Null);
                    o.insert("gameUvPolygons".into(), Value::Null);
                }
                report(&format!("{id}(meta)"), &meta_a, &meta_b);
                for key in ["polygons", "gameUvPolygons"] {
                    let (p, q) = (polygons(&layer[key]), polygons(&other[key]));
                    let (area_p, area_q): (f64, f64) = (p.iter().map(|t| court_core::geom::area(t)).sum(), q.iter().map(|t| court_core::geom::area(t)).sum());
                    println!("  {id}.{key}: count {} vs {}, area {area_p:.4} vs {area_q:.4} (rel {:e})", p.len(), q.len(), (area_p - area_q).abs() / area_q);
                }
                let (mp, mq) = (coverage_mask(&polygons(&layer["gameUvPolygons"])), coverage_mask(&polygons(&other["gameUvPolygons"])));
                let differing = mp.data.iter().zip(&mq.data).filter(|(s, t)| s != t).count();
                let covered = mq.data.iter().filter(|&&v| v != 0).count();
                let w = 8192usize;
                let near_edge = mp.data.iter().zip(&mq.data).enumerate().filter(|(_, (s, t))| s != t).filter(|(i, _)| {
                    let (x, y) = (i % w, i / w);
                    (y.saturating_sub(1)..=(y + 1).min(4095)).any(|yy| (x.saturating_sub(1)..=(x + 1).min(w - 1)).any(|xx| mq.data[yy * w + xx] != mq.data[*i]))
                }).count();
                println!("  {id}: raster 8192x4096 differing pixels {differing} of {covered} covered ({near_edge} adjacent to the reference edge)");
                let (cp, cq) = (a["gameUv"]["projectionCoverage"][id].as_f64().unwrap(), b["gameUv"]["projectionCoverage"][id].as_f64().unwrap());
                println!("  {id}: coverage {cp} vs {cq}");
            } else {
                report(id, layer, other);
                report(&format!("{id}(coverage)"), &a["gameUv"]["projectionCoverage"][id], &b["gameUv"]["projectionCoverage"][id]);
            }
        }
    }
    let coverage_keys = |v: &Value| v["gameUv"]["projectionCoverage"].as_object().unwrap().keys().cloned().collect::<Vec<_>>();
    println!("coverage key order equal: {}", coverage_keys(&a) == coverage_keys(&b));
    Ok(())
}
