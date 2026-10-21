using System.Text.Json;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;

namespace QuantConnect.LeanPlugins;

public sealed class PersistentOrderTagTransactionHandler : BrokerageTransactionHandler, ITransactionHandler
{
    private const int StorageVersion = 1;
    private const string StorageKeyPrefix = "lean-plugins/order-tags";

    private readonly object _persistenceLock = new();
    private IAlgorithm? _algorithm;
    private PersistedOrderTagStore? _persistedOrderTagStore;
    private string? _persistedStateFingerprint;
    private string _storageKey = string.Empty;

    public override void Initialize(
        IAlgorithm algorithm,
        IBrokerage brokerage,
        IResultHandler resultHandler)
    {
        _algorithm = algorithm;
        EnsureStoreLoaded(algorithm);

        base.Initialize(algorithm, brokerage, resultHandler);

        var restoredOpenOrders = GetOpenOrders();
        foreach (var restoredOpenOrder in restoredOpenOrders)
        {
            var restoredSubmittedEvent = new OrderEvent(
                restoredOpenOrder.Id,
                restoredOpenOrder.Symbol,
                restoredOpenOrder.Time,
                OrderStatus.Submitted,
                restoredOpenOrder.Direction,
                0m,
                0m,
                OrderFee.Zero,
                "Restored brokerage open order")
            {
                Quantity = restoredOpenOrder.Quantity
            };
            if (restoredOpenOrder is LimitOrder restoredLimitOrder)
            {
                restoredSubmittedEvent.LimitPrice = restoredLimitOrder.LimitPrice;
            }
            resultHandler.OrderEvent(restoredSubmittedEvent);
        }
        PersistOpenOrderTags();

        Log.Trace(
            $"{nameof(PersistentOrderTagTransactionHandler)}.Initialize(): status=ready " +
            $"brokerage={algorithm.BrokerageName} storage_key={_storageKey} " +
            $"restored_submit_events={restoredOpenOrders.Count}");
    }

    void ITransactionHandler.AddOpenOrder(Order order, IAlgorithm algorithm)
    {
        RestoreOrderTag(order, algorithm);
        base.AddOpenOrder(order, algorithm);
    }

    public override void ProcessSynchronousEvents()
    {
        base.ProcessSynchronousEvents();
        PersistOpenOrderTags();
    }

    public override void ProcessAsynchronousEvents()
    {
        base.ProcessAsynchronousEvents();
        PersistOpenOrderTags();
    }

    private void RestoreOrderTag(Order order, IAlgorithm algorithm)
    {
        if (!string.IsNullOrWhiteSpace(order.Tag))
        {
            Log.Trace(
                $"{nameof(PersistentOrderTagTransactionHandler)}.RestoreOrderTag(): " +
                $"status=preserved source=brokerage symbol={order.Symbol.Value} " +
                $"broker_ids={FormatBrokerageOrderIdentifiers(order.BrokerId)} tag={order.Tag}");
            return;
        }

        EnsureStoreLoaded(algorithm);

        PersistedOrderTag? persistedOrderTag;
        lock (_persistenceLock)
        {
            persistedOrderTag = _persistedOrderTagStore?.Orders.FirstOrDefault(candidate =>
                candidate.SymbolIdentifier == order.Symbol.ID.ToString()
                && candidate.OrderType == order.Type
                && candidate.OrderDirection == order.Direction
                && candidate.BrokerageOrderIdentifiers.Intersect(order.BrokerId).Any());
        }

        if (persistedOrderTag == null)
        {
            Log.Trace(
                $"{nameof(PersistentOrderTagTransactionHandler)}.RestoreOrderTag(): " +
                $"status=missing symbol={order.Symbol.Value} order_type={order.Type} " +
                $"direction={order.Direction} " +
                $"broker_ids={FormatBrokerageOrderIdentifiers(order.BrokerId)}");
            return;
        }

        order.ApplyUpdateOrderRequest(new UpdateOrderRequest(
            order.Time,
            order.Id,
            new UpdateOrderFields { Tag = persistedOrderTag.Tag }));

        Log.Trace(
            $"{nameof(PersistentOrderTagTransactionHandler)}.RestoreOrderTag(): " +
            $"status=restored source=object-store symbol={order.Symbol.Value} " +
            $"broker_ids={FormatBrokerageOrderIdentifiers(order.BrokerId)} " +
            $"tag={persistedOrderTag.Tag}");
    }

    private void EnsureStoreLoaded(IAlgorithm algorithm)
    {
        lock (_persistenceLock)
        {
            if (_persistedOrderTagStore != null)
            {
                return;
            }

            _storageKey = $"{StorageKeyPrefix}/{algorithm.BrokerageName}.json";
            try
            {
                _persistedOrderTagStore = algorithm.ObjectStore.ContainsKey(_storageKey)
                    ? JsonSerializer.Deserialize<PersistedOrderTagStore>(
                        algorithm.ObjectStore.Read(_storageKey))
                    : new PersistedOrderTagStore();

                if (_persistedOrderTagStore == null)
                {
                    throw new InvalidOperationException("Order tag storage is empty");
                }

                if (_persistedOrderTagStore.Version != StorageVersion)
                {
                    throw new InvalidOperationException(
                        $"Unsupported order tag storage version: {_persistedOrderTagStore.Version}");
                }

                _persistedStateFingerprint = CreateFingerprint(_persistedOrderTagStore.Orders);
                Log.Trace(
                    $"{nameof(PersistentOrderTagTransactionHandler)}.EnsureStoreLoaded(): " +
                    $"status=loaded storage_key={_storageKey} " +
                    $"order_count={_persistedOrderTagStore.Orders.Count}");
            }
            catch (Exception exception)
            {
                _persistedOrderTagStore = new PersistedOrderTagStore();
                _persistedStateFingerprint = null;
                Log.Error(
                    exception,
                    $"{nameof(PersistentOrderTagTransactionHandler)}.EnsureStoreLoaded(): " +
                    $"status=failed storage_key={_storageKey}");
            }
        }
    }

    private void PersistOpenOrderTags()
    {
        var algorithm = _algorithm;
        if (algorithm == null)
        {
            return;
        }

        lock (_persistenceLock)
        {
            var openOrderTags = GetOpenOrders()
                .Where(order =>
                    !string.IsNullOrWhiteSpace(order.Tag)
                    && order.BrokerId.Count > 0)
                .Select(order => new PersistedOrderTag
                {
                    BrokerageOrderIdentifiers = order.BrokerId
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(brokerageOrderIdentifier => brokerageOrderIdentifier, StringComparer.Ordinal)
                        .ToList(),
                    SymbolIdentifier = order.Symbol.ID.ToString(),
                    OrderType = order.Type,
                    OrderDirection = order.Direction,
                    Tag = order.Tag
                })
                .OrderBy(orderTag => orderTag.SymbolIdentifier, StringComparer.Ordinal)
                .ThenBy(orderTag => orderTag.OrderType)
                .ThenBy(orderTag => orderTag.OrderDirection)
                .ThenBy(orderTag => string.Join("\u001f", orderTag.BrokerageOrderIdentifiers), StringComparer.Ordinal)
                .ToList();

            var currentStateFingerprint = CreateFingerprint(openOrderTags);
            if (_persistedOrderTagStore == null || currentStateFingerprint == _persistedStateFingerprint)
            {
                return;
            }

            _persistedOrderTagStore.Orders = openOrderTags;
            if (!algorithm.ObjectStore.Save(
                    _storageKey,
                    JsonSerializer.Serialize(_persistedOrderTagStore)))
            {
                Log.Error(
                    $"{nameof(PersistentOrderTagTransactionHandler)}.PersistOpenOrderTags(): " +
                    $"status=failed storage_key={_storageKey} order_count={openOrderTags.Count}");
                return;
            }

            _persistedStateFingerprint = currentStateFingerprint;
            Log.Trace(
                $"{nameof(PersistentOrderTagTransactionHandler)}.PersistOpenOrderTags(): " +
                $"status=saved storage_key={_storageKey} order_count={openOrderTags.Count}");
        }
    }

    private static string CreateFingerprint(IEnumerable<PersistedOrderTag> orderTags)
    {
        return string.Join(
            "\n",
            orderTags.Select(orderTag => string.Join(
                "\u001f",
                orderTag.SymbolIdentifier,
                orderTag.OrderType,
                orderTag.OrderDirection,
                orderTag.Tag,
                string.Join("\u001e", orderTag.BrokerageOrderIdentifiers))));
    }

    private static string FormatBrokerageOrderIdentifiers(IEnumerable<string> brokerageOrderIdentifiers)
    {
        var formattedBrokerageOrderIdentifiers = string.Join(",", brokerageOrderIdentifiers);
        return string.IsNullOrEmpty(formattedBrokerageOrderIdentifiers)
            ? "none"
            : formattedBrokerageOrderIdentifiers;
    }

    private sealed class PersistedOrderTagStore
    {
        public int Version { get; set; } = StorageVersion;
        public List<PersistedOrderTag> Orders { get; set; } = [];
    }

    private sealed class PersistedOrderTag
    {
        public List<string> BrokerageOrderIdentifiers { get; set; } = [];
        public string SymbolIdentifier { get; set; } = string.Empty;
        public OrderType OrderType { get; set; }
        public OrderDirection OrderDirection { get; set; }
        public string Tag { get; set; } = string.Empty;
    }
}
