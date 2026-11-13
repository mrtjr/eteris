namespace Eteris.Browser.Watching;

// O que a vigia vigia: o preço da página ou o texto de um pedaço dela.
public enum WatchKind { Price, Change }

// O estado de uma vigia. «Blocked» (o site barra visitas automáticas) e «Missing» (o pedaço desapareceu) já não são
// visitados pela agenda: só a leitura passiva, quando o utilizador abre a página, os volta a ler.
public enum WatchStatus { New, Ok, Failing, Blocked, Missing }

// Porque é que uma visita não deu leitura. «HttpError» cobre também as páginas que não carregaram (sem rede, tempo
// esgotado, página vazia): falhas passageiras, que nunca bloqueiam a vigia.
public enum PageBlock { None, Challenge, AppRedirect, HttpError }

// O que uma verificação quer dizer para o utilizador; tudo o que não é «Nothing» dá um aviso.
public enum WatchOutcome { Nothing, Changed, PriceDropped, ReachedTarget, BecameBlocked, PieceMissing }

// Um preço lido. Source: "ld" (JSON-LD) | "itemprop" | "meta" | "texto" (o pedaço apontado).
public sealed record PriceReading(decimal Value, string? Currency, string Source);

// O que o script de leitura encontrou numa página.
public sealed record PageReading(PriceReading? Price, string? PieceText, bool PieceFound, string Title, string FinalUrl, bool ChallengeMarker);

// Uma vigia por criar.
public sealed record NewWatch(string Url, string Title, WatchKind Kind, string? Selector, bool UseSession, int IntervalMinutes, decimal? TargetPrice);

// Uma vigia guardada. LastPrice e LastText são os da última leitura boa (uma falha não os apaga); Failures conta as
// verificações falhadas seguidas (ver WatchRules.IsFailure) e LastBlock é o Block da última verificação.
public sealed record PageWatch(long Id, string Url, string Site, string Title, WatchKind Kind, string? Selector, bool UseSession,
    int IntervalMinutes, decimal? TargetPrice, bool Paused, WatchStatus Status, int Failures, DateTime CreatedUtc,
    DateTime? LastCheckUtc, DateTime NextCheckUtc, string? LastText, decimal? LastPrice, string? Currency, DateTime? FailingSinceUtc,
    PageBlock LastBlock);

// O resultado de uma verificação, pronto a gravar. Numa falha, Price, Currency e Text vêm a null.
public sealed record WatchCheck(DateTime AtUtc, WatchStatus Status, decimal? Price, string? Currency, string? Text, PageBlock Block);
