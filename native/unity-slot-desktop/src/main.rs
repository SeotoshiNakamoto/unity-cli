use std::ffi::c_void;
use std::{env, process};
use windows::Win32::{
    Foundation::{BOOL, HWND, LPARAM, RECT},
    UI::WindowsAndMessaging::{
        EnumWindows, GetWindow, GetWindowRect, GetWindowTextLengthW, GetWindowTextW,
        GetWindowThreadProcessId, IsWindowVisible, GW_OWNER,
    },
};

#[derive(Default)]
struct WindowSearch {
    pid: u32,
    best_hwnd: usize,
    best_area: i64,
    best_title: String,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("Error: {error}");
        process::exit(1);
    }
}

fn run() -> Result<(), String> {
    let args: Vec<String> = env::args().skip(1).collect();
    let action = args.first().map(String::as_str).unwrap_or("doctor");
    match action {
        "doctor" => doctor(),
        "move" => move_window(&args[1..]),
        _ => Err(format!(
            "unknown action {action:?}; available: doctor, move"
        )),
    }
}

fn doctor() -> Result<(), String> {
    let count = winvd::get_desktop_count().map_err(format_winvd)?;
    let current = winvd::get_current_desktop()
        .and_then(|desktop| desktop.get_index())
        .map_err(format_winvd)?;
    println!("{{\"desktopCount\":{count},\"currentDesktop\":{current}}}");
    Ok(())
}

fn move_window(args: &[String]) -> Result<(), String> {
    let pid: u32 = required_value(args, "--pid")?
        .parse()
        .map_err(|_| "--pid must be a positive integer".to_string())?;
    let desktop: u32 = required_value(args, "--desktop")?
        .parse()
        .map_err(|_| "--desktop must be a zero-based integer".to_string())?;
    if pid == 0 {
        return Err("--pid must be positive".to_string());
    }
    let count = winvd::get_desktop_count().map_err(format_winvd)?;
    if desktop >= count {
        return Err(format!(
            "desktop index {desktop} is outside the available range 0..{}",
            count.saturating_sub(1)
        ));
    }
    let search = find_main_window(pid)?;
    let hwnd = HWND(search.best_hwnd as *mut c_void);
    winvd::move_window_to_desktop(desktop, &hwnd).map_err(format_winvd)?;
    let actual = winvd::get_desktop_by_window(hwnd)
        .and_then(|value| value.get_index())
        .map_err(format_winvd)?;
    if actual != desktop {
        return Err(format!(
            "window desktop verification failed: expected {desktop}, got {actual}"
        ));
    }
    println!(
        "{{\"pid\":{pid},\"hwnd\":{},\"desktop\":{actual},\"title\":{}}}",
        search.best_hwnd,
        json_string(&search.best_title)
    );
    Ok(())
}

fn required_value<'a>(args: &'a [String], name: &str) -> Result<&'a str, String> {
    args.iter()
        .position(|arg| arg == name)
        .and_then(|index| args.get(index + 1))
        .map(String::as_str)
        .ok_or_else(|| format!("missing {name} <value>"))
}

fn find_main_window(pid: u32) -> Result<WindowSearch, String> {
    let mut search = WindowSearch {
        pid,
        ..WindowSearch::default()
    };
    unsafe {
        EnumWindows(
            Some(enum_window),
            LPARAM((&mut search as *mut WindowSearch) as isize),
        )
        .map_err(|error| format!("EnumWindows failed: {error}"))?;
    }
    if search.best_hwnd == 0 {
        return Err(format!("no visible top-level window found for pid {pid}"));
    }
    Ok(search)
}

unsafe extern "system" fn enum_window(hwnd: HWND, lparam: LPARAM) -> BOOL {
    let search = &mut *(lparam.0 as *mut WindowSearch);
    let mut owner_pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut owner_pid));
    if owner_pid != search.pid || !IsWindowVisible(hwnd).as_bool() {
        return BOOL(1);
    }
    if !GetWindow(hwnd, GW_OWNER).unwrap_or_default().0.is_null() {
        return BOOL(1);
    }
    let mut rect = RECT::default();
    if GetWindowRect(hwnd, &mut rect).is_err() {
        return BOOL(1);
    }
    let width = i64::from((rect.right - rect.left).max(0));
    let height = i64::from((rect.bottom - rect.top).max(0));
    let area = width * height;
    if area <= search.best_area {
        return BOOL(1);
    }
    search.best_hwnd = hwnd.0 as usize;
    search.best_area = area;
    search.best_title = window_title(hwnd);
    BOOL(1)
}

unsafe fn window_title(hwnd: HWND) -> String {
    let length = GetWindowTextLengthW(hwnd);
    if length <= 0 {
        return String::new();
    }
    let mut buffer = vec![0u16; length as usize + 1];
    let copied = GetWindowTextW(hwnd, &mut buffer);
    String::from_utf16_lossy(&buffer[..copied as usize])
}

fn format_winvd(error: winvd::Error) -> String {
    format!("virtual desktop API failed: {error:?}")
}

fn json_string(value: &str) -> String {
    let mut output = String::from("\"");
    for ch in value.chars() {
        match ch {
            '\\' => output.push_str("\\\\"),
            '"' => output.push_str("\\\""),
            '\n' => output.push_str("\\n"),
            '\r' => output.push_str("\\r"),
            '\t' => output.push_str("\\t"),
            ch if ch.is_control() => output.push_str(&format!("\\u{:04x}", ch as u32)),
            ch => output.push(ch),
        }
    }
    output.push('"');
    output
}
