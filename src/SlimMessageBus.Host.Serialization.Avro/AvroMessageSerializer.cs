﻿namespace SlimMessageBus.Host.Serialization.Avro;

using global::Avro;
using global::Avro.IO;
using global::Avro.Specific;

using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Apache Avro serialization implementation of <see cref="IMessageSerializer"/>
/// </summary>
public class AvroMessageSerializer : IMessageSerializer, IMessageSerializerProvider
{
    private readonly ILogger _logger;

    /// <summary>
    /// Allows to customize how are the <see cref="MemoryStream"/>s created and potentially introduce a strategy to reuse them.
    /// </summary>
    public Func<MemoryStream> WriteMemoryStreamFactory { get; set; }

    /// <summary>
    /// Allows to customize how are the <see cref="MemoryStream"/>s created and potentially introduce a strategy to reuse them.
    /// </summary>
    public Func<byte[], MemoryStream> ReadMemoryStreamFactory { get; set; }

    /// <summary>
    /// Allows to customize message creation of a given type. This is used while deserializing a message.
    /// </summary>
    public Func<Type, object> MessageFactory { get; set; }

    /// <summary>
    /// Used to look up a <see cref="Schema"/> for writing a message of type <see cref="Type"/>.
    /// </summary>
    public Func<Type, Schema> WriteSchemaLookup { get; set; }

    /// <summary>
    /// Used to look up a <see cref="Schema"/> for reading a message of type <see cref="Type"/>.
    /// </summary>
    public Func<Type, Schema> ReadSchemaLookup { get; set; }

    /// <summary>
    /// By default MessageFactory is set to use the <see cref="ReflectionMessageCreationStrategy"/> strategy, WriteSchemaLookup and ReadSchemaLookup is set to use <see cref="ReflectionSchemaLookupStrategy"/>.
    /// </summary>
    public AvroMessageSerializer(ILoggerFactory loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AvroMessageSerializer>();

        // Apply defaults
        WriteMemoryStreamFactory = () => new MemoryStream();
        ReadMemoryStreamFactory = (byte[] payload) => new MemoryStream(payload);

        var mf = new ReflectionMessageCreationStrategy(loggerFactory.CreateLogger<ReflectionMessageCreationStrategy>());
        var ml = new ReflectionSchemaLookupStrategy(loggerFactory.CreateLogger<ReflectionSchemaLookupStrategy>());

        MessageFactory = mf.Create;
        WriteSchemaLookup = ml.Lookup;
        ReadSchemaLookup = WriteSchemaLookup;
    }

    public AvroMessageSerializer(ILoggerFactory loggerFactory, IMessageCreationStrategy messageCreationStrategy, ISchemaLookupStrategy writerAndReaderSchemaLookupStrategy)
        : this(loggerFactory, messageCreationStrategy, writerAndReaderSchemaLookupStrategy, writerAndReaderSchemaLookupStrategy)
    {
    }

    public AvroMessageSerializer(ILoggerFactory loggerFactory, IMessageCreationStrategy messageCreationStrategy, ISchemaLookupStrategy writerSchemaLookupStrategy, ISchemaLookupStrategy readerSchemaLookupStrategy)
        : this(loggerFactory)
    {
        MessageFactory = messageCreationStrategy.Create;
        WriteSchemaLookup = writerSchemaLookupStrategy.Lookup;
        ReadSchemaLookup = readerSchemaLookupStrategy.Lookup;
    }

    public object Deserialize(Type t, byte[] payload)
    {
        using var ms = ReadMemoryStreamFactory(payload);

        var dec = new BinaryDecoder(ms);

        var message = MessageFactory(t);

        var readerSchema = ReadSchemaLookup(t);
        AssertSchemaNotNull(t, readerSchema, false);

        var writerSchema = WriteSchemaLookup(t);
        AssertSchemaNotNull(t, writerSchema, true);

        _logger.LogDebug("Type {Type} writer schema: {WriterSchema}, reader schema: {ReaderSchema}", t, writerSchema, readerSchema);

        var reader = new SpecificDefaultReader(writerSchema, readerSchema);
        reader.Read(message, dec);
        return message;
    }

    private static void AssertSchemaNotNull(Type t, Schema schema, bool writerSchema)
    {
        if (schema == null)
        {
            var role = writerSchema ? "Writer" : "Reader";
            throw new ArgumentNullException(nameof(schema), $"{role} schema lookup for type {t} returned null");
        }
    }

    public byte[] Serialize(Type t, object message)
    {
        using var ms = WriteMemoryStreamFactory();
        var enc = new BinaryEncoder(ms);

        var writerSchema = WriteSchemaLookup(t);
        AssertSchemaNotNull(t, writerSchema, true);

        _logger.LogDebug("Type {Type} writer schema: {WriterSchema}", t, writerSchema);

        var writer = new SpecificDefaultWriter(writerSchema); // Schema comes from pre-compiled, code-gen phase
        writer.Write(message, enc);
        return ms.ToArray();
    }

    // ToDo: Leverage to implement Avro specific feature: https://github.com/zarusz/SlimMessageBus/issues/370
    public IMessageSerializer GetSerializer(string path) => this;
}
