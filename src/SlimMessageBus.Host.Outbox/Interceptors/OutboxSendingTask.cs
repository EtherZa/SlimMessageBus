﻿namespace SlimMessageBus.Host.Outbox;

public class OutboxSendingTask(
    ILoggerFactory loggerFactory,
    OutboxSettings outboxSettings,
    IServiceProvider serviceProvider,
    IInstanceIdProvider instanceIdProvider)
    : IMessageBusLifecycleInterceptor, IAsyncDisposable
{
    private readonly ILogger<OutboxSendingTask> _logger = loggerFactory.CreateLogger<OutboxSendingTask>();
    private readonly OutboxSettings _outboxSettings = outboxSettings;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IInstanceIdProvider _instanceIdProvider = instanceIdProvider;

    private CancellationTokenSource _loopCts;
    private Task _loopTask;
    private int _busStartCount;

    private DateTime? _cleanupNextRun;

    private bool ShouldRunCleanup()
    {
        if (_outboxSettings.MessageCleanup?.Enabled == true)
        {
            var trigger = _cleanupNextRun is null || DateTime.UtcNow > _cleanupNextRun.Value;
            if (trigger)
            {
                _cleanupNextRun = DateTime.UtcNow.Add(_outboxSettings.MessageCleanup.Interval);
            }

            return trigger;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual Task DisposeAsyncCore() => Stop();

    protected Task Start()
    {
        if (_loopCts == null)
        {
            _logger.LogDebug("Outbox loop starting...");

            _loopCts = new CancellationTokenSource();
            _loopTask = Run();

        }
        return Task.CompletedTask;
    }

    protected async Task Stop()
    {
        _logger.LogDebug("Outbox loop stopping...");

        _loopCts?.Cancel();

        if (_loopTask != null)
        {
            await _loopTask.ConfigureAwait(false);
            _loopTask = null;
        }

        if (_loopCts != null)
        {
            _loopCts.Dispose();
            _loopCts = null;
        }
    }

    public Task OnBusLifecycle(MessageBusLifecycleEventType eventType, IMessageBus bus)
    {
        if (eventType == MessageBusLifecycleEventType.Started)
        {
            // The first started bus starts this outbox task
            if (Interlocked.Increment(ref _busStartCount) == 1)
            {
                return Start();
            }
        }
        if (eventType == MessageBusLifecycleEventType.Stopping)
        {
            // The last stopped bus stops this outbox task
            if (Interlocked.Decrement(ref _busStartCount) == 0)
            {
                return Stop();
            }
        }
        return Task.CompletedTask;
    }

    private async Task Run()
    {
        try
        {
            _logger.LogInformation("Outbox loop started");
            var scope = _serviceProvider.CreateScope();
            try
            {
                var outboxMigrationService = scope.ServiceProvider.GetRequiredService<IOutboxMigrationService>();
                await outboxMigrationService.Migrate(_loopCts.Token);

                var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

                var processedIds = new List<Guid>(_outboxSettings.PollBatchSize);

                for (var ct = _loopCts.Token; !ct.IsCancellationRequested;)
                {
                    var idleRun = true;
                    try
                    {
                        var lockExpiresOn = DateTime.UtcNow.Add(_outboxSettings.LockExpiration);
                        var lockedCount = await outboxRepository.TryToLock(_instanceIdProvider.GetInstanceId(), lockExpiresOn, ct).ConfigureAwait(false);
                        // Check if some messages where locked
                        if (lockedCount > 0)
                        {
                            idleRun = await SendMessages(scope.ServiceProvider, outboxRepository, processedIds, ct).ConfigureAwait(false);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while processing outbox messages");
                    }

                    if (idleRun)
                    {
                        if (ShouldRunCleanup())
                        {
                            _logger.LogTrace("Running cleanup of sent messages");
                            await outboxRepository.DeleteSent(DateTime.UtcNow.Add(-_outboxSettings.MessageCleanup.Age), ct).ConfigureAwait(false);
                        }

                        await Task.Delay(_outboxSettings.PollIdleSleep).ConfigureAwait(false);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // do nothing
            }
            finally
            {
                if (scope is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    scope.Dispose();
                }
                _logger.LogInformation("Outbox loop stopped");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Outbox loop has crashed");
        }
    }

    private async Task<bool> SendMessages(IServiceProvider serviceProvider, IOutboxRepository outboxRepository, List<Guid> processedIds, CancellationToken ct)
    {
        var messageBus = serviceProvider.GetRequiredService<IMessageBus>();
        var compositeMessageBus = messageBus as ICompositeMessageBus;
        var messageBusTarget = messageBus as IMessageBusTarget;

        var idleRun = true;

        for (var hasMore = true; hasMore && !ct.IsCancellationRequested;)
        {
            var outboxMessages = await outboxRepository.FindNextToSend(_instanceIdProvider.GetInstanceId(), ct);
            if (outboxMessages.Count == 0)
            {
                break;
            }

            try
            {
                for (var i = 0; i < outboxMessages.Count && !ct.IsCancellationRequested; i++)
                {
                    var outboxMessage = outboxMessages[i];

                    var now = DateTime.UtcNow;
                    if (now.Add(_outboxSettings.LockExpirationBuffer) > outboxMessage.LockExpiresOn)
                    {
                        _logger.LogDebug("Stopping the outbox message processing after {MessageCount} (out of {BatchCount}) because the message lock was close to expiration {LockBuffer}", i, _outboxSettings.PollBatchSize, _outboxSettings.LockExpirationBuffer);
                        hasMore = false;
                        break;
                    }

                    var bus = GetBus(compositeMessageBus, messageBusTarget, outboxMessage.BusName);
                    if (bus == null)
                    {
                        _logger.LogWarning("Not able to find matching bus provider for the outbox message with Id {MessageId} of type {MessageType} to path {Path} using {BusName} bus. The message will be skipped.", outboxMessage.Id, outboxMessage.MessageType.Name, outboxMessage.Path, outboxMessage.BusName);
                        continue;
                    }

                    _logger.LogDebug("Sending outbox message with Id {MessageId} of type {MessageType} to path {Path} using {BusName} bus", outboxMessage.Id, outboxMessage.MessageType.Name, outboxMessage.Path, outboxMessage.BusName);
                    var message = bus.Serializer.Deserialize(outboxMessage.MessageType, outboxMessage.MessagePayload);

                    // Add special header to supress from forwarding the message againt to outbox
                    var headers = outboxMessage.Headers ?? new Dictionary<string, object>();
                    headers.Add(OutboxForwardingPublishInterceptor<object>.SkipOutboxHeader, string.Empty);

                    if (!ct.IsCancellationRequested)
                    {
                        await bus.ProducePublish(message, path: outboxMessage.Path, headers: headers, messageBusTarget, cancellationToken: ct);

                        processedIds.Add(outboxMessage.Id);
                    }
                }
            }
            finally
            {
                // confirm what messages were processed 
                if (processedIds.Count > 0)
                {
                    _logger.LogDebug("Updating {MessageCount} outbox messages as sent", processedIds.Count);
                    await outboxRepository.UpdateToSent(processedIds, ct);

                    idleRun = false;

                    processedIds.Clear();
                }
            }
        }
        return idleRun;
    }

    private static IMasterMessageBus GetBus(ICompositeMessageBus compositeMessageBus, IMessageBusTarget messageBusTarget, string name)
    {
        if (name != null && compositeMessageBus != null)
        {
            return compositeMessageBus.GetChildBus(name);
        }
        if (messageBusTarget != null)
        {
            return messageBusTarget.Target as IMasterMessageBus;
        }
        return null;
    }
}
