# SMTP messaging handler

An external LEAN `IMessagingHandler` that sends `NotificationEmail` messages
through SMTP. Other LEAN packets and notification types remain handled by the
default local messaging handler.

Email delivery runs on a dedicated background thread. Calling
`self.notify.email(...)` only queues the notification and does not wait for the
SMTP request to finish.

## Build

Build the plugin against the same LEAN image used for live trading:

```bash
docker run --rm \
  --volume "$PWD/smtp-messaging:/plugin" \
  --workdir /plugin \
  --entrypoint dotnet \
  quantconnect/lean:latest \
  build QuantConnect.SmtpMessaging.csproj --configuration Release
```

The deployable directory is `smtp-messaging/bin/Release/net10.0`. It contains
the plugin and its MailKit dependencies; it does not contain a LEAN build.

## Configuration

Mount the build output as a read-only plugin directory and mount the SMTP
password as a separate read-only file. Do not store the password in Git or in
LEAN project configuration.

```text
plugin-directory=/LeanPlugins
messaging-handler=QuantConnect.SmtpMessaging.SmtpMessagingHandler
smtp-notification-host=smtp.126.com
smtp-notification-port=465
smtp-notification-security=SslOnConnect
smtp-notification-username=sender@example.com
smtp-notification-from-address=sender@example.com
smtp-notification-password-file=/run/secrets/lean-smtp-password
```

Valid security values are MailKit `SecureSocketOptions` names such as
`SslOnConnect`, `StartTls`, `Auto`, and `None`.

For Lean CLI, pass these values with repeated `--extra-config KEY VALUE`
arguments. Mount the two host paths with `--extra-docker-config`:

```json
{
  "volumes": {
    "/host/path/to/net10.0": {
      "bind": "/LeanPlugins",
      "mode": "ro"
    },
    "/host/path/to/smtp-password": {
      "bind": "/run/secrets/lean-smtp-password",
      "mode": "ro"
    }
  }
}
```

The password file should contain only the SMTP authorization password and use
owner-only permissions.

## Algorithm usage

LEAN enables notifications only in live mode:

```python
accepted = self.notify.email(
    "recipient@example.com",
    "Order update",
    "The order was filled",
)
```

`accepted` means LEAN accepted the notification into its in-memory queue. Use
the engine log to verify delivery:

```text
SmtpMessagingHandler.SendNotification(): status=queued
SmtpMessagingHandler.ProcessEmailQueue(): status=sent
SmtpMessagingHandler.ProcessEmailQueue(): status=failed
```
