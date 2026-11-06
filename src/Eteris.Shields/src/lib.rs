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
        let trusted = ParseOptions { permissions: adblock::resources::PermissionMask::from_bits(1), ..ParseOptions::default() };
        set.add_filter_list(trusted_rules.to_owned(), trusted);
        let mut engine = Engine::new_with_filter_set(set);
        if let Some(parsed) = resources.and_then(|json| serde_json::from_str::<Vec<Resource>>(json).ok()) {
            engine.use_resources(parsed);
        }
        Shields { engine }
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
