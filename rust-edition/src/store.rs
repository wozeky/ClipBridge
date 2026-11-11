use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};

#[derive(Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Settings {
    pub seconds: usize,
    pub hotkey: u32,
    pub quality: usize,
    pub method: usize,
    pub encoder: usize,
    pub audio_device: String,
    pub provider: usize,
    pub auto_upload: bool,
    pub auto_copy: bool,
    pub backup: Option<PathBuf>,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            seconds: 60,
            hotkey: 8,
            quality: 1,
            method: 0,
            encoder: 0,
            audio_device: String::new(),
            provider: 0,
            auto_upload: false,
            auto_copy: true,
            backup: None,
        }
    }
}

#[derive(Clone, Serialize, Deserialize)]
pub struct Clip {
    pub id: String,
    pub name: String,
    pub bytes: u64,
    pub seconds: f64,
    pub created: String,
    #[serde(default)]
    pub favorite: bool,
    #[serde(default)]
    pub link: Option<String>,
    #[serde(default)]
    pub expires: Option<i64>,
    #[serde(default)]
    pub backed_up: bool,
    #[serde(default)]
    pub bookmarks: Vec<f64>,
}
impl Clip {
    pub fn new(id: String, name: String, bytes: u64, seconds: f64) -> Self {
        Self {
            id,
            name,
            bytes,
            seconds,
            created: chrono::Local::now().to_rfc3339(),
            favorite: false,
            link: None,
            expires: None,
            backed_up: false,
            bookmarks: Vec::new(),
        }
    }
}

#[derive(Clone, Serialize, Deserialize)]
pub struct Upload {
    pub clip_id: String,
    pub provider: usize,
}

#[derive(Default, Serialize, Deserialize)]
#[serde(default)]
pub struct State {
    pub settings: Settings,
    pub clips: Vec<Clip>,
    pub uploads: Vec<Upload>,
}

pub fn root() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .or_else(|| std::env::var_os("XDG_DATA_HOME"))
        .map(PathBuf::from)
        .unwrap_or_else(|| std::env::temp_dir())
        .join("RollClipRust")
}

pub fn id() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    format!(
        "{}-{}-{}",
        chrono::Local::now().format("%Y%m%d-%H%M%S-%3f"),
        std::process::id(),
        COUNTER.fetch_add(1, Ordering::Relaxed)
    )
}

pub fn load(root: &Path) -> Result<State> {
    let path = root.join("state.json");
    let mut state = match fs::read(&path) {
        Ok(bytes) => serde_json::from_slice::<State>(&bytes).context("Arşiv verisi okunamadı")?,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => State::default(),
        Err(error) => return Err(error.into()),
    };
    // Clip IDs become path components. Accept only IDs written by this app.
    state.clips.retain(|clip| {
        !clip.id.is_empty() && clip.id.bytes().all(|c| c.is_ascii_digit() || c == b'-')
    });
    state
        .uploads
        .retain(|task| state.clips.iter().any(|clip| clip.id == task.clip_id));
    Ok(state)
}

pub fn save(root: &Path, state: &State) -> Result<()> {
    let temp = root.join(format!("state-{}.tmp", id()));
    fs::write(&temp, serde_json::to_vec_pretty(state)?)?;
    let result = fs::rename(&temp, root.join("state.json"));
    if result.is_err() {
        let _ = fs::remove_file(temp);
    }
    result?;
    Ok(())
}

pub fn video(root: &Path, id: &str) -> PathBuf {
    root.join("clips").join(format!("{id}.mp4"))
}
pub fn thumbnail(root: &Path, id: &str) -> PathBuf {
    root.join("thumbs").join(format!("{id}.png"))
}

pub fn backup(root: &Path, clip: &Clip, folder: &Path) -> Result<()> {
    let date: String = clip
        .created
        .chars()
        .take(10)
        .filter(|c| c.is_ascii_digit() || *c == '-')
        .collect();
    let destination = folder.join(date);
    fs::create_dir_all(&destination)?;
    let output = destination.join(format!("RollClip-Rust-{}.mp4", clip.id));
    let temp = destination.join(format!(".{}.tmp", id()));
    let copied = fs::copy(video(root, &clip.id), &temp)?;
    let result = if copied != clip.bytes {
        Err(std::io::Error::other("Yedek dosyasının boyutu farklı."))
    } else {
        fs::rename(&temp, output)
    };
    if result.is_err() {
        let _ = fs::remove_file(temp);
    }
    result?;
    Ok(())
}
