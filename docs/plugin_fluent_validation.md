# FluentValidation Plugin for SlimMessageBus <!-- omit in toc -->

Please read the [Introduction](intro.md) before reading this provider documentation.

- [Introduction](#introduction)
- [Configuration](#configuration)
  - [Configuring FluentValidation](#configuring-fluentvalidation)
    - [Custom exception](#custom-exception)
    - [Producer side validation](#producer-side-validation)
    - [Consumer side validation](#consumer-side-validation)
  - [Configuring without MSDI](#configuring-without-msdi)

## Introduction

The [`SlimMessageBus.Host.FluentValidation`](https://www.nuget.org/packages/SlimMessageBus.Host.FluentValidation) introduces validation on the producer or consumer side by leveraging the [FluentValidation](https://www.nuget.org/packages/FluentValidation) library.
The plugin is based on [`SlimMessageBus.Host.Interceptor`](https://www.nuget.org/packages/SlimMessageBus.Host.Interceptor) core interfaces and can work with any transport including the memory bus.

## Configuration

Consider the following command, with the validator (using FluentValidation) and command handler:

```cs
// The command
public record CreateCustomerCommand : IRequest<CommandResultWithId>
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

// The validator of the command (using FluentValidation)
public class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty();
        RuleFor(x => x.LastName).NotEmpty();
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Phone).NotEmpty().Length(6).When(x => x.Phone != null);
    }
}

// The handler of the command
public class CreateCustomerCommandHandler : IRequestHandler<CreateCustomerCommand, CommandResultWithId>
{
    public async Task<CommandResultWithId> OnHandle(CreateCustomerCommand command)
    {
        // ...
    }
}
```

### Configuring FluentValidation

Consider an in-process command that is delivered using the memory bus:

```cs
// Using minimal APIs
var builder = WebApplication.CreateBuilder(args);

// Configure SMB
builder.Services.AddSlimMessageBus(mbb =>
{
    mbb
        .WithProviderMemory()
        .AutoDeclareFrom(Assembly.GetExecutingAssembly())
        .AddServicesFromAssembly(Assembly.GetExecutingAssembly())
        .AddFluentValidation(opts =>
        {
            // SMB FluentValidation setup goes here
        });
});

// Register FluentValidation validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateCustomerCommandValidator>();
```

#### Custom exception

By default `FluentValidation.ValidationException` exception is raised on the producer and consumer when validation fails.
It is possible to configure custom exception (or perhaps to supress the validation errors):

```cs
builder.Services.AddSlimMessageBus(mbb =>
{
    mbb.AddFluentValidation(opts =>
    {
        // SMB FluentValidation setup goes here
        opts.AddValidationErrorsHandler(errors => new ApplicationException("Custom exception"));
    });
});
```

#### Producer side validation

The `.AddProducerValidatorsFromAssemblyContaining()` will register an SMB interceptor that will validate the message upon `.Publish()` or `.Send()` - on the producer side before the message even gets deliverd to the underlying transport. Continuing on the example from previous section:

```cs
builder.Services.AddSlimMessageBus(mbb =>
{
    mbb.AddFluentValidation(opts =>
    {
        // Register validation interceptors for message (here command) producers inside message bus
        // Required Package: SlimMessageBus.Host.FluentValidation
        opts.AddProducerValidatorsFromAssemblyContaining<CreateCustomerCommandValidator>();
    });
});
```

For example given an ASP.NET Minimal WebApi, the request can be delegated to SlimMessageBus in memory transport:

```cs
// Using minimal APIs
var app = builder.Build();

app.MapPost("/customer", (CreateCustomerCommand command, IMessageBus bus) => bus.Send(command));

await app.RunAsync();
```

In the situation that the incoming HTTP request where to deliver an invalid command, the request will fail with `FluentValidation.ValidationException: Validation failed` exception.

For full example, please see the [Sample.ValidatingWebApi](../src/Samples/Sample.ValidatingWebApi/) sample.

#### Consumer side validation

We can also enable validation of the incoming message just before it gets delivered to the respective `IConsumer<T>` or `IRequestHandler<T, R>` - on the consumer side.
Such validation would be needed in scenarios when an external system delivers messages onto the transport (Kafka, Azure Service Bus) which we do not trust, and therefore we could enable validation on the consumer end. This will prevent the invalid messages to enter the consumer or handler.

```cs
builder.Services.AddSlimMessageBus(mbb =>
{
    mbb.AddFluentValidation(opts =>
    {
        // Register validation interceptors for message (here command) consumers inside message bus
        // Required Package: SlimMessageBus.Host.FluentValidation
        opts.AddConsumerValidatorsFromAssemblyContaining<CreateCustomerCommandValidator>();
    });
});
```

In the situation that the message is invalid, the message will fail with `FluentValidation.ValidationException: Validation failed` exception and standard consumer error handling will take place (depending on the underlying transport it might get retried multiple times until it ends up on dead-letter queue).

### Configuring without MSDI

If you are using another DI container than Microsoft.Extensions.DependencyInjection, in order for the `SlimMessageBus.Host.FluentValidation` plugin to work, you simply need to:

- register the FluentValidator `IValidator<T>` validators in the container,
- register the respective `ProducerValidationInterceptor<T>` as `IProducerInterceptor<T>` for each of the message type `T` that needs to be validated on producer side,
- register the respective `ConsumerValidationInterceptor<T>` as `IConsumerInterceptor<T>` for each of the message type `T` that needs to be validated on consumer side,
- the scope of can be anything that you need (scoped, transient, singleton)
