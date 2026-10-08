using System.Globalization;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;

namespace QuantConnect.LeanPlugins;

public sealed class LeanLogOnlyDebugLiveTradingResultHandler : LiveTradingResultHandler, IResultHandler
{
    private readonly object _debugMessageExclusionsLock = new();
    private readonly Dictionary<string, int> _debugMessageExclusions = new(StringComparer.Ordinal);

    public override void Initialize(ResultHandlerInitializeParameters initializeParameters)
    {
        base.Initialize(initializeParameters);
        Log.Trace($"{nameof(LeanLogOnlyDebugLiveTradingResultHandler)}.Initialize(): status=ready");
    }

    public override void ProcessSynchronousEvents(bool forceProcess = false)
    {
        var queuedDebugMessages = Algorithm.DebugMessages.ToArray();
        lock (_debugMessageExclusionsLock)
        {
            foreach (var message in queuedDebugMessages)
            {
                _debugMessageExclusions.TryGetValue(message, out var exclusionCount);
                _debugMessageExclusions[message] = exclusionCount + 1;
            }
        }

        try
        {
            base.ProcessSynchronousEvents(forceProcess);
        }
        finally
        {
            foreach (var message in queuedDebugMessages)
            {
                TryConsumeDebugMessageExclusion(message);
            }
        }
    }

    public override void OrderEvent(OrderEvent orderEvent)
    {
        var brokerIdentifiers = string.Empty;
        var order = TransactionHandler.GetOrderById(orderEvent.OrderId);
        if (order != null && order.BrokerId.Count > 0)
        {
            brokerIdentifiers = string.Join(", ", order.BrokerId);
        }

        Log.Trace($"LiveTradingResultHandler.OrderEvent(): {orderEvent} BrokerId: {brokerIdentifiers}", true);
        Messages.Enqueue(new OrderEventPacket(AlgorithmId, orderEvent));
        EnqueueDebugPacket($"New Order Event: {orderEvent}");
    }

    void IResultHandler.DebugMessage(string message)
    {
        EnqueueDebugPacket(message);
    }

    void IResultHandler.SystemDebugMessage(string message)
    {
        message = FormatDebugMessage(message);
        Messages.Enqueue(new SystemDebugPacket(ProjectId, AlgorithmId, CompileId, message));
    }

    protected override void AddToLogStore(string message)
    {
        if (TryConsumeDebugMessageExclusion(message))
        {
            return;
        }

        base.AddToLogStore(message);
    }

    private void EnqueueDebugPacket(string message)
    {
        if (Messages.Count > 500)
        {
            return;
        }

        message = FormatDebugMessage(message);
        Messages.Enqueue(new DebugPacket(ProjectId, AlgorithmId, CompileId, message));
    }

    private string FormatDebugMessage(string message)
    {
        return Algorithm == null
            ? message
            : $"{Algorithm.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}";
    }

    private bool TryConsumeDebugMessageExclusion(string message)
    {
        lock (_debugMessageExclusionsLock)
        {
            if (!_debugMessageExclusions.TryGetValue(message, out var exclusionCount))
            {
                return false;
            }

            if (exclusionCount == 1)
            {
                _debugMessageExclusions.Remove(message);
            }
            else
            {
                _debugMessageExclusions[message] = exclusionCount - 1;
            }

            return true;
        }
    }
}
