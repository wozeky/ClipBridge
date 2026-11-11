use eframe::egui::{
    self, vec2, Color32, FontId, Painter, Pos2, Rect, Response, Shape, Stroke, StrokeKind, Ui,
};

pub const BACKGROUND: Color32 = Color32::from_rgb(20, 22, 23);
pub const SURFACE: Color32 = Color32::from_rgb(27, 30, 31);
pub const SELECTED: Color32 = Color32::from_rgb(37, 48, 42);
pub const TEXT: Color32 = Color32::from_rgb(237, 240, 237);
pub const MUTED: Color32 = Color32::from_rgb(159, 171, 166);
pub const ACCENT: Color32 = Color32::from_rgb(159, 226, 168);
pub const BORDER: Stroke = Stroke {
    width: 1.0,
    color: Color32::from_rgb(57, 64, 63),
};

pub fn visuals() -> egui::Visuals {
    let mut visuals = egui::Visuals::dark();
    visuals.panel_fill = BACKGROUND;
    visuals.window_fill = SURFACE;
    visuals.window_stroke = BORDER;
    visuals.extreme_bg_color = BACKGROUND;
    visuals.faint_bg_color = SURFACE;
    visuals.selection.bg_fill = SELECTED;
    visuals.selection.stroke = Stroke::new(1.0_f32, ACCENT);
    visuals.widgets.noninteractive.fg_stroke.color = TEXT;
    for (widget, fill) in [
        (&mut visuals.widgets.inactive, Color32::from_rgb(36, 40, 41)),
        (&mut visuals.widgets.hovered, Color32::from_rgb(48, 54, 53)),
        (&mut visuals.widgets.active, SELECTED),
        (&mut visuals.widgets.open, SELECTED),
    ] {
        widget.bg_fill = fill;
        widget.weak_bg_fill = fill;
        widget.corner_radius = egui::CornerRadius::same(8);
        widget.bg_stroke = BORDER;
        widget.fg_stroke.color = TEXT;
        widget.expansion = 0.0;
    }
    visuals.widgets.active.bg_stroke = Stroke::new(1.0_f32, ACCENT);
    visuals
}

#[derive(Clone, Copy)]
pub enum Icon {
    Video,
    Refresh,
    Minimize,
    Maximize,
    Restore,
    Close,
}

pub fn icon(p: &Painter, center: Pos2, icon: Icon, color: Color32) {
    let size = 16.0;
    let stroke = Stroke::new(1.5_f32, color);
    let pt = |x: f32, y: f32| center + vec2(x, y) * size;
    let line = |points: &[(f32, f32)]| {
        p.add(Shape::line(
            points.iter().map(|&(x, y)| pt(x, y)).collect(),
            stroke,
        ));
    };
    let rect = |x, y, w, h| {
        p.rect_stroke(
            Rect::from_min_max(pt(x, y), pt(x + w, y + h)),
            2.0,
            stroke,
            StrokeKind::Inside,
        );
    };
    match icon {
        Icon::Video => {
            rect(-0.42, -0.30, 0.58, 0.60);
            line(&[(0.16, -0.12), (0.42, -0.27), (0.42, 0.27), (0.16, 0.12)]);
        }
        Icon::Refresh => {
            let arc: Vec<_> = (0..=20)
                .map(|i| {
                    let a = -0.7 + i as f32 * 5.7 / 20.0;
                    (a.cos() * 0.34, a.sin() * 0.34)
                })
                .collect();
            line(&arc);
            line(&[(0.34, -0.40), (0.32, -0.16), (0.08, -0.19)]);
        }
        Icon::Minimize => line(&[(-0.30, 0.0), (0.30, 0.0)]),
        Icon::Maximize => rect(-0.27, -0.27, 0.54, 0.54),
        Icon::Restore => {
            rect(-0.30, -0.10, 0.44, 0.42);
            line(&[
                (-0.13, -0.15),
                (-0.13, -0.29),
                (0.31, -0.29),
                (0.31, 0.14),
                (0.19, 0.14),
            ]);
        }
        Icon::Close => {
            line(&[(-0.26, -0.26), (0.26, 0.26)]);
            line(&[(0.26, -0.26), (-0.26, 0.26)]);
        }
    }
}

pub fn icon_button(ui: &mut Ui, rect: Rect, label: &str, symbol: Icon, enabled: bool) -> Response {
    let response = ui
        .scope_builder(egui::UiBuilder::new().id_salt(label), |ui| {
            ui.add_enabled_ui(enabled, |ui| {
                ui.put(rect, egui::Button::new("").frame(false))
            })
            .inner
        })
        .inner;
    response.widget_info(|| egui::WidgetInfo::labeled(egui::WidgetType::Button, enabled, label));
    if response.hovered() {
        ui.painter()
            .rect_filled(rect, 8.0, ui.visuals().widgets.hovered.bg_fill);
    }
    if response.has_focus() {
        ui.painter()
            .rect_stroke(rect, 8.0, Stroke::new(1.0_f32, ACCENT), StrokeKind::Inside);
    }
    icon(
        ui.painter(),
        rect.center(),
        symbol,
        if response.hovered() { TEXT } else { MUTED },
    );
    response.on_hover_text(label)
}

pub fn keycap(ui: &mut Ui, hotkey: u32, active: bool) {
    let (_, rect) = ui.allocate_space(vec2(32.0, 24.0));
    let painter = ui.painter();
    painter.rect(rect, 6.0, BACKGROUND, BORDER, StrokeKind::Inside);
    painter.text(
        rect.center(),
        egui::Align2::CENTER_CENTER,
        format!("F{hotkey}"),
        FontId::monospace(12.0),
        if active { ACCENT } else { MUTED },
    );
}
