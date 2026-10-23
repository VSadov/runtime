# Thread pool external-work recency

The portable thread pool uses recent external worker requests to decide whether
an empty dispatch should spin before parking. An external request is a request
from a thread without work-queue dispatcher thread-local state, including an I/O
completion poller. Requests are recorded even when another request is already
outstanding.

The recency window is independent of the dispatch quantum:

| Setting | Value |
| --- | --- |
| Environment variable | `DOTNET_ThreadPool_SpuriousDispatchExternalWorkItemRecencyMs` |
| AppContext setting | `System.Threading.ThreadPool.SpuriousDispatchExternalWorkItemRecencyMs` |
| Default | `30` milliseconds |
| Valid values | `-1` through `Int32.MaxValue` |

The environment variable takes precedence over AppContext. The setting is read
when the worker-thread configuration is initialized; set it before starting the
process.

- A nonnegative value is the maximum age, in milliseconds, considered recent.
  The recency criterion parks an empty dispatch only when the signed elapsed time
  is **greater than** this value. An age equal to the window remains recent.
- `0` removes the positive-age history window. A same-tick request, or a request
  newer than the dispatch clock sample, still counts as recent.
- `-1` disables only the recency criterion. The existing processing-worker-count
  threshold still applies, and external-request timestamp bookkeeping remains
  enabled. This is not equivalent to removing the change entirely.
- Values below `-1` use the default window. Malformed or overflowing values use
  the existing configuration helper's AppContext/default fallback.

This is a best-effort scheduling hint, not a delay imposed on queued work. It
does not replace the thread pool's work-request or wakeup protocol and does not
apply when the Windows thread pool implementation is selected.

## Parameter sweeps

Use unpadded decimal values such as `-1`, `0`, `1`, `5`, `10`, `30`, and `100`.
The configuration helper also recognizes `0x` hexadecimal and leading-zero octal
strings.

For example, in PowerShell:

```powershell
$env:DOTNET_ThreadPool_SpuriousDispatchExternalWorkItemRecencyMs = '10'
# Start a fresh benchmark process.
```

For Crank, configure the application job rather than only the controller shell:

```text
--application.environmentVariables DOTNET_ThreadPool_SpuriousDispatchExternalWorkItemRecencyMs=10
```

Set the same value on `warmup` and `load` when the experiment also varies a
managed client's thread pool. Keep binaries, workload settings, and unrelated
configuration fixed; restart the processes for each value. Measure CPU cost,
throughput, and burst/idle latency rather than throughput alone.

A sweep requires binaries built with this setting. Existing frozen binaries
without the setting do not exercise it.
