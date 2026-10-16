//! Local, reversible logo background removal (port of LogoCleanupImage). Sources are never written.
use crate::error::{Error, Result};
use crate::raster::Rgba;
use std::collections::VecDeque;

pub const MAX_PIXELS: u64 = 24_000_000;

pub struct Cleanup {
    original: Vec<u8>,
    pub image: Rgba,
    undo: Vec<Vec<u8>>,
}

impl Cleanup {
    pub fn new(image: Rgba) -> Result<Self> {
        if image.width as u64 * image.height as u64 > MAX_PIXELS {
            return Err(Error::msg("Logo cleanup supports images up to 24 million pixels. Resize this logo before importing."));
        }
        Ok(Self { original: image.data.clone(), image, undo: Vec::new() })
    }
    pub fn can_undo(&self) -> bool {
        !self.undo.is_empty()
    }
    pub fn has_changes(&self) -> bool {
        self.image.data != self.original
    }
    pub fn pixel(&self, x: i64, y: i64) -> [u8; 4] {
        let x = x.clamp(0, self.image.width as i64 - 1) as u32;
        let y = y.clamp(0, self.image.height as i64 - 1) as u32;
        self.image.pixel(x, y)
    }
    fn remember(&mut self) {
        self.undo.push(self.image.data.clone());
        let limit = (128 * 1024 * 1024 / self.image.data.len().max(1)).clamp(1, 8);
        while self.undo.len() > limit {
            self.undo.remove(0);
        }
    }
    /// Magic wand: the clicked four-connected region, or every matching pixel.
    pub fn remove_region(&mut self, x: i64, y: i64, tolerance: u8, all_matching: bool) -> usize {
        if x < 0 || y < 0 || x >= self.image.width as i64 || y >= self.image.height as i64 {
            return 0;
        }
        let color = self.pixel(x, y);
        if color[3] == 0 {
            return 0;
        }
        let seeds = if all_matching { None } else { Some(vec![(y as usize) * self.image.width as usize + x as usize]) };
        self.remove(color, tolerance, seeds)
    }
    /// Removes edge-connected pixels matching the sampled background color.
    pub fn remove_background(&mut self, color: [u8; 4], tolerance: u8) -> usize {
        if color[3] == 0 {
            return 0;
        }
        let (w, h) = (self.image.width as usize, self.image.height as usize);
        let mut seeds = Vec::with_capacity(2 * (w + h));
        for x in 0..w {
            seeds.push(x);
            seeds.push((h - 1) * w + x);
        }
        for y in 1..h.saturating_sub(1) {
            seeds.push(y * w);
            seeds.push(y * w + w - 1);
        }
        self.remove(color, tolerance, Some(seeds))
    }
    fn remove(&mut self, color: [u8; 4], tolerance: u8, seeds: Option<Vec<usize>>) -> usize {
        let tolerance = tolerance as i32;
        let data = &self.image.data;
        let matches = |pixel: usize| {
            let i = pixel * 4;
            data[i + 3] > 0 && (0..3).all(|c| (data[i + c] as i32 - color[c] as i32).abs() <= tolerance)
        };
        let (w, h) = (self.image.width as usize, self.image.height as usize);
        let mut next = data.clone();
        let mut removed = 0;
        match seeds {
            None => {
                for pixel in 0..w * h {
                    if matches(pixel) {
                        next[pixel * 4 + 3] = 0;
                        removed += 1;
                    }
                }
            }
            Some(seeds) => {
                // Four-connected flood fill never crosses a different-color boundary or wraps rows.
                let mut visited = vec![false; w * h];
                let mut queue = VecDeque::new();
                let mut enqueue = |pixel: usize, queue: &mut VecDeque<usize>| {
                    if !visited[pixel] {
                        visited[pixel] = true;
                        if matches(pixel) {
                            queue.push_back(pixel);
                        }
                    }
                };
                for seed in seeds {
                    enqueue(seed, &mut queue);
                }
                while let Some(pixel) = queue.pop_front() {
                    next[pixel * 4 + 3] = 0;
                    removed += 1;
                    let (x, y) = (pixel % w, pixel / w);
                    if x > 0 { enqueue(pixel - 1, &mut queue); }
                    if x + 1 < w { enqueue(pixel + 1, &mut queue); }
                    if y > 0 { enqueue(pixel - w, &mut queue); }
                    if y + 1 < h { enqueue(pixel + w, &mut queue); }
                }
            }
        }
        if removed > 0 {
            self.remember();
            self.image.data = next;
        }
        removed
    }
    pub fn undo(&mut self) {
        if let Some(previous) = self.undo.pop() {
            self.image.data = previous;
        }
    }
    pub fn reset(&mut self) {
        if self.has_changes() {
            self.remember();
            self.image.data = self.original.clone();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wand_keeps_disconnected_regions() {
        // White border with a black ring; the center white hole is not edge-connected.
        let mut image = Rgba::new(5, 5, [255, 255, 255, 255]);
        for i in 1..4 {
            for (x, y) in [(i, 1), (i, 3), (1, i), (3, i)] {
                let p = (y * 5 + x) * 4;
                image.data[p..p + 3].copy_from_slice(&[0, 0, 0]);
            }
        }
        let mut cleanup = Cleanup::new(image).unwrap();
        assert_eq!(cleanup.remove_background([255, 255, 255, 255], 10), 16);
        assert_eq!(cleanup.pixel(2, 2)[3], 255);
        assert_eq!(cleanup.remove_region(2, 2, 10, false), 1);
        cleanup.undo();
        assert_eq!(cleanup.pixel(2, 2)[3], 255);
        cleanup.reset();
        assert!(!cleanup.has_changes());
    }
}
