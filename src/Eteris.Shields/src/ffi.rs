//! Interface C consumida por P/Invoke. Todas as strings são UTF-8 terminadas em NUL.
//! Nenhum pânico atravessa a fronteira: em erro devolve-se "não bloquear" / NULL.

use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};

use crate::Shields;

unsafe fn text<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().ok()
}

fn into_c(value: String) -> *mut c_char {
    CString::new(value).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

/// Cria o motor a partir do texto de todas as listas concatenadas. Devolve NULL em erro.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn shields_new(rules: *const c_char) -> *mut Shields {
    let Some(rules) = (unsafe { text(rules) }) else { return std::ptr::null_mut() };
    catch_unwind(|| Box::into_raw(Box::new(Shields::new(rules)))).unwrap_or(std::ptr::null_mut())
}

/// 1 = bloquear, 0 = deixar passar (também em qualquer erro).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn shields_check(
    handle: *const Shields,
    url: *const c_char,
    source_url: *const c_char,
    resource_type: *const c_char,
) -> i32 {
    if handle.is_null() {
        return 0;
    }
    let shields = unsafe { &*handle };
    let (Some(url), Some(source), Some(kind)) = (unsafe { text(url) }, unsafe { text(source_url) }, unsafe { text(resource_type) })
    else {
        return 0;
    };
    catch_unwind(AssertUnwindSafe(|| shields.should_block(url, source, kind) as i32)).unwrap_or(0)
}

/// CSS das regras específicas do site. Libertar com `shields_string_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn shields_url_css(handle: *const Shields, url: *const c_char) -> *mut c_char {
    if handle.is_null() {
        return std::ptr::null_mut();
    }
    let shields = unsafe { &*handle };
    let Some(url) = (unsafe { text(url) }) else { return std::ptr::null_mut() };
    catch_unwind(AssertUnwindSafe(|| into_c(shields.url_css(url)))).unwrap_or(std::ptr::null_mut())
}

/// CSS das regras genéricas; `classes` e `ids` separados por espaços. Libertar com `shields_string_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn shields_generic_css(
    handle: *const Shields,
    url: *const c_char,
    classes: *const c_char,
    ids: *const c_char,
) -> *mut c_char {
    if handle.is_null() {
        return std::ptr::null_mut();
    }
    let shields = unsafe { &*handle };
    let (Some(url), Some(classes), Some(ids)) = (unsafe { text(url) }, unsafe { text(classes) }, unsafe { text(ids) }) else {
        return std::ptr::null_mut();
    };
    catch_unwind(AssertUnwindSafe(|| {
        into_c(shields.generic_css(url, classes.split_whitespace(), ids.split_whitespace()))
    }))
    .unwrap_or(std::ptr::null_mut())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn shields_string_free(value: *mut c_char) {
    if !value.is_null() {
        drop(unsafe { CString::from_raw(value) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn shields_free(handle: *mut Shields) {
    if !handle.is_null() {
        drop(unsafe { Box::from_raw(handle) });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ffi_round_trip() {
        let rules = CString::new("||doubleclick.net^\npublico.pt##.ad-slot\n").unwrap();
        let url = CString::new("https://ad.doubleclick.net/x.js").unwrap();
        let page = CString::new("https://www.publico.pt/").unwrap();
        let script = CString::new("script").unwrap();
        unsafe {
            let handle = shields_new(rules.as_ptr());
            assert!(!handle.is_null());
            assert_eq!(shields_check(handle, url.as_ptr(), page.as_ptr(), script.as_ptr()), 1);
            assert_eq!(shields_check(handle, page.as_ptr(), page.as_ptr(), script.as_ptr()), 0);

            let css = shields_url_css(handle, page.as_ptr());
            assert_eq!(CStr::from_ptr(css).to_str().unwrap(), ".ad-slot{display:none!important}\n");
            shields_string_free(css);
            shields_free(handle);
        }
    }

    #[test]
    fn ffi_null_inputs_are_safe() {
        unsafe {
            assert!(shields_new(std::ptr::null()).is_null());
            assert_eq!(shields_check(std::ptr::null(), std::ptr::null(), std::ptr::null(), std::ptr::null()), 0);
            assert!(shields_url_css(std::ptr::null(), std::ptr::null()).is_null());
            shields_string_free(std::ptr::null_mut());
            shields_free(std::ptr::null_mut());
        }
    }

    #[test]
    fn ffi_invalid_utf8_is_not_blocked() {
        let rules = CString::new("||doubleclick.net^").unwrap();
        let bad = [0xC3u8, 0x28, 0x00];
        let kind = CString::new("script").unwrap();
        unsafe {
            let handle = shields_new(rules.as_ptr());
            assert_eq!(shields_check(handle, bad.as_ptr().cast(), bad.as_ptr().cast(), kind.as_ptr()), 0);
            shields_free(handle);
        }
    }
}
