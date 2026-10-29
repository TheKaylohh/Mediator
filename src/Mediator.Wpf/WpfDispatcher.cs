using System.Windows.Threading;
using Mediator.UI;

namespace Mediator.Wpf;

/// <summary>Runtime events arrive on pool threads; the ViewModel is only touched on the UI thread.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
