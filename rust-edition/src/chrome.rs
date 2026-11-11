use eframe::egui::{
    self, epaint::Mesh, pos2, vec2, Align2, Color32, FontFamily, FontId, Painter, Pos2, Rect,
    Response, Shape, Stroke, StrokeKind, Ui,
};

pub const TEXT: Color32 = Color32::from_rgb(233, 238, 233);
pub const MUTED: Color32 = Color32::from_rgb(156, 167, 158);
pub const ACCENT: Color32 = Color32::from_rgb(143, 255, 57);

pub struct Stage {
    origin: Pos2,
    pub scale: f32,
}
impl Stage {
    pub fn new(bounds: Rect) -> Self {
        let scale = (bounds.width() / 1000.0).min(bounds.height() / 750.0);
        let origin = bounds.center() - vec2(500.0, 375.0) * scale;
        Self { origin, scale }
    }
    pub fn rect(&self, x: f32, y: f32, w: f32, h: f32) -> Rect {
        Rect::from_min_size(
            self.origin + vec2(x, y) * self.scale,
            vec2(w, h) * self.scale,
        )
    }
    pub fn text(
        &self,
        p: &Painter,
        x: f32,
        y: f32,
        text: &str,
        size: f32,
        heading: bool,
        color: Color32,
    ) {
        let family = if heading {
            FontFamily::Name("Manrope".into())
        } else {
            FontFamily::Proportional
        };
        p.text(
            self.rect(x, y, 0.0, 0.0).min,
            Align2::LEFT_TOP,
            text,
            FontId::new(size * self.scale, family),
            color,
        );
    }
}

pub fn glass(p: &Painter, rect: Rect, radius: f32, emphasis: bool) {
    let mut shapes = Vec::new();
    glass_shapes(rect, radius, emphasis, &mut shapes);
    p.extend(shapes);
}

pub fn glass_shapes(rect: Rect, radius: f32, emphasis: bool, shapes: &mut Vec<Shape>) {
    for i in 0..3 {
        let spread = 2.0 + i as f32 * 2.5;
        shapes.push(Shape::rect_filled(
            rect.translate(vec2(0.0, 3.0)).expand(spread),
            radius + spread,
            Color32::from_black_alpha(21 - i * 5),
        ));
    }
    let points = rounded(rect, radius);
    let fill = |point: Pos2| {
        let u = (point.x - rect.left()) / rect.width();
        let v = (point.y - rect.top()) / rect.height();
        let shine = (1.0 - (u * 0.65 + v * 0.75)).max(0.0);
        if emphasis {
            Color32::from_rgba_unmultiplied(
                (22.0 + shine * 23.0) as u8,
                (39.0 + shine * 36.0) as u8,
                (13.0 + shine * 12.0) as u8,
                205,
            )
        } else {
            let level = (11.0 + shine * 34.0) as u8;
            Color32::from_rgba_unmultiplied(
                level,
                level + 4,
                level + 2,
                (168.0 - shine * 65.0) as u8,
            )
        }
    };
    shapes.push(gradient(&points, rect.center(), fill).into());
    shapes.push(Shape::rect_stroke(
        rect,
        radius,
        Stroke::new(0.8_f32, Color32::from_white_alpha(43)),
        StrokeKind::Inside,
    ));
    let edge: Vec<_> = points.iter().take(13).copied().collect();
    shapes.push(Shape::line(
        edge,
        Stroke::new(1.0_f32, Color32::from_white_alpha(70)),
    ));
}

fn rounded(rect: Rect, radius: f32) -> Vec<Pos2> {
    let r = radius.min(rect.width().min(rect.height()) * 0.5);
    let centers = [
        pos2(rect.left() + r, rect.top() + r),
        pos2(rect.right() - r, rect.top() + r),
        pos2(rect.right() - r, rect.bottom() - r),
        pos2(rect.left() + r, rect.bottom() - r),
    ];
    let mut points = Vec::with_capacity(36);
    for (corner, center) in centers.into_iter().enumerate() {
        for step in 0..=8 {
            let a = std::f32::consts::PI * (1.0 + corner as f32 * 0.5 + step as f32 / 16.0);
            points.push(center + vec2(a.cos(), a.sin()) * r);
        }
    }
    points
}

fn gradient(points: &[Pos2], center: Pos2, color: impl Fn(Pos2) -> Color32) -> Mesh {
    let mut mesh = Mesh::default();
    mesh.colored_vertex(center, color(center));
    for (i, &point) in points.iter().enumerate() {
        mesh.colored_vertex(point, color(point));
        mesh.add_triangle(0, i as u32 + 1, (i + 1) as u32 % points.len() as u32 + 1);
    }
    mesh
}

#[derive(Clone, Copy)]
pub enum Icon {
    Video,
    Library,
    Settings,
    Plus,
    Copy,
    Refresh,
    Home,
    Upload,
    Minimize,
    Maximize,
    Restore,
    Close,
}
pub fn icon(p: &Painter, center: Pos2, size: f32, icon: Icon, color: Color32) {
    let stroke = Stroke::new((size * 0.075).max(1.0), color);
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
        Icon::Library => {
            rect(-0.42, -0.32, 0.84, 0.64);
            line(&[(-0.12, -0.15), (0.18, 0.0), (-0.12, 0.15), (-0.12, -0.15)]);
        }
        Icon::Plus => {
            line(&[(-0.34, 0.0), (0.34, 0.0)]);
            line(&[(0.0, -0.34), (0.0, 0.34)]);
        }
        Icon::Copy => {
            rect(-0.22, -0.37, 0.55, 0.64);
            line(&[
                (-0.29, -0.20),
                (-0.39, -0.20),
                (-0.39, 0.40),
                (0.14, 0.40),
                (0.14, 0.33),
            ]);
        }
        Icon::Home => {
            line(&[(-0.38, -0.02), (0.0, -0.37), (0.38, -0.02)]);
            line(&[
                (-0.28, -0.08),
                (-0.28, 0.34),
                (-0.08, 0.34),
                (-0.08, 0.09),
                (0.09, 0.09),
                (0.09, 0.34),
                (0.28, 0.34),
                (0.28, -0.08),
            ]);
        }
        Icon::Settings => {
            let vertices: Vec<_> = (0..=32)
                .map(|i| {
                    let angle = i as f32 * std::f32::consts::TAU / 32.0;
                    let r = if i % 4 < 2 { 0.42 } else { 0.34 };
                    (angle.cos() * r, angle.sin() * r)
                })
                .collect();
            line(&vertices);
            p.circle_stroke(center, size * 0.15, stroke);
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
        Icon::Upload => {
            line(&[(-0.32, -0.34), (0.32, -0.34)]);
            line(&[(0.0, 0.39), (0.0, -0.17)]);
            line(&[(-0.22, 0.04), (0.0, -0.17), (0.22, 0.04)]);
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

pub fn hit(ui: &mut Ui, rect: Rect, label: &str, enabled: bool) -> Response {
    let mut response = ui
        .scope_builder(egui::UiBuilder::new().id_salt(label), |ui| {
            if !enabled {
                ui.disable();
            }
            ui.put(rect, egui::Button::new("").frame(false))
        })
        .inner;
    response.widget_info(|| egui::WidgetInfo::labeled(egui::WidgetType::Button, enabled, label));
    if enabled && response.hovered() {
        response = response.on_hover_cursor(egui::CursorIcon::PointingHand);
    }
    response.on_hover_text(label)
}

pub fn icon_button(
    ui: &mut Ui,
    rect: Rect,
    label: &str,
    symbol: Icon,
    emphasis: bool,
    enabled: bool,
) -> Response {
    let response = hit(ui, rect, label, enabled);
    glass(
        ui.painter(),
        rect,
        rect.height() * 0.5,
        emphasis || response.hovered(),
    );
    if response.has_focus() {
        ui.painter().rect_stroke(
            rect,
            rect.height() * 0.5,
            Stroke::new(1.0_f32, ACCENT),
            StrokeKind::Inside,
        );
    }
    icon(
        ui.painter(),
        rect.center(),
        rect.height() * 0.38,
        symbol,
        if emphasis || response.hovered() {
            ACCENT
        } else {
            TEXT
        },
    );
    response
}

pub fn switch(ui: &mut Ui, rect: Rect, label: &str, on: bool, enabled: bool) -> Response {
    let response = hit(ui, rect, label, enabled);
    let radius = rect.height() * 0.5;
    let fill = if on {
        Color32::from_rgb(28, 57, 16)
    } else {
        Color32::from_rgb(14, 17, 15)
    };
    ui.painter().rect(
        rect,
        radius,
        fill,
        Stroke::new(
            1.0_f32,
            if response.has_focus() || response.hovered() {
                ACCENT
            } else {
                Color32::from_white_alpha(70)
            },
        ),
        StrokeKind::Inside,
    );
    let x = if on {
        rect.right() - radius
    } else {
        rect.left() + radius
    };
    ui.painter().circle_filled(
        pos2(x, rect.center().y),
        radius * 0.70,
        if on { ACCENT } else { MUTED },
    );
    response
}

pub fn upload_plate(ui: &mut Ui, rect: Rect, enabled: bool) -> Response {
    let response = hit(ui, rect, "Son klibi kes ve paylaş", enabled);
    let p = ui.painter();
    let alpha = if response.hovered() {
        235
    } else if enabled {
        180
    } else {
        105
    };
    let top = Rect::from_min_max(
        rect.min,
        pos2(rect.right(), rect.bottom() - rect.height() * 0.23),
    );
    for face in [top.translate(vec2(0.0, rect.height() * 0.22)), top] {
        let points: Vec<_> = (0..48)
            .map(|i| {
                let a = i as f32 * std::f32::consts::TAU / 48.0;
                face.center() + vec2(a.cos() * face.width(), a.sin() * face.height()) * 0.5
            })
            .collect();
        let paint = |point: Pos2| {
            let x = (point.x - face.left()) / face.width();
            let y = (point.y - face.top()) / face.height();
            let band = ((x * 1.4 + y * 0.62) * 7.6).sin().powi(4);
            let l = (18.0 + band * if face == top { 150.0 } else { 56.0 }) as u8;
            Color32::from_rgba_unmultiplied(l, l + 7, l + 4, alpha)
        };
        p.add(gradient(&points, face.center(), paint));
        p.add(Shape::closed_line(
            points,
            Stroke::new(1.0_f32, Color32::from_white_alpha(alpha / 2)),
        ));
    }
    icon(
        p,
        top.center(),
        top.height() * 0.50,
        Icon::Upload,
        if response.hovered() {
            ACCENT
        } else if enabled {
            TEXT
        } else {
            Color32::from_white_alpha(80)
        },
    );
    response
}

pub fn replay_key(p: &Painter, rect: Rect, label: &str, active: bool) {
    let angle = -0.38_f32;
    let rotate = |point: Pos2| {
        let v = point - rect.center();
        rect.center()
            + vec2(
                v.x * angle.cos() - v.y * angle.sin(),
                v.x * angle.sin() + v.y * angle.cos(),
            )
    };
    let points: Vec<_> = rounded(rect, rect.height() * 0.29)
        .into_iter()
        .map(rotate)
        .collect();
    p.add(gradient(&points, rect.center(), |point| {
        let stripe = (1.0 - ((point.x - rect.left()) / rect.width()).clamp(0.0, 1.0)) * 35.0;
        let l = (8.0 + stripe) as u8;
        Color32::from_rgba_unmultiplied(l, l + 5, l + 3, if active { 212 } else { 125 })
    }));
    p.add(Shape::closed_line(
        points,
        Stroke::new(
            1.0_f32,
            Color32::from_white_alpha(if active { 65 } else { 24 }),
        ),
    ));
    let galley = p.layout_no_wrap(
        label.to_owned(),
        FontId::proportional(rect.height() * 0.36),
        if active {
            TEXT
        } else {
            Color32::from_white_alpha(80)
        },
    );
    let center = galley.rect.center().to_vec2();
    let pivot = vec2(
        center.x * angle.cos() - center.y * angle.sin(),
        center.x * angle.sin() + center.y * angle.cos(),
    );
    p.add(
        egui::epaint::TextShape::new(rect.center() - pivot, galley, Color32::PLACEHOLDER)
            .with_angle(angle),
    );
}
