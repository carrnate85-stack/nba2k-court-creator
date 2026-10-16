//! Small 2D helpers shared by stock geometry, paint regions and rasterization.
use i_overlay::core::fill_rule::FillRule;
use i_overlay::core::overlay_rule::OverlayRule;
use i_overlay::float::single::SingleFloatOverlay;

pub type Point = [f64; 2];
pub type Polygon = Vec<Point>;

pub fn cross(a: Point, b: Point, p: Point) -> f64 {
    (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
}

pub fn area(polygon: &[Point]) -> f64 {
    if polygon.is_empty() {
        return 0.0;
    }
    let mut sum = 0.0;
    for i in 0..polygon.len() {
        let a = polygon[i];
        let b = polygon[(i + 1) % polygon.len()];
        sum += a[0] * b[1] - b[0] * a[1];
    }
    sum.abs() / 2.0
}

pub fn signed_area(polygon: &[Point]) -> f64 {
    let mut sum = 0.0;
    for i in 0..polygon.len() {
        let a = polygon[i];
        let b = polygon[(i + 1) % polygon.len()];
        sum += a[0] * b[1] - b[0] * a[1];
    }
    sum / 2.0
}

/// Sutherland-Hodgman clip of `polygon` against a convex `boundary` (either winding).
pub fn clip_convex(polygon: &[Point], boundary: &[Point]) -> Polygon {
    if boundary.len() < 3 {
        return Vec::new();
    }
    let orientation = if cross(boundary[0], boundary[1], boundary[2]) >= 0.0 { 1.0 } else { -1.0 };
    let mut result: Polygon = polygon.to_vec();
    for i in 0..boundary.len() {
        if result.is_empty() {
            break;
        }
        let start = boundary[i];
        let end = boundary[(i + 1) % boundary.len()];
        let source = std::mem::take(&mut result);
        let mut previous = *source.last().unwrap();
        let mut previous_distance = orientation * cross(start, end, previous);
        for &point in &source {
            let distance = orientation * cross(start, end, point);
            if (distance >= 0.0) != (previous_distance >= 0.0) {
                let ratio = previous_distance / (previous_distance - distance);
                result.push([previous[0] + ratio * (point[0] - previous[0]), previous[1] + ratio * (point[1] - previous[1])]);
            }
            if distance >= 0.0 {
                result.push(point);
            }
            previous = point;
            previous_distance = distance;
        }
    }
    result
}

/// Andrew's monotone chain; returns a counter-clockwise hull without the closing point.
pub fn convex_hull(points: &[Point]) -> Polygon {
    let mut sorted: Vec<Point> = points.to_vec();
    sorted.sort_by(|a, b| a[0].total_cmp(&b[0]).then(a[1].total_cmp(&b[1])));
    sorted.dedup();
    if sorted.len() < 3 {
        return sorted;
    }
    let mut hull: Vec<Point> = Vec::with_capacity(sorted.len() * 2);
    for &p in &sorted {
        while hull.len() >= 2 && cross(hull[hull.len() - 2], hull[hull.len() - 1], p) <= 0.0 {
            hull.pop();
        }
        hull.push(p);
    }
    let lower = hull.len() + 1;
    for &p in sorted.iter().rev().skip(1) {
        while hull.len() >= lower && cross(hull[hull.len() - 2], hull[hull.len() - 1], p) <= 0.0 {
            hull.pop();
        }
        hull.push(p);
    }
    hull.pop();
    hull
}

/// Hull in Shapely/GEOS ring order: clockwise (y-up sense), starting at the lowest-y, then
/// lowest-x vertex. Clipping against it then yields exactly the original polygon vertices.
pub fn convex_hull_geos(points: &[Point]) -> Polygon {
    let hull = convex_hull(points);
    if hull.len() < 3 {
        return hull;
    }
    let mut clockwise: Polygon = std::iter::once(hull[0]).chain(hull[1..].iter().rev().copied()).collect();
    let start = (0..clockwise.len())
        .min_by(|&a, &b| clockwise[a][1].total_cmp(&clockwise[b][1]).then(clockwise[a][0].total_cmp(&clockwise[b][0])))
        .unwrap();
    clockwise.rotate_left(start);
    clockwise
}

/// Even-odd point test, adequate for the simple convex/triangulated regions used here.
pub fn contains(polygon: &[Point], p: Point) -> bool {
    let mut inside = false;
    let mut j = polygon.len().wrapping_sub(1);
    for i in 0..polygon.len() {
        let (a, b) = (polygon[i], polygon[j]);
        if (a[1] > p[1]) != (b[1] > p[1]) && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0] {
            inside = !inside;
        }
        j = i;
    }
    inside
}

/// Nonzero union-style test across many polygons (any polygon containing the point).
pub fn any_contains(polygons: &[Polygon], p: Point) -> bool {
    polygons.iter().any(|polygon| polygon.len() >= 3 && contains(polygon, p))
}

pub fn bounds(polygons: &[Polygon]) -> Option<[f64; 4]> {
    let mut points = polygons.iter().flatten();
    let first = points.next()?;
    let mut b = [first[0], first[1], first[0], first[1]];
    for p in points {
        b[0] = b[0].min(p[0]);
        b[1] = b[1].min(p[1]);
        b[2] = b[2].max(p[0]);
        b[3] = b[3].max(p[1]);
    }
    Some(b)
}

/// A shape is a list of contours: the first is the outer boundary, the rest are holes.
pub type Shape = Vec<Polygon>;

fn to_paths(polygons: &[Polygon]) -> Vec<Vec<[f64; 2]>> {
    polygons.iter().filter(|p| p.len() >= 3).cloned().collect()
}

/// Boolean overlay with the nonzero fill rule (union semantics inside each operand).
pub fn overlay(subject: &[Polygon], clip: &[Polygon], rule: OverlayRule) -> Vec<Shape> {
    let subject = to_paths(subject);
    let clip = to_paths(clip);
    subject.overlay(&clip, rule, FillRule::NonZero)
}

pub fn difference(subject: &[Polygon], clip: &[Polygon]) -> Vec<Shape> {
    overlay(subject, clip, OverlayRule::Difference)
}
pub fn intersection(subject: &[Polygon], clip: &[Polygon]) -> Vec<Shape> {
    overlay(subject, clip, OverlayRule::Intersect)
}
pub fn union(subject: &[Polygon]) -> Vec<Shape> {
    overlay(subject, &[], OverlayRule::Subject)
}

pub fn shapes_area(shapes: &[Shape]) -> f64 {
    shapes.iter().map(|shape| {
        let mut iter = shape.iter();
        let outer = iter.next().map(|p| area(p)).unwrap_or(0.0);
        outer - iter.map(|p| area(p)).sum::<f64>()
    }).sum()
}

/// Triangulates shapes with holes (earcut), verifying that coverage is retained.
pub fn triangulate(shapes: &[Shape]) -> Result<Vec<Polygon>, String> {
    let mut triangles = Vec::new();
    for shape in shapes {
        let mut vertices: Vec<f64> = Vec::new();
        let mut holes: Vec<usize> = Vec::new();
        for (index, contour) in shape.iter().enumerate() {
            if index > 0 {
                holes.push(vertices.len() / 2);
            }
            for p in contour {
                vertices.push(p[0]);
                vertices.push(p[1]);
            }
        }
        let indices = earcutr::earcut(&vertices, &holes, 2).map_err(|_| "Court region triangulation failed.".to_string())?;
        for tri in indices.chunks_exact(3) {
            let polygon: Polygon = tri.iter().map(|&i| [vertices[i * 2], vertices[i * 2 + 1]]).collect();
            if area(&polygon) > 0.0 {
                triangles.push(polygon);
            }
        }
    }
    let expected = shapes_area(shapes);
    let actual: f64 = triangles.iter().map(|t| area(t)).sum();
    if triangles.is_empty() || (expected - actual).abs() > (1e-6f64).max(expected.abs() * 1e-7) {
        return Err("Court region triangulation lost coverage.".into());
    }
    Ok(triangles)
}

/// Flattens shapes to simple polygons for nonzero rendering (holes keep reverse winding).
pub fn shape_contours(shapes: &[Shape]) -> Vec<Polygon> {
    let mut contours = Vec::new();
    for shape in shapes {
        for (index, contour) in shape.iter().enumerate() {
            let mut contour = contour.clone();
            let ccw = signed_area(&contour) > 0.0;
            // Outer contours positive, holes negative, so nonzero filling leaves holes empty.
            if (index == 0) != ccw {
                contour.reverse();
            }
            contours.push(contour);
        }
    }
    contours
}
