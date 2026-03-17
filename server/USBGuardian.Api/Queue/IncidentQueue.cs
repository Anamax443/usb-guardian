// ============================================================
// IncidentQueue.cs
// Bounded in-memory fronta pro příjem incident batchů.
// Odděluje HTTP příjem od DB zápisu – API odpovídá okamžitě
// (202 Accepted), worker zpracovává vlastním tempem.
//
// Bounded capacity = pojistka proti přetížení paměti.
// Při plné frontě vrátí controller 503 Service Unavailable.
// ============================================================

using System.Threading.Channels;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Queue;

/// <summary>
/// Jeden položka ve frontě – batch incidentů od jednoho agenta.
/// </summary>
public record IncidentBatchItem(
    IncidentBatchRequest Request,
    string?             SourceIp,
    DateTime            ReceivedAt);

/// <summary>
/// Singleton fronta – jeden Channel sdílený mezi controllery a workerem.
/// </summary>
public class IncidentQueue
{
    // Maximální počet čekajících batchů.
    // 500 PC × max 2 retry = 1000 je rozumný strop.
    // Při překročení → 503, agent zkusí příště (má retry logiku).
    private const int MaxCapacity = 1000;

    private readonly Channel<IncidentBatchItem> _channel;

    public IncidentQueue()
    {
        var options = new BoundedChannelOptions(MaxCapacity)
        {
            // Při plné frontě: Write okamžitě vrátí false (neblokuje HTTP thread)
            FullMode    = BoundedChannelFullMode.Wait,
            // Jeden writer (controller), jeden reader (worker)
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<IncidentBatchItem>(options);
    }

    /// <summary>
    /// Zařadí batch do fronty. Vrátí false pokud je fronta plná.
    /// </summary>
    public bool TryEnqueue(IncidentBatchItem item)
        => _channel.Writer.TryWrite(item);

    /// <summary>
    /// Čte batche z fronty (pro background worker).
    /// </summary>
    public ChannelReader<IncidentBatchItem> Reader => _channel.Reader;

    /// <summary>
    /// Počet čekajících položek – pro monitoring / health check.
    /// </summary>
    public int PendingCount => _channel.Reader.Count;
}
