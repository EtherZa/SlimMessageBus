﻿namespace SlimMessageBus.Host;

/// <summary>
/// Decorator for <see cref="IMessageProcessor{TMessage}"> that increases the amount of messages being concurrently processed.
/// The expectation is that <see cref="IMessageProcessor{TMessage}.ProcessMessage(TMessage)"/> will be executed synchronously (in sequential order) by the caller on which we want to increase amount of concurrent transportMessage being processed.
/// </summary>
/// <typeparam name="TMessage"></typeparam>
public sealed class ConcurrencyIncreasingMessageProcessorDecorator<TMessage> : IMessageProcessor<TMessage>, IDisposable
{
    private readonly ILogger _logger;
    private SemaphoreSlim _concurrentSemaphore;
    private readonly IMessageProcessor<TMessage> _target;
    private Exception _lastException;
    private TMessage _lastExceptionMessage;
    private AbstractConsumerSettings _lastExceptionSettings;
    private readonly object _lastExceptionLock = new();

    private int _pendingCount;

    public int PendingCount => _pendingCount;

    public IReadOnlyCollection<AbstractConsumerSettings> ConsumerSettings => _target.ConsumerSettings;

    public ConcurrencyIncreasingMessageProcessorDecorator(int concurrency, MessageBusBase messageBus, IMessageProcessor<TMessage> target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (messageBus is null) throw new ArgumentNullException(nameof(messageBus));
        if (concurrency <= 1) throw new ArgumentOutOfRangeException(nameof(concurrency));

        _logger = messageBus.LoggerFactory.CreateLogger<ConcurrencyIncreasingMessageProcessorDecorator<TMessage>>();
        _concurrentSemaphore = new SemaphoreSlim(concurrency);
        _target = target;
    }

    #region IDisposable

    public void Dispose()
    {
        _concurrentSemaphore?.Dispose();
        _concurrentSemaphore = null;
    }

    #endregion

    public async Task<ProcessMessageResult> ProcessMessage(TMessage transportMessage, IReadOnlyDictionary<string, object> messageHeaders, IDictionary<string, object> consumerContextProperties = null, IServiceProvider currentServiceProvider = null, CancellationToken cancellationToken = default)
    {
        // Ensure only desired number of messages are being processed concurrently
        await _concurrentSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Check if there was an exception from and earlier transportMessage processing
        var e = _lastException;
        if (e != null)
        {
            // report the last exception
            _lastException = null;
            return new(e, _lastExceptionSettings, null);
        }

        Interlocked.Increment(ref _pendingCount);

        // Fire and forget
        _ = ProcessInBackground(transportMessage, messageHeaders, currentServiceProvider, consumerContextProperties, cancellationToken);

        // Not exception - we don't know yet
        return new(null, null, null);
    }

    public TMessage GetMessageWithException()
    {
        lock (_lastExceptionLock)
        {
            var m = _lastExceptionMessage;
            _lastExceptionMessage = default;
            return m;
        }
    }

    public async Task WaitAll()
    {
        while (_pendingCount > 0)
        {
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private async Task ProcessInBackground(TMessage transportMessage, IReadOnlyDictionary<string, object> messageHeaders, IServiceProvider currentServiceProvider, IDictionary<string, object> consumerContextProperties, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Entering ProcessMessages for message {MessageType}", typeof(TMessage));
            var r = await _target.ProcessMessage(transportMessage, messageHeaders, consumerContextProperties, currentServiceProvider, cancellationToken).ConfigureAwait(false);
            if (r.Exception != null)
            {
                lock (_lastExceptionLock)
                {
                    // ensure there was no error before this one, in which case forget about this error (the whole event stream will be rewind back).
                    if (_lastException == null && _lastExceptionMessage == null)
                    {
                        _lastException = r.Exception;
                        _lastExceptionMessage = transportMessage;
                        _lastExceptionSettings = r.ConsumerSettings;
                    }
                }
            }
        }
        finally
        {
            _logger.LogDebug("Leaving ProcessMessages for message {MessageType}", typeof(TMessage));
            _concurrentSemaphore.Release();

            Interlocked.Decrement(ref _pendingCount);
        }
    }
}