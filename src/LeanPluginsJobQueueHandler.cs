using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Queues;

namespace QuantConnect.LeanPlugins;

public sealed class LeanPluginsJobQueueHandler : IJobQueueHandler
{
    private readonly JobQueue _defaultJobQueue = new();

    public void Initialize(IApi api, IMessagingHandler messagingHandler)
    {
        _defaultJobQueue.Initialize(api, messagingHandler);
    }

    public AlgorithmNodePacket NextJob(out string algorithmPath)
    {
        var algorithmJob = _defaultJobQueue.NextJob(out algorithmPath);
        var setupHandlerTypeName = algorithmJob is LiveNodePacket
            ? nameof(RestoredHoldingsSetupHandler)
            : null;
        var transactionHandlerTypeName = algorithmJob is LiveNodePacket
            ? nameof(PersistentOrderTagTransactionHandler)
            : null;
        if (setupHandlerTypeName != null)
        {
            SetCurrentEnvironmentConfiguration("setup-handler", setupHandlerTypeName);
        }
        if (transactionHandlerTypeName != null)
        {
            SetCurrentEnvironmentConfiguration("transaction-handler", transactionHandlerTypeName);
        }

        var resultHandlerTypeName = Config.Get("lean-plugins-result-handler");
        if (!string.IsNullOrWhiteSpace(resultHandlerTypeName))
        {
            SetCurrentEnvironmentConfiguration("result-handler", resultHandlerTypeName);
        }

        Log.Trace(
            $"{nameof(LeanPluginsJobQueueHandler)}.NextJob(): status=configured " +
            $"environment={Config.GetEnvironment()} " +
            $"setup_handler={Config.Get("setup-handler")} " +
            $"transaction_handler={Config.Get("transaction-handler")} " +
            $"result_handler={Config.Get("result-handler")}"
        );

        return algorithmJob;
    }

    private static void SetCurrentEnvironmentConfiguration(string configurationName, string value)
    {
        var environmentName = Config.GetEnvironment();
        Config.Set(
            string.IsNullOrWhiteSpace(environmentName)
                ? configurationName
                : $"{environmentName}.{configurationName}",
            value);
    }

    public void AcknowledgeJob(AlgorithmNodePacket algorithmJob)
    {
        _defaultJobQueue.AcknowledgeJob(algorithmJob);
    }
}
