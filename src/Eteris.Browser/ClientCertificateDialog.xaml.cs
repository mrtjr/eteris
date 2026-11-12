using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Eteris.Browser.Shields;

namespace Eteris.Browser;

// MOT-01: o site pede um certificado para identificar o utilizador (Shields/ClientCertificates.cs). Devolve a impressão
// digital do escolhido, ou null («Não enviar», Esc, fechar a janela). Nada fica lembrado, em nenhuma janela.
public partial class ClientCertificateDialog : Window
{
    private sealed record Item(string Text, string Thumbprint);

    private string? _chosen;

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
        List.MouseDoubleClick += (_, e) =>
        {
            if (List.SelectedItem is Item && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(List, source) is not null)
                Send();
        };
        SendButton.Click += (_, _) => Send();
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
