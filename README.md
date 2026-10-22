# LEAN plugins

Standalone plugins loaded through LEAN's `plugin-directory` configuration.
They extend a stock LEAN image without modifying or rebuilding the engine.

`SmtpMessagingHandler` sends local live-trading `self.notify.email(...)`
notifications through SMTP. When `smtp-runtime-error-recipient` is configured,
it also sends each LEAN `RuntimeErrorPacket` to that address before the live
process exits. Other LEAN packets and notification types remain handled by the
default local messaging handler. Email delivery runs on a dedicated background
thread, so the algorithm does not wait for SMTP I/O.

`LeanLogOnlyDebugLiveTradingResultHandler` keeps Debug and SystemDebug packets
in LEAN's `log.txt` while excluding them from `L-*-log.txt`. Log, handled error,
and runtime error messages remain in both logs.

`RestoredHoldingsSetupHandler` seeds brokerage holdings into LEAN's native
`TradeBuilder` after live setup. A later brokerage-side closing fill can then
produce a complete QC trade without applying the restored position to the
portfolio or cash book a second time.

`PersistentOrderTagTransactionHandler` stores tags for open live orders in the
project Object Store, using one file per concrete brokerage type and brokerage
order identifiers within that file. The stored brokerage name and type are
validated before restoration. During a restart it restores persisted tags
before LEAN creates order tickets, including when the brokerage supplies a
synthetic tag. It works with any brokerage that returns stable brokerage order
identifiers across restarts.

## Build and sync

Build all plugins on the Mac against the configured LEAN image:

```bash
make build
```

Run the plugin tests in the same image:

```bash
make test
```

The generic deployable directory is `bin/`. Sync it to the live server with:

```bash
make sync
```

Override `lean_engine_image`, `remote_host`, or `remote_plugin_directory` when
needed. No LEAN engine image is modified or rebuilt.

## Configuration

Mount the `bin/` directory as `/LeanPlugins` and mount the SMTP password as a
separate read-only file. Do not store the password in Git or LEAN project
configuration.

```text
plugin-directory=/LeanPlugins
job-queue-handler=QuantConnect.LeanPlugins.LeanPluginsJobQueueHandler
lean-plugins-result-handler=QuantConnect.LeanPlugins.LeanLogOnlyDebugLiveTradingResultHandler
messaging-handler=QuantConnect.LeanPlugins.SmtpMessagingHandler
smtp-notification-host=smtp.126.com
smtp-notification-port=465
smtp-notification-security=SslOnConnect
smtp-notification-username=sender@example.com
smtp-notification-from-address=sender@example.com
smtp-notification-password-file=/run/secrets/lean-smtp-password
smtp-runtime-error-recipient=recipient@example.com
```

Valid security values are MailKit `SecureSocketOptions` names such as
`SslOnConnect`, `StartTls`, `Auto`, and `None`.

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
PersistentOrderTagTransactionHandler.PersistOpenOrderTags(): status=saved
PersistentOrderTagTransactionHandler.RestoreOrderTag(): status=restored
PersistentOrderTagTransactionHandler.RestoreOrderTag(): status=missing
```
