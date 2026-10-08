using System.Collections.Concurrent;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Notifications;
using QuantConnect.Packets;

namespace QuantConnect.LeanPlugins;

public sealed class SmtpMessagingHandler : IMessagingHandler
{
    private const int EmailQueueCapacity = 100;
    private const int ShutdownTimeoutMilliseconds = 10_000;

    private readonly QuantConnect.Messaging.Messaging _defaultMessagingHandler = new();
    private BlockingCollection<(string Identifier, NotificationEmail Notification)>? _emailQueue;
    private Thread? _emailWorker;
    private string _smtpHost = string.Empty;
    private int _smtpPort;
    private SecureSocketOptions _smtpSecurity;
    private string _smtpUsername = string.Empty;
    private string _smtpPassword = string.Empty;
    private string _fromAddress = string.Empty;

    public bool HasSubscribers
    {
        get => _defaultMessagingHandler.HasSubscribers;
        set => _defaultMessagingHandler.HasSubscribers = value;
    }

    public void Initialize(MessagingHandlerInitializeParameters initializeParameters)
    {
        _defaultMessagingHandler.Initialize(initializeParameters);

        _smtpHost = GetRequiredConfiguration("smtp-notification-host");
        _smtpPort = Config.GetInt("smtp-notification-port", 465);
        _smtpUsername = GetRequiredConfiguration("smtp-notification-username");
        _fromAddress = Config.Get("smtp-notification-from-address", _smtpUsername);

        var passwordFilePath = GetRequiredConfiguration("smtp-notification-password-file");
        _smtpPassword = File.ReadAllText(passwordFilePath).Trim();
        if (string.IsNullOrWhiteSpace(_smtpPassword))
        {
            throw new InvalidOperationException($"SMTP password file is empty: {passwordFilePath}");
        }

        var smtpSecurityName = Config.Get("smtp-notification-security", nameof(SecureSocketOptions.SslOnConnect));
        if (!Enum.TryParse(smtpSecurityName, true, out _smtpSecurity))
        {
            throw new InvalidOperationException($"Unsupported smtp-notification-security value: {smtpSecurityName}");
        }

        _emailQueue = new BlockingCollection<(string Identifier, NotificationEmail Notification)>(EmailQueueCapacity);
        _emailWorker = new Thread(ProcessEmailQueue)
        {
            IsBackground = true,
            Name = nameof(SmtpMessagingHandler)
        };
        _emailWorker.Start();

        Log.Trace($"SmtpMessagingHandler.Initialize(): status=ready host={_smtpHost} port={_smtpPort} security={_smtpSecurity} from={_fromAddress}");
    }

    public void SetAuthentication(AlgorithmNodePacket job)
    {
        _defaultMessagingHandler.SetAuthentication(job);
    }

    public void Send(Packet packet)
    {
        _defaultMessagingHandler.Send(packet);
    }

    public void SendNotification(Notification notification)
    {
        if (notification is not NotificationEmail emailNotification)
        {
            _defaultMessagingHandler.SendNotification(notification);
            return;
        }

        var notificationIdentifier = Guid.NewGuid().ToString("N");
        if (_emailQueue == null || !_emailQueue.TryAdd((notificationIdentifier, emailNotification)))
        {
            Log.Error($"SmtpMessagingHandler.SendNotification(): status=queue_full id={notificationIdentifier} recipient={emailNotification.Address}");
            return;
        }

        Log.Trace($"SmtpMessagingHandler.SendNotification(): status=queued id={notificationIdentifier} recipient={emailNotification.Address}");
    }

    public void Dispose()
    {
        if (_emailQueue != null)
        {
            _emailQueue.CompleteAdding();
        }

        var workerStopped = _emailWorker == null || _emailWorker.Join(ShutdownTimeoutMilliseconds);
        if (!workerStopped)
        {
            Log.Error($"SmtpMessagingHandler.Dispose(): status=timeout pending={_emailQueue?.Count ?? 0}");
        }
        else
        {
            Log.Trace("SmtpMessagingHandler.Dispose(): status=complete");
        }

        if (workerStopped)
        {
            _emailQueue?.Dispose();
        }
        _defaultMessagingHandler.Dispose();
    }

    private void ProcessEmailQueue()
    {
        if (_emailQueue == null)
        {
            return;
        }

        foreach (var notificationItem in _emailQueue.GetConsumingEnumerable())
        {
            try
            {
                SendEmail(notificationItem.Notification);
                Log.Trace($"SmtpMessagingHandler.ProcessEmailQueue(): status=sent id={notificationItem.Identifier} recipient={notificationItem.Notification.Address}");
            }
            catch (Exception exception)
            {
                Log.Error(exception,
                    $"SmtpMessagingHandler.ProcessEmailQueue(): status=failed id={notificationItem.Identifier} recipient={notificationItem.Notification.Address}");
            }
        }
    }

    private void SendEmail(NotificationEmail emailNotification)
    {
        var emailMessage = new MimeMessage();
        emailMessage.From.Add(MailboxAddress.Parse(_fromAddress));
        emailMessage.To.Add(MailboxAddress.Parse(emailNotification.Address));
        emailMessage.Subject = emailNotification.Subject;

        var emailBody = new BodyBuilder
        {
            TextBody = emailNotification.Message
        };
        if (!string.IsNullOrEmpty(emailNotification.Data))
        {
            emailBody.Attachments.Add("attachment.txt", Encoding.UTF8.GetBytes(emailNotification.Data));
        }
        emailMessage.Body = emailBody.ToMessageBody();

        using var smtpClient = new SmtpClient();
        smtpClient.Connect(_smtpHost, _smtpPort, _smtpSecurity);
        smtpClient.Authenticate(_smtpUsername, _smtpPassword);
        smtpClient.Send(emailMessage);
        smtpClient.Disconnect(true);
    }

    private static string GetRequiredConfiguration(string configurationName)
    {
        var configurationValue = Config.Get(configurationName);
        if (string.IsNullOrWhiteSpace(configurationValue))
        {
            throw new InvalidOperationException($"Missing required LEAN configuration: {configurationName}");
        }

        return configurationValue;
    }
}
