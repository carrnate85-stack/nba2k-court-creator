//! Straight-RGBA raster operations reproducing the Pillow behavior used by the 1.7.17 exporter:
//! integer polygon filling, Lanczos/bicubic resampling with premultiplied alpha, bicubic affine
//! rotation and `alpha_composite`. Exports therefore keep the verified pixel layout.
use crate::error::{Error, Result};
use rayon::prelude::*;

#[derive(Clone, Debug)]
pub struct Rgba {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>,
}

pub const MAX_SIDE: u32 = 16_384;
pub const MAX_PIXELS: u64 = 64 * 1024 * 1024;

impl Rgba {
    pub fn new(width: u32, height: u32, fill: [u8; 4]) -> Self {
        let mut data = vec![0u8; width as usize * height as usize * 4];
        if fill != [0, 0, 0, 0] {
            for pixel in data.chunks_exact_mut(4) {
                pixel.copy_from_slice(&fill);
            }
        }
        Self { width, height, data }
    }
    pub fn from_raw(width: u32, height: u32, data: Vec<u8>) -> Result<Self> {
        if data.len() != width as usize * height as usize * 4 {
            return Err(Error::msg("Image dimensions do not match its pixel data."));
        }
        Ok(Self { width, height, data })
    }
    #[inline]
    pub fn pixel(&self, x: u32, y: u32) -> [u8; 4] {
        let i = (y as usize * self.width as usize + x as usize) * 4;
        [self.data[i], self.data[i + 1], self.data[i + 2], self.data[i + 3]]
    }
    pub fn set_alpha(&mut self, alpha: u8) {
        for pixel in self.data.chunks_exact_mut(4) {
            pixel[3] = alpha;
        }
    }
    pub fn mirror(&self) -> Self {
        let mut out = self.clone();
        let w = self.width as usize;
        out.data.par_chunks_exact_mut(w * 4).zip(self.data.par_chunks_exact(w * 4)).for_each(|(dst, src)| {
            for x in 0..w {
                dst[x * 4..x * 4 + 4].copy_from_slice(&src[(w - 1 - x) * 4..(w - x) * 4]);
            }
        });
        out
    }
    pub fn flip(&self) -> Self {
        let mut out = self.clone();
        let row = self.width as usize * 4;
        let h = self.height as usize;
        for y in 0..h {
            out.data[y * row..(y + 1) * row].copy_from_slice(&self.data[(h - 1 - y) * row..(h - y) * row]);
        }
        out
    }
    /// Lossless quarter turns (Pillow's transpose fast paths for rotate 90/180/270).
    pub fn rotate_quarter(&self, turns: u32) -> Self {
        let (w, h) = (self.width as usize, self.height as usize);
        match turns % 4 {
            0 => self.clone(),
            2 => {
                let mut out = self.clone();
                for (i, pixel) in self.data.chunks_exact(4).enumerate() {
                    let j = w * h - 1 - i;
                    out.data[j * 4..j * 4 + 4].copy_from_slice(pixel);
                }
                out
            }
            turns => {
                // ROTATE_90 is counter-clockwise; ROTATE_270 is clockwise.
                let mut out = Rgba::new(h as u32, w as u32, [0; 4]);
                for y in 0..h {
                    for x in 0..w {
                        let (nx, ny) = if turns == 1 { (y, w - 1 - x) } else { (h - 1 - y, x) };
                        let src = (y * w + x) * 4;
                        let dst = (ny * h + nx) * 4;
                        out.data[dst..dst + 4].copy_from_slice(&self.data[src..src + 4]);
                    }
                }
                out
            }
        }
    }
    /// `image.crop(box)` with integer box; areas outside the source become transparent black.
    pub fn crop(&self, left: i64, top: i64, right: i64, bottom: i64) -> Self {
        let (w, h) = ((right - left).max(0) as u32, (bottom - top).max(0) as u32);
        let mut out = Rgba::new(w, h, [0; 4]);
        for y in 0..h as i64 {
            let sy = y + top;
            if sy < 0 || sy >= self.height as i64 {
                continue;
            }
            for x in 0..w as i64 {
                let sx = x + left;
                if sx < 0 || sx >= self.width as i64 {
                    continue;
                }
                let s = (sy as usize * self.width as usize + sx as usize) * 4;
                let d = (y as usize * w as usize + x as usize) * 4;
                out.data[d..d + 4].copy_from_slice(&self.data[s..s + 4]);
            }
        }
        out
    }
}

/// 8-bit coverage mask (Pillow "L").
#[derive(Clone, Debug)]
pub struct Mask {
    pub width: u32,
    pub height: u32,
    pub data: Vec<u8>,
}

impl Mask {
    pub fn new(width: u32, height: u32) -> Self {
        Self { width, height, data: vec![0; width as usize * height as usize] }
    }
}

// --- Polygon filling (Pillow ImagingDrawPolygon / polygon_generic with integer vertices) ---

/// Pillow `Edge`: original first vertex, bounds and a single-precision slope.
struct Edge {
    x0: i32,
    y0: i32,
    xmin: i32,
    ymin: i32,
    xmax: i32,
    ymax: i32,
    dx: f32,
}

impl Edge {
    fn new(x0: i32, y0: i32, x1: i32, y1: i32) -> Self {
        let dx = if y0 == y1 { 0.0 } else { (x1 - x0) as f32 / (y1 - y0) as f32 };
        Self { x0, y0, xmin: x0.min(x1), xmax: x0.max(x1), ymin: y0.min(y1), ymax: y0.max(y1), dx }
    }
    #[inline]
    fn x_at(&self, y: i32) -> f32 {
        (y - self.y0) as f32 * self.dx + self.x0 as f32
    }
}

#[inline]
fn round_up(f: f32) -> i32 {
    if f >= 0.0 { (f + 0.5).floor() as i32 } else { -((f.abs() + 0.5).floor() as i32) }
}
#[inline]
fn round_down(f: f32) -> i32 {
    if f >= 0.0 { (f - 0.5).ceil() as i32 } else { -((f.abs() - 0.5).ceil() as i32) }
}

/// `ImagingDrawPolygon` edge list, including its merging of consecutive horizontal runs.
fn edges(xy: &[(i32, i32)]) -> Vec<Edge> {
    let count = xy.len();
    let mut e: Vec<Edge> = Vec::with_capacity(count);
    let mut i = 0;
    while i < count - 1 {
        let (x0, y0) = xy[i];
        let (x1, y1) = xy[i + 1];
        if y0 == y1 && i != 0 && y0 == xy[i - 1].1 {
            let previous_x = xy[i - 1].0;
            if let Some(last) = e.last_mut() {
                if x1 > x0 && x0 > previous_x {
                    last.xmax = x1;
                    i += 1;
                    continue;
                } else if x1 < x0 && x0 < previous_x {
                    last.xmin = x1;
                    i += 1;
                    continue;
                }
            }
        }
        e.push(Edge::new(x0, y0, x1, y1));
        i += 1;
    }
    if xy[count - 1] != xy[0] {
        e.push(Edge::new(xy[count - 1].0, xy[count - 1].1, xy[0].0, xy[0].1));
    }
    e
}

/// Calls `span(y, x0, x1)` exactly where Pillow 12.3's `polygon_generic` (no alpha) draws `hline`.
fn polygon_spans(polygon: &[[f64; 2]], width: u32, height: u32, mut span: impl FnMut(usize, usize, usize)) {
    if polygon.is_empty() {
        return;
    }
    let (w, h) = (width as i32, height as i32);
    let mut hline = |x0: i32, y: i32, x1: i32| {
        if y < 0 || y >= h {
            return;
        }
        let x0 = if x0 < 0 { 0 } else if x0 >= w { return } else { x0 };
        let x1 = if x1 < 0 { return } else if x1 >= w { w - 1 } else { x1 };
        if x0 <= x1 {
            span(y as usize, x0 as usize, x1 as usize);
        }
    };
    // `_draw_polygon` truncates the flattened coordinates to C ints.
    let xy: Vec<(i32, i32)> = polygon.iter().map(|p| (p[0] as i32, p[1] as i32)).collect();
    let e = edges(&xy);
    let mut ymin = h - 1;
    let mut ymax = 0;
    let mut table: Vec<&Edge> = Vec::with_capacity(e.len());
    for edge in &e {
        ymin = ymin.min(edge.ymin);
        ymax = ymax.max(edge.ymax);
        if edge.ymin == edge.ymax {
            hline(edge.xmin, edge.ymin, edge.xmax);
            continue;
        }
        table.push(edge);
    }
    let ymin = ymin.max(0);
    let ymax = ymax.min(h);
    let mut xx: Vec<f32> = Vec::with_capacity(table.len() * 2);
    for y in ymin..=ymax {
        xx.clear();
        for (i, current) in table.iter().enumerate() {
            if y < current.ymin || y > current.ymax {
                continue;
            }
            xx.push(current.x_at(y));
            let last = xx.len() - 1;
            if y == current.ymax && y < ymax {
                // Needed to draw consistent polygons.
                xx.push(xx[last]);
            } else if (y == current.ymin || y == current.ymax) && current.dx != 0.0 {
                // Connect discontiguous corners.
                for other in &table[..i] {
                    if (y != other.ymin && y != other.ymax) || other.dx == 0.0 {
                        continue;
                    }
                    if xx[last].round() == other.x_at(y).round() {
                        let offset = if y == current.ymax { -1 } else { 1 };
                        let adjacent = current.x_at(y + offset);
                        if y + offset >= other.ymin && y + offset <= other.ymax {
                            let adjacent_other = other.x_at(y + offset);
                            if xx[last] > adjacent + 1.0 && xx[last] > adjacent_other + 1.0 {
                                xx[last] = (adjacent as f64).max(adjacent_other as f64).round() as f32 + 1.0;
                            } else if xx[last] < adjacent - 1.0 && xx[last] < adjacent_other - 1.0 {
                                xx[last] = (adjacent as f64).min(adjacent_other as f64).round() as f32 - 1.0;
                            }
                            break;
                        }
                    }
                }
            }
        }
        xx.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
        let mut i = 1;
        while i < xx.len() {
            hline(round_up(xx[i - 1]), y, round_down(xx[i]));
            i += 2;
        }
    }
}

/// `ImageDraw.polygon(points, fill=color)` on an RGBA canvas.
pub fn fill_polygon(image: &mut Rgba, polygon: &[[f64; 2]], color: [u8; 4]) {
    let width = image.width as usize;
    let data = &mut image.data;
    polygon_spans(polygon, image.width, image.height, |y, x0, x1| {
        for x in x0..=x1 {
            data[(y * width + x) * 4..(y * width + x) * 4 + 4].copy_from_slice(&color);
        }
    });
}

pub fn fill_mask(mask: &mut Mask, polygon: &[[f64; 2]], value: u8) {
    let width = mask.width as usize;
    let data = &mut mask.data;
    polygon_spans(polygon, mask.width, mask.height, |y, x0, x1| {
        data[y * width + x0..=y * width + x1].fill(value);
    });
}

/// `ImageChops.multiply` of the alpha channel with a mask (integer division by 255).
pub fn multiply_alpha(image: &mut Rgba, mask: &Mask) {
    image.data.par_chunks_exact_mut(4).zip(mask.data.par_iter()).for_each(|(pixel, &m)| {
        pixel[3] = ((pixel[3] as u32 * m as u32) / 255) as u8;
    });
}

/// `canvas.paste(color, (0, 0), mask)` for an L mask.
pub fn paste_color(image: &mut Rgba, color: [u8; 4], mask: &Mask) {
    image.data.par_chunks_exact_mut(4).zip(mask.data.par_iter()).for_each(|(pixel, &m)| {
        if m == 255 {
            pixel.copy_from_slice(&color);
        } else if m != 0 {
            for c in 0..4 {
                // Pillow BLEND: DIV255(in1 * (255 - mask) + in2 * mask).
                let tmp = pixel[c] as u32 * (255 - m as u32) + color[c] as u32 * m as u32 + 128;
                pixel[c] = (((tmp >> 8) + tmp) >> 8) as u8;
            }
        }
    });
}

// --- Pillow AlphaComposite.c ---

#[inline]
fn composite_pixel(dst: &mut [u8], src: &[u8]) {
    let sa = src[3] as u32;
    if sa == 0 {
        return;
    }
    const PRECISION: u32 = 7;
    let blend = dst[3] as u32 * (255 - sa);
    let outa255 = sa * 255 + blend;
    let coef1 = sa * 255 * 255 * (1 << PRECISION) / outa255;
    let coef2 = 255 * (1 << PRECISION) - coef1;
    let shift = |a: u32| ((a >> 8) + a) >> 8;
    for c in 0..3 {
        let tmp = src[c] as u32 * coef1 + dst[c] as u32 * coef2;
        dst[c] = (shift(tmp + (0x80 << PRECISION)) >> PRECISION) as u8;
    }
    dst[3] = shift(outa255 + 0x80) as u8;
}

/// `canvas.alpha_composite(overlay, (left, top))`, clipped to the canvas.
pub fn alpha_composite(canvas: &mut Rgba, overlay: &Rgba, left: i64, top: i64) {
    let (cw, ch) = (canvas.width as i64, canvas.height as i64);
    let x0 = left.max(0);
    let x1 = (left + overlay.width as i64).min(cw);
    let y0 = top.max(0);
    let y1 = (top + overlay.height as i64).min(ch);
    if x0 >= x1 || y0 >= y1 {
        return;
    }
    let row = cw as usize * 4;
    canvas.data[y0 as usize * row..y1 as usize * row].par_chunks_exact_mut(row).enumerate().for_each(|(offset, line)| {
        let y = y0 + offset as i64;
        let sy = (y - top) as usize;
        for x in x0..x1 {
            let sx = (x - left) as usize;
            let s = (sy * overlay.width as usize + sx) * 4;
            composite_pixel(&mut line[x as usize * 4..x as usize * 4 + 4], &overlay.data[s..s + 4]);
        }
    });
}

/// `image.point(lambda v: round(v * opacity))` applied to alpha (Python rounds half to even).
pub fn scale_alpha(image: &mut Rgba, opacity: f64) {
    let table: Vec<u8> = (0..256).map(|v| (v as f64 * opacity).round_ties_even().clamp(0.0, 255.0) as u8).collect();
    image.data.par_chunks_exact_mut(4).for_each(|pixel| pixel[3] = table[pixel[3] as usize]);
}

// --- Premultiplication (Pillow RGBA <-> RGBa conversions) ---

pub fn premultiply(image: &mut Rgba) {
    image.data.par_chunks_exact_mut(4).for_each(|p| {
        let a = p[3] as u32;
        for c in 0..3 {
            let tmp = p[c] as u32 * a + 128;
            p[c] = (((tmp >> 8) + tmp) >> 8) as u8;
        }
    });
}

pub fn unpremultiply(image: &mut Rgba) {
    image.data.par_chunks_exact_mut(4).for_each(|p| {
        let a = p[3] as u32;
        if a != 0 && a != 255 {
            for c in 0..3 {
                p[c] = ((255 * p[c] as u32) / a).min(255) as u8;
            }
        }
    });
}

// --- Pillow Resample.c (8 bpc fixed point) ---

#[derive(Clone, Copy, PartialEq)]
pub enum Filter {
    Bilinear,
    Bicubic,
    Lanczos,
}

impl Filter {
    fn support(self) -> f64 {
        match self {
            Filter::Bilinear => 1.0,
            Filter::Bicubic => 2.0,
            Filter::Lanczos => 3.0,
        }
    }
    fn weight(self, x: f64) -> f64 {
        match self {
            Filter::Bilinear => {
                let x = x.abs();
                if x < 1.0 { 1.0 - x } else { 0.0 }
            }
            Filter::Bicubic => {
                let a = -0.5;
                let x = x.abs();
                if x < 1.0 {
                    ((a + 2.0) * x - (a + 3.0)) * x * x + 1.0
                } else if x < 2.0 {
                    (((x - 5.0) * x + 8.0) * x - 4.0) * a
                } else {
                    0.0
                }
            }
            Filter::Lanczos => {
                if (-3.0..3.0).contains(&x) { sinc(x) * sinc(x / 3.0) } else { 0.0 }
            }
        }
    }
}

fn sinc(x: f64) -> f64 {
    if x == 0.0 {
        return 1.0;
    }
    let x = x * std::f64::consts::PI;
    x.sin() / x
}

const PRECISION_BITS: u32 = 32 - 8 - 2;

struct Coefficients {
    bounds: Vec<(usize, usize)>,
    kernel: usize,
    weights: Vec<i32>,
}

fn coefficients(in_size: usize, in0: f64, in1: f64, out_size: usize, filter: Filter) -> Coefficients {
    let scale = (in1 - in0) / out_size as f64;
    let filterscale = scale.max(1.0);
    let support = filter.support() * filterscale;
    let kernel = support.ceil() as usize * 2 + 1;
    let mut bounds = Vec::with_capacity(out_size);
    let mut weights = vec![0i32; out_size * kernel];
    let mut values = vec![0f64; kernel];
    for xx in 0..out_size {
        let center = in0 + (xx as f64 + 0.5) * scale;
        let ss = 1.0 / filterscale;
        let xmin = ((center - support + 0.5) as i64).max(0) as usize;
        let mut xmax = ((center + support + 0.5) as i64).min(in_size as i64) as usize;
        xmax = xmax.saturating_sub(xmin);
        let mut total = 0.0;
        for x in 0..xmax {
            let w = filter.weight((x as f64 + xmin as f64 - center + 0.5) * ss);
            values[x] = w;
            total += w;
        }
        for x in 0..kernel {
            let k = if x < xmax && total != 0.0 { values[x] / total } else if x < xmax { values[x] } else { 0.0 };
            let fixed = k * (1u64 << PRECISION_BITS) as f64;
            weights[xx * kernel + x] = if k < 0.0 { (-0.5 + fixed) as i32 } else { (0.5 + fixed) as i32 };
        }
        bounds.push((xmin, xmax));
    }
    Coefficients { bounds, kernel, weights }
}

#[inline]
fn clip8(value: i32) -> u8 {
    (value >> PRECISION_BITS).clamp(0, 255) as u8
}

/// `image.resize(size, filter, box)` on straight RGBA (premultiplied while filtering, as Pillow does).
pub fn resize(image: &Rgba, width: u32, height: u32, filter: Filter, source_box: Option<[f64; 4]>) -> Rgba {
    let box_ = source_box.unwrap_or([0.0, 0.0, image.width as f64, image.height as f64]);
    if width == image.width && height == image.height && box_ == [0.0, 0.0, image.width as f64, image.height as f64] {
        return image.clone();
    }
    let mut source = image.clone();
    premultiply(&mut source);
    let mut result = resample(&source, width, height, filter, box_);
    unpremultiply(&mut result);
    result
}

/// Channel-independent resampling (no premultiplication), used for game-data artwork.
pub fn resample(image: &Rgba, width: u32, height: u32, filter: Filter, box_: [f64; 4]) -> Rgba {
    let (in_w, in_h) = (image.width as usize, image.height as usize);
    let (out_w, out_h) = (width as usize, height as usize);
    let need_h = out_w != in_w || box_[0] != 0.0 || box_[2] != in_w as f64;
    let need_v = out_h != in_h || box_[1] != 0.0 || box_[3] != in_h as f64;
    let vertical = coefficients(in_h, box_[1], box_[3], out_h, filter);
    let first_row = vertical.bounds.first().map(|b| b.0).unwrap_or(0);
    let last_row = vertical.bounds.last().map(|b| b.0 + b.1).unwrap_or(in_h);
    // Horizontal pass over only the rows the vertical pass reads.
    let temp = if need_h {
        let horizontal = coefficients(in_w, box_[0], box_[2], out_w, filter);
        let rows = last_row - first_row;
        let mut temp = vec![0u8; rows * out_w * 4];
        temp.par_chunks_exact_mut(out_w * 4).enumerate().for_each(|(row, line)| {
            let source = &image.data[(row + first_row) * in_w * 4..(row + first_row + 1) * in_w * 4];
            for xx in 0..out_w {
                let (xmin, xmax) = horizontal.bounds[xx];
                let k = &horizontal.weights[xx * horizontal.kernel..];
                let mut ss = [1i32 << (PRECISION_BITS - 1); 4];
                for x in 0..xmax {
                    let p = &source[(x + xmin) * 4..(x + xmin) * 4 + 4];
                    for c in 0..4 {
                        ss[c] = ss[c].wrapping_add(p[c] as i32 * k[x]);
                    }
                }
                for c in 0..4 {
                    line[xx * 4 + c] = clip8(ss[c]);
                }
            }
        });
        Rgba { width: out_w as u32, height: rows as u32, data: temp }
    } else {
        Rgba { width: in_w as u32, height: (last_row - first_row) as u32, data: image.data[first_row * in_w * 4..last_row * in_w * 4].to_vec() }
    };
    if !need_v {
        return Rgba { width: temp.width, height: temp.height, data: temp.data };
    }
    let tw = temp.width as usize;
    let mut out = vec![0u8; out_h * tw * 4];
    out.par_chunks_exact_mut(tw * 4).enumerate().for_each(|(yy, line)| {
        let (ymin, ymax) = vertical.bounds[yy];
        let ymin = ymin - first_row;
        let k = &vertical.weights[yy * vertical.kernel..];
        for xx in 0..tw {
            let mut ss = [1i32 << (PRECISION_BITS - 1); 4];
            for y in 0..ymax {
                let p = &temp.data[((y + ymin) * tw + xx) * 4..((y + ymin) * tw + xx) * 4 + 4];
                for c in 0..4 {
                    ss[c] = ss[c].wrapping_add(p[c] as i32 * k[y]);
                }
            }
            for c in 0..4 {
                line[xx * 4 + c] = clip8(ss[c]);
            }
        }
    });
    Rgba { width: tw as u32, height: out_h as u32, data: out }
}

/// `ImageOps.fit(image, size, LANCZOS)` with centered cropping.
pub fn fit(image: &Rgba, width: u32, height: u32, filter: Filter) -> Rgba {
    let (w, h) = (image.width as f64, image.height as f64);
    let live_ratio = w / h;
    let output_ratio = width as f64 / height as f64;
    let (crop_w, crop_h) = if live_ratio == output_ratio {
        (w, h)
    } else if live_ratio >= output_ratio {
        (output_ratio * h, h)
    } else {
        (w, w / output_ratio)
    };
    let left = (w - crop_w) * 0.5;
    let top = (h - crop_h) * 0.5;
    resize(image, width, height, filter, Some([left, top, left + crop_w, top + crop_h]))
}

// --- Pillow geometry.c bicubic affine transform ---

#[inline]
fn cubic(v1: f64, v2: f64, v3: f64, v4: f64, d: f64) -> f64 {
    let p1 = v2;
    let p2 = -v1 + v3;
    let p3 = 2.0 * (v1 - v2) + v3 - v4;
    let p4 = -v1 + v2 - v3 + v4;
    p1 + d * (p2 + d * (p3 + d * p4))
}

fn bicubic_sample(image: &Rgba, xin: f64, yin: f64) -> Option<[u8; 4]> {
    let (w, h) = (image.width as i64, image.height as i64);
    if xin < 0.0 || xin >= w as f64 || yin < 0.0 || yin >= h as f64 {
        return None;
    }
    let (xin, yin) = (xin - 0.5, yin - 0.5);
    let x = xin.floor() as i64;
    let y = yin.floor() as i64;
    let (dx, dy) = (xin - x as f64, yin - y as f64);
    let (x, y) = (x - 1, y - 1);
    let clip_x = |v: i64| v.clamp(0, w - 1) as usize;
    let row = |yy: i64| yy.clamp(0, h - 1) as usize;
    let xs = [clip_x(x), clip_x(x + 1), clip_x(x + 2), clip_x(x + 3)];
    let mut out = [0u8; 4];
    for c in 0..4 {
        let at = |r: usize, i: usize| image.data[(r * w as usize + xs[i]) * 4 + c] as f64;
        let line = |r: usize| cubic(at(r, 0), at(r, 1), at(r, 2), at(r, 3), dx);
        let v1 = line(row(y));
        let v2 = if y + 1 >= 0 && y + 1 < h { line((y + 1) as usize) } else { v1 };
        let v3 = if y + 2 >= 0 && y + 2 < h { line((y + 2) as usize) } else { v2 };
        let v4 = if y + 3 >= 0 && y + 3 < h { line((y + 3) as usize) } else { v3 };
        let v = cubic(v1, v2, v3, v4, dy);
        out[c] = if v <= 0.0 { 0 } else if v >= 255.0 { 255 } else { v as u8 };
    }
    Some(out)
}

/// `image.transform(size, AFFINE, matrix, BICUBIC, fillcolor=(0,0,0,0))` on premultiplied pixels.
pub fn affine_bicubic(image: &Rgba, width: u32, height: u32, matrix: [f64; 6], premultiplied_alpha: bool) -> Rgba {
    let mut source = image.clone();
    if premultiplied_alpha {
        premultiply(&mut source);
    }
    let mut out = Rgba::new(width, height, [0; 4]);
    out.data.par_chunks_exact_mut(width as usize * 4).enumerate().for_each(|(y, line)| {
        let yin = y as f64 + 0.5;
        for x in 0..width as usize {
            let xin = x as f64 + 0.5;
            let sx = matrix[0] * xin + matrix[1] * yin + matrix[2];
            let sy = matrix[3] * xin + matrix[4] * yin + matrix[5];
            if let Some(pixel) = bicubic_sample(&source, sx, sy) {
                line[x * 4..x * 4 + 4].copy_from_slice(&pixel);
            }
        }
    });
    if premultiplied_alpha {
        unpremultiply(&mut out);
    }
    out
}

fn round15(value: f64) -> f64 {
    (value * 1e15).round() / 1e15
}

/// `image.rotate(angle, expand=True, resample=BICUBIC)` (counter-clockwise degrees).
pub fn rotate_expand(image: &Rgba, angle: f64) -> Rgba {
    let angle = angle.rem_euclid(360.0);
    if angle == 0.0 {
        return image.clone();
    }
    if angle == 180.0 {
        return image.rotate_quarter(2);
    }
    if angle == 90.0 {
        return image.rotate_quarter(1);
    }
    if angle == 270.0 {
        return image.rotate_quarter(3);
    }
    let (w, h) = (image.width as f64, image.height as f64);
    let radians = -angle.to_radians();
    let mut m = [round15(radians.cos()), round15(radians.sin()), 0.0, round15(-radians.sin()), round15(radians.cos()), 0.0];
    let transform = |x: f64, y: f64, m: &[f64; 6]| (m[0] * x + m[1] * y + m[2], m[3] * x + m[4] * y + m[5]);
    let (cx, cy) = (w / 2.0, h / 2.0);
    let (a, b) = transform(-cx, -cy, &m);
    m[2] = a + cx;
    m[5] = b + cy;
    let corners = [(0.0, 0.0), (w, 0.0), (w, h), (0.0, h)].map(|(x, y)| transform(x, y, &m));
    let nw = corners.iter().map(|c| c.0).fold(f64::MIN, f64::max).ceil() - corners.iter().map(|c| c.0).fold(f64::MAX, f64::min).floor();
    let nh = corners.iter().map(|c| c.1).fold(f64::MIN, f64::max).ceil() - corners.iter().map(|c| c.1).fold(f64::MAX, f64::min).floor();
    let (a, b) = transform(-(nw - w) / 2.0, -(nh - h) / 2.0, &m);
    m[2] = a;
    m[5] = b;
    affine_bicubic(image, nw as u32, nh as u32, m, true)
}

// --- Decoding and encoding ---

pub fn check_dimensions(width: u32, height: u32) -> Result<()> {
    if width == 0 || height == 0 || width > MAX_SIDE || height > MAX_SIDE || width as u64 * height as u64 > MAX_PIXELS {
        return Err(Error::msg("Artwork is too large. Use an image up to 16384 pixels per side and 64 megapixels."));
    }
    Ok(())
}

/// Decodes PNG/JPEG/BMP/TGA/TIFF/WebP/GIF (first frame) or a single 2D DDS into straight RGBA.
pub fn decode(bytes: &[u8], extension_hint: Option<&str>) -> Result<Rgba> {
    if bytes.starts_with(b"DDS ") {
        return decode_dds(bytes);
    }
    let mut reader = image::ImageReader::new(std::io::Cursor::new(bytes)).with_guessed_format()?;
    if reader.format().is_none() {
        if let Some(format) = extension_hint.and_then(image::ImageFormat::from_extension) {
            reader.set_format(format);
        }
    }
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(MAX_SIDE);
    limits.max_image_height = Some(MAX_SIDE);
    limits.max_alloc = Some(1024 * 1024 * 1024);
    reader.limits(limits);
    let decoded = reader.decode().map_err(|error| Error::msg(format!("This image is malformed or has an unsupported format: {error}")))?;
    check_dimensions(decoded.width(), decoded.height())?;
    let rgba = decoded.into_rgba8();
    Rgba::from_raw(rgba.width(), rgba.height(), rgba.into_raw())
}

pub fn decode_dds(bytes: &[u8]) -> Result<Rgba> {
    let profile = canvas_engine::dds::Metadata::inspect(bytes)?;
    let level = canvas_engine::dds::decode_level(bytes, &profile, 0, &|| false)?;
    check_dimensions(level.width, level.height)?;
    Rgba::from_raw(level.width, level.height, level.pixels)
}

pub fn decode_file(path: &std::path::Path, revision: Option<&str>) -> Result<Rgba> {
    let bytes = crate::files::read_pinned(path, revision)?;
    decode(&bytes, path.extension().and_then(|e| e.to_str()))
}

/// PNG with straight alpha. `fast` mirrors Pillow's compress_level=1 used before DDS conversion.
pub fn encode_png(image: &Rgba, fast: bool) -> Result<Vec<u8>> {
    use image::ImageEncoder;
    use image::codecs::png::{CompressionType, FilterType, PngEncoder};
    let mut out = Vec::new();
    let encoder = PngEncoder::new_with_quality(&mut out, if fast { CompressionType::Fast } else { CompressionType::Default }, FilterType::Adaptive);
    encoder.write_image(&image.data, image.width, image.height, image::ExtendedColorType::Rgba8)?;
    Ok(out)
}

pub fn encode_jpeg_rgb(image: &Rgba, quality: u8) -> Result<Vec<u8>> {
    let rgb: Vec<u8> = image.data.chunks_exact(4).flat_map(|p| [p[0], p[1], p[2]]).collect();
    let mut out = Vec::new();
    let mut encoder = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut out, quality);
    encoder.encode(&rgb, image.width, image.height, image::ExtendedColorType::Rgb8)?;
    Ok(out)
}

/// Aspect-preserving box thumbnail (Pillow `thumbnail`, never upscaling).
pub fn thumbnail(image: &Rgba, max_width: u32, max_height: u32) -> Rgba {
    let scale = (max_width as f64 / image.width as f64).min(max_height as f64 / image.height as f64).min(1.0);
    let width = ((image.width as f64 * scale).round() as u32).max(1);
    let height = ((image.height as f64 * scale).round() as u32).max(1);
    resize(image, width, height, Filter::Lanczos, None)
}

/// Fast preview downscale for UI previews (not used for exports).
pub fn preview(image: &Rgba, max_side: u32) -> Rgba {
    let largest = image.width.max(image.height);
    if largest <= max_side {
        return image.clone();
    }
    let scale = max_side as f64 / largest as f64;
    let width = ((image.width as f64 * scale).round() as u32).max(1);
    let height = ((image.height as f64 * scale).round() as u32).max(1);
    resize(image, width, height, Filter::Bilinear, None)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pillow_square_fill_is_inclusive() {
        let mut image = Rgba::new(16, 16, [0; 4]);
        fill_polygon(&mut image, &[[0.0, 0.0], [10.0, 0.0], [10.0, 10.0], [0.0, 10.0]], [255; 4]);
        let filled = image.data.chunks_exact(4).filter(|p| p[3] == 255).count();
        assert_eq!(filled, 121);
    }
    #[test]
    fn composite_opaque_replaces() {
        let mut canvas = Rgba::new(2, 1, [10, 20, 30, 255]);
        let overlay = Rgba::new(1, 1, [200, 100, 50, 255]);
        alpha_composite(&mut canvas, &overlay, 1, 0);
        assert_eq!(canvas.pixel(1, 0), [200, 100, 50, 255]);
        assert_eq!(canvas.pixel(0, 0), [10, 20, 30, 255]);
    }
    #[test]
    fn resize_identity_and_fit() {
        let image = Rgba::new(40, 20, [9, 8, 7, 255]);
        let out = fit(&image, 30, 18, Filter::Lanczos);
        assert_eq!((out.width, out.height), (30, 18));
        assert!(out.data.chunks_exact(4).all(|p| p == [9, 8, 7, 255]));
    }
}

/// Image dimensions without decoding pixels.
pub fn dimensions(bytes: &[u8], extension_hint: Option<&str>) -> Result<(u32, u32)> {
    if bytes.starts_with(b"DDS ") {
        let profile = canvas_engine::dds::Metadata::inspect(bytes)?;
        return Ok((profile.width, profile.height));
    }
    let mut reader = image::ImageReader::new(std::io::Cursor::new(bytes)).with_guessed_format()?;
    if reader.format().is_none() {
        if let Some(format) = extension_hint.and_then(image::ImageFormat::from_extension) {
            reader.set_format(format);
        }
    }
    Ok(reader.into_dimensions()?)
}
