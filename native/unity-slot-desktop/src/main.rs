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
        "doctor" => doctor(&args[1..]),
        "move" => move_window(&args[1..]),
        _ => Err(format!(
            "unknown action {action:?}; available: doctor, move"
        )),
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct DesktopSnapshot {
    index: u32,
    name: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ResolvedDesktop {
    index: u32,
    name: String,
    source: &'static str,
}

fn doctor(args: &[String]) -> Result<(), String> {
    let desktops = desktop_snapshots()?;
    let count = desktops.len() as u32;
    let current = winvd::get_current_desktop()
        .and_then(|desktop| desktop.get_index())
        .map_err(format_winvd)?;
    let selected = if has_desktop_target(args) {
        Some(resolve_desktop(args, &desktops)?)
    } else {
        None
    };
    let desktop_json = desktops
        .iter()
        .map(|desktop| {
            format!(
                "{{\"index\":{},\"name\":{}}}",
                desktop.index,
                json_string(&desktop.name)
            )
        })
        .collect::<Vec<_>>()
        .join(",");
    let selection_json = selected.map_or_else(String::new, |desktop| {
        format!(
            ",\"selectedDesktop\":{},\"selectedName\":{},\"selectionSource\":{}",
            desktop.index,
            json_string(&desktop.name),
            json_string(desktop.source)
        )
    });
    println!(
        "{{\"desktopCount\":{count},\"currentDesktop\":{current},\"desktops\":[{desktop_json}]{selection_json}}}"
    );
    Ok(())
}

fn move_window(args: &[String]) -> Result<(), String> {
    let pid: u32 = required_value(args, "--pid")?
        .parse()
        .map_err(|_| "--pid must be a positive integer".to_string())?;
    if pid == 0 {
        return Err("--pid must be positive".to_string());
    }
    let desktops = desktop_snapshots()?;
    let desktop = resolve_desktop(args, &desktops)?;
    let search = find_main_window(pid)?;
    let hwnd = HWND(search.best_hwnd as *mut c_void);
    winvd::move_window_to_desktop(desktop.index, &hwnd).map_err(format_winvd)?;
    let actual = winvd::get_desktop_by_window(hwnd)
        .and_then(|value| value.get_index())
        .map_err(format_winvd)?;
    if actual != desktop.index {
        return Err(format!(
            "window desktop verification failed: expected {}, got {actual}",
            desktop.index
        ));
    }
    println!(
        "{{\"pid\":{pid},\"hwnd\":{},\"desktop\":{actual},\"desktopName\":{},\"selectionSource\":{},\"title\":{}}}",
        search.best_hwnd,
        json_string(&desktop.name),
        json_string(desktop.source),
        json_string(&search.best_title)
    );
    Ok(())
}

fn desktop_snapshots() -> Result<Vec<DesktopSnapshot>, String> {
    let desktops = winvd::get_desktops().map_err(format_winvd)?;
    desktops
        .into_iter()
        .map(|desktop| {
            let index = desktop.get_index().map_err(format_winvd)?;
            let name = desktop.get_name().unwrap_or_default();
            Ok(DesktopSnapshot { index, name })
        })
        .collect()
}

fn has_desktop_target(args: &[String]) -> bool {
    ["--desktop", "--desktop-name", "--fallback-from-end"]
        .iter()
        .any(|name| args.iter().any(|arg| arg == name))
}

fn resolve_desktop(
    args: &[String],
    desktops: &[DesktopSnapshot],
) -> Result<ResolvedDesktop, String> {
    let configured_index = optional_value(args, "--desktop")
        .map(|value| {
            value
                .parse::<u32>()
                .map_err(|_| "--desktop must be a zero-based integer".to_string())
        })
        .transpose()?;
    let configured_name = optional_value(args, "--desktop-name");
    let fallback_from_end = optional_value(args, "--fallback-from-end")
        .map(|value| {
            value
                .parse::<u32>()
                .map_err(|_| "--fallback-from-end must be a positive integer".to_string())
        })
        .transpose()?;
    if configured_index.is_some() && (configured_name.is_some() || fallback_from_end.is_some()) {
        return Err("--desktop cannot be combined with name/fallback selection".to_string());
    }
    choose_desktop(
        desktops,
        configured_index,
        configured_name,
        fallback_from_end,
    )
}

fn choose_desktop(
    desktops: &[DesktopSnapshot],
    configured_index: Option<u32>,
    configured_name: Option<&str>,
    fallback_from_end: Option<u32>,
) -> Result<ResolvedDesktop, String> {
    if desktops.is_empty() {
        return Err("no virtual desktops are available".to_string());
    }
    if let Some(index) = configured_index {
        let desktop = desktops
            .iter()
            .find(|desktop| desktop.index == index)
            .ok_or_else(|| format!("desktop index {index} is unavailable"))?;
        return Ok(ResolvedDesktop {
            index: desktop.index,
            name: desktop.name.clone(),
            source: "index",
        });
    }
    if let Some(name) = configured_name.filter(|value| !value.trim().is_empty()) {
        let matches = desktops
            .iter()
            .filter(|desktop| desktop.name == name)
            .collect::<Vec<_>>();
        if matches.len() == 1 {
            return Ok(ResolvedDesktop {
                index: matches[0].index,
                name: matches[0].name.clone(),
                source: "name",
            });
        }
    }
    if let Some(from_end) = fallback_from_end {
        if from_end == 0 || from_end as usize > desktops.len() {
            return Err(format!(
                "fallback position {from_end} from end is unavailable; desktop count is {}",
                desktops.len()
            ));
        }
        let desktop = &desktops[desktops.len() - from_end as usize];
        return Ok(ResolvedDesktop {
            index: desktop.index,
            name: desktop.name.clone(),
            source: "fallback-from-end",
        });
    }
    if let Some(name) = configured_name {
        return Err(format!(
            "desktop name {name:?} did not resolve uniquely and no fallback was configured"
        ));
    }
    Err(
        "missing desktop target; use --desktop or --desktop-name with optional fallback"
            .to_string(),
    )
}

fn required_value<'a>(args: &'a [String], name: &str) -> Result<&'a str, String> {
    args.iter()
        .position(|arg| arg == name)
        .and_then(|index| args.get(index + 1))
        .map(String::as_str)
        .ok_or_else(|| format!("missing {name} <value>"))
}

fn optional_value<'a>(args: &'a [String], name: &str) -> Option<&'a str> {
    args.iter()
        .position(|arg| arg == name)
        .and_then(|index| args.get(index + 1))
        .map(String::as_str)
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

#[cfg(test)]
mod tests {
    use super::*;

    fn desktops() -> Vec<DesktopSnapshot> {
        ["메인", "인프라", "LLM 유니티 슬롯 1", "LLM 유니티 슬롯 2"]
            .into_iter()
            .enumerate()
            .map(|(index, name)| DesktopSnapshot {
                index: index as u32,
                name: name.to_string(),
            })
            .collect()
    }

    #[test]
    fn resolves_exact_name_before_fallback() {
        let resolved =
            choose_desktop(&desktops(), None, Some("LLM 유니티 슬롯 1"), Some(2)).unwrap();
        assert_eq!(resolved.index, 2);
        assert_eq!(resolved.source, "name");
    }

    #[test]
    fn falls_back_from_end_when_name_is_missing() {
        let resolved = choose_desktop(&desktops(), None, Some("renamed"), Some(1)).unwrap();
        assert_eq!(resolved.index, 3);
        assert_eq!(resolved.source, "fallback-from-end");
    }

    #[test]
    fn rejects_unavailable_fallback() {
        let error = choose_desktop(&desktops()[..1], None, Some("missing"), Some(2)).unwrap_err();
        assert!(error.contains("desktop count is 1"));
    }
}
