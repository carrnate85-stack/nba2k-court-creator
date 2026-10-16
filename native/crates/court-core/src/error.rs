//! User-facing errors. Messages are shown in the status bar, so they stay short and actionable.
use std::fmt;

#[derive(Debug, Clone)]
pub struct Error(pub String);

pub type Result<T> = std::result::Result<T, Error>;

impl Error {
    pub fn msg(text: impl Into<String>) -> Self {
        Self(text.into())
    }
    /// Cancellation is reported separately so callers can ignore superseded work.
    pub fn cancelled() -> Self {
        Self(CANCELLED.into())
    }
    pub fn is_cancelled(&self) -> bool {
        self.0 == CANCELLED
    }
    pub fn context(self, prefix: &str) -> Self {
        Self(format!("{prefix}{}", self.0))
    }
}

const CANCELLED: &str = "The operation was cancelled.";

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Error {}

impl From<std::io::Error> for Error {
    fn from(value: std::io::Error) -> Self {
        Self(value.to_string())
    }
}
impl From<serde_json::Error> for Error {
    fn from(value: serde_json::Error) -> Self {
        Self(format!("Invalid JSON: {value}"))
    }
}
impl From<image::ImageError> for Error {
    fn from(value: image::ImageError) -> Self {
        Self(format!("The image could not be read: {value}"))
    }
}
impl From<canvas_engine::Error> for Error {
    fn from(value: canvas_engine::Error) -> Self {
        Self(value.to_string())
    }
}
impl From<String> for Error {
    fn from(value: String) -> Self {
        Self(value)
    }
}
impl From<&str> for Error {
    fn from(value: &str) -> Self {
        Self(value.into())
    }
}

/// Bail out with a formatted user-facing message.
#[macro_export]
macro_rules! bail {
    ($($arg:tt)*) => { return Err($crate::error::Error(format!($($arg)*))) };
}

/// Returns early when the supplied cancellation check reports true.
pub fn check(cancelled: &dyn Fn() -> bool) -> Result<()> {
    if cancelled() { Err(Error::cancelled()) } else { Ok(()) }
}
