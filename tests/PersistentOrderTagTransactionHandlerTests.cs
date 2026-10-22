using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.LeanPlugins.Tests;

[TestFixture]
[NonParallelizable]
public sealed class PersistentOrderTagTransactionHandlerTests
{
    private static readonly MethodInfo RestoreOrderTagMethod =
        typeof(PersistentOrderTagTransactionHandler).GetMethod(
            "RestoreOrderTag",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RestoreOrderTag method was not found");
    private static readonly MethodInfo ConfigurePersistenceMethod =
        typeof(PersistentOrderTagTransactionHandler).GetMethod(
            "ConfigurePersistence",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ConfigurePersistence method was not found");
    private static readonly MethodInfo SetCurrentEnvironmentConfigurationMethod =
        typeof(LeanPluginsJobQueueHandler).GetMethod(
            "SetCurrentEnvironmentConfiguration",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "SetCurrentEnvironmentConfiguration method was not found");
    private static readonly FieldInfo LiveModeField =
        typeof(QCAlgorithm).GetField("_liveMode", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("QCAlgorithm live mode field was not found");

    [Test]
    public void SetsConfigurationOnCurrentEnvironment()
    {
        const string environmentName = "plugin-test";
        const string expectedSetupHandler = "CustomSetupHandler";
        var previousEnvironmentName = Config.Get("environment");
        var previousEnvironments = Config.GetToken("environments")?.DeepClone();
        try
        {
            Config.Set("environments", new JObject
            {
                [environmentName] = new JObject
                {
                    ["setup-handler"] = "DefaultSetupHandler"
                }
            });
            Config.Set("environment", environmentName);

            SetCurrentEnvironmentConfigurationMethod.Invoke(
                null,
                new object[] { "setup-handler", expectedSetupHandler });

            Assert.That(Config.Get("setup-handler"), Is.EqualTo(expectedSetupHandler));
        }
        finally
        {
            Config.Set("environment", previousEnvironmentName);
            Config.Set("environments", previousEnvironments ?? new JObject());
        }
    }

    [Test]
    public void EmitsSubmittedEventForRestoredOpenOrder()
    {
        var algorithm = CreateAlgorithm(out _);
        LiveModeField.SetValue(algorithm, true);
        var restoredOpenOrder = CreateOrder("broker-order-123");
        algorithm.Securities.Add(new Security(
            restoredOpenOrder.Symbol,
            SecurityExchangeHours.AlwaysOpen(TimeZones.Utc),
            new Cash(Currencies.USD, 0m, 1m),
            SymbolProperties.GetDefault(Currencies.USD),
            new IdentityCurrencyConverter(Currencies.USD),
            RegisteredSecurityDataTypesProvider.Null,
            new SecurityCache()));
        var transactionHandler = new PersistentOrderTagTransactionHandler();
        algorithm.Transactions.SetOrderProcessor(transactionHandler);
        var resultHandler = new RecordingBacktestingResultHandler();
        using var brokerage = new BacktestingBrokerage(algorithm);
        ConfigurePersistenceMethod.Invoke(
            transactionHandler,
            new object[] { algorithm, brokerage });
        ((ITransactionHandler)transactionHandler).AddOpenOrder(restoredOpenOrder, algorithm);

        try
        {
            transactionHandler.Initialize(algorithm, brokerage, resultHandler);
            Assert.That(resultHandler.OrderEvents, Is.Empty);

            transactionHandler.ProcessSynchronousEvents();
            transactionHandler.ProcessSynchronousEvents();
        }
        finally
        {
            transactionHandler.Exit();
        }

        var restoredSubmittedEvent = resultHandler.OrderEvents.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                ((ITransactionHandler)transactionHandler).OrderEvents.Single(),
                Is.SameAs(restoredSubmittedEvent));
            Assert.That(restoredSubmittedEvent.OrderId, Is.EqualTo(restoredOpenOrder.Id));
            Assert.That(restoredSubmittedEvent.Id, Is.Zero);
            Assert.That(restoredSubmittedEvent.UtcTime, Is.EqualTo(restoredOpenOrder.Time));
            Assert.That(restoredSubmittedEvent.Status, Is.EqualTo(OrderStatus.Submitted));
            Assert.That(restoredSubmittedEvent.Quantity, Is.EqualTo(restoredOpenOrder.Quantity));
            Assert.That(restoredSubmittedEvent.LimitPrice, Is.EqualTo(100m));
            Assert.That(restoredSubmittedEvent.Message, Is.EqualTo("Restored brokerage open order"));
        });
    }

    [Test]
    public void RestoresTagWhenBrokerageIdentifierAndOrderFingerprintMatch()
    {
        const string brokerageOrderIdentifier = "broker-order-123";
        const string persistedTag = "entry:gap-breakout";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder(brokerageOrderIdentifier);
        SavePersistedOrderTag(objectStore, algorithm, order, brokerageOrderIdentifier, persistedTag);

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.EqualTo(persistedTag));
    }

    [Test]
    public void RestoresPersistedTagOverBrokerageProvidedTag()
    {
        const string brokerageOrderIdentifier = "broker-order-123";
        const string brokerageProvidedTag = "brokerage-tag";
        const string persistedTag = "persisted-tag";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder(brokerageOrderIdentifier, brokerageProvidedTag);
        SavePersistedOrderTag(objectStore, algorithm, order, brokerageOrderIdentifier, persistedTag);

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.EqualTo(persistedTag));
    }

    [Test]
    public void PreservesBrokerageProvidedTagWhenPersistedOrderDoesNotMatch()
    {
        const string brokerageProvidedTag = "brokerage-tag";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder("current-broker-order", brokerageProvidedTag);
        SavePersistedOrderTag(
            objectStore,
            algorithm,
            order,
            "different-broker-order",
            "persisted-tag");

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.EqualTo(brokerageProvidedTag));
    }

    [Test]
    public void DoesNotRestoreTagWhenBrokerageIdentifierDoesNotMatch()
    {
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder("current-broker-order");
        SavePersistedOrderTag(objectStore, algorithm, order, "different-broker-order", "persisted-tag");

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.Empty);
    }

    [Test]
    public void DoesNotRestoreTagWhenOrderFingerprintDoesNotMatch()
    {
        const string brokerageOrderIdentifier = "broker-order-123";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder(brokerageOrderIdentifier);
        var differentOrder = new LimitOrder(
            CreateEquitySymbol("QQQ"),
            1m,
            100m,
            DateTime.UtcNow);
        SavePersistedOrderTag(
            objectStore,
            algorithm,
            differentOrder,
            brokerageOrderIdentifier,
            "persisted-tag");

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.Empty);
    }

    [Test]
    public void DoesNotRestoreTagWhenStoredBrokerageDoesNotMatch()
    {
        const string brokerageOrderIdentifier = "broker-order-123";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder(brokerageOrderIdentifier);
        SavePersistedOrderTag(
            objectStore,
            algorithm,
            order,
            brokerageOrderIdentifier,
            "persisted-tag",
            storedBrokerageType: "Different.Brokerage");

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.Empty);
    }

    [Test]
    public void RestoresTagFromLegacyBrokerageNameStorage()
    {
        const string brokerageOrderIdentifier = "broker-order-123";
        const string persistedTag = "legacy-tag";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder(brokerageOrderIdentifier);
        SaveLegacyPersistedOrderTag(
            objectStore,
            algorithm,
            order,
            brokerageOrderIdentifier,
            persistedTag);

        RestoreOrderTag(new PersistentOrderTagTransactionHandler(), order, algorithm);

        Assert.That(order.Tag, Is.EqualTo(persistedTag));
    }

    private static QCAlgorithm CreateAlgorithm(out InMemoryObjectStore objectStore)
    {
        Config.Set("data-folder", "/Lean/Data");
        Globals.Reset();
        var algorithm = new QCAlgorithm();
        algorithm.ProjectId = 123;
        objectStore = new InMemoryObjectStore();
        algorithm.SetObjectStore(objectStore);
        return algorithm;
    }

    private static LimitOrder CreateOrder(
        string brokerageOrderIdentifier,
        string tag = "")
    {
        var order = new LimitOrder(
            CreateEquitySymbol("SPY"),
            1m,
            100m,
            DateTime.UtcNow,
            tag);
        order.BrokerId.Add(brokerageOrderIdentifier);
        return order;
    }

    private static Symbol CreateEquitySymbol(string ticker)
    {
        return new Symbol(
            SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                ticker,
                Market.USA),
            ticker);
    }

    private static void SavePersistedOrderTag(
        InMemoryObjectStore objectStore,
        QCAlgorithm algorithm,
        Order order,
        string brokerageOrderIdentifier,
        string tag,
        string? storedBrokerageName = null,
        string? storedBrokerageType = null)
    {
        using var brokerage = new BacktestingBrokerage(algorithm);
        var persistedOrderTagStore = new
        {
            Version = 2,
            BrokerageName = storedBrokerageName ?? brokerage.Name,
            BrokerageType = storedBrokerageType ?? brokerage.GetType().FullName,
            Orders = new[]
            {
                new
                {
                    BrokerageOrderIdentifiers = new[] { brokerageOrderIdentifier },
                    SymbolIdentifier = order.Symbol.ID.ToString(),
                    OrderType = order.Type,
                    OrderDirection = order.Direction,
                    Tag = tag
                }
            }
        };
        var storageKey = $"lean-plugins/order-tags/{brokerage.GetType().Name}.json";
        objectStore.SaveBytes(
            storageKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(persistedOrderTagStore)));
    }

    private static void SaveLegacyPersistedOrderTag(
        InMemoryObjectStore objectStore,
        QCAlgorithm algorithm,
        Order order,
        string brokerageOrderIdentifier,
        string tag)
    {
        var persistedOrderTagStore = new
        {
            Version = 1,
            Orders = new[]
            {
                new
                {
                    BrokerageOrderIdentifiers = new[] { brokerageOrderIdentifier },
                    SymbolIdentifier = order.Symbol.ID.ToString(),
                    OrderType = order.Type,
                    OrderDirection = order.Direction,
                    Tag = tag
                }
            }
        };
        var storageKey = $"lean-plugins/order-tags/{algorithm.BrokerageName}.json";
        objectStore.SaveBytes(
            storageKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(persistedOrderTagStore)));
    }

    private static void RestoreOrderTag(
        PersistentOrderTagTransactionHandler transactionHandler,
        Order order,
        QCAlgorithm algorithm)
    {
        using var brokerage = new BacktestingBrokerage(algorithm);
        ConfigurePersistenceMethod.Invoke(
            transactionHandler,
            new object[] { algorithm, brokerage });
        RestoreOrderTagMethod.Invoke(transactionHandler, new object[] { order, algorithm });
    }

    private sealed class RecordingBacktestingResultHandler : BacktestingResultHandler
    {
        public List<OrderEvent> OrderEvents { get; } = [];

        public override void OrderEvent(OrderEvent orderEvent)
        {
            OrderEvents.Add(orderEvent);
        }
    }

    private sealed class InMemoryObjectStore : IObjectStore
    {
        private readonly Dictionary<string, byte[]> _storedObjects = new(StringComparer.Ordinal);

        public long MaxSize => long.MaxValue;
        public int MaxFiles => int.MaxValue;
        public ICollection<string> Keys => _storedObjects.Keys;

        public event EventHandler<ObjectStoreErrorRaisedEventArgs> ErrorRaised
        {
            add { }
            remove { }
        }

        public void Initialize(
            int userId,
            int projectId,
            string userToken,
            Controls controls,
            AlgorithmMode algorithmMode)
        {
        }

        public bool ContainsKey(string path)
        {
            return _storedObjects.ContainsKey(path);
        }

        public byte[] ReadBytes(string path)
        {
            return _storedObjects[path];
        }

        public bool SaveBytes(string path, byte[] contents)
        {
            _storedObjects[path] = contents;
            return true;
        }

        public bool Delete(string path)
        {
            return _storedObjects.Remove(path);
        }

        public string GetFilePath(string path)
        {
            return path;
        }

        public void Clear()
        {
            _storedObjects.Clear();
        }

        public void Dispose()
        {
        }

        public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator()
        {
            return _storedObjects.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
