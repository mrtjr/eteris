namespace Eteris.Browser.Tests;

// Testes que leem de volta estado estático global da app que outros testes, a correr em paralelo, também escrevem
// (o livro do relatório de privacidade, a língua com que o motor arrancou). O xunit corre as coleções sem paralelismo
// sozinhas, depois de todas as outras terminarem: assim o Take() do livro não apanha contagens de um PrivacyRequestHandler
// de outro teste, nem um CefConfigurator.Build paralelo troca a língua entre o Configure e a verificação.
// A app continua com um só estado global; só os testes que o leem de volta ficam aqui.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    public const string Name = "Estado global da app (sem paralelismo)";
}
