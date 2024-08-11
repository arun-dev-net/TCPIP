 public class SocketConnectionPoolManager
 {
     // Define the request and response data
     private readonly string requestMessage = "AREYOUTHERE";
     private readonly string expectedResponseMessage = "YESIAMTHERE";

     // Encoding for ASCII and EBCDIC
     private static readonly Encoding asciiEncoding = Encoding.ASCII;
     private static readonly Encoding ebcdicEncoding = Encoding.GetEncoding("IBM037"); // IBM037 is a common EBCDIC codepage


     private readonly ConcurrentQueue<PooledConnection> _connectionPool;
     private int _currentPoolSize;
     private readonly int _maxPoolSize;
     private readonly int _minPoolSize;
     private readonly TimeSpan _checkInterval;
     private readonly TimeSpan _connectionTimeout;
     private readonly string _host;
     private readonly int _port;
     private readonly SemaphoreSlim _semaphore;
     private readonly SemaphoreSlim _poolLock = new SemaphoreSlim(1, 1);

     // Configurable thresholds
     public TimeSpan ConnectionLatencyThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
     public int MaxErrorCount { get; set; } = 5;
     public byte CustomHealthCheckMessage { get; set; } = 0x01;
     public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(2);

     public SocketConnectionPoolManager(
         string host,
         int port,
         int maxPoolSize,
         int minPoolSize,
         TimeSpan checkInterval,
         TimeSpan connectionTimeout)
     {
         _connectionPool = new ConcurrentQueue<PooledConnection>();
         _currentPoolSize = 0;
         _maxPoolSize = maxPoolSize;
         _minPoolSize = minPoolSize;
         _checkInterval = checkInterval;
         _connectionTimeout = connectionTimeout;
         _host = host;
         _port = port;
         _semaphore = new SemaphoreSlim(maxPoolSize);

         // Pre-warm the pool with minimum connections
         Task.WhenAll(Enumerable.Range(0, _minPoolSize).Select(_ => CreateAndAddConnectionToPoolAsync())).Wait();

         // Start the periodic connection check task
         Task.Run(() => PeriodicallyCheckConnectionsAsync());
     }

     public async Task<Socket> GetConnectionAsync()
     {
         await _semaphore.WaitAsync();

         PooledConnection availableConnection = null;

         await _poolLock.WaitAsync();

         try
         {
             if (_connectionPool.TryDequeue(out availableConnection) && !availableConnection.InUse && IsConnectionHealthy(availableConnection))
             {
                 availableConnection.InUse = true;
                 Log.Information("Retrieved connection from pool. Pool size: {PoolSize}", _connectionPool.Count);
                 return availableConnection.Socket;
             }

             if (_currentPoolSize < _maxPoolSize)
             {
                 Log.Information("No available connection in pool, creating a new one.");
                 var newConnection = await CreateNewConnectionAsync();
                 newConnection.InUse = true;
                 return newConnection.Socket;
             }
         }
         finally
         {
             _poolLock.Release();
         }

         throw new InvalidOperationException("No available connections in the pool and max pool size reached.");
     }

     public async Task ReturnConnectionAsync(Socket socket)
     {
         await _poolLock.WaitAsync();

         try
         {
             if (_connectionPool.Any(pc => pc.Socket == socket))
             {
                 var pooledConnection = _connectionPool.First(pc => pc.Socket == socket);
                 pooledConnection.InUse = false;
                 pooledConnection.LastUsed = DateTime.UtcNow;

                 if (_currentPoolSize > _minPoolSize && !IsConnectionAlive(pooledConnection.Socket))
                 {
                     _connectionPool.TryDequeue(out _);
                     Interlocked.Decrement(ref _currentPoolSize);
                     socket.Close();
                     Log.Information("Connection disposed and removed from pool due to poor health.");
                 }
                 else
                 {
                     _connectionPool.Enqueue(pooledConnection);
                     Log.Information("Returned connection to pool. Pool size: {PoolSize}", _connectionPool.Count);
                 }
             }
         }
         finally
         {
             _poolLock.Release();
             _semaphore.Release();
         }
     }

     private async Task CreateAndAddConnectionToPoolAsync()
     {
         var connection = await CreateNewConnectionAsync();
         _connectionPool.Enqueue(connection);
         Interlocked.Increment(ref _currentPoolSize);
     }

     private async Task<PooledConnection> CreateNewConnectionAsync()
     {
         using (var cts = new CancellationTokenSource(_connectionTimeout))
         {
             var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

             try
             {
                 await socket.ConnectAsync(_host, _port).WaitAsync(cts.Token);
                 Log.Information("Created new connection to {Host}:{Port}", _host, _port);
                 var pooledConnection = new PooledConnection(socket)
                 {
                     CreatedAt = DateTime.UtcNow,
                     LastUsed = DateTime.UtcNow
                 };
                 return pooledConnection;
             }
             catch (OperationCanceledException)
             {
                 Log.Warning("Connection attempt timed out.");
                 throw new TimeoutException("The connection attempt timed out.");
             }
             catch (Exception ex)
             {
                 Log.Error(ex, "Failed to create connection.");
                 throw;
             }
         }
     }

     private async Task PeriodicallyCheckConnectionsAsync()
     {
         while (true)
         {
             await _poolLock.WaitAsync();

             try
             {
                 foreach (var pooledConnection in _connectionPool.ToList())
                 {
                     if (!pooledConnection.InUse)
                     {
                         if (!IsConnectionAlive(pooledConnection.Socket) || !IsConnectionHealthy(pooledConnection))
                         {
                             Log.Information("Connection not healthy, reconnecting.");
                             _connectionPool.TryDequeue(out _);
                             Interlocked.Decrement(ref _currentPoolSize);
                             await CreateAndAddConnectionToPoolAsync();
                         }
                     }
                 }

                 if (_currentPoolSize < _minPoolSize)
                 {
                     Log.Information("Scaling up the pool to meet minimum size.");
                     await CreateAndAddConnectionToPoolAsync();
                 }
             }
             finally
             {
                 _poolLock.Release();
             }

             await Task.Delay(_checkInterval);
         }
     }

     private bool IsConnectionAlive(Socket socket)
     {
         try
         {
             return socket.Connected && !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
         }
         catch
         {
             return false;
         }
     }

     private bool IsConnectionHealthy(PooledConnection connection)
     {
         if (!IsConnectionAlive(connection.Socket))
         {
             Log.Information("Connection is not alive.");
             return AttemptConnectionRecovery(connection);
         }

         var latency = MeasureConnectionLatency(connection);
         if (latency > ConnectionLatencyThreshold)
         {
             Log.Information("Connection latency too high: {Latency} ms.", latency.TotalMilliseconds);
             return AttemptConnectionRecovery(connection);
         }

         if (connection.ErrorCount > MaxErrorCount)
         {
             Log.Information("Connection has encountered too many errors: {ErrorCount}.", connection.ErrorCount);
             return AttemptConnectionRecovery(connection);
         }

         if (!PerformCustomHealthChecks(connection))
         {
             Log.Information("Custom health checks failed.");
             return AttemptConnectionRecovery(connection);
         }

         return true;
     }

     private TimeSpan MeasureConnectionLatency(PooledConnection connection)
     {
         try
         {
             var stopwatch = System.Diagnostics.Stopwatch.StartNew();
             var buffer = new byte[1] { 0x00 };

             var sendTask = connection.Socket.SendAsync(buffer, SocketFlags.None);
             if (!sendTask.Wait(TimeSpan.FromMilliseconds(200)))
             {
                 Log.Information("Latency measurement send operation timed out.");
                 return TimeSpan.MaxValue;
             }

             var receiveTask = connection.Socket.ReceiveAsync(buffer, SocketFlags.None);
             if (!receiveTask.Wait(TimeSpan.FromMilliseconds(200)))
             {
                 Log.Information("Latency measurement receive operation timed out.");
                 return TimeSpan.MaxValue;
             }

             stopwatch.Stop();
             return stopwatch.Elapsed;
         }
         catch (Exception ex)
         {
             Log.Error(ex, "Latency measurement failed.");
             return TimeSpan.MaxValue;
         }
     }

     private bool PerformCustomHealthChecks(PooledConnection connection)
     {
         try
         {
             var stream = new NetworkStream(connection.Socket);
             byte[] buffer = ConvertAsciiToEbcdic(requestMessage);

             var writeTask = stream.WriteAsync(buffer, 0, buffer.Length);
             if (!writeTask.Wait(HealthCheckTimeout))
             {
                 Log.Warning("Custom health check write operation timed out.");
                 return false;
             }

             byte[] responseBuffer = new byte[expectedResponseMessage.Length];
             var readTask = stream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
             if (!readTask.Wait(HealthCheckTimeout))
             {
                 Log.Warning("Custom health check read operation timed out.");
                 return false;
             }

             string responseAscii = ConvertEbcdicToAscii(responseBuffer);
             if (responseAscii != expectedResponseMessage)
             {
                 Log.Warning("Custom health check failed: unexpected response.");
                 return false;
             }

             Log.Information("Custom health check passed.");
             return true;
         }
         catch (Exception ex)
         {
             Log.Error($"Custom health check failed: {ex.Message}");
             return false;
         }
     }

     private bool AttemptConnectionRecovery(PooledConnection oldConnection)
     {
         try
         {
             oldConnection.Socket.Close();
             var newConnection = new PooledConnection(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
             newConnection.Socket.Connect(_host, _port);

             lock (_poolLock)
             {
                 _connectionPool.Enqueue(newConnection);
                 Interlocked.Increment(ref _currentPoolSize);
             }

             Log.Information("Connection recovered and replaced.");
             return true;
         }
         catch (Exception ex)
         {
             Log.Error(ex, "Failed to recover connection.");
             return false;
         }
     }

     private byte[] ConvertAsciiToEbcdic(string asciiString)
     {
         byte[] asciiBytes = asciiEncoding.GetBytes(asciiString);
         return Encoding.Convert(asciiEncoding, ebcdicEncoding, asciiBytes);
     }

     // Method to convert EBCDIC to ASCII
     private string ConvertEbcdicToAscii(byte[] ebcdicBytes)
     {
         byte[] asciiBytes = Encoding.Convert(ebcdicEncoding, asciiEncoding, ebcdicBytes);
         return asciiEncoding.GetString(asciiBytes);
     }

     
 }

 public class PooledConnection
 {
     public Socket Socket { get; }
     public bool InUse { get; set; }
     public DateTime CreatedAt { get; set; }
     public DateTime LastUsed { get; set; }
     public int ErrorCount { get; set; }

     public PooledConnection(Socket socket)
     {
         Socket = socket;
         InUse = false;
         CreatedAt = DateTime.UtcNow;
         LastUsed = DateTime.UtcNow;
         ErrorCount = 0;
     }
 }
