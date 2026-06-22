using System.Windows.Threading;

namespace AOI_Monitor.Services;

public static class UiDispatcher
{
    public static void InvokeIfAvailable(Dispatcher dispatcher, Action action)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
