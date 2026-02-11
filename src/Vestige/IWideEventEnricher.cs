namespace Vestige;

/// <summary>
/// Hook into the event lifecycle to add context fields.
/// Register multiple enrichers; they run in DI registration order.
/// </summary>
public interface IWideEventEnricher
{
    /// <summary>Called immediately after a new <see cref="WideEvent"/> is created.</summary>
    Task OnEventCreatedAsync(WideEvent ev, CancellationToken cancellationToken = default);

    /// <summary>Called just before the event is serialized and sent to sinks.</summary>
    Task OnBeforeEmitAsync(WideEvent ev, CancellationToken cancellationToken = default);
}
