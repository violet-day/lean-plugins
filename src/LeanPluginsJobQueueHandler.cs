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
        var resultHandlerTypeName = Config.Get("lean-plugins-result-handler");
        if (!string.IsNullOrWhiteSpace(resultHandlerTypeName))
        {
            var environmentName = Config.GetEnvironment();
            var resultHandlerConfigurationName = string.IsNullOrWhiteSpace(environmentName)
                ? "result-handler"
                : $"{environmentName}.result-handler";
            Config.Set(resultHandlerConfigurationName, resultHandlerTypeName);
            Log.Trace(
                $"{nameof(LeanPluginsJobQueueHandler)}.NextJob(): status=configured " +
                $"environment={environmentName} result_handler={resultHandlerTypeName}"
            );
        }

        return algorithmJob;
    }

    public void AcknowledgeJob(AlgorithmNodePacket algorithmJob)
    {
        _defaultJobQueue.AcknowledgeJob(algorithmJob);
    }
}
