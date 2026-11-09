// O script do cofre do Éteris (docs/palavras-passe.md, bloco C). Corre num mundo isolado de cada documento http(s)
// (Vault/VaultChannel.cs): partilha o DOM com a página, mas a página não vê as variáveis, os ouvintes nem a ligação dele.
// Fala com o navegador só pela ligação do mundo isolado (mensagens JSON validadas do outro lado) e nunca escreve no DOM
// fora do preenchimento pedido pelo navegador (guarda: VaultChannelGuardTests).
(function (LIGACAO) {
  'use strict';

  // ---- Classificador: funções puras sobre descritores (testadas no Node, CofreClassifierTests) ----
  const Classifier = Object.freeze({ version: 1 });

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

  const api = Object.freeze({
    fill: () => 0,
    rect: () => null,
    forget: () => null,
  });
  Object.defineProperty(globalThis, '__cofre', { value: api, writable: false, configurable: false, enumerable: false });
  send({ t: 'ready', v: Classifier.version });
  return undefined;
})(__ETERIS_LIGACAO__);
