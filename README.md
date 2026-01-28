# Akka.NET ThreadPool Starvation Demo

This project demonstrates how ThreadPool starvation caused by sync-over-async blocking patterns affects Akka.NET cluster health. It uses .NET Aspire for orchestration with a full observability stack (Seq, Prometheus, Grafana) and **dotnet-monitor for automated diagnostic capture**.

## The Problem

When actors use blocking calls like `.Result` or `.Wait()` on async operations, they hold ThreadPool threads hostage. Under load, this exhausts the ThreadPool, causing:

- Cluster heartbeats to fail (nodes marked UNREACHABLE)
- Split Brain Resolver to down nodes
- Application crashes or hangs
- Timeouts on all operations

This is a common anti-pattern that's difficult to diagnose without proper tooling.

## Key Features

- **Automated diagnostic capture** with dotnet-monitor when starvation is detected
- **Real-time ThreadPool monitoring** via dedicated monitoring thread (survives starvation)
- **Multiple trigger scenarios**: ThreadPool starvation, lock contention, GC pressure, thread explosion
- **Full observability stack**: Seq (logs/traces), Prometheus (metrics), Grafana (dashboards)

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        .NET Aspire                                   │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────────┐       │
│  │   AkkaApp    │    │ ObserverApp  │    │  Azure Storage   │       │
│  │  (worker)    │◄──►│  (observer)  │◄──►│    Emulator      │       │
│  │              │    │              │    │  (Discovery)     │       │
│  │ Blocking     │    │ Cluster      │    └──────────────────┘       │
│  │ Actors       │    │ Observer     │                               │
│  └──────┬───────┘    └──────┬───────┘                               │
│         │                   │                                        │
│         │ Named Pipe        └─────────┬─────────┐                   │
│         ▼                             │ OTLP    │                   │
│  ┌──────────────┐                     ▼         │                   │
│  │dotnet-monitor│           ┌─────────────────┐ │                   │
│  │  (separate)  │           │  OTEL Collector │ │                   │
│  │              │           └────────┬────────┘ │                   │
│  │ Triggers:    │                    │          │                   │
│  │ - Starvation │           ┌────────┴────────┐ │                   │
│  │ - GC Pressure│           ▼                 ▼ │                   │
│  │ - Contention │    ┌─────────────┐   ┌─────────────┐             │
│  └──────────────┘    │     Seq     │   │ Prometheus  │◄─────┐      │
│         │            │  (Logs &    │   │  (Metrics)  │      │      │
│         ▼            │   Traces)   │   └─────────────┘      │      │
│  ┌──────────────┐    └─────────────┘          │      ┌──────┴──────┐
│  │  Artifacts   │                             └─────►│   Grafana   │
│  │ .nettrace    │                                    │ (Dashboards)│
│  │ .dmp files   │                                    └─────────────┘
│  └──────────────┘                                                   │
└─────────────────────────────────────────────────────────────────────┘
```

### Components

| Component                  | Role            | Description                                                                |
|----------------------------|-----------------|----------------------------------------------------------------------------|
| **AkkaApp**                | `worker`        | Hosts blocking actors that cause ThreadPool starvation (full telemetry)    |
| **ObserverApp**            | `observer`      | Joins cluster without blocking actors, observes cluster health (logs only) |
| **dotnet-monitor**         | Diagnostics     | Automated trace/dump capture when starvation detected (run separately)     |
| **Azure Storage Emulator** | Discovery       | Akka.Discovery.Azure for cluster node discovery                            |
| **OTEL Collector**         | Telemetry       | Routes traces, metrics, and logs to backends                               |
| **Seq**                    | Logging/Tracing | Centralized log aggregation and trace viewing                              |
| **Prometheus**             | Metrics         | Time-series metrics collection                                             |
| **Grafana**                | Dashboards      | Visualization and alerting                                                 |

## Project Structure

```
AkkaAndDotNetMonitor/
├── src/
│   ├── AkkaApp/                        # Worker node with blocking actors
│   │   ├── Program.cs                  # ASP.NET Core + Akka.Cluster setup
│   │   ├── BlockingOrderActor.cs       # Actor using .Result (THE BUG)
│   │   ├── SlowServiceActor.cs         # Simulates slow downstream service (non-blocking)
│   │   ├── ThreadPoolMonitorService.cs # Dedicated monitoring thread (survives starvation)
│   │   ├── Messages.cs                 # Message types
│   │   ├── Configuration/              # Akka options binding
│   │   └── appsettings.json            # Akka cluster configuration
│   ├── ObserverApp/                    # Observer node (no blocking actors)
│   │   ├── Program.cs                  # ASP.NET Core + Akka.Cluster setup
│   │   ├── Configuration/              # Akka options binding
│   │   └── appsettings.json            # Akka cluster configuration
│   ├── AkkaApp.Aspire/                 # Aspire orchestration
│   │   └── Program.cs                  # Service definitions
│   ├── prometheus/                     # Prometheus configuration
│   │   └── prometheus.yaml
│   ├── grafana/                        # Grafana provisioning
│   │   ├── config/
│   │   └── dashboards/
│   └── otel_collector/                 # OTEL Collector configuration
│       └── config.yaml
├── settings.json                       # dotnet-monitor collection rules configuration
└── README.md
```

## Prerequisites

1. **.NET 8 SDK** or later
2. **Docker Desktop** (for Aspire containers)
3. **dotnet-monitor** (for automated diagnostic capture)
   ```bash
   dotnet tool install -g dotnet-monitor
   ```

## Running the Demo

### Step 1: Start dotnet-monitor (Terminal 1)

**Important:** Start dotnet-monitor BEFORE Aspire. The app will wait for dotnet-monitor to connect.

```bash
dotnet-monitor collect --no-auth --configuration-file-path settings.json
```

You should see:
```
info: Microsoft.Diagnostics.Tools.Monitor.DiagnosticPortServer[0]
      Listening on pipe: dotnet-monitor-pipe
```

### Step 2: Start Aspire (Terminal 2)

```bash
dotnet run --project src/AkkaApp.Aspire
```

This starts all components:
- AkkaApp (worker node with blocking actors)
- ObserverApp (observer node)
- Azure Storage Emulator
- Seq, Prometheus, Grafana
- OTEL Collector

The Aspire dashboard opens automatically at `https://localhost:17225`.

When AkkaApp connects, dotnet-monitor will show:
```
info: Microsoft.Diagnostics.Tools.Monitor.HostBuilder[0]
      Process 12345 connected.
```

### Access Points

| Service              | URL                             | Description               |
|----------------------|---------------------------------|---------------------------|
| **Aspire Dashboard** | https://localhost:17225         | Orchestration overview    |
| **AkkaApp**          | http://localhost:5080           | Worker node endpoints     |
| **ObserverApp**      | http://localhost:5081           | Observer node endpoints   |
| **Seq**              | http://localhost:5341           | Logs and traces           |
| **Grafana**          | http://localhost:3000           | Dashboards (admin/admin)  |
| **Prometheus**       | http://localhost:9090           | Metrics                   |
| **Artifacts**        | C:\tmp\dotnet-monitor\artifacts | Captured traces and dumps |

## dotnet-monitor Collection Rules

The `settings.json` configures automatic diagnostic capture for various scenarios:

| Rule                   | Trigger          | Condition                    | Captures                  |
|------------------------|------------------|------------------------------|---------------------------|
| `Startup`              | App connects     | Immediately                  | 5s CPU trace (baseline)   |
| `ThreadPoolStarvation` | Throughput drops | < 1 item/sec for 2s          | 10s CPU trace + Full dump |
| `HighLockContention`   | Lock contention  | > 100 contentions/sec for 3s | 10s CPU trace             |
| `GCPressure`           | GC overhead      | > 50% time in GC for 5s      | 10s CPU trace + GC dump   |
| `ThreadExplosion`      | Thread growth    | > 100 threads for 5s         | 10s CPU trace + Full dump |

### Analyzing Captured Artifacts

**Trace files (.nettrace):**

The `.nettrace` format contains CPU samples with call stacks over time - ideal for identifying blocking patterns.

```bash
# Option 1: PerfView (recommended for Windows)
# Download from https://github.com/microsoft/perfview/releases
perfview C:\tmp\dotnet-monitor\artifacts\trace.nettrace
# Navigate to: Advanced Group → Thread Time Stacks → Look for blocked threads

# Option 2: Convert to Speedscope (browser-based, cross-platform)
dotnet-trace convert C:\tmp\dotnet-monitor\artifacts\trace.nettrace --format speedscope
# Opens https://speedscope.app and load the .speedscope.json file

# Option 3: Visual Studio
# Double-click the .nettrace file to open in Performance Profiler

# Option 4: Convert to Chromium format
dotnet-trace convert C:\tmp\dotnet-monitor\artifacts\trace.nettrace --format chromium
# Open chrome://tracing in Chrome and load the file
```

**What to look for in traces:**
- Multiple `.NET TP Worker` threads with identical stacks = ThreadPool starvation
- `Task.GetResultCore` or `SpinThenBlockingWait` in stacks = sync-over-async blocking
- `Monitor.Enter` / `Monitor.ReliableEnter` = lock contention
- Threads spending time in `Thread.Sleep` = explicit blocking

**Dump files (.dmp):**
```bash
# Analyze with dotnet-dump
dotnet-dump analyze C:\tmp\dotnet-monitor\artifacts\dump.dmp

# Inside dotnet-dump, get all thread stacks:
> clrstack -all

# Or open in Visual Studio / WinDbg for full analysis
```

## Triggering ThreadPool Starvation

### Step 1: Check Initial State

```bash
# Check AkkaApp status
curl http://localhost:5080/

# Check cluster membership
curl http://localhost:5080/cluster

# Check ThreadPool status
curl http://localhost:5080/threadpool
```

### Step 2: Trigger Load

```bash
# Trigger blocking operations (uses default count = ProcessorCount * 4)
curl http://localhost:5080/trigger

# Or specify a count
curl "http://localhost:5080/trigger?count=100"
```

### Step 3: Observe the Starvation

**In dotnet-monitor (Terminal 1):**
```
info: CollectionRuleService[31]
      Collection rule 'ThreadPoolStarvation' started.
info: CollectionRuleService[32]
      Collecting trace for 10 seconds...
info: CollectionRuleService[33]
      Collecting dump...
```

**In Seq logs:**
- `[ThreadPoolMonitor] STARVATION DETECTED!` messages
- Cluster members becoming unreachable
- Heartbeat failures

**In Grafana:**
- ThreadPool thread count at max
- Work items completed dropping to 0
- Queue length may stay at 0 (work is blocked ON threads, not queued)

**Artifacts captured:**
```bash
dir C:\tmp\dotnet-monitor\artifacts
# Shows .nettrace and .dmp files with timestamps
```

## API Endpoints

### AkkaApp (Worker Node)

| Method | Endpoint           | Description                                                 |
|--------|--------------------|-------------------------------------------------------------|
| GET    | `/`                | Node status, processor count, thread config                 |
| GET    | `/cluster`         | Cluster membership and leader info                          |
| GET    | `/threadpool`      | ThreadPool worker/IO thread statistics                      |
| GET    | `/trigger?count=N` | Trigger N blocking operations (default: ProcessorCount * 4) |
| GET    | `/capture-stacks`  | Manual trigger for dotnet-monitor stack capture             |
| GET    | `/metrics`         | Prometheus metrics endpoint                                 |

### ObserverApp (Observer Node)

| Method | Endpoint      | Description                        |
|--------|---------------|------------------------------------|
| GET    | `/`           | Node status                        |
| GET    | `/cluster`    | Cluster membership and leader info |
| GET    | `/threadpool` | ThreadPool statistics              |

## How Starvation Works

The demo dynamically scales based on your CPU:

```csharp
var maxThreads = Environment.ProcessorCount;
var blockingActorCount = Environment.ProcessorCount * 2;
ThreadPool.SetMinThreads(Math.Max(4, maxThreads / 4), Math.Max(4, maxThreads / 4));
ThreadPool.SetMaxThreads(maxThreads, maxThreads);
```

- **ThreadPool max threads** = `ProcessorCount`
- **Blocking actors** = `ProcessorCount * 2` (always more than available threads)

When `/trigger` is called, each blocking actor uses `.Result` on an Ask call, holding a ThreadPool thread hostage while waiting. Since there are more actors than threads, starvation is guaranteed.

### The SlowServiceActor Design

The `SlowServiceActor` uses `ScheduleTellOnce` to respond after a delay **without blocking its thread**:

```csharp
Receive<ValidateOrder>(msg =>
{
    var sender = Sender;
    // Non-blocking delay - allows many concurrent requests in-flight
    Context.System.Scheduler.ScheduleTellOnce(
        TimeSpan.FromMilliseconds(500),
        sender,
        new ValidationResult(msg.OrderId, IsValid: true),
        Self);
});
```

This allows multiple `BlockingOrderActor` instances to have concurrent Ask operations in-flight, all blocking their threads with `.Result`, which creates true ThreadPool starvation.

### ThreadPoolMonitorService

A dedicated monitoring service runs on its **own thread** (not the ThreadPool), allowing it to detect and log starvation even when the ThreadPool is completely blocked:

```csharp
// Runs on dedicated thread, survives ThreadPool starvation
var monitorThread = new Thread(() => MonitorLoop(stoppingToken))
{
    Name = "ThreadPoolMonitor",
    IsBackground = true,
    Priority = ThreadPriority.AboveNormal
};
```

This service:
- Measures ThreadPool queue delay in real-time
- Logs warnings when starvation is detected
- Continues reporting to Seq even during complete starvation
- Uses `Thread.Sleep` instead of `Task.Delay` to avoid ThreadPool dependency

## Detectable Blocking Scenarios

The dotnet-monitor configuration detects multiple blocking patterns:

| Scenario             | Root Cause              | Detection Metric        | What to Look For in Traces                   |
|----------------------|-------------------------|-------------------------|----------------------------------------------|
| **Sync-over-async**  | `.Result`, `.Wait()`    | Throughput drops to 0   | `Task.GetResultCore`, `SpinThenBlockingWait` |
| **Lock contention**  | `lock`, `Monitor.Enter` | High contention count   | `Monitor.Enter`, `Monitor.ReliableEnter`     |
| **GC pressure**      | Memory allocation       | Time in GC > 50%        | GC events, allocation stacks                 |
| **Thread explosion** | Unbounded pool growth   | Thread count > 100      | Many `.NET TP Worker` threads                |
| **Database blocks**  | Sync DB calls           | Same as sync-over-async | `SqlCommand.ExecuteReader`                   |
| **I/O blocks**       | Sync file/network ops   | Same as sync-over-async | `FileStream.Read`, `Socket.Receive`          |

**Note:** Disk I/O saturation is OS-level and not directly detectable via .NET counters. Use infrastructure monitoring for disk issues.

## Fixing the Code

### Before (Blocking)

```csharp
public class BlockingOrderActor : ReceiveActor
{
    public BlockingOrderActor(IActorRef slowService)
    {
        Receive<ProcessOrder>(msg =>
        {
            // BLOCKS ThreadPool thread!
            var validation = slowService.Ask<ValidationResult>(
                new ValidateOrder(msg.OrderId)).Result;

            Sender.Tell(new OrderResult(msg.OrderId, true));
        });
    }
}
```

### After (Non-Blocking)

```csharp
public class NonBlockingOrderActor : ReceiveActor
{
    public NonBlockingOrderActor(IActorRef slowService)
    {
        ReceiveAsync<ProcessOrder>(async msg =>
        {
            // Releases thread while waiting
            var validation = await slowService.Ask<ValidationResult>(
                new ValidateOrder(msg.OrderId));

            Sender.Tell(new OrderResult(msg.OrderId, true));
        });
    }
}
```

## Blocking Patterns Reference

### 1. Sync-Over-Async (Most Common)

These patterns block a thread while waiting for an async operation to complete.

| Pattern                         | Stack Signature         | Fix                         |
|---------------------------------|-------------------------|-----------------------------|
| `task.Result`                   | `Task`1.GetResultCore`  | `await task`                |
| `task.Wait()`                   | `Task.InternalWaitCore` | `await task`                |
| `task.GetAwaiter().GetResult()` | `Task`1.GetResultCore`  | `await task`                |
| `Task.WaitAll(tasks)`           | `Task.WaitAllCore`      | `await Task.WhenAll(tasks)` |
| `Task.WaitAny(tasks)`           | `Task.WaitAnyCore`      | `await Task.WhenAny(tasks)` |

**Example stack trace:**
```
System.Threading.ManualResetEventSlim.Wait
System.Threading.Tasks.Task.SpinThenBlockingWait
System.Threading.Tasks.Task.InternalWaitCore
System.Threading.Tasks.Task`1.GetResultCore        ← HERE
YourNamespace.YourActor.<Handler>b__0
```

### 2. Thread.Sleep

| Pattern                  | Stack Signature                 | Fix                          |
|--------------------------|---------------------------------|------------------------------|
| `Thread.Sleep(ms)`       | `System.Threading.Thread.Sleep` | `await Task.Delay(ms)`       |
| `Thread.Sleep(TimeSpan)` | `System.Threading.Thread.Sleep` | `await Task.Delay(TimeSpan)` |

**Example stack trace:**
```
System.Threading.Thread.Sleep
YourNamespace.YourActor.HandleMessage
```

### 3. Database Operations (ADO.NET)

| Pattern                     | Stack Signature              | Fix                                    |
|-----------------------------|------------------------------|----------------------------------------|
| `command.ExecuteReader()`   | `SqlCommand.ExecuteReader`   | `await command.ExecuteReaderAsync()`   |
| `command.ExecuteNonQuery()` | `SqlCommand.ExecuteNonQuery` | `await command.ExecuteNonQueryAsync()` |
| `command.ExecuteScalar()`   | `SqlCommand.ExecuteScalar`   | `await command.ExecuteScalarAsync()`   |
| `reader.Read()`             | `SqlDataReader.Read`         | `await reader.ReadAsync()`             |
| `connection.Open()`         | `SqlConnection.Open`         | `await connection.OpenAsync()`         |

**Example stack trace:**
```
System.Data.SqlClient.SqlCommand.ExecuteReader
System.Data.SqlClient.SqlCommand.ExecuteReader
YourNamespace.YourActor.QueryDatabase
```

### 4. Entity Framework

| Pattern                 | Stack Signature                    | Fix                                |
|-------------------------|------------------------------------|------------------------------------|
| `context.SaveChanges()` | `DbContext.SaveChanges`            | `await context.SaveChangesAsync()` |
| `query.ToList()`        | `Enumerable.ToList` + EF internals | `await query.ToListAsync()`        |
| `query.First()`         | `Queryable.First`                  | `await query.FirstAsync()`         |
| `query.Single()`        | `Queryable.Single`                 | `await query.SingleAsync()`        |
| `query.Count()`         | `Queryable.Count`                  | `await query.CountAsync()`         |
| `query.Any()`           | `Queryable.Any`                    | `await query.AnyAsync()`           |

**Example stack trace:**
```
Microsoft.EntityFrameworkCore.Storage.RelationalCommand.ExecuteReader
Microsoft.EntityFrameworkCore.Query.Internal.QueryingEnumerable`1.Enumerator.MoveNext
System.Linq.Enumerable.ToList
YourNamespace.YourActor.LoadData
```

### 5. Dapper

| Pattern                      | Stack Signature        | Fix                                     |
|------------------------------|------------------------|-----------------------------------------|
| `connection.Query<T>()`      | `SqlMapper.Query`      | `await connection.QueryAsync<T>()`      |
| `connection.Execute()`       | `SqlMapper.Execute`    | `await connection.ExecuteAsync()`       |
| `connection.QueryFirst<T>()` | `SqlMapper.QueryFirst` | `await connection.QueryFirstAsync<T>()` |

### 6. File I/O

| Pattern                    | Stack Signature                | Fix                                   |
|----------------------------|--------------------------------|---------------------------------------|
| `File.ReadAllText()`       | `System.IO.File.ReadAllText`   | `await File.ReadAllTextAsync()`       |
| `File.ReadAllBytes()`      | `System.IO.File.ReadAllBytes`  | `await File.ReadAllBytesAsync()`      |
| `File.ReadAllLines()`      | `System.IO.File.ReadAllLines`  | `await File.ReadAllLinesAsync()`      |
| `File.WriteAllText()`      | `System.IO.File.WriteAllText`  | `await File.WriteAllTextAsync()`      |
| `File.WriteAllBytes()`     | `System.IO.File.WriteAllBytes` | `await File.WriteAllBytesAsync()`     |
| `File.Copy()`              | `System.IO.File.Copy`          | Use streams with async                |
| `stream.Read()`            | `FileStream.Read`              | `await stream.ReadAsync()`            |
| `stream.Write()`           | `FileStream.Write`             | `await stream.WriteAsync()`           |
| `streamReader.ReadToEnd()` | `StreamReader.ReadToEnd`       | `await streamReader.ReadToEndAsync()` |
| `streamReader.ReadLine()`  | `StreamReader.ReadLine`        | `await streamReader.ReadLineAsync()`  |

**Example stack trace:**
```
System.IO.File.ReadAllText
System.IO.File.ReadAllText
YourNamespace.YourActor.LoadConfiguration
```

### 7. HTTP Operations

| Pattern                        | Stack Signature              | Fix                                  |
|--------------------------------|------------------------------|--------------------------------------|
| `httpClient.Send()`            | `HttpClient.Send`            | `await httpClient.SendAsync()`       |
| `HttpWebRequest.GetResponse()` | `HttpWebRequest.GetResponse` | Use `HttpClient` with async          |
| `WebClient.DownloadString()`   | `WebClient.DownloadString`   | Use `HttpClient.GetStringAsync()`    |
| `WebClient.DownloadData()`     | `WebClient.DownloadData`     | Use `HttpClient.GetByteArrayAsync()` |
| `WebClient.UploadString()`     | `WebClient.UploadString`     | Use `HttpClient.PostAsync()`         |

**Example stack trace:**
```
System.Net.HttpWebRequest.GetResponse
YourNamespace.YourActor.CallExternalApi
```

### 8. gRPC Operations

| Pattern                              | Stack Signature | Fix                      |
|--------------------------------------|-----------------|--------------------------|
| `client.SyncMethod()`                | gRPC sync call  | Use async client methods |
| `asyncCall.GetAwaiter().GetResult()` | `GetResultCore` | `await asyncCall`        |

### 9. Locking and Synchronization Primitives

| Pattern                         | Stack Signature                          | Fix                                           |
|---------------------------------|------------------------------------------|-----------------------------------------------|
| `lock` contention               | `Monitor.Enter`, `Monitor.ReliableEnter` | Reduce lock scope, use async patterns         |
| `Monitor.Wait()`                | `Monitor.Wait`                           | Use `SemaphoreSlim` with async                |
| `Mutex.WaitOne()`               | `Mutex.WaitOne`                          | Use `SemaphoreSlim.WaitAsync()`               |
| `Semaphore.WaitOne()`           | `Semaphore.WaitOne`                      | Use `SemaphoreSlim.WaitAsync()`               |
| `SemaphoreSlim.Wait()`          | `SemaphoreSlim.Wait`                     | `await semaphoreSlim.WaitAsync()`             |
| `ManualResetEvent.WaitOne()`    | `ManualResetEvent.WaitOne`               | Use `SemaphoreSlim` or `TaskCompletionSource` |
| `AutoResetEvent.WaitOne()`      | `AutoResetEvent.WaitOne`                 | Use `SemaphoreSlim` or `TaskCompletionSource` |
| `CountdownEvent.Wait()`         | `CountdownEvent.Wait`                    | Use async coordination patterns               |
| `Barrier.SignalAndWait()`       | `Barrier.SignalAndWait`                  | Use async coordination patterns               |
| `SpinWait.SpinUntil()`          | `SpinWait.SpinUntil`                     | Avoid in async code                           |
| `ReaderWriterLock.AcquireX()`   | `ReaderWriterLock.AcquireReaderLock`     | Use `ReaderWriterLockSlim` or async patterns  |
| `ReaderWriterLockSlim.EnterX()` | `ReaderWriterLockSlim.EnterReadLock`     | Consider async alternatives                   |

**Example stack trace:**
```
System.Threading.Monitor.Enter
System.Threading.Monitor.ReliableEnter
YourNamespace.YourActor.ProcessWithLock
```

### 10. Blocking Collections

| Pattern                                        | Stack Signature                | Fix                         |
|------------------------------------------------|--------------------------------|-----------------------------|
| `BlockingCollection<T>.Take()`                 | `BlockingCollection`1.Take`    | Use `Channel<T>` with async |
| `BlockingCollection<T>.Add()`                  | `BlockingCollection`1.Add`     | Use `Channel<T>` with async |
| `BlockingCollection<T>.TryTake()` with timeout | `BlockingCollection`1.TryTake` | Use `Channel<T>`            |
| `ConcurrentQueue` with spin-wait               | `SpinWait` patterns            | Use `Channel<T>`            |

**Example stack trace:**
```
System.Collections.Concurrent.BlockingCollection`1.Take
System.Collections.Concurrent.BlockingCollection`1.TryTakeWithNoTimeValidation
YourNamespace.YourActor.ConsumeFromQueue
```

### 11. Process and External Commands

| Pattern                              | Stack Signature          | Fix                                             |
|--------------------------------------|--------------------------|-------------------------------------------------|
| `Process.WaitForExit()`              | `Process.WaitForExit`    | `await process.WaitForExitAsync()`              |
| `Process.Start()` + sync wait        | `Process.WaitForExit`    | Use async patterns                              |
| `process.StandardOutput.ReadToEnd()` | `StreamReader.ReadToEnd` | `await process.StandardOutput.ReadToEndAsync()` |

**Example stack trace:**
```
System.Diagnostics.Process.WaitForExit
YourNamespace.YourActor.RunExternalTool
```

### 12. Console I/O

| Pattern              | Stack Signature    | Fix                            |
|----------------------|--------------------|--------------------------------|
| `Console.ReadLine()` | `Console.ReadLine` | Avoid in actors; use messaging |
| `Console.ReadKey()`  | `Console.ReadKey`  | Avoid in actors; use messaging |
| `Console.Read()`     | `Console.Read`     | Avoid in actors; use messaging |

**Note**: Console I/O should never be done inside actors. Use a dedicated console handler that sends messages to actors.

### 13. Network Operations

| Pattern                  | Stack Signature        | Fix                                 |
|--------------------------|------------------------|-------------------------------------|
| `socket.Connect()`       | `Socket.Connect`       | `await socket.ConnectAsync()`       |
| `socket.Send()`          | `Socket.Send`          | `await socket.SendAsync()`          |
| `socket.Receive()`       | `Socket.Receive`       | `await socket.ReceiveAsync()`       |
| `tcpClient.Connect()`    | `TcpClient.Connect`    | `await tcpClient.ConnectAsync()`    |
| `networkStream.Read()`   | `NetworkStream.Read`   | `await networkStream.ReadAsync()`   |
| `networkStream.Write()`  | `NetworkStream.Write`  | `await networkStream.WriteAsync()`  |
| `Dns.GetHostEntry()`     | `Dns.GetHostEntry`     | `await Dns.GetHostEntryAsync()`     |
| `Dns.GetHostAddresses()` | `Dns.GetHostAddresses` | `await Dns.GetHostAddressesAsync()` |

### 14. Serialization (Can Be CPU-Bound But Blocking)

| Pattern                                      | Stack Signature              | Fix                                    |
|----------------------------------------------|------------------------------|----------------------------------------|
| `JsonSerializer.Deserialize()` on large data | `JsonSerializer.Deserialize` | Offload to background or use streaming |
| `XmlSerializer.Deserialize()`                | `XmlSerializer.Deserialize`  | Consider async streams                 |

### 15. Akka.NET Specific Patterns

| Pattern                              | Stack Signature                    | Fix                                      |
|--------------------------------------|------------------------------------|------------------------------------------|
| `Ask<T>().Result`                    | `GetResultCore` + `FutureActorRef` | Use `ReceiveAsync` with `await Ask<T>()` |
| `Ask<T>().Wait()`                    | `InternalWaitCore`                 | Use `ReceiveAsync` with `await Ask<T>()` |
| `ActorSelection.ResolveOne().Result` | `GetResultCore`                    | `await selection.ResolveOne()`           |
| `GracefulStop().Result`              | `GetResultCore`                    | `await actor.GracefulStop()`             |
| Blocking in `PreStart()`             | Various                            | Move to async initialization message     |
| Blocking in `PostStop()`             | Various                            | Use async cleanup patterns               |
| Blocking in `PreRestart()`           | Various                            | Keep minimal, avoid I/O                  |
| `Inbox.Receive()`                    | `Inbox.Receive`                    | Use `Ask` pattern or `ReceiveAsync`      |

**Example stack trace (Ask blocking):**
```
System.Threading.ManualResetEventSlim.Wait
System.Threading.Tasks.Task.SpinThenBlockingWait
System.Threading.Tasks.Task`1.GetResultCore
YourNamespace.YourActor.<.ctor>b__1_0              ← Actor handler
Akka.Actor.ReceiveActor.ExecutePartialMessageHandler
```

### 16. Compression Operations

| Pattern                     | Stack Signature      | Fix                               |
|-----------------------------|----------------------|-----------------------------------|
| `GZipStream.Read()` sync    | `GZipStream.Read`    | `await gzipStream.ReadAsync()`    |
| `DeflateStream.Read()` sync | `DeflateStream.Read` | `await deflateStream.ReadAsync()` |

### 17. Cryptography Operations

| Pattern                               | Stack Signature             | Fix                                  |
|---------------------------------------|-----------------------------|--------------------------------------|
| `aes.CreateEncryptor()` + sync stream | Various crypto classes      | Use async stream operations          |
| Large `ComputeHash()` operations      | `HashAlgorithm.ComputeHash` | `await ComputeHashAsync()` (.NET 5+) |

---

## Comprehensive Grep Pattern

To search for blocking patterns in exported stack traces (from dump analysis or PerfView export):

```bash
# First, export stacks from a dump file:
# dotnet-dump analyze dump.dmp -c "clrstack -all" > stacks.txt

# Then search for blocking patterns:
grep -E "GetResultCore|InternalWaitCore|WaitAllCore|WaitAnyCore|\
Thread\.Sleep|ExecuteReader|ExecuteNonQuery|ExecuteScalar|\
DbContext\.SaveChanges|\.ToList|\.First\"|\.Single\"|\
SqlMapper\.Query|SqlMapper\.Execute|\
File\.ReadAll|File\.WriteAll|StreamReader\.Read|FileStream\.Read|\
HttpWebRequest\.GetResponse|WebClient\.Download|WebClient\.Upload|\
Monitor\.Enter|Monitor\.Wait|Monitor\.ReliableEnter|\
Mutex\.WaitOne|Semaphore\.WaitOne|SemaphoreSlim\.Wait|\
ManualResetEvent\.WaitOne|AutoResetEvent\.WaitOne|\
CountdownEvent\.Wait|Barrier\.SignalAndWait|\
ReaderWriterLock|ReaderWriterLockSlim\.Enter|\
BlockingCollection.*Take|BlockingCollection.*Add|\
Process\.WaitForExit|Console\.Read|\
Socket\.Connect|Socket\.Send|Socket\.Receive|\
TcpClient\.Connect|NetworkStream\.Read|NetworkStream\.Write|\
Dns\.GetHost" stacks.txt
```

---

## Quick Diagnosis Checklist

When analyzing issues:

1. **Multiple `.NET TP Worker` threads** with same blocking pattern = ThreadPool starvation
2. **`GetResultCore` in stack** = `.Result` was called (sync-over-async)
3. **`InternalWaitCore` in stack** = `.Wait()` was called
4. **Cluster nodes going unreachable** = Heartbeats can't be processed
5. **Queue length growing** = Work backing up faster than threads available

## Additional Resources

### Akka.NET Documentation
- [Split Brain Resolver](https://getakka.net/articles/clustering/split-brain-resolver.html)
- [Dispatchers](https://getakka.net/articles/actors/dispatchers.html)
- [Async Actors](https://getakka.net/articles/actors/receive-actor-api.html#async-handlers)

### .NET Performance
- [ThreadPool Starvation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation)
- [Async Best Practices](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)

### dotnet-monitor
- [dotnet-monitor Documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-monitor)
- [Collection Rules](https://github.com/dotnet/dotnet-monitor/blob/main/documentation/collectionrules/collectionrules.md)
- [Trigger Shortcuts](https://github.com/dotnet/dotnet-monitor/blob/main/documentation/collectionrules/triggershortcuts.md)
- [TraceProfile Values](https://github.com/dotnet/dotnet-monitor/blob/main/documentation/api/trace-get.md)

### .NET Aspire
- [Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
