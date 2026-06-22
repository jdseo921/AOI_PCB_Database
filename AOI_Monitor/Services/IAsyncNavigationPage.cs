namespace AOI_Monitor.Services;

public interface IAsyncNavigationPage
{
    Task OnNavigatedToAsync(CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);

    void CancelWork();
}
