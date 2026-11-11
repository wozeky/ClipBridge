#[derive(Clone)]
pub struct Target {
    pub handle: usize,
    pub title: String,
}

#[cfg(windows)]
mod native {
    use super::Target;
    use anyhow::{ensure, Result};
    use std::{
        ffi::OsStr,
        os::windows::ffi::OsStrExt,
        path::Path,
        ptr::{copy_nonoverlapping, null, null_mut},
        sync::{
            atomic::{AtomicU32, Ordering},
            mpsc::Sender,
            Arc,
        },
        time::Duration,
    };
    use windows_sys::Win32::{
        Foundation::{BOOL, HWND, LPARAM},
        System::{
            DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData},
            Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE},
            Ole::CF_HDROP,
        },
        UI::{
            Input::KeyboardAndMouse::{RegisterHotKey, UnregisterHotKey, MOD_NOREPEAT},
            Shell::{ShellExecuteW, DROPFILES},
            WindowsAndMessaging::{
                EnumWindows, GetWindowTextLengthW, GetWindowTextW, IsWindowVisible, PeekMessageW,
                MSG, PM_REMOVE, SW_SHOWNORMAL, WM_HOTKEY,
            },
        },
    };

    pub struct Instance(windows_sys::Win32::Foundation::HANDLE);
    impl Drop for Instance {
        fn drop(&mut self) {
            unsafe {
                windows_sys::Win32::Foundation::CloseHandle(self.0);
            }
        }
    }

    pub fn instance() -> Option<Instance> {
        use windows_sys::Win32::{
            Foundation::{GetLastError, ERROR_ALREADY_EXISTS},
            System::Threading::CreateMutexW,
        };
        let name: Vec<u16> = "Local\\RollClipRust.Desktop"
            .encode_utf16()
            .chain([0])
            .collect();
        let handle = unsafe { CreateMutexW(null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return None;
        }
        let instance = Instance(handle);
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            None
        } else {
            Some(instance)
        }
    }

    pub fn targets() -> Vec<Target> {
        unsafe extern "system" fn collect(window: HWND, data: LPARAM) -> BOOL {
            if IsWindowVisible(window) == 0 {
                return 1;
            }
            let length = GetWindowTextLengthW(window);
            if length <= 0 || length > 1024 {
                return 1;
            }
            let mut text = vec![0; length as usize + 1];
            let copied = GetWindowTextW(window, text.as_mut_ptr(), length + 1);
            if copied > 0 {
                let title = String::from_utf16_lossy(&text[..copied as usize]);
                if !title.starts_with("RollClip Rust") && title != "Program Manager" {
                    (*(data as *mut Vec<Target>)).push(Target {
                        handle: window as usize,
                        title,
                    });
                }
            }
            1
        }
        let mut result: Vec<Target> = Vec::new();
        unsafe {
            EnumWindows(Some(collect), &mut result as *mut _ as LPARAM);
        }
        result.sort_by(|a, b| a.title.to_lowercase().cmp(&b.title.to_lowercase()));
        result
    }

    pub fn find_preview_window() -> Result<Target> {
        let title = "RollClip Rust";
        let name: Vec<u16> = title.encode_utf16().chain([0]).collect();
        let handle = unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::FindWindowW(null(), name.as_ptr())
        };
        ensure!(!handle.is_null(), "Test penceresi bulunamadı.");
        Ok(Target {
            handle: handle as usize,
            title: title.into(),
        })
    }

    pub fn hotkey(selected: Arc<AtomicU32>, events: Sender<Result<()>>) {
        std::thread::spawn(move || unsafe {
            let mut message: MSG = std::mem::zeroed();
            PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE);
            let mut registered = false;
            let mut previous = 0;
            loop {
                let key = selected.load(Ordering::Relaxed);
                if key == u32::MAX {
                    break;
                }
                if key != previous {
                    if registered {
                        UnregisterHotKey(null_mut(), 1);
                    }
                    registered =
                        key != 0 && RegisterHotKey(null_mut(), 1, MOD_NOREPEAT, 0x6f + key) != 0;
                    if key != 0
                        && !registered
                        && events
                            .send(Err(anyhow::anyhow!(
                                "F{key} başka bir uygulamada kullanılıyor."
                            )))
                            .is_err()
                    {
                        break;
                    }
                    previous = key;
                }
                while PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) != 0 {
                    if message.message == WM_HOTKEY && events.send(Ok(())).is_err() {
                        return;
                    }
                }
                std::thread::sleep(Duration::from_millis(30));
            }
            if registered {
                UnregisterHotKey(null_mut(), 1);
            }
        });
    }

    pub fn open(path: &OsStr) -> Result<()> {
        let wide: Vec<u16> = path.encode_wide().chain([0]).collect();
        let code = unsafe {
            ShellExecuteW(
                null_mut(),
                null(),
                wide.as_ptr(),
                null(),
                null(),
                SW_SHOWNORMAL,
            )
        };
        ensure!(code as isize > 32, "Windows bu öğeyi açamadı.");
        Ok(())
    }

    pub fn copy_file(path: &Path) -> Result<()> {
        let wide: Vec<u16> = path.as_os_str().encode_wide().chain([0, 0]).collect();
        unsafe {
            let owner = windows_sys::Win32::UI::Input::KeyboardAndMouse::GetActiveWindow();
            ensure!(
                !owner.is_null() && OpenClipboard(owner) != 0,
                "Windows panosu meşgul."
            );
            struct Clipboard;
            impl Drop for Clipboard {
                fn drop(&mut self) {
                    unsafe {
                        CloseClipboard();
                    }
                }
            }
            let _clipboard = Clipboard;
            let offset = std::mem::size_of::<DROPFILES>();
            let memory = GlobalAlloc(GMEM_MOVEABLE, offset + wide.len() * 2);
            ensure!(!memory.is_null(), "Pano belleği ayrılamadı.");
            let buffer = GlobalLock(memory);
            if buffer.is_null() {
                windows_sys::Win32::Foundation::GlobalFree(memory);
                anyhow::bail!("Pano belleği kilitlenemedi.");
            }
            let mut header: DROPFILES = std::mem::zeroed();
            header.pFiles = offset as u32;
            header.fWide = 1;
            buffer.cast::<DROPFILES>().write(header);
            copy_nonoverlapping(
                wide.as_ptr(),
                buffer.cast::<u8>().add(offset).cast::<u16>(),
                wide.len(),
            );
            GlobalUnlock(memory);
            if EmptyClipboard() == 0 || SetClipboardData(CF_HDROP as u32, memory).is_null() {
                windows_sys::Win32::Foundation::GlobalFree(memory);
                anyhow::bail!("Video panoya kopyalanamadı.");
            }
        }
        Ok(())
    }
}

#[cfg(windows)]
pub use native::*;

#[cfg(not(windows))]
pub fn targets() -> Vec<Target> {
    Vec::new()
}
#[cfg(not(windows))]
pub fn hotkey(
    _: std::sync::Arc<std::sync::atomic::AtomicU32>,
    _: std::sync::mpsc::Sender<anyhow::Result<()>>,
) {
}
#[cfg(not(windows))]
pub fn open(path: &std::ffi::OsStr) -> anyhow::Result<()> {
    std::process::Command::new("xdg-open").arg(path).spawn()?;
    Ok(())
}
#[cfg(not(windows))]
pub fn copy_file(_: &std::path::Path) -> anyhow::Result<()> {
    anyhow::bail!("Dosya panosu bu sürümde Windows gerektirir.")
}
