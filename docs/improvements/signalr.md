# SignalR — Broadcaster Lifetime Concerns

`SignalRNotificationBroadcaster` is wired via `INotificationBroadcaster` interface. Works but the broadcaster is a singleton while the hub context is scoped per connection. Should verify no lifetime issues.
