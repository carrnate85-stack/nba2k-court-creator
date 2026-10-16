//! Editable court text: WPF-compatible `textSettings` metadata and DirectWrite rasterization
//! through the shared Canvas text renderer (no .NET; the PNG keeps exports font-independent).
use crate::error::{Error, Result};
use crate::raster::Rgba;
use canvas_engine::text::{Alignment, Settings};
use serde_json::{Value, json};

/// Reads Court Creator (PascalCase) or Canvas (camelCase) text settings.
pub fn from_json(value: &Value) -> Result<Settings> {
    let settings: Settings = serde_json::from_value(value.clone()).map_err(|_| Error::msg("Invalid text layer settings."))?;
    settings.validate().map_err(|e| Error::msg(e.to_string()))?;
    Ok(settings)
}

/// Writes the property names the WPF studio's `TextLayerSettings` serializer used.
pub fn to_json(settings: &Settings) -> Value {
    json!({
        "Text": settings.text, "FontFamily": settings.font_family, "FontSize": settings.font_size,
        "FillColor": settings.fill_color, "OutlineColor": settings.outline_color, "OutlineWidth": settings.outline_width,
        "Bold": settings.bold, "Italic": settings.italic, "Alignment": u8::from(settings.alignment),
        "LineHeight": settings.line_height, "LetterSpacing": settings.letter_spacing,
    })
}

/// New text defaults (`TEXT`, Arial 180, primary fill, black outline width 0, bold).
pub fn defaults(primary: &str) -> Settings {
    Settings {
        text: "TEXT".into(),
        font_family: "Arial".into(),
        font_size: 180.0,
        fill_color: primary.into(),
        outline_color: "#000000".into(),
        outline_width: 0.0,
        bold: true,
        italic: false,
        alignment: Alignment::Left,
        line_height: 0.0,
        letter_spacing: 0.0,
    }
}

#[cfg(windows)]
pub fn render(settings: &Settings) -> Result<Rgba> {
    settings.validate().map_err(|e| Error::msg(e.to_string()))?;
    let rendered = canvas_text::render(settings).map_err(|e| Error::msg(format!("Text could not be rendered: {e}")))?;
    if rendered.width > 32768 || rendered.height > 32768 {
        return Err(Error::msg("Text dimensions exceed the supported layer size."));
    }
    Rgba::from_raw(rendered.width, rendered.height, rendered.pixels)
}

#[cfg(not(windows))]
pub fn render(_settings: &Settings) -> Result<Rgba> {
    Err(Error::msg("Text rendering requires Windows."))
}

#[cfg(windows)]
pub fn fonts() -> Result<Vec<String>> {
    canvas_text::font_families().map_err(|e| Error::msg(e.to_string()))
}
#[cfg(not(windows))]
pub fn fonts() -> Result<Vec<String>> {
    Ok(vec!["Arial".into()])
}
