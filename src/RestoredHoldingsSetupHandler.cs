using QuantConnect.Lean.Engine.Setup;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;

namespace QuantConnect.LeanPlugins;

public sealed class RestoredHoldingsSetupHandler : BrokerageSetupHandler, ISetupHandler
{
    bool ISetupHandler.Setup(SetupHandlerParameters parameters)
    {
        if (parameters.TransactionHandler is PersistentOrderTagTransactionHandler transactionHandler)
        {
            transactionHandler.ConfigurePersistence(parameters.Algorithm, parameters.Brokerage);
        }

        if (!base.Setup(parameters))
        {
            return false;
        }

        try
        {
            var restoredAtUtc = parameters.Algorithm.UtcTime;
            var restoredHoldings = parameters.Brokerage.GetAccountHoldings();

            for (var holdingIndex = 0; holdingIndex < restoredHoldings.Count; holdingIndex++)
            {
                var restoredHolding = restoredHoldings[holdingIndex];
                if (restoredHolding.Quantity == 0)
                {
                    continue;
                }

                var security = parameters.Algorithm.Securities[restoredHolding.Symbol];
                var restoredHoldingOrderId = -(holdingIndex + 1);
                var restoredHoldingFill = new OrderEvent(
                    restoredHoldingOrderId,
                    restoredHolding.Symbol,
                    restoredAtUtc,
                    OrderStatus.Filled,
                    restoredHolding.Quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell,
                    restoredHolding.AveragePrice,
                    restoredHolding.Quantity,
                    OrderFee.Zero,
                    "Restored brokerage holding");

                parameters.Algorithm.TradeBuilder.ProcessFill(
                    restoredHoldingFill,
                    restoredHolding.ConversionRate ?? security.QuoteCurrency.ConversionRate,
                    0m,
                    security.SymbolProperties.ContractMultiplier);

                Log.Trace(
                    $"{nameof(RestoredHoldingsSetupHandler)}.Setup(): status=seeded " +
                    $"symbol={restoredHolding.Symbol.Value} quantity={restoredHolding.Quantity} " +
                    $"average_price={restoredHolding.AveragePrice} order_id={restoredHoldingOrderId}");
            }

            return true;
        }
        catch (Exception exception)
        {
            Errors.Add(exception);
            Log.Error(
                exception,
                $"{nameof(RestoredHoldingsSetupHandler)}.Setup(): status=failed");
            return false;
        }
    }

}
