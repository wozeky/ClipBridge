#![cfg_attr(windows, windows_subsystem = "windows")]

mod chrome;
mod media;
mod platform;
mod store;

use anyhow::Result;
use eframe::egui::{
    self, Align, Color32, FontData, FontFamily, FontId, Frame, Layout, RichText, Stroke,
    TextureHandle, Vec2,
};
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU32, Ordering},
        mpsc::{self, Receiver, Sender},
        Arc,
    },
    thread,
    time::{Duration, Instant},
};
use store::{Clip, State};

const MUTED: Color32 = Color32::from_rgb(172, 184, 176);
const DURATIONS: [usize; 5] = [15, 30, 60, 120, 180];
const RELEASES: &str = "https://github.com/wozeky/RollClip/releases";

fn init(root: &Path) -> Result<()> {
    for folder in ["clips", "thumbs", "jobs", "buffer"] {
        fs::create_dir_all(root.join(folder))?;
    }
    Ok(())
}

fn main() -> eframe::Result<()> {
    let args: Vec<_> = std::env::args_os().collect();
    if args.get(1).is_some_and(|x| x == "--self-test") {
        let root = args
            .get(2)
            .map(PathBuf::from)
            .expect("temporary test folder required");
        let report = init(&root).and_then(|()| media::smoke(&root));
        let message = match &report {
            Ok(()) => "PASS: replay, trim, thumbnail and state".into(),
            Err(error) => format!("{error:#}"),
        };
        let _ = fs::write(root.join("test-result.txt"), message);
        std::process::exit(if report.is_ok() { 0 } else { 1 });
    }
    #[cfg(windows)]
    if args.get(1).is_some_and(|x| x == "--window-test") {
        let root = args
            .get(2)
            .map(PathBuf::from)
            .expect("temporary test folder required");
        let result = init(&root).and_then(|()| media::window_smoke(&root));
        let message = match result {
            Ok(method) => format!("PASS: live desktop window captured · {method}"),
            Err(error) => format!("Live window capture failed: {error:#}"),
        };
        let _ = fs::write(root.join("test-result.txt"), message);
        return Ok(());
    }
    let ui_smoke = args
        .get(1)
        .filter(|s| *s == "--ui-smoke")
        .and_then(|_| args.get(2))
        .map(PathBuf::from);
    #[cfg(windows)]
    let Some(_instance) = platform::instance() else {
        return Ok(());
    };
    if let Some(marker) = ui_smoke.clone() {
        std::panic::set_hook(Box::new(move |panic| {
            let _ = fs::write(&marker, format!("Rust UI panic: {panic}"));
        }));
    }
    let options = eframe::NativeOptions {
        #[cfg(windows)]
        renderer: eframe::Renderer::Wgpu,
        #[cfg(windows)]
        wgpu_options: windows_renderer(),
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1040.0, 750.0])
            .with_decorations(false)
            .with_min_inner_size([820.0, 600.0]),
        ..Default::default()
    };
    let diagnostic = ui_smoke.clone();
    let result = eframe::run_native(
        "RollClip Rust",
        options,
        Box::new(move |context| Ok(Box::new(App::new(context, ui_smoke)))),
    );
    if let Err(error) = &result {
        if let Some(path) = diagnostic {
            let _ = fs::write(path, format!("Rust UI startup failed: {error:?}"));
        } else {
            let folder = store::root();
            let _ = fs::create_dir_all(&folder);
            let _ = fs::write(folder.join("startup-error.txt"), format!("{error:?}"));
        }
    }
    result
}

#[cfg(windows)]
fn windows_renderer() -> eframe::egui_wgpu::WgpuConfiguration {
    use eframe::{
        egui_wgpu::{WgpuConfiguration, WgpuSetup, WgpuSetupCreateNew},
        wgpu,
    };
    let mut setup = WgpuSetupCreateNew::default();
    setup.instance_descriptor.backends = wgpu::Backends::DX12 | wgpu::Backends::VULKAN;
    setup.native_adapter_selector = Some(Arc::new(|adapters, surface| {
        adapters
            .iter()
            .filter(|adapter| surface.is_none_or(|surface| adapter.is_surface_supported(surface)))
            .max_by_key(|adapter| match adapter.get_info().device_type {
                wgpu::DeviceType::DiscreteGpu => 3,
                wgpu::DeviceType::IntegratedGpu => 2,
                _ => 1,
            })
            .cloned()
            .ok_or_else(|| {
                format!(
                    "No suitable DX12/Vulkan adapter among {} candidates",
                    adapters.len()
                )
            })
    }));
    WgpuConfiguration {
        wgpu_setup: WgpuSetup::CreateNew(setup),
        ..Default::default()
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Page {
    Home,
    Library,
    Editor,
    Settings,
}

enum Outcome {
    Recording(media::Recorder),
    Clip(Clip, bool, bool),
    Backup(String),
    Preview(PathBuf),
    Devices(Vec<String>),
}

struct Editor {
    id: String,
    start: f64,
    end: f64,
    seek: f64,
    muted: bool,
    cap: u64,
    texture: Option<TextureHandle>,
}

struct App {
    root: PathBuf,
    state: State,
    read_only: bool,
    page: Page,
    targets: Vec<platform::Target>,
    target: Option<usize>,
    recorder: Option<media::Recorder>,
    prune: Instant,
    status: String,
    hotkey: Arc<AtomicU32>,
    hotkey_rx: Receiver<Result<()>>,
    tx: Sender<Result<Outcome>>,
    rx: Receiver<Result<Outcome>>,
    busy: bool,
    capture_starting: bool,
    upload_tx: Sender<(store::Upload, Result<String>)>,
    upload_rx: Receiver<(store::Upload, Result<String>)>,
    uploading: bool,
    devices: Vec<String>,
    selected: Option<String>,
    search: String,
    favorites: bool,
    shared_only: bool,
    grid: bool,
    sort: usize,
    delete: Option<String>,
    editor: Option<Editor>,
    backdrop: TextureHandle,
    textures: HashMap<String, Option<TextureHandle>>,
    smoke: Option<PathBuf>,
    screenshot_requested: bool,
    smoke_page: usize,
}

impl App {
    fn new(context: &eframe::CreationContext<'_>, smoke: Option<PathBuf>) -> Self {
        let root = smoke
            .as_ref()
            .and_then(|path| path.parent())
            .map(|p| p.join("profile"))
            .unwrap_or_else(store::root);
        let (state, status, read_only) = match init(&root).and_then(|()| store::load(&root)) {
            Ok(state) => (state, "Hazır · Rust Ön Sürüm".to_owned(), false),
            Err(error) => (State::default(), format!("{error:#}"), true),
        };
        let ctx = &context.egui_ctx;
        let mut fonts = egui::FontDefinitions::default();
        fonts.font_data.insert(
            "Manrope".into(),
            Arc::new(FontData::from_static(include_bytes!(
                "../../src/RollClip.App/Assets/Fonts/Manrope-SemiBold.ttf"
            ))),
        );
        fonts
            .families
            .insert(FontFamily::Name("Manrope".into()), vec!["Manrope".into()]);
        ctx.set_fonts(fonts);
        let mut visuals = egui::Visuals::dark();
        visuals.override_text_color = Some(Color32::from_rgb(235, 239, 235));
        visuals.panel_fill = Color32::from_rgb(7, 9, 8);
        visuals.window_fill = Color32::from_rgb(17, 23, 20);
        visuals.widgets.inactive.bg_fill = Color32::from_rgb(24, 31, 28);
        visuals.widgets.hovered.bg_fill = Color32::from_rgb(39, 53, 43);
        visuals.selection.bg_fill = Color32::from_rgb(42, 60, 25);
        visuals.selection.stroke = Stroke::new(1.0_f32, chrome::ACCENT);
        for widget in [
            &mut visuals.widgets.inactive,
            &mut visuals.widgets.hovered,
            &mut visuals.widgets.active,
        ] {
            widget.corner_radius = egui::CornerRadius::same(16);
            widget.bg_stroke = Stroke::new(1.0_f32, Color32::from_white_alpha(58));
        }
        ctx.set_theme(egui::Theme::Dark);
        ctx.set_visuals_of(egui::Theme::Dark, visuals);
        ctx.style_mut(|style| style.spacing.item_spacing = Vec2::new(9.0, 9.0));
        let image = image::load_from_memory(include_bytes!(
            "../../src/RollClip.App/Assets/ChromePoster.png"
        ))
        .expect("bundled background");
        let backdrop = texture(ctx, "chrome", image);
        let (tx, rx) = mpsc::channel();
        let (upload_tx, upload_rx) = mpsc::channel();
        let (hotkey_tx, hotkey_rx) = mpsc::channel();
        let hotkey = Arc::new(AtomicU32::new(0));
        platform::hotkey(hotkey.clone(), hotkey_tx);
        Self {
            root,
            state,
            read_only,
            page: Page::Home,
            targets: platform::targets(),
            target: None,
            recorder: None,
            prune: Instant::now(),
            status,
            hotkey,
            hotkey_rx,
            tx,
            rx,
            busy: false,
            capture_starting: false,
            upload_tx,
            upload_rx,
            uploading: false,
            devices: Vec::new(),
            selected: None,
            search: String::new(),
            favorites: false,
            shared_only: false,
            grid: true,
            sort: 0,
            delete: None,
            editor: None,
            backdrop,
            textures: HashMap::new(),
            smoke,
            screenshot_requested: false,
            smoke_page: 0,
        }
    }

    fn save(&mut self) {
        if !self.read_only {
            if let Err(error) = store::save(&self.root, &self.state) {
                self.status = format!("{error:#}");
            }
        }
    }

    fn job(&mut self, label: &str, work: impl FnOnce(&Path) -> Result<Outcome> + Send + 'static) {
        if self.busy || self.read_only {
            return;
        }
        self.status = label.into();
        self.busy = true;
        let tx = self.tx.clone();
        let root = self.root.clone();
        let backup = self.state.settings.backup.clone();
        thread::spawn(move || {
            let mut result = work(&root);
            if let (Ok(Outcome::Clip(clip, _, _)), Some(folder)) = (&mut result, backup) {
                clip.backed_up = store::backup(&root, clip, &folder).is_ok();
            }
            let _ = tx.send(result);
        });
    }

    fn messages(&mut self, ctx: &egui::Context) {
        while let Ok(result) = self.rx.try_recv() {
            self.busy = false;
            self.capture_starting = false;
            match result {
                Err(error) => self.status = format!("{error:#}"),
                Ok(Outcome::Recording(recorder)) => {
                    self.status = format!("Kayıt açık · {}", recorder.label);
                    self.recorder = Some(recorder);
                    self.hotkey
                        .store(self.state.settings.hotkey, Ordering::Relaxed);
                }
                Ok(Outcome::Clip(clip, replay, copy)) => {
                    self.status = format!("Klip hazır · {:.1} sn", clip.seconds);
                    if self.state.settings.backup.is_some() && !clip.backed_up {
                        self.status.push_str(" · Yedekleme başarısız");
                    }
                    if copy {
                        match platform::copy_file(&store::video(&self.root, &clip.id)) {
                            Ok(()) => self.status = "MP4 panoya kopyalandı.".into(),
                            Err(error) => self.status = format!("MP4 kaydedildi. {error}"),
                        }
                    }
                    self.selected = Some(clip.id.clone());
                    let id = clip.id.clone();
                    self.state.clips.push(clip);
                    if replay && self.state.settings.auto_upload {
                        self.queue_upload(id);
                    } else {
                        self.save();
                    }
                }
                Ok(Outcome::Backup(id)) => {
                    if let Some(clip) = self.state.clips.iter_mut().find(|clip| clip.id == id) {
                        clip.backed_up = true;
                    }
                    self.status = "Yerel yedek hazır.".into();
                    self.save();
                }
                Ok(Outcome::Preview(path)) => {
                    if let Some(editor) = &mut self.editor {
                        if let Ok(image) = image::open(&path) {
                            editor.texture = Some(texture(ctx, "edit-frame", image));
                        }
                    }
                    let _ = fs::remove_file(path);
                    self.status = "Önizleme karesi hazır.".into();
                }
                Ok(Outcome::Devices(devices)) => {
                    self.status = format!("{} ses aygıtı bulundu.", devices.len());
                    self.devices = devices;
                }
            }
        }
        while let Ok(event) = self.hotkey_rx.try_recv() {
            match event {
                Ok(()) => self.save_replay(),
                Err(error) => self.status = error.to_string(),
            }
        }
        if self.prune.elapsed() >= Duration::from_secs(1) {
            self.prune = Instant::now();
            if let Some(recorder) = &mut self.recorder {
                if recorder.stopped() {
                    self.recorder = None;
                    self.hotkey.store(0, Ordering::Relaxed);
                    self.status = "Yakalama durdu. Pencereyi tekrar seçip deneyin.".into();
                } else if let Err(error) = recorder.prune(self.state.settings.seconds.clamp(1, 180))
                {
                    self.recorder = None;
                    self.hotkey.store(0, Ordering::Relaxed);
                    self.status = format!("Tampon temizlenemedi. {error}");
                }
            }
        }
        while let Ok((task, result)) = self.upload_rx.try_recv() {
            self.uploading = false;
            self.state
                .uploads
                .retain(|item| item.clip_id != task.clip_id);
            match result {
                Ok(link) => {
                    if let Some(clip) = self
                        .state
                        .clips
                        .iter_mut()
                        .find(|clip| clip.id == task.clip_id)
                    {
                        clip.link = Some(link.clone());
                        clip.expires =
                            (task.provider == 0).then(|| chrono::Utc::now().timestamp() + 86400);
                    }
                    if self.state.settings.auto_copy {
                        ctx.copy_text(link);
                    }
                    self.status = "Paylaşım bağlantısı hazır.".into();
                }
                Err(error) => self.status = format!("Yükleme başarısız. {error:#}"),
            }
            self.save();
        }
        if !self.uploading && !self.read_only {
            if let Some(task) = self.state.uploads.first().cloned() {
                self.uploading = true;
                let path = store::video(&self.root, &task.clip_id);
                let tx = self.upload_tx.clone();
                thread::spawn(move || {
                    let result = media::upload(&path, task.provider);
                    let _ = tx.send((task, result));
                });
            }
        }
    }

    fn toggle_capture(&mut self) {
        if self.recorder.take().is_some() {
            self.hotkey.store(0, Ordering::Relaxed);
            self.status = "Kayıt kapalı.".into();
            return;
        }
        let target = if self.state.settings.method == 3 {
            Some(platform::Target {
                title: "DXGI ekranı".into(),
                handle: 0,
            })
        } else {
            self.targets
                .iter()
                .find(|target| Some(target.handle) == self.target)
                .cloned()
        };
        let Some(target) = target else {
            self.status = "Önce yakalanacak pencereyi seçin.".into();
            return;
        };
        let settings = self.state.settings.clone();
        self.capture_starting = true;
        self.job("Yakalama başlatılıyor…", move |root| {
            Ok(Outcome::Recording(media::Recorder::start(
                root, &target, &settings,
            )?))
        });
    }

    fn save_replay(&mut self) {
        if self.busy || self.read_only {
            return;
        }
        let Some(recorder) = &self.recorder else {
            return;
        };
        match recorder.snapshot(&self.root, self.state.settings.seconds.clamp(1, 180)) {
            Ok(snapshot) => self.job("Klip kaydediliyor…", move |root| {
                Ok(Outcome::Clip(media::join(root, &snapshot)?, true, false))
            }),
            Err(error) => self.status = error.to_string(),
        }
    }

    fn import(&mut self, path: Option<PathBuf>) {
        if self.busy || self.read_only {
            return;
        }
        let path = path.or_else(|| {
            rfd::FileDialog::new()
                .add_filter("MP4", &["mp4"])
                .pick_file()
        });
        if let Some(path) = path {
            self.job("MP4 içe aktarılıyor…", move |root| {
                Ok(Outcome::Clip(media::import(root, &path)?, false, false))
            });
        }
    }

    fn queue_upload(&mut self, clip_id: String) {
        if self.read_only
            || self
                .state
                .uploads
                .iter()
                .any(|task| task.clip_id == clip_id)
        {
            return;
        }
        self.state.uploads.push(store::Upload {
            clip_id,
            provider: self.state.settings.provider,
        });
        self.save();
        self.status = "Klip yükleme kuyruğuna alındı.".into();
    }

    fn edit(&mut self, clip: Clip) {
        self.editor = Some(Editor {
            id: clip.id,
            start: 0.0,
            end: clip.seconds,
            seek: 0.0,
            muted: false,
            cap: 19,
            texture: None,
        });
        self.page = Page::Editor;
    }

    fn open(&mut self, value: &std::ffi::OsStr) {
        if let Err(error) = platform::open(value) {
            self.status = error.to_string();
        }
    }

    fn home(&mut self, ui: &mut egui::Ui) {
        use chrome::{Icon, Stage, ACCENT, TEXT};
        let stage = Stage::new(ui.max_rect());
        let rect = |x, y, w, h| stage.rect(x, y, w, h);
        let painter = ui.painter().clone();
        let glass = |x, y, w, h, radius| {
            chrome::glass(&painter, rect(x, y, w, h), radius * stage.scale, false)
        };
        if chrome::icon_button(
            ui,
            rect(51.0, 208.0, 51.0, 51.0),
            "RollClip",
            Icon::Video,
            true,
            true,
        )
        .clicked()
        {
            self.page = Page::Home;
        }
        stage.text(
            &painter,
            114.0,
            213.0,
            "Klip al, kes, Discord’a gönder.",
            12.0,
            false,
            MUTED,
        );
        stage.text(&painter, 114.0, 231.0, "RollClip", 19.0, true, TEXT);
        if chrome::icon_button(
            ui,
            rect(356.0, 208.0, 51.0, 51.0),
            "Tanılamayı kopyala",
            Icon::Copy,
            false,
            true,
        )
        .clicked()
        {
            self.copy_status(ui.ctx());
        }
        if chrome::icon_button(
            ui,
            rect(425.0, 208.0, 51.0, 51.0),
            "Klip arşivini aç",
            Icon::Library,
            false,
            true,
        )
        .clicked()
        {
            self.page = Page::Library;
        }

        glass(51.0, 280.0, 425.0, 58.0, 29.0);
        if chrome::icon_button(
            ui,
            rect(57.0, 285.0, 47.0, 47.0),
            "Pencereleri yenile",
            Icon::Refresh,
            false,
            true,
        )
        .clicked()
        {
            self.targets = platform::targets();
        }
        let selection = rect(111.0, 287.0, 355.0, 44.0);
        let name = self
            .targets
            .iter()
            .find(|target| Some(target.handle) == self.target)
            .map(|target| target.title.as_str())
            .unwrap_or("Oyun penceresini seç");
        ui.scope_builder(egui::UiBuilder::new().max_rect(selection), |ui| {
            if self.recorder.is_some() || self.capture_starting {
                ui.disable();
            }
            ui.spacing_mut().interact_size.y = selection.height();
            egui::ComboBox::from_id_salt("capture-target")
                .width(selection.width())
                .selected_text(short(name, 48))
                .show_ui(ui, |ui| {
                    for target in &self.targets {
                        ui.selectable_value(
                            &mut self.target,
                            Some(target.handle),
                            short(&target.title, 65),
                        );
                    }
                });
        });

        glass(51.0, 356.0, 425.0, 185.0, 38.0);
        stage.text(&painter, 80.0, 383.0, "Klip süresi", 19.0, true, TEXT);
        stage.text(
            &painter,
            80.0,
            410.0,
            "Son anları tek tuşla kaydet.",
            12.0,
            false,
            MUTED,
        );
        for (index, seconds) in DURATIONS.into_iter().enumerate() {
            let slot = rect(81.0 + index as f32 * 73.0, 464.0, 73.0, 55.0);
            let active = self.state.settings.seconds == seconds;
            let response = chrome::hit(ui, slot, &format!("{seconds} saniye"), true);
            if active || response.hovered() {
                painter.rect_filled(
                    slot,
                    5.0,
                    Color32::from_white_alpha(if active { 17 } else { 10 }),
                );
            }
            if response.has_focus() {
                painter.rect_stroke(
                    slot,
                    5.0,
                    Stroke::new(1.0_f32, ACCENT),
                    egui::StrokeKind::Inside,
                );
            }
            painter.text(
                slot.center(),
                egui::Align2::CENTER_CENTER,
                seconds.to_string(),
                FontId::proportional(stage.scale * if active { 30.0 } else { 15.0 }),
                if active { TEXT } else { MUTED },
            );
            if response.clicked() && !active {
                self.state.settings.seconds = seconds;
                self.save();
            }
        }

        glass(522.0, 221.0, 207.0, 208.0, 37.0);
        stage.text(&painter, 548.0, 248.0, "Kayıt", 19.0, true, TEXT);
        let armed = self.recorder.is_some();
        stage.text(
            &painter,
            548.0,
            280.0,
            if armed {
                "Kayıt açık"
            } else if self.capture_starting {
                "Başlatılıyor…"
            } else {
                "Kayıt kapalı"
            },
            12.0,
            false,
            if armed { ACCENT } else { MUTED },
        );
        if chrome::switch(
            ui,
            rect(660.0, 245.0, 48.0, 27.0),
            if armed {
                "Kaydı durdur"
            } else {
                "Kaydı başlat"
            },
            armed,
            !self.read_only && (!self.busy || armed),
        )
        .clicked()
        {
            self.toggle_capture();
        }
        let replay = rect(581.0, 327.0, 99.0, 83.0);
        let replay_button = chrome::hit(
            ui,
            replay,
            "Son klibi MP4 olarak kaydet",
            armed && !self.busy,
        );
        let face = painter.with_clip_rect(rect(523.0, 222.0, 205.0, 205.0));
        chrome::replay_key(
            &face,
            replay,
            &format!("F{}", self.state.settings.hotkey),
            armed || replay_button.hovered(),
        );
        if replay_button.clicked() {
            self.save_replay();
        }

        glass(742.0, 221.0, 207.0, 208.0, 37.0);
        stage.text(&painter, 767.0, 248.0, "Paylaşım", 19.0, true, TEXT);
        stage.text(&painter, 767.0, 282.0, "Otomatik", 12.0, false, MUTED);
        if chrome::switch(
            ui,
            rect(883.0, 275.0, 46.0, 26.0),
            "Yeni klipleri otomatik yükle",
            self.state.settings.auto_upload,
            !self.read_only,
        )
        .clicked()
        {
            self.state.settings.auto_upload = !self.state.settings.auto_upload;
            self.save();
        }
        let clip = self.state.clips.last().cloned();
        if chrome::upload_plate(
            ui,
            rect(765.0, 316.0, 160.0, 82.0),
            clip.is_some() && !self.busy,
        )
        .clicked()
        {
            if let Some(clip) = clip {
                self.edit(clip);
            }
        }

        glass(533.0, 449.0, 405.0, 86.0, 43.0);
        let home = rect(557.0, 467.0, 114.0, 49.0);
        let response = chrome::hit(ui, home, "Ana ekran", true);
        chrome::glass(&painter, home, home.height() * 0.5, true);
        chrome::icon(
            &painter,
            rect(574.0, 491.0, 0.0, 0.0).min,
            15.0 * stage.scale,
            Icon::Home,
            ACCENT,
        );
        stage.text(
            &painter,
            587.0,
            485.0,
            "Ana ekran",
            12.0,
            false,
            if response.hovered() { ACCENT } else { TEXT },
        );
        for (i, (icon, label)) in [
            (Icon::Library, "Klip arşivi"),
            (Icon::Settings, "Ayarlar"),
            (Icon::Plus, "MP4 içe aktar"),
            (Icon::Copy, "Durumu kopyala"),
        ]
        .into_iter()
        .enumerate()
        {
            if chrome::icon_button(
                ui,
                rect(683.0 + 57.0 * i as f32, 467.0, 49.0, 49.0),
                label,
                icon,
                false,
                i != 2 || !self.busy && !self.read_only,
            )
            .clicked()
            {
                match icon {
                    Icon::Library => self.page = Page::Library,
                    Icon::Settings => self.page = Page::Settings,
                    Icon::Plus => self.import(None),
                    Icon::Copy => self.copy_status(ui.ctx()),
                    _ => {}
                }
            }
        }
        if self.status != "Hazır · Rust Ön Sürüm" {
            stage.text(
                &painter,
                536.0,
                551.0,
                &short(&self.status, 60),
                12.0,
                false,
                if armed { ACCENT } else { MUTED },
            );
        }
    }

    fn copy_status(&mut self, ctx: &egui::Context) {
        ctx.copy_text(self.status.clone());
        self.status = "Durum panoya kopyalandı.".into();
    }

    fn window_controls(&mut self, ui: &mut egui::Ui, bounds: egui::Rect) {
        use chrome::Icon;
        let maximized = ui
            .ctx()
            .input(|input| input.viewport().maximized.unwrap_or(false));
        if !maximized {
            let grip = egui::Rect::from_min_max(bounds.max - Vec2::splat(17.0), bounds.max);
            let response = ui
                .interact(grip, ui.id().with("resize-grip"), egui::Sense::drag())
                .on_hover_cursor(egui::CursorIcon::ResizeNwSe);
            if response.drag_started() {
                ui.ctx()
                    .send_viewport_cmd(egui::ViewportCommand::BeginResize(
                        egui::ResizeDirection::SouthEast,
                    ));
            }
        }
        let drag = egui::Rect::from_min_max(
            bounds.min,
            egui::pos2(bounds.right() - 128.0, bounds.top() + 32.0),
        );
        let response = ui.interact(
            drag,
            ui.id().with("title-drag"),
            egui::Sense::click_and_drag(),
        );
        if response.double_clicked() {
            ui.ctx()
                .send_viewport_cmd(egui::ViewportCommand::Maximized(!maximized));
        } else if response.drag_started() {
            ui.ctx().send_viewport_cmd(egui::ViewportCommand::StartDrag);
        }
        for (index, (label, icon, command)) in [
            (
                "Simge durumuna küçült",
                Icon::Minimize,
                egui::ViewportCommand::Minimized(true),
            ),
            (
                if maximized { "Geri yükle" } else { "Büyüt" },
                if maximized {
                    Icon::Restore
                } else {
                    Icon::Maximize
                },
                egui::ViewportCommand::Maximized(!maximized),
            ),
            ("Pencereyi kapat", Icon::Close, egui::ViewportCommand::Close),
        ]
        .into_iter()
        .enumerate()
        {
            let rect = egui::Rect::from_min_size(
                egui::pos2(
                    bounds.right() - 116.0 + index as f32 * 37.0,
                    bounds.top() + 5.0,
                ),
                Vec2::new(35.0, 25.0),
            );
            if chrome::icon_button(ui, rect, label, icon, false, true).clicked() {
                ui.ctx().send_viewport_cmd(command);
            }
        }
    }

    fn panel_header(&mut self, ui: &mut egui::Ui, title: &str) {
        ui.horizontal(|ui| {
            heading(ui, title, 23.0);
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                let (_, rect) = ui.allocate_space(Vec2::splat(31.0));
                if chrome::icon_button(ui, rect, "Ana ekrana dön", chrome::Icon::Close, false, true)
                    .clicked()
                {
                    self.page = if self.page == Page::Editor {
                        Page::Library
                    } else {
                        Page::Home
                    };
                }
                if self.page == Page::Library
                    && ui
                        .add_enabled(
                            !self.busy && !self.read_only,
                            egui::Button::new("MP4 içe aktar"),
                        )
                        .clicked()
                {
                    self.import(None);
                }
            });
        });
        ui.add_space(6.0);
    }

    fn library(&mut self, ui: &mut egui::Ui) {
        glass_panel(ui, |ui| {
            self.panel_header(ui, "Klip arşivi");
            ui.horizontal_wrapped(|ui| {
                ui.add(
                    egui::TextEdit::singleline(&mut self.search).hint_text("Klip adına göre ara"),
                );
                ui.toggle_value(&mut self.favorites, "Favoriler");
                ui.toggle_value(&mut self.shared_only, "Yüklenenler");
                ui.toggle_value(&mut self.grid, "Izgara");
                egui::ComboBox::from_id_salt("sort")
                    .selected_text(["En yeni", "En uzun", "En büyük"][self.sort])
                    .show_ui(ui, |ui| {
                        for (i, label) in ["En yeni", "En uzun", "En büyük"].into_iter().enumerate()
                        {
                            ui.selectable_value(&mut self.sort, i, label);
                        }
                    });
            });
            let query = self.search.to_lowercase();
            let mut clips: Vec<_> = self
                .state
                .clips
                .iter()
                .filter(|c| {
                    (!self.favorites || c.favorite)
                        && (!self.shared_only || c.link.is_some())
                        && c.name.to_lowercase().contains(&query)
                })
                .cloned()
                .collect();
            match self.sort {
                1 => clips.sort_by(|a, b| b.seconds.total_cmp(&a.seconds)),
                2 => clips.sort_by(|a, b| b.bytes.cmp(&a.bytes)),
                _ => clips.reverse(),
            }
            if clips.is_empty() {
                ui.colored_label(MUTED, "Klip bulunamadı. MP4 içe aktar veya F8 ile klip al.");
            }
            let height = (ui.available_height() - 124.0).clamp(170.0, 460.0);
            egui::ScrollArea::vertical()
                .max_height(height)
                .show(ui, |ui| {
                    if self.grid {
                        let row_width = (ui.available_width() / 3.0 - 28.0).max(130.0);
                        for row in clips.chunks(3) {
                            ui.horizontal(|ui| {
                                for clip in row {
                                    ui.push_id(&clip.id, |ui| {
                                        let mut edit = false;
                                        glass_surface(ui, 13.0, 17.0, |ui| {
                                            ui.set_width(row_width - 28.0);
                                            let thumb = self
                                                .textures
                                                .entry(clip.id.clone())
                                                .or_insert_with(|| {
                                                    image::open(store::thumbnail(
                                                        &self.root, &clip.id,
                                                    ))
                                                    .ok()
                                                    .map(|image| texture(ui.ctx(), &clip.id, image))
                                                });
                                            let image_size = Vec2::new(
                                                row_width - 28.0,
                                                (row_width - 28.0) * 0.56,
                                            );
                                            if let Some(thumb) = thumb {
                                                let image =
                                                    egui::Image::new((thumb.id(), image_size))
                                                        .corner_radius(10);
                                                let response = ui
                                                    .add(egui::Button::image(image).frame(false))
                                                    .on_hover_text(
                                                        "Klibi seç · Düzenlemek için çift tıkla",
                                                    );
                                                if response.clicked() {
                                                    self.selected = Some(clip.id.clone());
                                                }
                                                edit = response.double_clicked();
                                            }
                                            if ui
                                                .selectable_label(
                                                    self.selected.as_ref() == Some(&clip.id),
                                                    clip_label(clip),
                                                )
                                                .clicked()
                                            {
                                                self.selected = Some(clip.id.clone());
                                            }
                                        });
                                        if edit {
                                            self.edit(clip.clone());
                                        }
                                    });
                                }
                            });
                        }
                    } else {
                        for clip in &clips {
                            if ui
                                .selectable_label(
                                    self.selected.as_ref() == Some(&clip.id),
                                    clip_label(clip),
                                )
                                .clicked()
                            {
                                self.selected = Some(clip.id.clone());
                            }
                        }
                    }
                });
            let selected = self
                .selected
                .as_ref()
                .and_then(|id| self.state.clips.iter().find(|clip| &clip.id == id))
                .cloned();
            if let Some(clip) = selected {
                self.clip_actions(ui, clip);
            }
        });
    }

    fn clip_actions(&mut self, ui: &mut egui::Ui, clip: Clip) {
        ui.separator();
        let pending = self
            .state
            .uploads
            .iter()
            .any(|task| task.clip_id == clip.id);
        ui.label(&clip.name);
        ui.horizontal_wrapped(|ui| {
            if ui.button("Kes ve paylaş").clicked() {
                self.edit(clip.clone());
            }
            if ui.button("Oynat").clicked() {
                self.open(store::video(&self.root, &clip.id).as_os_str());
            }
            if ui
                .add_enabled(
                    !pending && !self.read_only,
                    egui::Button::new(if pending {
                        "Yükleme kuyruğunda"
                    } else {
                        "Bağlantı oluştur"
                    }),
                )
                .clicked()
            {
                self.queue_upload(clip.id.clone());
            }
            if let Some(link) = &clip.link {
                if clip
                    .expires
                    .is_some_and(|time| time <= chrono::Utc::now().timestamp())
                {
                    ui.colored_label(MUTED, "Bağlantı süresi doldu");
                } else if ui.button("Bağlantıyı kopyala").clicked() {
                    ui.ctx().copy_text(link.clone());
                    self.status = "Bağlantı panoya kopyalandı.".into();
                }
            }
            if ui
                .button(if clip.favorite {
                    "Favoriden çıkar"
                } else {
                    "Favori"
                })
                .clicked()
            {
                if let Some(item) = self.state.clips.iter_mut().find(|c| c.id == clip.id) {
                    item.favorite = !clip.favorite;
                }
                self.save();
            }
            if ui.button("Klip klasörü").clicked() {
                self.open(self.root.join("clips").as_os_str());
            }
            if let Some(folder) = self.state.settings.backup.clone() {
                if ui
                    .add_enabled(
                        !self.busy,
                        egui::Button::new(if clip.backed_up {
                            "Tekrar yedekle"
                        } else {
                            "Yedekle"
                        }),
                    )
                    .clicked()
                {
                    let clip = clip.clone();
                    self.job("Yedekleniyor…", move |root| {
                        store::backup(root, &clip, &folder)?;
                        Ok(Outcome::Backup(clip.id))
                    });
                }
            }
            if ui
                .add_enabled(
                    !self.busy && !pending && !self.read_only,
                    egui::Button::new("Sil"),
                )
                .clicked()
            {
                self.delete = Some(clip.id.clone());
            }
        });
    }

    fn editor(&mut self, ui: &mut egui::Ui) {
        let Some(mut editor) = self.editor.take() else {
            self.page = Page::Library;
            return;
        };
        let Some(clip) = self.state.clips.iter().find(|c| c.id == editor.id).cloned() else {
            self.page = Page::Library;
            return;
        };
        glass_panel(ui, |ui| {
            self.panel_header(ui, "Kes ve paylaş");
            ui.label(&clip.name);
            if editor.texture.is_none() {
                editor.texture = image::open(store::thumbnail(&self.root, &clip.id))
                    .ok()
                    .map(|im| texture(ui.ctx(), "preview", im));
            }
            if let Some(image) = &editor.texture {
                ui.image((image.id(), Vec2::new(360.0, 202.0)));
            }
            ui.add(egui::Slider::new(&mut editor.seek, 0.0..=clip.seconds).text("Konum"));
            ui.horizontal_wrapped(|ui| {
                if ui
                    .add_enabled(!self.busy, egui::Button::new("Kareyi göster"))
                    .clicked()
                {
                    let id = clip.id.clone();
                    let at = editor.seek.min((clip.seconds - 0.15).max(0.0));
                    self.job("Önizleme hazırlanıyor…", move |root| {
                        let output = root.join("jobs").join(format!("{}.png", store::id()));
                        media::frame(&store::video(root, &id), &output, at)?;
                        Ok(Outcome::Preview(output))
                    });
                }
                if ui.button("Orijinali oynat").clicked() {
                    self.open(store::video(&self.root, &clip.id).as_os_str());
                }
                if ui.button("Başlangıcı burada yap").clicked() {
                    editor.start = editor.seek;
                }
                if ui.button("Bitişi burada yap").clicked() {
                    editor.end = editor.seek;
                }
                if ui.button("Yer imi ekle").clicked() {
                    if let Some(item) = self.state.clips.iter_mut().find(|c| c.id == clip.id) {
                        if item.bookmarks.len() < 50 {
                            item.bookmarks.push(editor.seek);
                        }
                    }
                    self.save();
                }
            });
            ui.horizontal_wrapped(|ui| {
                for (index, point) in clip.bookmarks.iter().enumerate() {
                    let response = ui.button(format!("{point:.1} sn"));
                    if response.clicked() {
                        editor.seek = *point;
                    }
                    response.context_menu(|ui| {
                        if ui.button("Yer imini sil").clicked() {
                            if let Some(item) =
                                self.state.clips.iter_mut().find(|c| c.id == clip.id)
                            {
                                item.bookmarks.remove(index);
                            }
                            self.save();
                            ui.close_menu();
                        }
                    });
                }
            });
            ui.horizontal_wrapped(|ui| {
                ui.add(
                    egui::DragValue::new(&mut editor.start)
                        .range(0.0..=clip.seconds)
                        .prefix("Başlangıç ")
                        .suffix(" sn"),
                );
                ui.add(
                    egui::DragValue::new(&mut editor.end)
                        .range(0.0..=clip.seconds)
                        .prefix("Bitiş ")
                        .suffix(" sn"),
                );
                ui.checkbox(&mut editor.muted, "Sesi kapat");
                egui::ComboBox::from_id_salt("export-cap")
                    .selected_text(format!("{} MB", editor.cap))
                    .show_ui(ui, |ui| {
                        for cap in [10, 19, 50, 100, 500] {
                            ui.selectable_value(&mut editor.cap, cap, format!("{cap} MB"));
                        }
                    });
            });
            ui.horizontal_wrapped(|ui| {
                for (label, copy) in [
                    ("Kırpılmış kopyayı kaydet", false),
                    ("Paylaşım dosyasını kopyala", true),
                ] {
                    if ui
                        .add_enabled(
                            !self.busy && editor.end > editor.start,
                            egui::Button::new(label),
                        )
                        .clicked()
                    {
                        let (clip, start, end, muted, cap) = (
                            clip.clone(),
                            editor.start,
                            editor.end,
                            editor.muted,
                            editor.cap,
                        );
                        self.job("Klip dışa aktarılıyor…", move |root| {
                            Ok(Outcome::Clip(
                                media::trim(root, &clip, start, end, muted, copy.then_some(cap))?,
                                false,
                                copy,
                            ))
                        });
                    }
                }
            });
        });
        self.editor = Some(editor);
    }

    fn settings(&mut self, ui: &mut egui::Ui) {
        glass_panel(ui, |ui| {
            self.panel_header(ui, "Ayarlar");
            egui::ScrollArea::vertical().show(ui, |ui| {
                ui.columns(2, |cols| {
                    let left = &mut cols[0];
                    heading(left, "Kayıt", 17.0);
                    choice(
                        left,
                        "Kalite",
                        &mut self.state.settings.quality,
                        &["Yüksek · 1080p60", "Dengeli · 1080p30", "Hafif · 720p30"],
                    );
                    choice(
                        left,
                        "Yakalama",
                        &mut self.state.settings.method,
                        &[
                            "Otomatik · WGC → GDI",
                            "WGC · Pencere",
                            "GDI · Pencere",
                            "DXGI · Tüm ekran",
                        ],
                    );
                    choice(
                        left,
                        "Kodlayıcı",
                        &mut self.state.settings.encoder,
                        &[
                            "Media Foundation · Yazılım",
                            "NVIDIA NVENC",
                            "AMD AMF",
                            "Intel QSV",
                        ],
                    );
                    let mut key = self.state.settings.hotkey.clamp(6, 11) as usize - 6;
                    choice(
                        left,
                        "Kısayol",
                        &mut key,
                        &["F6", "F7", "F8", "F9", "F10", "F11"],
                    );
                    self.state.settings.hotkey = key as u32 + 6;
                    if self.state.settings.method == 3 {
                        left.colored_label(
                            MUTED,
                            "DXGI seçiliyken Kayıt düğmesi ekran 0’ın tamamını kaydeder.",
                        );
                    }
                    left.label("DirectShow ses aygıtı");
                    left.add(
                        egui::TextEdit::singleline(&mut self.state.settings.audio_device)
                            .hint_text("Boş bırakılırsa sessiz kayıt"),
                    );
                    if left
                        .add_enabled(
                            !self.busy && self.recorder.is_none(),
                            egui::Button::new("Ses aygıtlarını listele"),
                        )
                        .clicked()
                    {
                        self.job("Ses aygıtları aranıyor…", |_| {
                            Ok(Outcome::Devices(media::audio_devices()?))
                        });
                    }
                    for device in &self.devices {
                        if left
                            .selectable_label(self.state.settings.audio_device == *device, device)
                            .clicked()
                        {
                            self.state.settings.audio_device.clone_from(device);
                        }
                    }
                    let right = &mut cols[1];
                    heading(right, "Paylaşım ve arşiv", 17.0);
                    choice(
                        right,
                        "Servis",
                        &mut self.state.settings.provider,
                        &["Litterbox · 24 saat", "Gofile"],
                    );
                    right.checkbox(
                        &mut self.state.settings.auto_upload,
                        "Yeni kayıt kliplerini otomatik yükle",
                    );
                    right.checkbox(
                        &mut self.state.settings.auto_copy,
                        "Hazır bağlantıyı panoya kopyala",
                    );
                    if right.button("Yedek klasörü seç").clicked() {
                        if let Some(path) = rfd::FileDialog::new().pick_folder() {
                            self.state.settings.backup = Some(path);
                        }
                    }
                    if let Some(path) = &self.state.settings.backup {
                        right.label(path.to_string_lossy());
                        if right.button("Yedeklemeyi kapat").clicked() {
                            self.state.settings.backup = None;
                        }
                    }
                    let bytes: u64 = self.state.clips.iter().map(|c| c.bytes).sum();
                    right.colored_label(
                        MUTED,
                        format!(
                            "{} klip · {:.1} MB",
                            self.state.clips.len(),
                            bytes as f64 / 1_000_000.0
                        ),
                    );
                    if right.button("Sürüm notları / indir").clicked() {
                        self.open(std::ffi::OsStr::new(RELEASES));
                    }
                });
            });
            if ui
                .add_enabled(!self.read_only, egui::Button::new("Ayarları kaydet"))
                .clicked()
            {
                self.status = "Ayarlar kaydedildi.".into();
                if self.recorder.is_some() {
                    self.hotkey
                        .store(self.state.settings.hotkey, Ordering::Relaxed);
                }
                self.save();
            }
        });
    }
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _: &mut eframe::Frame) {
        self.messages(ctx);
        if self.busy && ctx.input(|input| input.viewport().close_requested()) {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            self.status = "Klip işlemi bitince pencereyi kapatabilirsiniz.".into();
        }
        if let Some(path) = ctx.input(|i| i.raw.dropped_files.first().and_then(|f| f.path.clone()))
        {
            if path
                .extension()
                .is_some_and(|ext| ext.eq_ignore_ascii_case("mp4"))
            {
                self.import(Some(path));
            }
        }
        egui::CentralPanel::default()
            .frame(Frame::new().fill(Color32::from_rgb(2, 3, 3)))
            .show(ctx, |ui| {
                let bounds = ui.max_rect();
                ui.painter().image(
                    self.backdrop.id(),
                    bounds,
                    egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0)),
                    Color32::WHITE,
                );
                self.window_controls(ui, bounds);
                if self.page == Page::Home {
                    self.home(ui);
                } else {
                    let available = bounds.shrink2(Vec2::new(26.0, 42.0));
                    let width = available.width().min(940.0);
                    let content = egui::Rect::from_min_max(
                        egui::pos2(
                            bounds.center().x - width / 2.0,
                            available.top() + 52.0_f32.min(available.height() * 0.08),
                        ),
                        egui::pos2(bounds.center().x + width / 2.0, available.bottom()),
                    );
                    ui.scope_builder(egui::UiBuilder::new().max_rect(content), |ui| {
                        ui.set_width(content.width());
                        ui.set_clip_rect(content);
                        egui::ScrollArea::vertical()
                            .id_salt("main-content")
                            .show(ui, |ui| {
                                ui.set_width(content.width());
                                match self.page {
                                    Page::Library => self.library(ui),
                                    Page::Editor => self.editor(ui),
                                    Page::Settings => self.settings(ui),
                                    Page::Home => {}
                                }
                            });
                    });
                    let status = short(&self.status, 108);
                    ui.painter().text(
                        egui::pos2(content.left() + 7.0, content.bottom() + 16.0),
                        egui::Align2::LEFT_TOP,
                        status,
                        FontId::proportional(12.0),
                        MUTED,
                    );
                    if self.busy || self.uploading {
                        ui.put(
                            egui::Rect::from_min_size(
                                egui::pos2(content.right() - 28.0, content.bottom() + 12.0),
                                Vec2::splat(20.0),
                            ),
                            egui::Spinner::new(),
                        );
                    }
                }
            });
        if let Some(id) = self.delete.clone() {
            egui::Window::new("Klibi sil")
                .collapsible(false)
                .resizable(false)
                .show(ctx, |ui| {
                    ui.label("Bu Rust arşivindeki kopya silinsin mi?");
                    ui.horizontal(|ui| {
                        if ui.button("Vazgeç").clicked() {
                            self.delete = None;
                        }
                        if ui.button("Sil").clicked() {
                            match fs::remove_file(store::video(&self.root, &id)) {
                                Ok(()) => {
                                    self.state.clips.retain(|c| c.id != id);
                                    let _ = fs::remove_file(store::thumbnail(&self.root, &id));
                                    self.textures.remove(&id);
                                    self.selected = None;
                                    self.status = "Klip silindi.".into();
                                    self.save();
                                }
                                Err(error) => self.status = error.to_string(),
                            }
                            self.delete = None;
                        }
                    });
                });
        }
        if let Some(marker) = &self.smoke {
            if !ctx.style().visuals.dark_mode {
                let _ = fs::write(marker, "RollClip dark theme was not applied.");
                self.smoke = None;
            } else if self.read_only {
                let _ = fs::write(marker, &self.status);
                self.smoke = None;
            } else if !self.screenshot_requested {
                self.screenshot_requested = true;
                ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(egui::UserData::default()));
            } else if let Some(image) = ctx.input(|input| {
                input.events.iter().find_map(|event| {
                    if let egui::Event::Screenshot { image, .. } = event {
                        Some(image.clone())
                    } else {
                        None
                    }
                })
            }) {
                let pixels: Vec<u8> = image
                    .pixels
                    .iter()
                    .flat_map(|color| color.to_array())
                    .collect();
                let screen = ["Ana-Ekran", "Klip-Arsivi", "Ayarlar", "Editor"][self.smoke_page];
                let path = marker.with_file_name(format!("RollClip-Rust-{screen}.png"));
                let result = image::save_buffer(
                    path,
                    &pixels,
                    image.width() as u32,
                    image.height() as u32,
                    image::ColorType::Rgba8,
                );
                match result {
                    Err(error) => {
                        let _ = fs::write(marker, error.to_string());
                        self.smoke = None;
                    }
                    Ok(()) if self.smoke_page == 3 => {
                        let _ = fs::write(
                            marker,
                            "PASS: home, populated gallery, settings and editor rendered",
                        );
                        self.smoke = None;
                        self.page = Page::Home;
                    }
                    Ok(()) => {
                        self.smoke_page += 1;
                        self.screenshot_requested = false;
                        match self.smoke_page {
                            1 => {
                                self.page = Page::Library;
                            }
                            2 => {
                                self.page = Page::Settings;
                            }
                            _ => {
                                if let Some(clip) = self.state.clips.first().cloned() {
                                    self.edit(clip);
                                } else {
                                    let _ = fs::write(
                                        marker,
                                        "UI screenshot fixture is missing a clip.",
                                    );
                                    self.smoke = None;
                                }
                            }
                        }
                        ctx.request_repaint();
                    }
                }
            }
        }
        ctx.request_repaint_after(Duration::from_millis(
            if self.busy || self.uploading || self.recorder.is_some() {
                65
            } else {
                500
            },
        ));
    }
}

impl Drop for App {
    fn drop(&mut self) {
        self.hotkey.store(u32::MAX, Ordering::Relaxed);
    }
}

fn glass_panel(ui: &mut egui::Ui, contents: impl FnOnce(&mut egui::Ui)) {
    glass_surface(ui, 24.0, 31.0, |ui| {
        ui.set_min_width(ui.available_width());
        contents(ui);
    });
}

fn glass_surface(
    ui: &mut egui::Ui,
    padding: f32,
    radius: f32,
    contents: impl FnOnce(&mut egui::Ui),
) {
    let painter = ui.painter().clone();
    let layer = painter.add(egui::Shape::Noop);
    let response = Frame::new()
        .inner_margin(padding)
        .show(ui, contents)
        .response;
    let mut shapes = Vec::new();
    chrome::glass_shapes(response.rect, radius, false, &mut shapes);
    painter.set(layer, egui::Shape::Vec(shapes));
}

fn heading(ui: &mut egui::Ui, label: &str, size: f32) {
    ui.label(RichText::new(label).font(FontId::new(size, FontFamily::Name("Manrope".into()))));
}

fn choice(ui: &mut egui::Ui, label: &str, selected: &mut usize, values: &[&str]) {
    ui.label(label);
    egui::ComboBox::from_id_salt(label)
        .selected_text(values.get(*selected).copied().unwrap_or(values[0]))
        .show_ui(ui, |ui| {
            for (i, value) in values.iter().enumerate() {
                ui.selectable_value(selected, i, *value);
            }
        });
}

fn texture(ctx: &egui::Context, id: &str, image: image::DynamicImage) -> TextureHandle {
    let rgba = image.to_rgba8();
    ctx.load_texture(
        id,
        egui::ColorImage::from_rgba_unmultiplied(
            [rgba.width() as usize, rgba.height() as usize],
            rgba.as_raw(),
        ),
        egui::TextureOptions::LINEAR,
    )
}

fn short(text: &str, max: usize) -> String {
    let mut chars = text.chars();
    let shortened: String = chars.by_ref().take(max).collect();
    if chars.next().is_some() {
        shortened + "…"
    } else {
        shortened
    }
}

fn clip_label(clip: &Clip) -> String {
    format!(
        "{}{} · {:.1} sn · {:.1} MB",
        if clip.favorite { "★ " } else { "" },
        short(&clip.name, 36),
        clip.seconds,
        clip.bytes as f64 / 1_000_000.0
    )
}
