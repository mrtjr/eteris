using System.ComponentModel;
using System.Windows;

namespace Eteris.Browser.Infrastructure;

// Vigia uma propriedade de dependência e liberta-a. DependencyPropertyDescriptor.AddValueChanged guarda o alvo e o
// tratador numa tabela estática do WPF, com referência forte, até alguém chamar RemoveValueChanged: sem isso, cada página
// e cada janela que o usavam ficavam na memória até o Éteris sair. Quem vigia guarda o PropertyWatch e chama Dispose
// quando o alvo sai. Só o fio da interface.
public sealed class PropertyWatch : IDisposable
{
    private readonly DependencyPropertyDescriptor _descriptor;
    private readonly DependencyObject _target;
    private readonly EventHandler _handler;
    private bool _disposed;

    private PropertyWatch(DependencyPropertyDescriptor descriptor, DependencyObject target, EventHandler handler)
    {
        _descriptor = descriptor;
        _target = target;
        _handler = handler;
    }

    public static PropertyWatch Start(DependencyObject target, DependencyProperty property, Action changed)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
        EventHandler handler = (_, _) => changed();
        descriptor.AddValueChanged(target, handler);
        return new PropertyWatch(descriptor, target, handler);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _descriptor.RemoveValueChanged(_target, _handler);
    }
}
