# LEAN plugins

Standalone plugins loaded through LEAN's `plugin-directory` configuration.
They extend a stock LEAN image without modifying or rebuilding the engine.

## Plugins

- [`smtp-messaging`](smtp-messaging/README.md) sends local live-trading
  `self.notify.email(...)` notifications through SMTP.
