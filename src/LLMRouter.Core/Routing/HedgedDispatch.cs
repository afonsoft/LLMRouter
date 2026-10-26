namespace LLMRouter.Core.Routing;

public static class HedgedDispatch
{
    public static async Task<T?> ExecuteAsync<T>(
        Func<CancellationToken, Task<T?>> primary,
        Func<CancellationToken, Task<T?>> secondary,
        TimeSpan delay,
        Func<T?, bool> isSuccess,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = primary(linked.Token);
        var completed = await Task.WhenAny(first, Task.Delay(delay, ct));
        if (completed == first)
        {
            var firstResult = await first;
            if (isSuccess(firstResult))
            {
                linked.Cancel();
                return firstResult;
            }
        }

        var second = secondary(linked.Token);
        var winner = await Task.WhenAny(first, second);
        var winnerResult = await winner;
        if (isSuccess(winnerResult))
        {
            linked.Cancel();
            return winnerResult;
        }

        var other = winner == first ? second : first;
        var otherResult = await other;
        linked.Cancel();
        return isSuccess(otherResult) ? otherResult : winnerResult;
    }
}
