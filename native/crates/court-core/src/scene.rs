//! `level_floor.SCNE` floor scenes: bounded parsing (complete or outer-brace-free), container
//! validation, Python-identical serialization and the export material/line adjustments.
use crate::error::{Error, Result};
use crate::pyjson::{self, Style};
use serde_json::{Map, Value, json};

pub const MAX_SCENE_BYTES: usize = 16 * 1024 * 1024;
pub const SCENE_ENTRY: &str = "level_floor.SCNE";

pub struct Scene {
    pub document: Value,
    pub wrapped: bool,
}

fn mapping<'a>(container: &'a Map<String, Value>, key: &str) -> Result<Option<&'a Map<String, Value>>> {
    match container.get(key) {
        None => Ok(None),
        Some(Value::Object(map)) => Ok(Some(map)),
        Some(_) => Err(Error::msg(format!("The floor scene's {key} must be an object."))),
    }
}

fn validate_containers(document: &Value) -> Result<()> {
    for scene in document.as_object().into_iter().flat_map(|m| m.values()) {
        let Some(scene) = scene.as_object() else { continue };
        for model in mapping(scene, "Model")?.into_iter().flat_map(|m| m.values()) {
            let model = model.as_object().ok_or_else(|| Error::msg("The floor scene's Model entries must be objects."))?;
            match model.get("Prim") {
                None => {}
                Some(Value::Array(primitives)) => {
                    for primitive in primitives {
                        let primitive = primitive.as_object().ok_or_else(|| Error::msg("The floor scene's Model.Prim entries must be objects."))?;
                        if primitive.get("Mesh").is_some_and(|m| !m.is_string()) {
                            return Err(Error::msg("The floor scene's primitive Mesh must be a name."));
                        }
                        if primitive.get("Material").is_some_and(|m| !m.is_string() && !m.is_null()) {
                            return Err(Error::msg("The floor scene's primitive Material must be a name or null."));
                        }
                    }
                }
                Some(_) => return Err(Error::msg("The floor scene's Model.Prim must be an array.")),
            }
        }
        for key in ["Material", "Effect"] {
            for item in mapping(scene, key)?.into_iter().flat_map(|m| m.values()) {
                let item = item.as_object().ok_or_else(|| Error::msg(format!("The floor scene's {key} entries must be objects.")))?;
                for field in ["Resource", "Parameter"] {
                    mapping(item, field)?;
                }
            }
        }
    }
    Ok(())
}

impl Scene {
    pub fn parse(data: &[u8]) -> Result<Self> {
        if data.len() > MAX_SCENE_BYTES {
            return Err(Error::msg("The floor scene exceeds the 16 MB size limit."));
        }
        let payload = data.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(data);
        let payload = payload.trim_ascii();
        let wrapped = payload.starts_with(b"{");
        let text = std::str::from_utf8(payload).map_err(|_| Error::msg("Invalid floor scene: invalid UTF-8."))?;
        let source = if wrapped { text.to_string() } else { format!("{{{text}}}") };
        pyjson::check_depth(&source, pyjson::MAX_DEPTH).map_err(|e| e.context("Invalid floor scene: "))?;
        let document: Value = serde_json::from_str(&source).map_err(|e| Error::msg(format!("Invalid floor scene: {e}")))?;
        if !document.is_object() {
            return Err(Error::msg("The floor scene must be an object."));
        }
        validate_containers(&document)?;
        Ok(Self { document, wrapped })
    }

    /// `json.dumps(document, ensure_ascii=True, indent=2)`, unwrapped for fragment scenes.
    pub fn serialize(&self) -> Result<Vec<u8>> {
        let text = pyjson::dumps(&self.document, Style::SCENE);
        let data = if self.wrapped { text } else { text[1..text.len() - 1].trim().to_string() };
        if data.len() > MAX_SCENE_BYTES {
            return Err(Error::msg("The prepared floor scene exceeds the 16 MB size limit."));
        }
        Ok(data.into_bytes())
    }

    pub fn scenes(&self) -> impl Iterator<Item = &Map<String, Value>> {
        self.document.as_object().into_iter().flat_map(|m| m.values()).filter_map(Value::as_object)
    }

    pub fn models(&self) -> impl Iterator<Item = &Value> {
        self.scenes().flat_map(|s| s.get("Model").and_then(Value::as_object).into_iter().flat_map(|m| m.values()))
    }

    pub fn has_floor_surface(&self) -> bool {
        self.models().any(|model| {
            model.get("Prim").and_then(Value::as_array).into_iter().flatten().any(|p| {
                let mesh = p.get("Mesh").and_then(Value::as_str).unwrap_or("").to_lowercase();
                mesh.contains("full_court_floor") && !mesh.contains("line")
            })
        })
    }
}

/// Removes stock line primitives; the baked texture carries the markings instead.
pub fn clean_floor_scene(data: &[u8]) -> Result<Vec<u8>> {
    let mut scene = Scene::parse(data)?;
    let mut surfaces = 0;
    for value in scene.document.as_object_mut().unwrap().values_mut() {
        let Some(models) = value.get_mut("Model").and_then(Value::as_object_mut) else { continue };
        for model in models.values_mut() {
            let Some(primitives) = model.get("Prim").and_then(Value::as_array).cloned() else { continue };
            let kept: Vec<Value> = primitives.iter().filter(|p| !p.get("Mesh").and_then(Value::as_str).unwrap_or("").to_lowercase().contains("line")).cloned().collect();
            surfaces += kept.iter().filter(|p| p.get("Mesh").and_then(Value::as_str).unwrap_or("").to_lowercase().contains("full_court_floor")).count();
            if !primitives.is_empty() {
                model["Prim"] = Value::Array(kept);
            }
        }
    }
    if surfaces == 0 {
        return Err(Error::msg("The export base has no recognized full-court floor surface."));
    }
    scene.serialize()
}

/// Port of `native_export_scene`: disables the second logo sample of the primary material.
pub fn native_export_scene(data: &[u8]) -> Result<Vec<u8>> {
    let mut scene = Scene::parse(data)?;
    let mut changed = 0;
    for value in scene.document.as_object_mut().unwrap().values_mut() {
        if !value.is_object() {
            continue;
        }
        let mut active: Vec<String> = Vec::new();
        for model in value.get("Model").and_then(Value::as_object).into_iter().flat_map(|m| m.values()) {
            for primitive in model.get("Prim").and_then(Value::as_array).into_iter().flatten() {
                let name = primitive.get("Material").and_then(Value::as_str).map(str::to_string);
                // Python collects a set; None is a member too but never names a material.
                if let Some(name) = name {
                    if !active.contains(&name) {
                        active.push(name);
                    }
                }
            }
        }
        for name in active {
            let Some(material) = value.get_mut("Material").and_then(Value::as_object_mut).and_then(|m| m.get_mut(&name)) else { continue };
            let resources = material.get("Resource").and_then(Value::as_object);
            if resources.is_some_and(|r| r.contains_key("Logo0Texture") && r.contains_key("Logo1Texture")) {
                let material = material.as_object_mut().unwrap();
                let parameters = material.entry("Parameter").or_insert_with(|| json!({}));
                let parameters = parameters.as_object_mut().ok_or_else(|| Error::msg("The floor scene's Parameter must be an object."))?;
                for (key, number) in [("Logo1OffsetX", 100.0), ("Logo1OffsetY", 100.0), ("Logo1ScaleX", 1.0), ("Logo1ScaleY", 1.0), ("Logo1Rotation", 0.0)] {
                    parameters.insert(key.into(), json!(number));
                }
                changed += 1;
            }
        }
    }
    if changed != 1 {
        return Err(Error::msg("Could not isolate the primary full-court material."));
    }
    scene.serialize()
}
