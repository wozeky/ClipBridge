use crate::{
    platform::Target,
    store::{self, Clip, Settings},
};
use anyhow::{bail, ensure, Context, Result};
use std::{
    ffi::OsStr,
    fs,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};

pub fn tool(name: &str) -> PathBuf {
    let filename = format!("{name}{}", std::env::consts::EXE_SUFFIX);
    if let Ok(exe) = std::env::current_exe() {
        if let Some(folder) = exe.parent() {
            let packaged = folder.join("ffmpeg").join("bin").join(&filename);
            if packaged.is_file() {
                return packaged;
            }
        }
    }
    PathBuf::from(filename)
}

pub fn command(program: impl AsRef<OsStr>) -> Command {
    let mut command = Command::new(program);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000);
    }
    #[cfg(not(windows))]
    let _ = &mut command;
    command
}

fn ffmpeg() -> Command {
    let mut cmd = command(tool("ffmpeg"));
    cmd.args(["-hide_banner", "-loglevel", "error", "-nostdin", "-y"]);
    cmd
}

fn run(cmd: &mut Command) -> Result<String> {
    let output = cmd.output().context("Medya işlemi başlatılamadı")?;
    if !output.status.success() {
        let log = String::from_utf8_lossy(&output.stderr);
        bail!("{}", log.chars().take(700).collect::<String>());
    }
    Ok(String::from_utf8_lossy(&output.stdout).into_owned())
}

#[derive(Clone, Copy, PartialEq)]
enum Method {
    Wgc,
    Gdi,
    Dxgi,
    Test,
}
impl Method {
    fn name(self) -> &'static str {
        match self {
            Self::Wgc => "WGC",
            Self::Gdi => "GDI",
            Self::Dxgi => "DXGI",
            Self::Test => "Test",
        }
    }
}

pub struct Recorder {
    child: Child,
    folder: PathBuf,
    pub label: String,
}

fn software_encoder() -> &'static str {
    if cfg!(windows) {
        "h264_mf"
    } else {
        "libx264"
    }
}

impl Recorder {
    pub fn start(root: &Path, target: &Target, settings: &Settings) -> Result<Self> {
        ensure!(cfg!(windows), "Pencere yakalama Windows gerektirir.");
        let methods: &[Method] = match settings.method {
            1 => &[Method::Wgc],
            2 => &[Method::Gdi],
            3 => &[Method::Dxgi],
            _ => &[Method::Wgc, Method::Gdi],
        };
        let encoders: &[&str] = match settings.encoder {
            1 => &["h264_nvenc"],
            2 => &["h264_amf"],
            3 => &["h264_qsv"],
            _ => &[software_encoder()],
        };
        let mut details = String::new();
        for &method in methods {
            for &encoder in encoders {
                match Self::spawn(root, target.handle, settings, method, encoder) {
                    Ok(recorder) => return Ok(recorder),
                    Err(error) => details = format!("{} / {encoder}: {error}", method.name()),
                }
            }
        }
        bail!("Kayıt başlatılamadı. {details}")
    }

    fn spawn(
        root: &Path,
        handle: usize,
        settings: &Settings,
        method: Method,
        encoder: &str,
    ) -> Result<Self> {
        let folder = root.join("buffer").join(store::id());
        fs::create_dir_all(&folder)?;
        let (width, height, fps) = if method == Method::Test {
            (320, 180, 30)
        } else {
            match settings.quality {
                0 => (1920, 1080, 60),
                2 => (1280, 720, 30),
                _ => (1920, 1080, 30),
            }
        };
        let fps = fps.to_string();
        let mut cmd = ffmpeg();
        cmd.args(["-thread_queue_size", "8"]);
        match method {
            Method::Wgc => cmd.args(["-f", "lavfi", "-i", &format!("gfxcapture=hwnd={handle}:max_framerate={fps}:width={width}:height={height}:resize_mode=scale_aspect:display_border=1")]),
            Method::Dxgi => cmd.args(["-f", "lavfi", "-i", &format!("ddagrab=output_idx=0:framerate={fps}:draw_mouse=0")]),
            Method::Gdi => cmd.args(["-f", "gdigrab", "-framerate", &fps, "-draw_mouse", "0", "-i", &format!("hwnd={handle}")]),
            Method::Test => cmd.args(["-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30"]),
        };
        let audio = method != Method::Test && !settings.audio_device.trim().is_empty();
        if audio {
            cmd.args([
                "-thread_queue_size",
                "256",
                "-f",
                "dshow",
                "-i",
                &format!("audio={}", settings.audio_device),
            ]);
        }
        let mut filters = String::new();
        if matches!(method, Method::Wgc | Method::Dxgi) {
            filters.push_str("hwdownload,format=bgra,");
        }
        filters.push_str(&format!("scale={width}:{height}:flags=bilinear:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format={}", if encoder == "libx264" { "yuv420p" } else { "nv12" }));
        cmd.args(["-map", "0:v:0", "-vf", &filters, "-c:v", encoder]);
        match encoder {
            "h264_mf" => {
                cmd.args(["-hw_encoding", "0"]);
            }
            "libx264" => {
                cmd.args(["-preset", "ultrafast", "-threads", "2"]);
            }
            _ => {}
        }
        if audio {
            cmd.args(["-map", "1:a:0", "-c:a", "aac", "-b:a", "160k"]);
        }
        cmd.args([
            "-r",
            &fps,
            "-fps_mode",
            "cfr",
            "-b:v",
            "6000k",
            "-g",
            &fps,
            "-bf",
            "0",
            "-force_key_frames",
            "expr:gte(t,n_forced)",
            "-f",
            "segment",
            "-segment_time",
            "1",
            "-reset_timestamps",
            "1",
            "-segment_format",
            "mp4",
        ])
        .arg(folder.join("%010d.mp4"))
        .stdin(Stdio::null())
        .stdout(Stdio::null());
        let log = folder.join("capture.log");
        let child = match cmd.stderr(fs::File::create(&log)?).spawn() {
            Ok(child) => child,
            Err(error) => {
                let _ = fs::remove_dir_all(&folder);
                return Err(error.into());
            }
        };
        let mut recorder = Self {
            child,
            folder,
            label: format!("{} · {encoder}", method.name()),
        };
        let deadline = Instant::now() + Duration::from_secs(12);
        loop {
            if recorder.child.try_wait()?.is_some() || Instant::now() >= deadline {
                let details = fs::read_to_string(&log).unwrap_or_default();
                bail!(
                    "{}",
                    if details.trim().is_empty() {
                        "İlk kareler alınamadı.".into()
                    } else {
                        details.chars().take(500).collect::<String>()
                    }
                );
            }
            if !recorder.completed()?.is_empty() {
                return Ok(recorder);
            }
            thread::sleep(Duration::from_millis(100));
        }
    }

    pub fn stopped(&mut self) -> bool {
        matches!(self.child.try_wait(), Ok(Some(_)) | Err(_))
    }

    fn completed(&self) -> Result<Vec<PathBuf>> {
        let mut segments: Vec<_> = fs::read_dir(&self.folder)?
            .filter_map(|entry| entry.ok())
            .map(|entry| entry.path())
            .filter(|path| path.extension().is_some_and(|ext| ext == "mp4"))
            .collect();
        segments.sort();
        segments.pop(); // The encoder may still have this file open.
        Ok(segments)
    }

    pub fn prune(&self, seconds: usize) -> Result<()> {
        let files = self.completed()?;
        for file in files.iter().take(files.len().saturating_sub(seconds + 4)) {
            fs::remove_file(file)?;
        }
        Ok(())
    }

    pub fn snapshot(&self, root: &Path, seconds: usize) -> Result<PathBuf> {
        let files = self.completed()?;
        ensure!(
            !files.is_empty(),
            "Henüz kaydedilecek tamamlanmış bölüm yok."
        );
        let output = root.join("jobs").join(store::id());
        fs::create_dir_all(&output)?;
        let result: Result<()> = (|| {
            let mut listing = String::new();
            for (index, file) in files
                .iter()
                .skip(files.len().saturating_sub(seconds))
                .enumerate()
            {
                let name = format!("{index:04}.mp4");
                let copy = output.join(&name);
                if fs::hard_link(file, &copy).is_err() {
                    fs::copy(file, copy)?;
                }
                listing.push_str(&format!("file '{name}'\n"));
            }
            fs::write(output.join("segments.txt"), listing)?;
            Ok(())
        })();
        if result.is_err() {
            let _ = fs::remove_dir_all(&output);
        }
        result?;
        Ok(output)
    }
}

impl Drop for Recorder {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = fs::remove_dir_all(&self.folder);
    }
}

pub fn join(root: &Path, snapshot: &Path) -> Result<Clip> {
    let result = make_clip(root, "RollClip", |output| {
        run(ffmpeg()
            .args(["-f", "concat", "-safe", "1", "-i"])
            .arg(snapshot.join("segments.txt"))
            .args(["-c", "copy", "-movflags", "+faststart"])
            .arg(output))?;
        Ok(())
    });
    let _ = fs::remove_dir_all(snapshot);
    result
}

pub fn import(root: &Path, source: &Path) -> Result<Clip> {
    let name = source.file_stem().unwrap_or_default().to_string_lossy();
    make_clip(root, &name, |output| {
        fs::copy(source, output)?;
        Ok(())
    })
}

pub fn trim(
    root: &Path,
    clip: &Clip,
    start: f64,
    end: f64,
    muted: bool,
    cap_mb: Option<u64>,
) -> Result<Clip> {
    ensure!(
        start.is_finite()
            && end.is_finite()
            && start >= 0.0
            && end > start
            && end <= clip.seconds + 0.01,
        "Geçerli bir klip aralığı seç."
    );
    make_clip(root, &format!("{} · Kesit", clip.name), |output| {
        let source = store::video(root, &clip.id);
        let length = end - start;
        let limit = cap_mb.map(|mb| mb * 1_000_000);
        if start == 0.0
            && end == clip.seconds
            && !muted
            && limit.is_some_and(|bytes| clip.bytes <= bytes)
        {
            fs::copy(&source, output)?;
            return Ok(());
        }
        let rate = limit
            .map(|bytes| {
                ((bytes as f64 * 8.0 * 0.91 / length / 1000.0) as u64)
                    .saturating_sub(if muted { 0 } else { 96 })
                    .clamp(80, 8000)
            })
            .unwrap_or(4500);
        let mut cmd = ffmpeg();
        cmd.args(["-ss", &format!("{start:.3}"), "-i"])
            .arg(source)
            .args([
                "-t",
                &format!("{length:.3}"),
                "-map",
                "0:v:0",
                "-vf",
                "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
                "-c:v",
                software_encoder(),
                "-b:v",
                &format!("{rate}k"),
            ]);
        if cfg!(windows) {
            cmd.args(["-hw_encoding", "0"]);
        }
        if muted {
            cmd.arg("-an");
        } else {
            cmd.args(["-map", "0:a:0?", "-c:a", "aac", "-b:a", "96k"]);
        }
        run(cmd.args(["-movflags", "+faststart"]).arg(output))?;
        if let Some(limit) = limit {
            ensure!(
                fs::metadata(output)?.len() <= limit,
                "Çıktı boyut sınırını aştı. Daha kısa bir aralık seç."
            );
        }
        Ok(())
    })
}

fn make_clip(root: &Path, name: &str, write: impl FnOnce(&Path) -> Result<()>) -> Result<Clip> {
    let id = store::id();
    let target = store::video(root, &id);
    let temp = target.with_extension("part.mp4");
    let result: Result<Clip> = (|| {
        write(&temp)?;
        let seconds = duration(&temp)?;
        let bytes = fs::metadata(&temp)?.len();
        fs::rename(&temp, &target)?;
        let _ = frame(
            &target,
            &store::thumbnail(root, &id),
            (seconds / 4.0).min(1.0),
        );
        Ok(Clip::new(id, name.to_owned(), bytes, seconds))
    })();
    if result.is_err() {
        let _ = fs::remove_file(temp);
    }
    result
}

fn duration(path: &Path) -> Result<f64> {
    let text = run(command(tool("ffprobe"))
        .args([
            "-v",
            "error",
            "-show_entries",
            "stream=codec_type:format=duration",
            "-of",
            "json",
        ])
        .arg(path))?;
    let data: serde_json::Value = serde_json::from_str(&text)?;
    let has_video = data["streams"]
        .as_array()
        .is_some_and(|streams| streams.iter().any(|s| s["codec_type"] == "video"));
    let seconds: f64 = data["format"]["duration"]
        .as_str()
        .context("MP4 süresi bulunamadı")?
        .parse()?;
    ensure!(
        has_video && seconds.is_finite() && seconds > 0.0,
        "Geçerli video bulunamadı."
    );
    Ok(seconds)
}

pub fn frame(source: &Path, output: &Path, seconds: f64) -> Result<()> {
    run(ffmpeg()
        .args(["-ss", &format!("{seconds:.3}"), "-i"])
        .arg(source)
        .args([
            "-frames:v",
            "1",
            "-vf",
            "scale=384:-2",
            "-threads",
            "1",
            "-update",
            "1",
        ])
        .arg(output))?;
    ensure!(output.is_file(), "Bu noktada önizleme karesi yok.");
    Ok(())
}

pub fn upload(path: &Path, provider: usize) -> Result<String> {
    ensure!(
        fs::metadata(path)?.len() <= 1_000_000_000,
        "Yükleme sınırı 1 GB."
    );
    // The relative filename is generated by RollClip, not interpreted as a user-controlled curl form.
    let name = path
        .file_name()
        .context("Klip dosyası bulunamadı")?
        .to_string_lossy();
    let part = if provider == 1 {
        "file"
    } else {
        "fileToUpload"
    };
    let field = format!("{part}=@{name};filename=rollclip.mp4;type=video/mp4");
    let mut curl = command("curl");
    curl.current_dir(path.parent().context("Klip klasörü bulunamadı")?)
        .args([
            "--disable",
            "--fail",
            "--silent",
            "--show-error",
            "--max-time",
            "180",
        ]);
    let output = if provider == 1 {
        run(curl.args(["-F", &field, "https://upload.gofile.io/uploadfile"]))?
    } else {
        run(curl.args([
            "-F",
            "reqtype=fileupload",
            "-F",
            "time=24h",
            "-F",
            &field,
            "https://litterbox.catbox.moe/resources/internals/api.php",
        ]))?
    };
    let link = if provider == 1 {
        serde_json::from_str::<serde_json::Value>(&output)?["data"]["downloadPage"]
            .as_str()
            .unwrap_or_default()
            .to_owned()
    } else {
        output.trim().to_owned()
    };
    ensure!(
        link.starts_with(if provider == 1 {
            "https://gofile.io/d/"
        } else {
            "https://litter.catbox.moe/"
        }) && !link.chars().any(char::is_whitespace),
        "Sağlayıcı geçerli bir bağlantı döndürmedi."
    );
    Ok(link)
}

pub fn smoke(root: &Path) -> Result<()> {
    let settings = Settings::default();
    let recorder = Recorder::spawn(root, 0, &settings, Method::Test, software_encoder())?;
    thread::sleep(Duration::from_secs(2));
    let snapshot = recorder.snapshot(root, 2)?;
    drop(recorder);
    let clip = join(root, &snapshot)?;
    ensure!(
        clip.seconds >= 1.5 && clip.seconds < 2.5,
        "Replay süresi beklenen aralıkta değil."
    );
    let cut = trim(root, &clip, 0.2, 1.2, true, Some(10))?;
    ensure!(
        (0.8..1.2).contains(&cut.seconds),
        "Kesit süresi beklenen aralıkta değil."
    );
    ensure!(
        store::thumbnail(root, &cut.id).is_file(),
        "Küçük resim üretilmedi."
    );
    let mut state = store::State::default();
    state.clips.push(clip);
    store::save(root, &state)?;
    state.clips.push(cut);
    store::save(root, &state)?;
    ensure!(
        store::load(root)?.clips.len() == 2,
        "Arşiv kayıtları okunamadı."
    );
    Ok(())
}

pub fn audio_devices() -> Result<Vec<String>> {
    ensure!(
        cfg!(windows),
        "DirectShow ses aygıtları Windows gerektirir."
    );
    let result = command(tool("ffmpeg"))
        .args([
            "-hide_banner",
            "-list_devices",
            "true",
            "-f",
            "dshow",
            "-i",
            "dummy",
        ])
        .output()?;
    let text = String::from_utf8_lossy(&result.stderr);
    Ok(text
        .lines()
        .filter_map(|line| {
            if !line.contains("(audio)") {
                return None;
            }
            line.split('"').nth(1).map(str::to_owned)
        })
        .collect())
}
