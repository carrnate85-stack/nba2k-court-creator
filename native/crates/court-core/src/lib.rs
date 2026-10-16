//! NBA 2K Court Creator engine: stock geometry, court composition, projects, floor catalog and
//! NBA 2K27 IFF export. Shared pixel codecs and text rendering come from the 2K Canvas crates.
pub mod error;
pub mod pyjson;
pub mod files;
pub mod legacy;
pub mod geom;
pub mod geometry;
pub mod raster;
pub mod hardwood;
pub mod render;
pub mod project;
pub mod catalog;

pub use error::{Error, Result};
pub mod lock;
pub mod ziparc;
pub mod scene;
pub mod oodle;
pub mod game;
pub mod floor_geometry;
pub mod iff;
pub mod export;
pub mod geometry_build;
pub mod prepare;
pub mod import;
pub mod document;
pub mod cleanup;
pub mod text;
