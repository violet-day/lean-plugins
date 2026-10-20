using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Packets;

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
    public void PreservesBrokerageProvidedTag()
    {
        const string brokerageOrderIdentifier = "broker-order-123";
        const string brokerageProvidedTag = "brokerage-tag";
        var algorithm = CreateAlgorithm(out var objectStore);
        var order = CreateOrder(brokerageOrderIdentifier, brokerageProvidedTag);
        SavePersistedOrderTag(objectStore, algorithm, order, brokerageOrderIdentifier, "persisted-tag");

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

    private static QCAlgorithm CreateAlgorithm(out InMemoryObjectStore objectStore)
    {
        Config.Set("data-folder", "/Lean/Data");
        Globals.Reset();
        var algorithm = new QCAlgorithm();
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
        RestoreOrderTagMethod.Invoke(transactionHandler, new object[] { order, algorithm });
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
