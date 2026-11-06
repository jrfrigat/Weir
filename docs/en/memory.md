# Memory and large responses

> [Русский](../ru/memory.md) - [Configuration](configuration.md) - [Transports](transports.md)

Weir reads result sets through `DbDataReader` with `SequentialAccess`, resolves column names and typed
getters once per result set, and writes UTF-8 JSON without materializing row objects. Dapper remains
in metadata/control-plane queries; it does not materialize data-plane result rows. Removing the package
would not remove the allocations made by database drivers.

## Choose how to deliver the response

For large exports, choose **Stream**, disable response caching and result capture on that endpoint,
and consume the HTTP body as a stream. The writer flushes bounded chunks and awaits network writes;
slow clients exert backpressure. Buffering proxies or a client that parses the whole body can still
hide that behavior. See [streaming through a proxy](deployment.md#streaming-through-the-proxy).

**Full** generates the envelope before handing it over. It supports a clean error before HTTP starts,
but retains the complete response. Full and cache fills use segmented pooled storage, then transmit
bounded chunks. A cached response also needs an owned immutable byte snapshot. Entry size, total local
payload, entry count, active buffers and pending stores each have explicit limits in
[configuration](configuration.md). Exceeding an active/body bound returns 503 before transmission;
after streaming starts, an execution failure aborts the response. An overlarge cache entry is delivered
but skipped by the cache. Limits do not silently rerun a procedure or switch Full into Stream.

HTTP, WebSocket and gRPC streaming share the processing pipeline. Unary gRPC requires a complete
protobuf payload and also participates in buffer admission. The admin endpoint test writes directly
from the engine without an additional full response copy. Prefer streaming gRPC for large results.

## Large values and remaining costs

The in-box connectors stream large declared text and binary values through pooled chunks. Small
declared strings may use `GetString`. PostgreSQL's raw UTF-8 path is used only with confirmed UTF-8
client encoding; other encodings use the character path. SQL Server's `GetChars` is synchronous because
ADO.NET has no asynchronous equivalent, with bounded reads and cancellation checks between chunks.
Provider-specific types that cannot safely use these paths retain typed/fallback handling.

`MaxBinaryBytes` caps emitted BLOB bytes. The unread remainder is drained in bounded chunks so driver
cleanup does not materialize it. Nulls, escaping, Unicode boundaries, Base64, result sets and output
parameters retain the response envelope contract. A truncated response is never cached.

This is allocation-light processing, not a promise of zero managed allocations or zero Gen 2 GC.
Drivers, requests, connections and supported fallback values allocate. Memory also depends on active
requests, driver/network buffers, runtime behavior and configured caches. A constant RSS independent
of unlimited concurrency is impossible. Active-buffer admission bounds the memory it owns; it is
not a hard process-RSS cap. Connection concurrency limits bound database pressure separately.

## Linux container memory

RSS includes native allocator pools and thread stacks as well as the managed heap and response bytes.
Linux can back a sparsely used thread stack with a 2 MiB transparent huge page. Native allocator arenas
can also retain free blocks after requests finish. Neither quantity is a live response payload.

The shipped glibc-based image sets
`GLIBC_TUNABLES=glibc.pthread.stack_hugetlb=0:glibc.malloc.arena_max=4` before starting .NET. This keeps
pthread stacks on small pages and limits native arenas to four. It preserves the runtime's choice of
GC heap pages and does not force collection or trim the working set. These are image startup defaults,
not runtime settings editable in the admin panel. Other hosts and libc implementations keep their own
defaults unless configured separately.

Overriding `GLIBC_TUNABLES` in Docker/Compose replaces the whole value. Include the stack and arena
items alongside other tunables when retaining these defaults. Benchmark a different arena count for
workloads or native plugins that need more allocator concurrency. This is not a process-RSS cap:
retain headroom for native/runtime pools and enforce response, cache, connection and container limits.

## Local cache or Redis

Memory is the default. It stores serialized UTF-8 bodies and bounds both payload bytes and entry count,
including tiny entries. Redis is optional and shares snapshots between instances; configure its
connection, namespace, timeout and TTL ceiling as described in [configuration](configuration.md).
Redis server memory is separate from Weir, but retrieving or sending a snapshot still uses local
memory. Configure Redis `maxmemory` and eviction. Purges fence late fills, and incomplete/truncated
responses are never published. Background stores have bounded count, bytes and deadlines.

The dashboard plots process RSS, local stored payload and total environment memory on one MiB axis. Payload is part of RSS,
not an additional quantity to add to it; cache metadata and free pools are excluded from that gauge.
Redis without a local layer reports zero local stored payload. Unknown/custom measurements are gaps.
Total memory is physical RAM constrained by the visible container/job memory limit, not remaining free
memory or a GC heap budget. Linux reads the smallest visible cgroup v1/v2 ancestor limit; Windows reads
physical RAM and current-job limits. Other processes can also consume this capacity. Hide Total memory
in the chart legend to refit the scale to RSS and cache. The total is unknown if a required OS query
or visible limit cannot be read; it is not replaced with zero. A separate GC heap hard limit does not
change this capacity line. Sampling never forces collection or trims the process working set.

## Reproduce a measurement

The sample client's `load` command drains measured responses with a reused 16 KiB buffer per worker.
Use `--skip-preflight` for large results: the normal preflight parses an envelope on the client.
Use `--read-delay 3` to delay each aggregate 16 KiB consumed, and `--cancel-after 1000` to cancel the
measured window after one second. Warm-up is separate. Latency samples and their final sorted copy
belong to the client, so client RAM is not a server-memory measurement. See [samples](../../samples/README.md).

An optional BenchmarkDotNet `MemoryDiagnoser` benchmark isolates serializer overhead from database
driver costs. It links the production writer and uses synthetic typed readers with reused or newly
materialized strings. It is not an HTTP throughput benchmark and does not run during ordinary tests:

```sh
dotnet run --project benchmarks/Weir.Benchmarks -c Release -- --filter '*Serialize*'
```

Measure the server separately on both target operating systems, with warm-up, representative column
types, concurrency, cache misses/hits, slow readers and cancellation. Compare allocated bytes with
allocated bytes and RSS with RSS. A short wall-clock run is a diagnostic, not a throughput guarantee.
