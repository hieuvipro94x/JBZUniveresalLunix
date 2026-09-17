using System.IO;
using System.Threading.Channels;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

/// <summary>One response reader per command; a mismatched index never completes a model write.</summary>
public static class JbzModelAckWaiter
{
    public static async Task<JbzBoardEvent> WaitAsync(
        JbzProtocolCommand command,
        ChannelReader<JbzBoardEvent> responses,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.Expectation.Timeout);
        var recent = new Queue<string>();
        string[] parts = command.Expectation.Value.Split(',');
        string familyPrefix = parts.Length > 2 ? $"{parts[0]},{parts[1]}," : string.Empty;
        while (true)
        {
            JbzBoardEvent response;
            try { response = await responses.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout waiting for ACK to {command.Text} after {command.Expectation.Timeout.TotalMilliseconds:0} ms; RX: {string.Join(" | ", recent)}");
            }
            if (response.Family == JbzEventFamily.Error)
                throw new IOException($"Board rejected {command.Text}: {response.Raw}");
            if (command.Expectation.Matches(response.Raw)) return response;
            if (recent.Count == 5) recent.Dequeue();
            recent.Enqueue(response.Raw);
            if (familyPrefix.Length > 0 && response.Raw.StartsWith(familyPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid ACK for {command.Text}: {response.Raw}");
        }
    }
}
