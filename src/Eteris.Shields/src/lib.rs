//! Escudo do Éteris: o motor de bloqueio do Brave (adblock-rust) exposto ao C# por uma interface C.

mod ffi;

use adblock::Engine;
use adblock::lists::{FilterSet, ParseOptions};
use adblock::request::Request;
use adblock::resources::Resource;

pub use ffi::*;

pub struct Shields {
    engine: Engine,
}

impl Shields {
    /// `trusted_rules`: listas do próprio uBlock (ids `ublock-*`), que podem usar scriptlets "trusted" —
    /// o mesmo modelo de confiança do uBlock Origin.
    /// `resources`: scriptlets do uBlock em JSON (ver build/scriptlets). Inválidos ou ausentes → sem scriptlets.
    pub fn new(rules: &str, trusted_rules: &str, resources: Option<&str>) -> Self {
        let mut set = FilterSet::new(false);
        set.add_filter_list(rules.to_owned(), ParseOptions::default());
        set.add_filter_list(trusted_rules.to_owned(), trusted_options());
        let mut engine = Engine::new_with_filter_set(set);
        if let Some(parsed) = resources.and_then(|json| serde_json::from_str::<Vec<Resource>>(json).ok()) {
            engine.use_resources(parsed);
        }
        Shields { engine }
    }

    /// O mesmo motor, lido de ficheiros (sem passar o texto das listas pelo C#: são ~8 M carateres que, como strings
    /// .NET, deixavam ~60 MB de lixo no heap grande até ao 1.º GC completo).
    /// `rule_paths`/`trusted_paths`: listas a ler (as que faltam ou não se leem saltam-se). `resources_path`: JSON dos
    /// scriptlets. `cache_path`: o motor já compilado (`Engine::serialize`). Se existe e é válido, nada se compila; senão
    /// compila-se e grava-se lá (o nome do ficheiro identifica as listas e a versão: quem chama invalida-o mudando-o).
    /// Devolve o motor e se veio da cache.
    pub fn from_files(
        rule_paths: &[&str],
        trusted_paths: &[&str],
        resources_path: Option<&str>,
        cache_path: Option<&str>,
    ) -> (Self, bool) {
        let resources = resources_path
            .and_then(|path| std::fs::read(path).ok())
            .and_then(|bytes| serde_json::from_slice::<Vec<Resource>>(&bytes).ok());
        if let Some(bytes) = cache_path.and_then(|path| std::fs::read(path).ok()) {
            let mut engine = Engine::default();
            if engine.deserialize(&bytes).is_ok() {
                if let Some(parsed) = resources {
                    engine.use_resources(parsed);
                }
                return (Shields { engine }, true);
            }
        }
        let mut set = FilterSet::new(false);
        for path in rule_paths {
            if let Some(text) = read_list(path) {
                set.add_filter_list(text, ParseOptions::default());
            }
        }
        for path in trusted_paths {
            if let Some(text) = read_list(path) {
                set.add_filter_list(text, trusted_options());
            }
        }
        let mut engine = Engine::new_with_filter_set(set);
        if let Some(path) = cache_path {
            write_cache(path, &engine.serialize());
        }
        if let Some(parsed) = resources {
            engine.use_resources(parsed);
        }
        (Shields { engine }, false)
    }

    /// JavaScript dos scriptlets (`##+js(...)`) para a página; vazio se não houver nenhum.
    pub fn scriptlets(&self, url: &str) -> String {
        self.engine.url_cosmetic_resources(url).injected_script
    }

    pub fn should_block(&self, url: &str, source_url: &str, resource_type: &str) -> bool {
        match Request::new(url, source_url, resource_type, "get") {
            Ok(request) => self.engine.check_network_request(&request).should_block(),
            Err(_) => false,
        }
    }

    /// Regras cosméticas específicas do site, uma regra CSS por seletor:
    /// um seletor inválido só invalida a sua própria regra.
    pub fn url_css(&self, url: &str) -> String {
        to_css(self.engine.url_cosmetic_resources(url).hide_selectors)
    }

    /// Regras cosméticas genéricas (##.classe / ###id) que se aplicam às classes e ids presentes na página.
    pub fn generic_css<'a>(
        &self,
        url: &str,
        classes: impl IntoIterator<Item = &'a str>,
        ids: impl IntoIterator<Item = &'a str>,
    ) -> String {
        let resources = self.engine.url_cosmetic_resources(url);
        if resources.generichide {
            return String::new();
        }
        to_css(self.engine.hidden_class_id_selectors(classes, ids, &resources.exceptions))
    }
}

/// Listas do próprio uBlock: podem usar scriptlets "trusted".
fn trusted_options() -> ParseOptions {
    ParseOptions { permissions: adblock::resources::PermissionMask::from_bits(1), ..ParseOptions::default() }
}

/// Texto de uma lista, como o C# o lia (File.ReadAllText): sem o BOM, e um byte inválido não deita fora a lista.
fn read_list(path: &str) -> Option<String> {
    let bytes = std::fs::read(path).ok()?;
    let text = String::from_utf8(bytes).unwrap_or_else(|e| String::from_utf8_lossy(e.as_bytes()).into_owned());
    Some(match text.strip_prefix('\u{feff}') {
        Some(rest) => rest.to_owned(),
        None => text,
    })
}

/// Grava o motor compilado: primeiro num ficheiro à parte e depois troca-o, para quem lê nunca ver meio ficheiro.
/// Uma falha (disco cheio, pasta só de leitura) só faz o próximo arranque compilar de novo.
fn write_cache(path: &str, bytes: &[u8]) {
    let temporary = format!("{path}.{}.tmp", std::process::id());
    if std::fs::write(&temporary, bytes).is_err() || std::fs::rename(&temporary, path).is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
}

fn to_css(selectors: impl IntoIterator<Item = String>) -> String {
    let mut css = String::new();
    for selector in selectors {
        css.push_str(&selector);
        css.push_str("{display:none!important}\n");
    }
    css
}

#[cfg(test)]
mod tests {
    use super::*;

    const RULES: &str = "\
||doubleclick.net^
||google-analytics.com^$third-party
@@||doubleclick.net/allowed/$script
publico.pt##.ad-slot
##.banner-publicidade
example.com#@#.banner-publicidade
";

    fn shields() -> Shields {
        Shields::new(RULES, "", None)
    }

    // Recurso no formato gerado por build/scriptlets/build-scriptlets.mjs (conteúdo em base64).
    // base64 de: function testMark(value) { window.__eterisTest = value; }
    const TEST_RESOURCES: &str = r#"[{"name":"test-mark.js","aliases":["tm.js"],"kind":{"mime":"application/javascript"},"content":"ZnVuY3Rpb24gdGVzdE1hcmsodmFsdWUpIHsgd2luZG93Ll9fZXRlcmlzVGVzdCA9IHZhbHVlOyB9","dependencies":[]}]"#;

    #[test]
    fn scriptlet_rule_produces_injectable_script() {
        let s = Shields::new("publico.pt##+js(tm, ola)\n", "", Some(TEST_RESOURCES));
        let script = s.scriptlets("https://www.publico.pt/");
        assert!(script.contains("testMark"), "{script}");
        assert!(script.contains("ola"), "{script}");
    }

    // base64 de: function trustedMark(value) { window.__eterisTrusted = value; }  — exige permissão 1
    const TRUSTED_RESOURCES: &str = r#"[{"name":"trusted-mark.js","aliases":[],"kind":{"mime":"application/javascript"},"content":"ZnVuY3Rpb24gdHJ1c3RlZE1hcmsodmFsdWUpIHsgd2luZG93Ll9fZXRlcmlzVHJ1c3RlZCA9IHZhbHVlOyB9","dependencies":[],"permission":1}]"#;

    #[test]
    fn trusted_scriptlet_runs_from_trusted_list() {
        let s = Shields::new("", "publico.pt##+js(trusted-mark, ok)\n", Some(TRUSTED_RESOURCES));
        assert!(s.scriptlets("https://www.publico.pt/").contains("trustedMark"));
    }

    #[test]
    fn trusted_scriptlet_is_refused_from_normal_list() {
        let s = Shields::new("publico.pt##+js(trusted-mark, ok)\n", "", Some(TRUSTED_RESOURCES));
        assert_eq!(s.scriptlets("https://www.publico.pt/"), "");
    }

    #[test]
    fn scriptlets_only_for_matching_site() {
        let s = Shields::new("publico.pt##+js(tm, ola)\n", "", Some(TEST_RESOURCES));
        assert_eq!(s.scriptlets("https://www.expresso.pt/"), "");
    }

    #[test]
    fn invalid_resources_json_still_builds_engine() {
        let s = Shields::new(RULES, "", Some("isto não é json"));
        assert!(s.should_block("https://ad.doubleclick.net/pixel.gif", "https://www.publico.pt/", "image"));
        assert_eq!(s.scriptlets("https://www.publico.pt/"), "");
    }

    #[test]
    fn blocks_listed_tracker() {
        assert!(shields().should_block("https://ad.doubleclick.net/pixel.gif", "https://www.publico.pt/", "image"));
    }

    #[test]
    fn allows_unlisted_request() {
        assert!(!shields().should_block("https://pt.wikipedia.org/wiki/Lisboa", "https://pt.wikipedia.org/", "document"));
    }

    #[test]
    fn exception_rule_wins() {
        assert!(!shields().should_block("https://ad.doubleclick.net/allowed/x.js", "https://www.publico.pt/", "script"));
    }

    #[test]
    fn third_party_option_is_respected() {
        let s = shields();
        assert!(s.should_block("https://www.google-analytics.com/collect", "https://www.publico.pt/", "xmlhttprequest"));
        assert!(!s.should_block("https://www.google-analytics.com/collect", "https://analytics.google-analytics.com/", "xmlhttprequest"));
    }

    #[test]
    fn invalid_url_is_not_blocked() {
        assert!(!shields().should_block("not a url", "https://www.publico.pt/", "script"));
    }

    fn temp_dir(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("eteris-shields-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    // O motor lido da cache responde como o compilado das listas: bloqueios, exceções, cosméticas e scriptlets (com a
    // permissão "trusted" só para as listas do uBlock).
    #[test]
    fn from_files_cache_round_trip_keeps_every_answer() {
        let dir = temp_dir("cache");
        let rules = dir.join("normal.txt");
        let trusted = dir.join("ublock.txt");
        let resources = dir.join("scriptlets.json");
        let cache = dir.join("motor.dat");
        std::fs::write(&rules, format!("\u{feff}{RULES}publico.pt##+js(tm, ola)\nexpresso.pt##+js(trusted-mark, nao)\n")).unwrap();
        std::fs::write(&trusted, "sapo.pt##+js(trusted-mark, sim)\n").unwrap();
        let both = format!("[{},{}]", &TEST_RESOURCES[1..TEST_RESOURCES.len() - 1], &TRUSTED_RESOURCES[1..TRUSTED_RESOURCES.len() - 1]);
        std::fs::write(&resources, both).unwrap();
        let (r, t, j, c) = (rules.to_str().unwrap(), trusted.to_str().unwrap(), resources.to_str().unwrap(), cache.to_str().unwrap());

        let (compiled, from_cache) = Shields::from_files(&[r, "nao-existe.txt"], &[t], Some(j), Some(c));
        assert!(!from_cache);
        assert!(cache.exists());
        let (cached, from_cache) = Shields::from_files(&[], &[], Some(j), Some(c));
        assert!(from_cache);
        for s in [&compiled, &cached] {
            assert!(s.should_block("https://ad.doubleclick.net/pixel.gif", "https://www.publico.pt/", "image"));
            assert!(!s.should_block("https://ad.doubleclick.net/allowed/x.js", "https://www.publico.pt/", "script"));
            assert!(s.should_block("https://www.google-analytics.com/collect", "https://www.publico.pt/", "xmlhttprequest"));
            assert_eq!(s.url_css("https://www.publico.pt/"), ".ad-slot{display:none!important}\n");
            assert_eq!(s.generic_css("https://example.com/", ["banner-publicidade"], []), "");
            assert!(s.scriptlets("https://www.publico.pt/").contains("testMark"));
            assert!(s.scriptlets("https://www.sapo.pt/").contains("trustedMark"));
            assert_eq!(s.scriptlets("https://www.expresso.pt/"), "");
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    // Uma cache estragada (cortada, ou de outra coisa) não conta: compila-se das listas e grava-se de novo.
    #[test]
    fn from_files_rebuilds_a_broken_cache() {
        let dir = temp_dir("broken");
        let rules = dir.join("normal.txt");
        let cache = dir.join("motor.dat");
        std::fs::write(&rules, RULES).unwrap();
        std::fs::write(&cache, b"isto nao e um motor").unwrap();
        let (r, c) = (rules.to_str().unwrap(), cache.to_str().unwrap());
        let (s, from_cache) = Shields::from_files(&[r], &[], None, Some(c));
        assert!(!from_cache);
        assert!(s.should_block("https://ad.doubleclick.net/pixel.gif", "https://www.publico.pt/", "image"));
        assert!(Shields::from_files(&[], &[], None, Some(c)).1);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn site_specific_css_hides_selector() {
        assert_eq!(shields().url_css("https://www.publico.pt/"), ".ad-slot{display:none!important}\n");
        assert_eq!(shields().url_css("https://www.expresso.pt/"), "");
    }

    #[test]
    fn generic_css_matches_classes_on_page() {
        let css = shields().generic_css("https://www.publico.pt/", ["banner-publicidade", "artigo"], []);
        assert_eq!(css, ".banner-publicidade{display:none!important}\n");
    }

    #[test]
    fn generic_css_honours_site_exception() {
        assert_eq!(shields().generic_css("https://example.com/", ["banner-publicidade"], []), "");
    }
}
