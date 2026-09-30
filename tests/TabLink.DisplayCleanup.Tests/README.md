# Display cleanup regression harness

Run `dotnet run --project tests/TabLink.DisplayCleanup.Tests -c Release`.

The project compiles the production `DisplaySessionAllocator.cs` and exercises
its actual asynchronous acquire/release paths. Display discovery, topology
mutation and session guards are deterministic in-memory doubles. The executable
cannot invoke Windows display APIs, write guard files, launch watchers or touch
the running TabLink application. No administrator permission is required.

Cases enforce the single-display contract: a second device is rejected before
driver or display work, the only slot is reusable only after cleanup, failed
cleanup blocks every replacement, stale/double disposal is harmless, and
release/reconnect and cancellation serialize through the allocator gate.

The fake `ISingleDisplayDriverController` also proves driver preparation runs
once before target discovery, normal disconnect detaches the display before
removing the owned device, failures and post-helper cancellation roll back the
temporary device, and a failed removal is retried before another installation.
These verify allocator/lifecycle orchestration; real `SessionGuard` file-marker
behavior belongs to the display lifecycle tests.
