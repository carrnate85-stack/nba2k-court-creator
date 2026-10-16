//! Non-destructive hardwood settings shared by previews and exports (port of hardwood_adjustments.py).
use crate::error::{Error, Result};
use crate::raster::Rgba;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub brightness: i32,
    pub contrast: i32,
    pub saturation: i32,
    pub scale: i32,
    pub rotation: i32,
}

impl Default for Settings {
    fn default() -> Self {
        Self { brightness: 0, contrast: 0, saturation: 0, scale: 100, rotation: 0 }
    }
}

pub const LIMITS: [(&str, i32, i32, i32); 5] = [
    ("brightness", -100, 100, 0),
    ("contrast", -100, 100, 0),
    ("saturation", -100, 100, 0),
    ("scale", 50, 200, 100),
    ("rotation", -180, 180, 0),
];

impl Settings {
    /// Reads `floor.textureSettings`, rejecting non-integer or out-of-range values like the original.
    pub fn read(floor: &Value) -> Result<Self> {
        let raw = match floor.get("textureSettings") {
            None | Some(Value::Null) => return Ok(Self::default()),
            Some(Value::Object(map)) => map,
            Some(_) => return Err(Error::msg("Invalid hardwood texture settings.")),
        };
        let mut values = [0i32; 5];
        for (index, (key, low, high, default)) in LIMITS.iter().enumerate() {
            values[index] = match raw.get(*key) {
                None | Some(Value::Null) => *default,
                Some(value) => {
                    let number = value.as_i64().filter(|_| value.is_i64() || value.is_u64()).ok_or_else(|| Error::msg(format!("Invalid hardwood {key}.")))?;
                    if number < *low as i64 || number > *high as i64 {
                        return Err(Error::msg(format!("Invalid hardwood {key}.")));
                    }
                    number as i32
                }
            };
        }
        Ok(Self { brightness: values[0], contrast: values[1], saturation: values[2], scale: values[3], rotation: values[4] })
    }
    pub fn validate(&self) -> Result<()> {
        let values = [self.brightness, self.contrast, self.saturation, self.scale, self.rotation];
        for (value, (key, low, high, _)) in values.iter().zip(LIMITS) {
            if *value < low || *value > high {
                return Err(Error::msg(format!("Enter {key} from {low} to {high}.")));
            }
        }
        Ok(())
    }
    pub fn to_json(&self) -> Value {
        json!({"brightness": self.brightness, "contrast": self.contrast, "saturation": self.saturation, "scale": self.scale, "rotation": self.rotation})
    }
    pub fn has_color(&self) -> bool {
        self.brightness != 0 || self.contrast != 0 || self.saturation != 0
    }
    pub fn has_placement(&self) -> bool {
        self.scale != 100 || self.rotation != 0
    }
}

/// Canvas ColorAdjustmentService RGB math; alpha is retained.
pub fn adjust(image: &mut Rgba, settings: &Settings) {
    if !settings.has_color() {
        return;
    }
    let contrast = settings.contrast as f64 * 2.55;
    let factor = 259.0 * (contrast + 255.0) / (255.0 * (259.0 - contrast));
    let brightness = settings.brightness as f64 * 2.55;
    let saturation_gain = 1.0 + settings.saturation as f64 / 100.0;
    image.data.par_chunks_exact_mut(4 * 1024).for_each(|chunk| {
        for pixel in chunk.chunks_exact_mut(4) {
            let mut rgb = [0f64; 3];
            for c in 0..3 {
                rgb[c] = (factor * (pixel[c] as f64 - 128.0) + 128.0 + brightness).clamp(0.0, 255.0);
            }
            let maximum = rgb[0].max(rgb[1]).max(rgb[2]);
            let minimum = rgb[0].min(rgb[1]).min(rgb[2]);
            let delta = maximum - minimum;
            let saturation = if maximum > 0.0 { delta / maximum } else { 0.0 };
            let adjusted = (saturation * saturation_gain).clamp(0.0, 1.0);
            let ratio = if saturation > 0.0 { adjusted / saturation } else { 1.0 };
            for c in 0..3 {
                let value = maximum - (maximum - rgb[c]) * ratio;
                pixel[c] = value.clamp(0.0, 255.0).round_ties_even() as u8;
            }
        }
    });
}

/// Scales/rotates a repeating full-court texture around its center before clipping.
pub fn place(image: &Rgba, settings: &Settings) -> Rgba {
    if !settings.has_placement() {
        return image.clone();
    }
    let (w, h) = (image.width as i64, image.height as i64);
    let angle = (settings.rotation as f64).to_radians();
    let (cosine, sine, zoom) = (angle.cos(), angle.sin(), settings.scale as f64 / 100.0);
    let (cx, cy) = (w as f64 / 2.0, h as f64 / 2.0);
    let mut out = Rgba::new(image.width, image.height, [0; 4]);
    out.data.par_chunks_exact_mut(w as usize * 4).enumerate().for_each(|(row, line)| {
        let y = row as f64 + 0.5 - cy;
        for col in 0..w as usize {
            let x = col as f64 + 0.5 - cx;
            let sx = (cosine * x + sine * y) / zoom + cx - 0.5;
            let sy = (-sine * x + cosine * y) / zoom + cy - 0.5;
            let x0 = sx.floor();
            let y0 = sy.floor();
            let (fx, fy) = (sx - x0, sy - y0);
            let (x0, y0) = (x0 as i64, y0 as i64);
            let at = |yy: i64, xx: i64| -> usize { ((yy.rem_euclid(h) * w + xx.rem_euclid(w)) * 4) as usize };
            let (a, b, c, d) = (at(y0, x0), at(y0, x0 + 1), at(y0 + 1, x0), at(y0 + 1, x0 + 1));
            for k in 0..4 {
                let value = (image.data[a + k] as f64 * (1.0 - fx) + image.data[b + k] as f64 * fx) * (1.0 - fy)
                    + (image.data[c + k] as f64 * (1.0 - fx) + image.data[d + k] as f64 * fx) * fy;
                line[col * 4 + k] = value.round_ties_even() as u8;
            }
        }
    });
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn neutral_settings_are_identity() {
        let mut image = Rgba::new(4, 2, [10, 120, 200, 255]);
        let before = image.clone();
        adjust(&mut image, &Settings::default());
        assert_eq!(image.data, before.data);
        assert_eq!(place(&image, &Settings::default()).data, before.data);
    }
    #[test]
    fn reads_and_rejects_settings() {
        assert_eq!(Settings::read(&json!({})).unwrap(), Settings::default());
        assert_eq!(Settings::read(&json!({"textureSettings": {"brightness": 5}})).unwrap().brightness, 5);
        assert!(Settings::read(&json!({"textureSettings": {"scale": 20}})).is_err());
        assert!(Settings::read(&json!({"textureSettings": {"scale": 1.5}})).is_err());
    }
}
