namespace SlimMessageBus.Host.Nats;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

public class NatsMessageBus : MessageBusBase<NatsMessageBusSettings>
{
    private readonly ILogger _logger;
    private NatsConnection _connection;

    public NatsMessageBus(MessageBusSettings settings, NatsMessageBusSettings providerSettings) : base(settings, providerSettings)
    {
        _logger = LoggerFactory.CreateLogger<NatsMessageBus>();
        OnBuildProvider();
    }

    protected override IMessageBusSettingsValidationService ValidationService => new NatsMessageBusSettingsValidationService(Settings, ProviderSettings);

    public bool IsConnected => _connection is { ConnectionState: NatsConnectionState.Open };

    protected override void Build()
    {
        base.Build();
        InitTaskList.Add(CreateConnectionAsync, CancellationToken);
    }

    private Task CreateConnectionAsync()
    {
        try
        {
            _connection = new NatsConnection(new NatsOpts
            {
                Url = ProviderSettings.Endpoint,
                LoggerFactory = LoggerFactory,
                AuthOpts = ProviderSettings.AuthOpts,
                Verbose = false,
                Name = ProviderSettings.ClientName
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not initialize Nats connection: {ErrorMessage}", e.Message);
        }

        return Task.CompletedTask;
    }

    protected override async Task CreateConsumers()
    {
        if (_connection == null)
        {
            throw new ConsumerMessageBusException("The connection is not available at this time");
        }

        await base.CreateConsumers();


        foreach (var (subject, consumerSettings) in Settings.Consumers.GroupBy(x => x.Path).ToDictionary(x => x.Key, x => x.ToList()))
        {
            var messageSerializer = SerializerProvider.GetSerializer(subject);
            object MessageProvider(Type messageType, NatsMsg<byte[]> transportMessage) => messageSerializer.Deserialize(messageType, transportMessage.Data);

            var processor = new MessageProcessor<NatsMsg<byte[]>>(
                consumerSettings,
                messageBus: this,
                messageProvider: MessageProvider,
                path: subject,
                this,
                consumerErrorHandlerOpenGenericType: typeof(INatsConsumerErrorHandler<>));

            AddSubjectConsumer(consumerSettings, subject, processor);
        }

        if (Settings.RequestResponse != null)
        {
            var subject = Settings.RequestResponse.Path;

            var messageSerializer = SerializerProvider.GetSerializer(subject);
            object MessageProvider(Type messageType, NatsMsg<byte[]> transportMessage) => messageSerializer.Deserialize(messageType, transportMessage.Data);

            var processor = new ResponseMessageProcessor<NatsMsg<byte[]>>(LoggerFactory, Settings.RequestResponse, MessageProvider, PendingRequestStore, TimeProvider);
            AddSubjectConsumer([], subject, processor);
        }
    }

    private void AddSubjectConsumer(IEnumerable<AbstractConsumerSettings> consumerSettings, string subject, IMessageProcessor<NatsMsg<byte[]>> processor)
    {
        _logger.LogInformation("Creating consumer for {Subject}", subject);
        var consumer = new NatsSubjectConsumer<byte[]>(LoggerFactory.CreateLogger<NatsSubjectConsumer<byte[]>>(), consumerSettings, interceptors: Settings.ServiceProvider.GetServices<IAbstractConsumerInterceptor>(), subject, _connection, processor);
        AddConsumer(consumer);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore().ConfigureAwait(false);

        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public override async Task ProduceToTransport(object message, Type messageType, string path, IDictionary<string, object> messageHeaders, IMessageBusTarget targetBus, CancellationToken cancellationToken)
    {
        try
        {
            OnProduceToTransport(message, messageType, path, messageHeaders);

            var messagePayload = SerializerProvider.GetSerializer(path).Serialize(messageType, message);

            var replyTo = messageHeaders.TryGetValue("ReplyTo", out var replyToValue)
                ? replyToValue.ToString()
                : null;

            NatsMsg<byte[]> m = new()
            {
                Data = messagePayload,
                Subject = path,
                Headers = [],
                ReplyTo = replyTo
            };

            foreach (var header in messageHeaders)
            {
                m.Headers.Add(new KeyValuePair<string, StringValues>(header.Key, header.Value.ToString()));
            }

            await _connection.PublishAsync(m, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not ProducerMessageBusException && ex is not TaskCanceledException)
        {
            throw new ProducerMessageBusException(GetProducerErrorMessage(path, message, messageType, ex), ex);
        }

    }
}