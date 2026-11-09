namespace Eteris.Browser.Tests;

// Testes que criam janelas WPF (Popups, Windows, HwndSource/MediaContext) ou mexem no motor de composição: o motor é um
// só por processo e arranca e desliga sob um lock global (MediaSystem do WPF). Em paralelo, os arranques e as paragens
// de uns atrasam os outros — no CI (4 vCPU, sem GPU) uma thread STA chegou a ficar mais de 20 s presa. O xunit corre as
// coleções sem paralelismo sozinhas, depois de todas as outras terminarem, como as de GlobalStateCollection.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF com janelas (sem paralelismo)";
}
