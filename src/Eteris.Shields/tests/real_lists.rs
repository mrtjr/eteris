//! Usa as mesmas listas que o Éteris distribui (as do uBlock Origin Lite).

use std::fs;
use std::path::Path;
use std::time::Instant;

use eteris_shields::Shields;

fn load() -> Shields {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../Eteris.Browser/Shields/lists");
    let mut rules = String::new();
    for entry in fs::read_dir(&dir).expect("pasta das listas") {
        rules.push_str(&fs::read_to_string(entry.unwrap().path()).unwrap());
        rules.push('\n');
    }
    let started = Instant::now();
    let shields = Shields::new(&rules);
    eprintln!("motor criado em {:?} a partir de {} KB", started.elapsed(), rules.len() / 1024);
    shields
}

#[test]
fn real_lists_block_what_the_c_sharp_list_blocked_on_publico() {
    let shields = load();
    let page = "https://www.publico.pt/";
    for (url, kind) in [
        ("https://www.googletagmanager.com/gtm.js?id=GTM-X", "script"),
        ("https://securepubads.g.doubleclick.net/tag/js/gpt.js", "script"),
        ("https://www.google-analytics.com/g/collect?v=2", "xmlhttprequest"),
        ("https://static.hotjar.com/c/hotjar-1.js", "script"),
        ("https://a.teads.tv/page/1/tag", "script"),
    ] {
        assert!(shields.should_block(url, page, kind), "devia bloquear {url}");
    }
}

#[test]
fn real_lists_do_not_break_ordinary_pages() {
    let shields = load();
    for (url, page, kind) in [
        ("https://pt.wikipedia.org/wiki/Lisboa", "https://pt.wikipedia.org/", "document"),
        ("https://github.githubassets.com/assets/app.js", "https://github.com/", "script"),
        ("https://www.publico.pt/static/main.css", "https://www.publico.pt/", "stylesheet"),
    ] {
        assert!(!shields.should_block(url, page, kind), "não devia bloquear {url}");
    }
}

#[test]
fn real_lists_have_cosmetic_rules_for_publico() {
    let css = load().url_css("https://www.publico.pt/");
    assert!(css.contains(".pubtxt{display:none!important}"), "{css}");
}

#[test]
fn real_lists_generic_rules_hide_common_ad_classes() {
    let css = load().generic_css("https://www.publico.pt/", ["ad-slot", "artigo"], ["topo"]);
    assert!(css.contains(".ad-slot{display:none!important}"), "CSS genérico: {css:?}");
}
