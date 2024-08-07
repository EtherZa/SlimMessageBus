# Hybrid Provider for SlimMessageBus <!-- omit in toc -->

Please read the [Introduction](intro.md) before reading this provider documentation.

- [What is the Hybrid provider?](#what-is-the-hybrid-provider)
- [Use cases](#use-cases)
- [Configuration](#configuration)
  - [Shared configuration](#shared-configuration)
  - [Configuration modularization](#configuration-modularization)

## What is the Hybrid provider?

The Hybrid bus enables a composition of many transport providers into one bus.
This allows different layers of the application to work with just one `IMessageBus` interface, and to rely on the hybrid bus implementation to route produced messages to the respective transport provider based on configuration.

> Since version 2.0.0 the hybrid bus has been incorporated into every transport - the [SlimMessageBus.Host.Hybrid](https://www.nuget.org/packages/SlimMessageBus.Host.Hybrid/) package has been deprecated.

![](provider_hybrid_1.png)

## Use cases

A typical example would be when an micro-service has a domain layer which uses domain events passed in memory (SlimMessageBus.Host.Memory transport), but any other layer (application or adapter) need to communicate with the outside world using something like Azure Service Bus or Apache Kafka transports.

![](provider_hybrid_2.png)

## Configuration

Here is an example configuration taken from [Sample.Hybrid.ConsoleApp](../src/Samples/Sample.Hybrid.ConsoleApp) sample:

```cs
services.AddSlimMessageBus(mbb =>
{
    // In summary:
    // - The CustomerChangedEvent messages will be going through the SMB Memory provider.
    // - The SendEmailCommand messages will be going through the SMB Azure Service Bus provider.
    // - Each of the bus providers will serialize messages using JSON and use the same DI to resolve consumers/handlers.
    mbb
        // No need to specify the hybrid provider - it is the default bus since 2.0.0
        //.WithProviderHybrid()

        // Bus 1
        .AddChildBus("Memory", (mbbChild) =>
        {
            mbbChild
                .Produce<CustomerEmailChangedEvent>(x => x.DefaultTopic(x.MessageType.Name))
                .Consume<CustomerEmailChangedEvent>(x => x.Topic(x.MessageType.Name).WithConsumer<CustomerChangedEventHandler>())
                .WithProviderMemory();
        })
        // Bus 2
        .AddChildBus("AzureSB", (mbbChild) =>
        {
            var serviceBusConnectionString = "...";
            mbbChild
                .Produce<SendEmailCommand>(x => x.DefaultQueue("test-ping-queue"))
                .Consume<SendEmailCommand>(x => x.Queue("test-ping-queue").WithConsumer<SmtpEmailService>())
                .WithProviderServiceBus(cfg => cfg.ConnectionString = serviceBusConnectionString);
        })
        .AddJsonSerializer() // serialization setup will be shared between bus 1 and 2
        .AddServicesFromAssemblyContaining<CustomerChangedEventHandler>(); // register all the found consumers and handlers in DI 
});
```

In the example above, we define the hybrid bus to create two kinds of transports - Memory and Azure Service Bus:

- The message type `CustomerEmailChangedEvent` published will be routed to the memory bus for delivery.
- Conversely, the `SendEmailCommand` will be routed to the Azure Service Bus transport.

> Routing is determined based on the message type.

The `IMessageBus` injected into any layer of your application will be the hybrid bus, therefore production of a message will be routed to the respective bus implementation (memory or Azure SB in our example).

It is important to understand, that handlers (`IRequestHandler<>`) or consumers (`IConsumer<>`) registered will be managed by the respective child bus that they are configured on.

There can be more than one child bus that can consume the given message type. In this case hybrid bus will route the message to all of the child bus.
By default any matching child bus will be executed in sequence. There is also an option to execute this in parallel (see the `PublishExecutionMode` setting on `HybridMessageBusSettings`).

> A given request message type can only be handled by one child bus, however, non-request messages can by consumed by multiple child buses.

The request messages need exactly one handler to calculate the response, therefore if we had more than one handler for a given request it would be ambiguous which response to return.

### Shared configuration

Any setting applied at the hybrid bus builder level will be inherited by each child transport bus. In the example mentioned, the memory and Azure SB buses will inherit the serializer and dependency resolver.

Individual child buses can provide their own serialization (or any other setting) and effectively override the serialization (or any other setting).

> The Hybrid bus builder configurations of the producer (`Produce()`) and consumer (`Consume()`) will be added into every child bus producer/consumer registration list.

### Configuration modularization

The [Modularization of configuration](intro.md#modularization-of-configuration) section mentions the ability to use `services.AddSlimMessageBus()` multiple times in order to segregate the configuration of the message bus by the application modules (or layers).
That allows for modularization of transports that are being introduced by the respective application layers (hexagonal architecture).
