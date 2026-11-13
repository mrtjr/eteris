using System.Windows;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;

namespace Eteris.Browser;

// MOT-01: o site pede um certificado para identificar o utilizador (Shields/ClientCertificates.cs). Devolve a impressão
// digital do escolhido, ou null («Não enviar», Esc, fechar a janela). Nada fica lembrado, em nenhuma janela.
public partial class ClientCertificateDialog : Window
{
    private sealed record Item(string Text, string Thumbprint);

    private string? _chosen;
    // Até o diálogo aparecer, o botão não aceita cliques (MaxValue: a guarda ainda não começou a contar).
    private DateTime _shownUtc = DateTime.MaxValue;
    private DateTime _activatedUtc = DateTime.MinValue;

    private ClientCertificateDialog(string host, int port, IReadOnlyList<ClientCertificateRow> rows)
    {
        InitializeComponent();
        // Negrito só no site (o nome vem do pedido, nunca é lido como marcação).
        Question.Inlines.Add(new System.Windows.Documents.Run(host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture))
        { FontWeight = FontWeights.SemiBold });
        Question.Inlines.Add(new System.Windows.Documents.Run(" pede um certificado para o identificar. O site fica a saber quem é."));
        foreach (var row in rows)
            List.Items.Add(new Item($"{row.Subject} — emitido por {row.Issuer} — válido até {row.ValidUntil}", row.Thumbprint));
        List.SelectionChanged += (_, _) => SendButton.IsEnabled = List.SelectedItem is Item;
        // Enviar é só pelo botão, e nunca no meio segundo a seguir a o diálogo aparecer ou voltar a ficar ativo (a guarda
        // do cartão de permissão, Permissions/PermissionCard.cs): um duplo clique dado na página, com o diálogo a abrir
        // debaixo do cursor, escolhia e enviava o certificado. Sem duplo clique na lista.
        Loaded += (_, _) => _shownUtc = DateTime.UtcNow;
        Activated += (_, _) => _activatedUtc = DateTime.UtcNow;
        SendButton.Click += (_, _) => { if (PermissionCard.AcceptsClick(_shownUtc, _activatedUtc, DateTime.UtcNow)) Send(); };
        // O Enter fica sempre com o botão por omissão («Não enviar»): enviar o certificado é um clique deliberado.
        CancelButton.Click += (_, _) => Close();
    }

    private void Send()
    {
        if (List.SelectedItem is not Item item) return;
        _chosen = item.Thumbprint;
        Close();
    }

    // Pergunta ao utilizador, com o dono na janela do pedido. Corre na thread da janela.
    public static string? Ask(Window owner, string host, int port, IReadOnlyList<ClientCertificateRow> rows)
    {
        try
        {
            var dialog = new ClientCertificateDialog(host, port, rows);
            if (owner.IsLoaded) dialog.Owner = owner;
            dialog.ShowDialog();
            return dialog._chosen;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
