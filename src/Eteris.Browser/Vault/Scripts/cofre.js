// O script do cofre do Éteris (docs/palavras-passe.md, bloco C). Corre num mundo isolado de cada documento http(s)
// (Vault/VaultChannel.cs): partilha o DOM com a página, mas a página não vê as variáveis, os ouvintes nem a ligação dele.
// Fala com o navegador só pela ligação do mundo isolado (mensagens JSON validadas do outro lado) e nunca escreve no DOM
// fora do preenchimento pedido pelo navegador (guarda: VaultChannelGuardTests).
(function (LIGACAO, OFERECER) {
  'use strict';

  // ---- Classificador: funções puras sobre descritores (testadas no Node, CofreClassifierTests) ----
  const Classifier = (() => {
    // O texto dos atributos como se compara: «Nº» → número, camelCase separado, sem acentos, minúsculas, só letras e
    // algarismos separados por um espaço.
    function norm(s) {
      return String(s == null ? '' : s)
        .replace(/(^|[^A-Za-z])[Nn]\s*\.?\s*[º°](?![A-Za-z])/g, '$1 numero ')
        .replace(/([a-z])([A-Z])/g, '$1 $2')
        .normalize('NFD').replace(/[̀-ͯ]/g, '')
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, ' ')
        .trim();
    }

    // autocomplete= (HTML): o último símbolo que não é secção nem modificador.
    const AUTOCOMPLETE = {
      'username': 'username', 'email': 'email', 'current-password': 'password', 'new-password': 'new-password',
      'one-time-code': 'one-time-code', 'name': 'name', 'given-name': 'given-name', 'family-name': 'family-name',
      'organization': 'organization', 'street-address': 'street-address', 'address-line1': 'street-address',
      'address-line2': 'address-line2', 'address-line3': 'address-line3', 'address-level1': 'region',
      'address-level2': 'city', 'address-level3': 'neighborhood', 'postal-code': 'postal-code', 'country': 'country',
      'country-name': 'country', 'tel': 'tel', 'tel-national': 'tel', 'cc-name': 'cc-name', 'cc-given-name': 'cc-name',
      'cc-number': 'cc-number', 'cc-exp': 'cc-exp', 'cc-exp-month': 'cc-exp-month', 'cc-exp-year': 'cc-exp-year',
      'cc-csc': 'cc-csc', 'cc-type': 'cc-type',
    };
    const MODIFIERS = /^(section-.*|shipping|billing|home|work|mobile|fax|pager|webauthn)$/;
    const PASSWORDISH = new Set(['password', 'new-password', 'one-time-code']);

    // Heurísticas por name, id, rótulo, placeholder e aria-label, em português (PT e BR), inglês e espanhol. A ordem
    // conta: a primeira que encaixa ganha.
    const IGNORE = /\b(estado civil|data de nascimento|nascimento|birth ?day|birth ?date|date of birth|fecha de nacimiento|captcha|search|pesquisa|pesquisar|procurar|buscar|busca|cupao|cupom|coupon|promo|desconto|discount)\b/;
    const RULES = [
      ['cc-csc', /\b(cvv2?|cvc2?|csc|cvn|cid|security code|codigo de seguranca|cod seguranca|codigo seguranca|card verification|verification value|codigo cvv)\b/],
      ['one-time-code', /\b(otp|one time|2fa|mfa|totp|verification code|codigo de verificacao|codigo de confirmacao|codigo sms|sms code|authentication code|authenticator|autenticacao)\b/],
      ['cc-number', /\b(card ?number|card no|cc ?num(ber)?|ccnumber|credit ?card|numero (do |de )?cartao|numero (de |de la )?tarjeta|cartao de credito)\b/],
      ['cc-name', /\b(name on (the )?card|card ?holder|holder( name)?|titular|nome (no|do) cartao|nome impresso|nombre (en|del) (la )?tarjeta|cc name|ccname)\b/],
      ['cc-exp-month', /\b(exp(iry|iration)?( date)? month|cc exp month|mes (de )?(validade|vencimento|expiracao)|mes vencimiento)\b/],
      ['cc-exp-year', /\b(exp(iry|iration)?( date)? year|cc exp year|ano (de )?(validade|vencimento|expiracao)|ano vencimiento)\b/],
      ['cc-exp', /\b(exp(iry|iration)?( date)?|cc exp|validade|vencimento|vencimiento|valid (thru|until)|mm ?(aa|yy))\b/],
      ['iban', /\b(iban)\b/],
      ['tax-id', /\b(cpf|cnpj|cpf ?cnpj|nif|nipc|contribuinte|tax ?(id|number)|vat( number)?)\b/],
      ['id-number', /\b(passaporte|passport|cartao de cidadao|cartao cidadao|bilhete de identidade|rg|registro geral|identidade|documento de identidade|dni|cnh|carta de conducao|drivers? licen[cs]e|id number|numero (do |de )?documento)\b/],
      ['plate', /\b(matricula|placa|license plate|licence plate|registration plate|plate)\b/],
      ['postal-code', /\b(cep|codigo postal|cod postal|cp|postal ?code|post ?code|zip( ?code)?|codigo zip)\b/],
      ['tel', /\b(telefone|telemovel|celular|phone|mobile|tel|telephone|movil|whatsapp|fone)\b/],
      ['email', /\b(e ?mail|correo|correio eletronico)\b/],
      ['neighborhood', /\b(bairro|barrio|neighbou?rhood)\b/],
      ['address-complement', /\b(complemento|complement|apartamento|apto|apt|andar|piso|fracao|suite|address ?(line )?2|line ?2)\b/],
      ['username', /\b(user ?name|user ?id|userid|login|log in|loginfmt|sign ?in|utilizador|usuario|account|conta|identifier|identificador)\b/],
      ['given-name', /\b(first ?name|fname|given ?name|primeiro nome|nome proprio|forename)\b/],
      ['family-name', /\b(last ?name|lname|surname|family ?name|apelidos?|sobrenome|apellidos?|ultimo nome)\b/],
      ['organization', /\b(empresa|company|organi[sz]ation|organizacao|razao social|business name)\b/],
      ['city', /\b(cidade|city|localidade|ciudad|municipio|concelho|town|locality)\b/],
      ['region', /\b(estado|uf|distrito|state|province|provincia|region|regiao|county)\b/],
      ['country', /\b(pais|country)\b/],
      ['address-number', /\b(street number|house number|numero (da |de )?(porta|casa)|porta)\b/],
      ['street-address', /\b(morada|endereco|rua|logradouro|address( ?line)?( ?1)?|street|direccion|calle|line ?1)\b/],
      ['address-number', /\b(numero|num|number)\b/],
      ['name', /\b(full ?name|nome completo|nombre completo|your name|seu nome|name|nome|nombre)\b/],
    ];
    const NEW_PASSWORD = /\b(new|nova|novo|nueva|nuevo|confirm\w*|repet\w*|repeat|repita|again|retype|novamente|create|criar|crie|choose|escolh\w*|verify)\b/;
    const CONFIRM = /\b(confirm\w*|repet\w*|repeat|repita|again|retype|novamente|verify)\b/;
    const SKIPPED_TYPES = new Set(['hidden', 'checkbox', 'radio', 'submit', 'button', 'image', 'file', 'reset', 'range', 'color', 'search']);
    const TEXTUAL = new Set(['text', 'email', 'tel', 'number', 'url', '']);
    const NUMERIC_TEL = new Set(['tax-id', 'postal-code', 'cc-number', 'cc-csc', 'cc-exp', 'one-time-code', 'id-number', 'iban', 'address-number']);
    const SELECT_KINDS = new Set(['cc-exp-month', 'cc-exp-year', 'country', 'region', 'cc-type']);
    const ADDRESS_KINDS = new Set(['street-address', 'address-line2', 'address-line3', 'address-number', 'address-complement', 'neighborhood', 'city', 'region', 'postal-code', 'country']);
    const KINDS = Object.freeze(Object.values(AUTOCOMPLETE).concat(RULES.map(r => r[0])).filter((k, i, a) => a.indexOf(k) === i));

    function textOf(d) {
      return [d.name, d.id, d.label, d.placeholder, d.ariaLabel].map(norm).filter(Boolean).join(' ');
    }

    function fromAutocomplete(value) {
      const tokens = String(value || '').toLowerCase().split(/\s+/).filter(t => t && !MODIFIERS.test(t));
      const last = tokens[tokens.length - 1];
      return last && Object.prototype.hasOwnProperty.call(AUTOCOMPLETE, last) ? AUTOCOMPLETE[last] : null;
    }

    function fromText(text) {
      if (!text || IGNORE.test(text)) return null;
      for (const [kind, re] of RULES) if (re.test(text)) return kind;
      return null;
    }

    // O tipo de um campo sozinho (null: o cofre não lhe toca).
    function classifyField(d) {
      const tag = String(d.tag || 'input').toLowerCase();
      const type = String(d.type || (tag === 'input' ? 'text' : tag)).toLowerCase();
      if (tag === 'input' && SKIPPED_TYPES.has(type)) return null;
      const text = textOf(d);
      const auto = fromAutocomplete(d.autocomplete);
      if (type === 'password') {
        if (auto && PASSWORDISH.has(auto)) return auto;
        const guess = fromText(text);
        if (guess === 'cc-csc' || guess === 'one-time-code') return guess;
        return NEW_PASSWORD.test(text) ? 'new-password' : 'password';
      }
      if (auto && !(auto === 'username' && type === 'password')) {
        if (tag === 'select') return SELECT_KINDS.has(auto) ? auto : null;
        return auto;
      }
      const guess = fromText(text);
      if (tag === 'select') return guess && SELECT_KINDS.has(guess) ? guess : null;
      if (tag === 'textarea') return guess === 'street-address' ? guess : null;
      if (type === 'email') return guess === 'username' ? 'username' : 'email';
      if (type === 'tel') return guess && (NUMERIC_TEL.has(guess) || guess === 'tel' || guess === 'username') ? guess : (text ? null : 'tel');
      if (!TEXTUAL.has(type)) return null;
      return guess;
    }

    // O formulário inteiro: o tipo (login, signup, change, address, payment, other) e o tipo final de cada campo. Só os
    // campos visíveis contam. Num formulário com palavra-passe, o utilizador é o campo de texto visível mais próximo
    // antes da 1.ª palavra-passe (de preferência um marcado como utilizador ou e-mail), e só há um. Os campos de cartão
    // só contam com um número de cartão no mesmo formulário (o CVC fica marcado, para nunca se preencher nem guardar).
    function classifyForm(fields) {
      const kinds = fields.map(f => (f.visible === false ? null : (f.kind || null)));
      if (!kinds.includes('cc-number'))
        for (let i = 0; i < kinds.length; i++)
          if (kinds[i] && kinds[i].startsWith('cc-') && kinds[i] !== 'cc-csc') kinds[i] = null;
      const pw = [];
      kinds.forEach((k, i) => { if (k === 'password' || k === 'new-password') pw.push(i); });
      let formKind = 'other';
      let user = -1;
      if (pw.length === 0) {
        if (kinds.includes('cc-number')) formKind = 'payment';
        else if (kinds.filter(k => ADDRESS_KINDS.has(k)).length >= 2 && kinds.some(k => k === 'postal-code' || k === 'street-address'))
          formKind = 'address';
      } else {
        const first = pw[0];
        for (let i = first - 1; i >= 0; i--) {
          const f = fields[i];
          if (f.visible === false || !TEXTUAL.has(String(f.type || 'text').toLowerCase())) continue;
          if (kinds[i] === 'username' || kinds[i] === 'email') { user = i; break; }
          if (user < 0 && kinds[i] !== 'one-time-code') user = i;
        }
        const explicitCurrent = i => /\bcurrent-password\b/.test(String(fields[i].autocomplete || '').toLowerCase());
        const confirmLike = i => CONFIRM.test(textOf(fields[i]));
        if (pw.length === 1) {
          formKind = kinds[first] === 'new-password' ? 'signup' : 'login';
        } else if (pw.length === 2) {
          const [a, b] = pw;
          const currentThenNew = kinds[a] === 'password' && kinds[b] === 'new-password' && (explicitCurrent(a) || !confirmLike(b));
          if (currentThenNew) formKind = 'change';
          else {
            kinds[a] = kinds[b] = 'new-password';
            formKind = user >= 0 ? 'signup' : 'change';
          }
        } else {
          formKind = 'change';
          kinds[first] = kinds[first] === 'new-password' && !explicitCurrent(first) ? 'new-password' : 'password';
          for (const i of pw.slice(1)) kinds[i] = 'new-password';
        }
        if (formKind === 'signup') for (const i of pw) kinds[i] = 'new-password';
        for (let i = 0; i < kinds.length; i++) if (kinds[i] === 'username' && i !== user) kinds[i] = null;
        if (user >= 0) kinds[user] = 'username';
      }
      return { formKind, kinds, user, passwords: pw };
    }

    // Só se oferece num campo que se vê: com tamanho, mostrado, opaco o bastante (com os antepassados), dentro do ecrã e
    // não tapado (o centro devolve o próprio campo ou um descendente).
    function isVisible(m) {
      return m.width >= 5 && m.height >= 5 && m.display !== 'none' && m.visibility !== 'hidden' && m.visibility !== 'collapse'
        && m.opacity >= 0.1 && m.inViewport === true && m.hit === true;
    }

    function debounce(fn, ms, timers) {
      let pending = null;
      return function () {
        if (pending !== null) timers.clear(pending);
        pending = timers.set(() => { pending = null; fn(); }, ms);
      };
    }

    const FIELD_TAGS = new Set(['INPUT', 'SELECT', 'TEXTAREA']);
    // Uma mudança no DOM interessa ao cofre só se entrar ou sair um campo (ou um nó que tenha campos lá dentro), ou se
    // mudar um atributo de um campo.
    function touchesFields(records) {
      for (const r of records) {
        if (r.type === 'attributes') { if (r.target && FIELD_TAGS.has(r.target.nodeName)) return true; continue; }
        for (const list of [r.addedNodes || [], r.removedNodes || []])
          for (const n of list) {
            if (!n || n.nodeType !== 1) continue;
            if (FIELD_TAGS.has(n.nodeName)) return true;
            try { if (n.querySelector && n.querySelector('input,select,textarea')) return true; } catch (e) { /* nó estranho */ }
          }
      }
      return false;
    }

    const USERLIKE = new Set(['username', 'email']);

    // O que vai para o navegador no envio de um formulário (C4): só inícios de sessão, criações de conta e mudanças, com
    // a palavra-passe escrita (na criação e na mudança, a nova, igual na confirmação). info: {formKind, kinds, user};
    // values: o valor de cada campo. null: nada a oferecer guardar (um código de uma utilização nunca é palavra-passe).
    function submission(info, values) {
      if (!info || (info.formKind !== 'login' && info.formKind !== 'signup' && info.formKind !== 'change')) return null;
      const val = i => (typeof values[i] === 'string' ? values[i] : '');
      const of = kind => info.kinds.map((k, i) => (k === kind ? val(i) : '')).filter(v => v !== '');
      const current = of('password'), fresh = of('new-password');
      const username = info.user >= 0 ? val(info.user) : '';
      if (info.formKind === 'login') return current.length ? { formKind: 'login', username, password: current[0] } : null;
      if (!fresh.length || fresh.some(v => v !== fresh[0])) return null;
      const out = { formKind: info.formKind, username };
      if (info.formKind === 'change' && current.length) out.password = current[0];
      out.newPassword = fresh[0];
      return out;
    }

    // As restrições que a página pôs num campo de palavra-passe nova, para o gerador (C5): só as que existem.
    function constraints(f) {
      const out = {};
      if (Number.isInteger(f.minLength) && f.minLength > 0 && f.minLength <= 4096) out.minLength = f.minLength;
      if (Number.isInteger(f.maxLength) && f.maxLength > 0 && f.maxLength <= 4096) out.maxLength = f.maxLength;
      if (typeof f.pattern === 'string' && f.pattern !== '' && f.pattern.length <= 512) out.pattern = f.pattern;
      return out;
    }

    // O que se preenche no formulário escolhido: cada campo visível com tipo recebe o valor desse tipo, se o navegador o
    // mandou (uma palavra-passe guardada manda o utilizador e a atual; o gerador, a nova). Nunca o CVC nem um código de
    // uma utilização; nunca um campo sem tipo (escondido, tapado, fora do ecrã). Pares [índice, valor].
    // Contactos e cartões (C6): o valor de cada campo no formato dele — nome completo, rua com o número quando o formulário
    // não tem campo para ele, validade MM/AA ou MM/AAAA, mês e ano em listas, país e estado pelo nome ou pelo código, e só
    // os algarismos num campo curto demais para o formato (CPF, CEP). metas[i]: {maxLength, placeholder, options, filled};
    // opts.keep: os campos que o utilizador já escreveu ficam como estão (menos o campo onde escolheu, opts.anchor).
    const NEVER_FILL = new Set(['cc-csc', 'one-time-code']);
    function fillPlan(kinds, visible, values, metas, opts) {
      const plan = [];
      const seen = kinds.filter((k, i) => visible[i] && k);
      kinds.forEach((kind, i) => {
        if (!visible[i] || !kind || NEVER_FILL.has(kind)) return;
        const meta = (metas && metas[i]) || {};
        if (opts && opts.keep && meta.filled && i !== opts.anchor) return;
        const v = valueFor(kind, values || {}, seen, meta);
        if (typeof v === 'string' && v !== '') plan.push([i, v]);
      });
      return plan;
    }

    function valueFor(kind, values, kinds, meta) {
      const has = k => Object.prototype.hasOwnProperty.call(values, k) && typeof values[k] === 'string' && values[k] !== '';
      let v = has(kind) ? values[kind] : null;
      if (kind === 'name' && !v && (has('given-name') || has('family-name')))
        v = [values['given-name'], values['family-name']].filter(Boolean).join(' ');
      if (kind === 'street-address' && v && has('address-number') && !kinds.includes('address-number')) v = v + ', ' + values['address-number'];
      if (kind === 'cc-exp' && has('cc-exp-month') && has('cc-exp-year')) v = expiry(values['cc-exp-month'], values['cc-exp-year'], meta);
      if (kind === 'cc-exp-year' && v && meta.maxLength === 2) v = v.slice(-2);
      if (!v) return null;
      if (meta.options) return pickOption(meta.options, [v, values[kind + ':alt']]);
      return fit(v, meta);
    }

    // MM/AA por omissão; MM/AAAA num campo que o pede (placeholder ou comprimento); MMAA num campo de 4.
    function expiry(month, year, meta) {
      const m = String(month).padStart(2, '0'), y = String(year);
      if (meta.maxLength === 4) return m + y.slice(-2);
      const long = meta.maxLength >= 7 || /(aaaa|yyyy)/i.test(String(meta.placeholder || ''));
      return m + '/' + (long ? y : y.slice(-2));
    }

    function fit(v, meta) {
      if (meta.maxLength > 0 && v.length > meta.maxLength) {
        const plain = v.replace(/[^0-9A-Za-z]/g, '');
        if (plain.length <= meta.maxLength) return plain;
      }
      return v;
    }

    // A opção de uma lista: pelo valor ou pelo texto (sem acentos nem maiúsculas), ou o mesmo número (08 = 8, 2029 = 29).
    function pickOption(options, wanted) {
      for (const w of wanted) {
        if (typeof w !== 'string' || w === '') continue;
        const nw = norm(w);
        const exact = options.find(o => norm(o.value) === nw || norm(o.text) === nw);
        if (exact) return exact.value;
        if (/^\d+$/.test(w)) {
          const n = Number(w);
          const num = options.find(o => (/^\d+$/.test(String(o.value)) && Number(o.value) === n) || (/^\d{4}$/.test(w) && String(o.value) === w.slice(-2)));
          if (num) return num.value;
        }
      }
      return null;
    }

    // Depois de preencher um cartão, o foco vai para o código de segurança (vazio); de resto, volta ao campo escolhido.
    function focusAfter(kinds, visible, values, anchor) {
      if (values && typeof values['cc-number'] === 'string') {
        const csc = kinds.findIndex((k, i) => k === 'cc-csc' && visible[i]);
        if (csc >= 0) return csc;
      }
      return anchor;
    }

    // <preencher>
    // O único sítio onde o cofre escreve na página, e só a pedido do navegador depois de um gesto do utilizador: o foco,
    // o valor pelo setter nativo do protótipo (não pelo da instância, que um framework da página pode ter posto para saber
    // o último valor que viu — passar por cima dele é o que o faz reparar na mudança) e os eventos input e change.
    function fillField(field, value) {
      if (!field || typeof value !== 'string') return false;
      let setter = null;
      for (let p = Object.getPrototypeOf(field); p && !setter; p = Object.getPrototypeOf(p)) {
        const d = Object.getOwnPropertyDescriptor(p, 'value');
        if (d && typeof d.set === 'function') setter = d.set;
      }
      if (!setter) return false;
      try { field.focus({ preventScroll: true }); } catch (e) { /* campo que não aceita foco */ }
      setter.call(field, value);
      field.dispatchEvent(new Event('input', { bubbles: true }));
      field.dispatchEvent(new Event('change', { bubbles: true }));
      return true;
    }

    // Depois de preencher, o foco volta ao campo onde o utilizador estava.
    function focusBack(field) {
      try { field.focus({ preventScroll: true }); } catch (e) { /* o campo saiu */ }
    }
    // </preencher>

    return Object.freeze({
      version: 1, KINDS, norm, textOf, classifyField, classifyForm, isVisible, debounce, touchesFields, USERLIKE, TEXTUAL,
      fillPlan, fillField, focusBack, submission, constraints, fit, focusAfter,
    });
  })();

  // No Node (testes) não há ligação nem DOM: só o classificador.
  if (LIGACAO === null) return Classifier;
  if (Object.prototype.hasOwnProperty.call(globalThis, '__cofre')) return undefined;

  // A ligação sai logo do objeto global: nem dentro do mundo isolado fica à vista.
  const binding = globalThis[LIGACAO];
  delete globalThis[LIGACAO];
  if (typeof binding !== 'function') return undefined;
  const send = (message) => {
    try { binding(JSON.stringify(message)); } catch (e) { /* o documento está a sair */ }
  };

  // ---- Leitura do DOM (só leitura) ----

  const MAX_FIELDS = 200;
  const FIELD_SELECTOR = 'input,select,textarea';
  const ids = new WeakMap();
  const byId = new Map();
  let nextId = 1;
  function idOf(node, prefix) {
    let id = ids.get(node);
    if (!id) {
      id = prefix + (nextId++).toString(36);
      ids.set(node, id);
      byId.set(id, new WeakRef(node));
    }
    return id;
  }
  function nodeOf(id) {
    const ref = byId.get(id);
    const node = ref && ref.deref();
    if (!node) byId.delete(id);
    return node || null;
  }

  function isField(node) {
    return !!node && node.nodeType === 1 && (node.nodeName === 'INPUT' || node.nodeName === 'SELECT' || node.nodeName === 'TEXTAREA');
  }

  // O formulário de um campo: o <form> dele, ou (campos soltos) a raiz em que está — o documento ou a raiz sombra.
  function ownerOf(field) {
    return field.form || field.getRootNode();
  }

  function fieldsOf(owner) {
    let list;
    try {
      list = owner.nodeName === 'FORM' ? Array.from(owner.elements).filter(isField)
        : Array.from(owner.querySelectorAll(FIELD_SELECTOR)).filter(f => !f.form);
    } catch (e) { list = []; }
    return list.slice(0, MAX_FIELDS);
  }

  function labelOf(field) {
    let text = '';
    try {
      if (field.labels) for (const l of field.labels) text += ' ' + l.textContent;
      const by = field.getAttribute('aria-labelledby');
      if (by) {
        const root = field.getRootNode();
        for (const id of by.split(/\s+/)) {
          const n = root.getElementById ? root.getElementById(id) : null;
          if (n) text += ' ' + n.textContent;
        }
      }
      if (!text.trim()) {
        const prev = field.previousElementSibling;
        if (prev && !isField(prev) && prev.textContent && prev.textContent.length <= 80) text = prev.textContent;
      }
    } catch (e) { /* atributo estranho */ }
    return text.trim().slice(0, 200);
  }

  function effectiveOpacity(node) {
    let opacity = 1;
    for (let n = node, depth = 0; n && n.nodeType === 1 && depth < 40 && opacity >= 0.1; depth++) {
      const value = parseFloat(getComputedStyle(n).opacity);
      if (!Number.isNaN(value)) opacity *= value;
      n = n.parentElement || (n.getRootNode && n.getRootNode().host) || null;
    }
    return opacity;
  }

  // O que o classificador precisa de saber sobre a visibilidade de um campo, e o retângulo dele.
  function measure(field) {
    const r = field.getBoundingClientRect();
    const style = getComputedStyle(field);
    const vw = innerWidth, vh = innerHeight;
    const inViewport = r.right > 0 && r.bottom > 0 && r.left < vw && r.top < vh;
    let hit = false;
    if (inViewport && r.width > 0 && r.height > 0) {
      const x = Math.min(Math.max(r.left + r.width / 2, 0), vw - 1);
      const y = Math.min(Math.max(r.top + r.height / 2, 0), vh - 1);
      const root = field.getRootNode();
      const top = (root.elementFromPoint ? root.elementFromPoint(x, y) : null) || document.elementFromPoint(x, y);
      const label = top && top.closest ? top.closest('label') : null;
      hit = !!top && (top === field || field.contains(top) || (label !== null && label.control === field));
    }
    return {
      width: r.width, height: r.height, display: style.display, visibility: style.visibility,
      opacity: effectiveOpacity(field), inViewport, hit, rect: r,
    };
  }

  function describe(field, visible) {
    return {
      tag: field.nodeName.toLowerCase(), type: String(field.type || '').toLowerCase(), name: field.getAttribute('name') || '',
      id: field.id || '', autocomplete: field.getAttribute('autocomplete') || '', label: labelOf(field),
      placeholder: field.getAttribute('placeholder') || '', ariaLabel: field.getAttribute('aria-label') || '', visible,
    };
  }

  // A posição do campo na área visível da página de topo (os iframes da mesma origem somam a posição deles).
  function topRect(field) {
    const r = field.getBoundingClientRect();
    let x = r.left, y = r.top;
    try {
      for (let w = window; w !== w.top && w.frameElement; w = w.parent) {
        const f = w.frameElement.getBoundingClientRect();
        x += f.left + w.frameElement.clientLeft;
        y += f.top + w.frameElement.clientTop;
      }
    } catch (e) { /* iframe de outra origem: não acontece (o script só entra nos da mesma) */ }
    const round = v => Math.round(v * 100) / 100;
    return [round(x), round(y), round(Math.max(0, r.width)), round(Math.max(0, r.height))];
  }

  // A classificação de cada formulário, refeita quando o DOM muda campos (MutationObserver com debounce de 250 ms, só
  // depois do 1.º campo focado: uma página onde ninguém escreve não paga nada).
  let cache = new WeakMap();
  let observing = false;
  const invalidate = Classifier.debounce(() => { cache = new WeakMap(); }, 250, { set: (f, ms) => setTimeout(f, ms), clear: t => clearTimeout(t) });
  function observe() {
    if (observing) return;
    observing = true;
    try {
      new MutationObserver(records => { if (Classifier.touchesFields(records)) invalidate(); })
        .observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['type', 'autocomplete', 'name'] });
    } catch (e) { observing = false; }
  }

  // fresh: a classificação refaz-se (a cada foco: a visibilidade dos campos muda sem o DOM mudar, por exemplo ao rolar
  // ou ao abrir uma janela de início de sessão por CSS); sem fresh serve a última (ao escrever).
  function formInfo(field, fresh) {
    return ownerInfo(ownerOf(field), fresh, field);
  }

  function ownerInfo(owner, fresh, field) {
    let info = cache.get(owner);
    if (!fresh && info && (!field || info.fields.includes(field))) return info;
    const fields = fieldsOf(owner);
    const descriptors = fields.map(f => {
      const d = describe(f, undefined);
      d.kind = Classifier.classifyField(d);
      return d;
    });
    // A visibilidade só se mede nos campos que contam (medir é caro): os que têm tipo e os de texto, que podem ser o
    // utilizador de um início de sessão.
    descriptors.forEach((d, i) => {
      if (d.kind || (d.tag === 'input' && Classifier.TEXTUAL.has(d.type))) d.visible = Classifier.isVisible(measure(fields[i]));
    });
    const result = Classifier.classifyForm(descriptors);
    info = { owner, id: idOf(owner, 'm'), fields, kinds: result.kinds, formKind: result.formKind, user: result.user };
    cache.set(owner, info);
    return info;
  }

  // ---- Mensagens para o navegador ----

  let filling = false;
  // Os focos que o próprio preenchimento causa não voltam a pedir a lista.
  let quietUntil = 0;
  let focused = null;
  let leftWithWindow = null;

  function deepTarget(event) {
    const path = event.composedPath ? event.composedPath() : [];
    return path.length ? path[0] : event.target;
  }

  function deepActive() {
    let a = document.activeElement;
    while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement;
    return a;
  }

  function report(field) {
    if (!isField(field) || performance.now() < quietUntil) return;
    const info = formInfo(field, true);
    // Um campo escondido, tapado ou fora do ecrã não tem tipo (nada se oferece nele).
    const kind = info.kinds[info.fields.indexOf(field)];
    if (!kind) { focused = null; return; }
    focused = field;
    observe();
    const root = field.getRootNode();
    if (root !== document && root.host) listen(root);
    const message = { t: 'focus', field: idOf(field, 'f'), form: info.id, kind, formKind: info.formKind, rect: topRect(field), visible: true };
    if (Classifier.USERLIKE.has(kind)) message.prefix = String(field.value || '').slice(0, 256);
    send(message);
    // Uma palavra-passe nova: as restrições do campo, para o gerador (C5). Nunca na janela privada (D-3).
    if (OFERECER && kind === 'new-password') send(generateMessage(field, info));
  }

  function generateMessage(field, info) {
    const limits = Classifier.constraints({ minLength: field.minLength, maxLength: field.maxLength, pattern: field.getAttribute('pattern') });
    return Object.assign({ t: 'generate', field: idOf(field, 'f'), form: info.id }, limits);
  }

  // Os ouvintes do cofre (no mundo isolado: a página não os vê). Ficam na janela e em cada raiz sombra com campos: o foco
  // que passa de um campo para outro da mesma raiz sombra não chega à janela (o alvo e o relatedTarget coincidem lá fora).
  const seen = new WeakSet();
  const once = (handler) => (e) => {
    if (seen.has(e)) return;
    seen.add(e);
    handler(e);
  };

  const onFocusIn = once((e) => {
    if (filling) return;
    const field = deepTarget(e);
    // A janela voltou a ter o foco com o mesmo campo: não é um gesto novo.
    if (field === leftWithWindow) { leftWithWindow = null; return; }
    leftWithWindow = null;
    // Depois de o motor pôr o campo à vista (o foco pelo teclado rola a página): mede-se no fotograma seguinte.
    requestAnimationFrame(() => { if (!filling && deepActive() === field) report(field); });
  });

  // Um clique num campo que já tem o foco (depois de Esc, por exemplo) volta a pedir as sugestões.
  const onPointerDown = once((e) => {
    if (filling || !e.isTrusted) return;
    const field = deepTarget(e);
    if (isField(field) && field === deepActive()) report(field);
  });

  const onFocusOut = once((e) => {
    if (filling) return;
    const field = deepTarget(e);
    if (!isField(field) || field !== focused) return;
    // O foco saiu da página (outra janela): espera-se para ver; ao voltar, o mesmo campo não reabre a lista.
    setTimeout(() => {
      if (!document.hasFocus()) { leftWithWindow = field; return; }
      if (deepActive() === field) return;
      if (focused === field) focused = null;
      send({ t: 'blur', field: idOf(field, 'f') });
    }, 0);
  });

  const onInput = once((e) => {
    if (filling) return;
    const field = deepTarget(e);
    if (!isField(field) || field !== focused) return;
    const info = formInfo(field);
    const kind = info.kinds[info.fields.indexOf(field)];
    const message = { t: 'input', field: idOf(field, 'f') };
    if (Classifier.USERLIKE.has(kind)) message.prefix = String(field.value || '').slice(0, 256);
    send(message);
  });

  // ---- O envio de um formulário (C4) ----
  // Captura do submit, do Enter num campo e do clique num botão (antes dos ouvintes da página: os valores ainda estão
  // lá). O navegador guarda o candidato só na memória e só oferece depois de um sucesso provável: o frame principal
  // navega sem o início de sessão voltar, ou o formulário desaparece (formGone).
  let lastSubmit = null;
  let goneWatch = null;

  function valuesOf(info) {
    return info.fields.map(f => (f.nodeName === 'INPUT' ? String(f.value || '') : ''));
  }

  function submitted(owner) {
    if (!OFERECER || !owner) return;
    let info;
    try { info = ownerInfo(owner, true); } catch (e) { return; }
    const sub = Classifier.submission(info, valuesOf(info));
    if (!sub) return;
    const key = [info.id, sub.username, sub.password || '', sub.newPassword || ''].join('|');
    const now = performance.now();
    if (lastSubmit && lastSubmit.key === key && now - lastSubmit.at < 1500) return;
    lastSubmit = { key, at: now };
    const message = { t: 'submit', form: info.id, formKind: sub.formKind };
    if (sub.username) message.username = sub.username;
    if (sub.password) message.password = sub.password;
    if (sub.newPassword) message.newPassword = sub.newPassword;
    send(message);
    watchGone(info);
  }

  // O formulário enviado desaparece (retirado, escondido) nos 20 s seguintes sem um início de sessão voltar: correu bem.
  function watchGone(info) {
    if (goneWatch !== null) clearInterval(goneWatch);
    const pw = info.fields.find((f, i) => info.kinds[i] === 'password' || info.kinds[i] === 'new-password');
    if (!pw) return;
    const started = performance.now();
    const timer = setInterval(() => {
      if (performance.now() - started > 20000) { clearInterval(timer); if (goneWatch === timer) goneWatch = null; return; }
      if (pw.isConnected && pw.getClientRects().length > 0 && getComputedStyle(pw).visibility !== 'hidden') return;
      clearInterval(timer);
      if (goneWatch === timer) goneWatch = null;
      setTimeout(() => { if (info.formKind !== 'login' || probe() !== 'login') send({ t: 'formGone', form: info.id }); }, 800);
    }, 300);
    goneWatch = timer;
  }

  // A página tem um início de sessão à vista? (o navegador pergunta depois de uma navegação: erro de login)
  function probe() {
    if (document.readyState === 'loading') return 'loading';
    let list;
    try { list = Array.from(document.querySelectorAll('input[type=password]')).slice(0, 50); } catch (e) { return 'none'; }
    for (const f of list) {
      const info = formInfo(f, true);
      if (info.formKind === 'login' && info.kinds[info.fields.indexOf(f)] === 'password') return 'login';
    }
    return 'none';
  }

  const onSubmit = once((e) => {
    const form = deepTarget(e);
    if (form && form.nodeName === 'FORM') submitted(form);
  });

  const onEnter = once((e) => {
    if (e.key !== 'Enter' || !e.isTrusted) return;
    const field = deepTarget(e);
    if (isField(field) && field.nodeName === 'INPUT') submitted(ownerOf(field));
  });

  const onButton = once((e) => {
    if (!e.isTrusted) return;
    const target = deepTarget(e);
    const button = target && target.closest ? target.closest('button,input[type=submit],input[type=image],[role=button]') : null;
    if (!button) return;
    submitted(button.form || button.closest('form') || button.getRootNode());
  });

  // Clique direito num campo (ou a tecla de menu): o navegador põe no menu «Preencher com…» e, numa palavra-passe,
  // «Gerar palavra-passe forte» (C5, C6). Só o que o menu precisa; nada se preenche sem a escolha.
  const onContext = once((e) => {
    if (!e.isTrusted) return;
    const field = deepTarget(e);
    if (!isField(field) || field.nodeName === 'SELECT') return;
    const info = formInfo(field, true);
    const kind = info.kinds[info.fields.indexOf(field)];
    const message = { t: 'context', field: idOf(field, 'f'), form: info.id, rect: topRect(field) };
    const password = String(field.type || '').toLowerCase() === 'password';
    if (kind) message.kind = kind;
    else if (password) message.kind = 'password';
    if (OFERECER && password) Object.assign(message, Classifier.constraints({ minLength: field.minLength, maxLength: field.maxLength, pattern: field.getAttribute('pattern') }));
    send(message);
  });

  const listening = new WeakSet();
  function listen(target) {
    if (listening.has(target)) return;
    listening.add(target);
    target.addEventListener('focusin', onFocusIn, true);
    target.addEventListener('focusout', onFocusOut, true);
    target.addEventListener('pointerdown', onPointerDown, true);
    target.addEventListener('input', onInput, true);
    target.addEventListener('contextmenu', onContext, true);
    if (OFERECER) {
      target.addEventListener('submit', onSubmit, true);
      target.addEventListener('keydown', onEnter, true);
      target.addEventListener('click', onButton, true);
    }
  }
  listen(window);

  let scrollQueued = false;
  function onScroll() {
    if (!focused || scrollQueued) return;
    scrollQueued = true;
    requestAnimationFrame(() => {
      scrollQueued = false;
      if (focused && focused.isConnected) send({ t: 'scroll', field: idOf(focused, 'f'), rect: topRect(focused) });
    });
  }
  addEventListener('scroll', onScroll, { capture: true, passive: true });
  addEventListener('resize', onScroll, { passive: true });

  // ---- O que o navegador pode pedir ----

  // Preenche o formulário do campo onde o utilizador escolheu a sugestão (arg.field, arg.form): só esse formulário, só
  // os campos com tipo e visíveis. Devolve quantos campos preencheu.
  function fill(arg) {
    if (!arg || typeof arg !== 'object' || typeof arg.form !== 'string' || typeof arg.field !== 'string') return 0;
    const owner = nodeOf(arg.form);
    const anchor = nodeOf(arg.field);
    if (!owner || !anchor || !anchor.isConnected || ownerOf(anchor) !== owner) return 0;
    const info = formInfo(anchor, true);
    const at = info.fields.indexOf(anchor);
    const metas = info.fields.map(f => ({
      maxLength: f.nodeName === 'SELECT' ? 0 : Math.max(0, Number(f.maxLength) || 0),
      placeholder: f.getAttribute('placeholder') || '',
      filled: f.nodeName !== 'SELECT' && String(f.value || '') !== '',
      options: f.nodeName === 'SELECT' ? Array.from(f.options).slice(0, 400).map(o => ({ value: o.value, text: o.text })) : null,
    }));
    const visible = info.kinds.map(() => true);
    // value: só o campo escolhido (menu de contexto: documento, viagem, palavra-passe gerada num campo qualquer).
    const plan = typeof arg.value === 'string'
      ? (arg.value !== '' ? [[at, Classifier.fit(arg.value, metas[at])]] : [])
      : Classifier.fillPlan(info.kinds, visible, arg.values || {}, metas, { keep: arg.keep === true, anchor: at });
    if (!plan.length) return 0;
    let count = 0;
    filling = true;
    try {
      for (const [i, value] of plan) if (Classifier.fillField(info.fields[i], value)) count++;
      const next = typeof arg.value === 'string' ? at : Classifier.focusAfter(info.kinds, visible, arg.values || {}, at);
      Classifier.focusBack(info.fields[next] || anchor);
    } finally {
      filling = false;
      quietUntil = performance.now() + 150;
    }
    return count;
  }

  const api = Object.freeze({
    fill,
    rect: (field) => {
      const node = typeof field === 'string' ? nodeOf(field) : null;
      return node && node.isConnected ? topRect(node) : null;
    },
    forget: () => null,
    probe: () => probe(),
  });
  Object.defineProperty(globalThis, '__cofre', { value: api, writable: false, configurable: false, enumerable: false });
  send({ t: 'ready', v: Classifier.version });
  return undefined;
})(__ETERIS_LIGACAO__, __ETERIS_OFERECER__);
