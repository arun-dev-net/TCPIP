using System.Collections.Concurrent;
using System.Net.Sockets;

public class TcpConnectionPoolManager
{
    private readonly ConcurrentQueue<PooledConnection> _connectionPool;
    private int _currentPoolSize;
    private readonly int _maxPoolSize;
    private readonly int _minPoolSize;
    private TimeSpan _checkInterval;
    private readonly TimeSpan _connectionTimeout;
    private readonly bool _debugLog;
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _semaphore;
    private readonly SemaphoreSlim _poolLock = new SemaphoreSlim(1, 1);

    // Configurable thresholds
    public TimeSpan ConnectionLatencyThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
    public int MaxErrorCount { get; set; } = 5;
    public byte CustomHealthCheckMessage { get; set; } = 0x01;
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TcpConnectionPoolManager(
        string host,
        int port,
        int maxPoolSize,
        int minPoolSize,
        TimeSpan checkInterval,
        TimeSpan connectionTimeout,
        bool debugLog = false)
    {
        _connectionPool = new ConcurrentQueue<PooledConnection>();
        _currentPoolSize = 0;
        _maxPoolSize = maxPoolSize;
        _minPoolSize = minPoolSize;
        _checkInterval = checkInterval;
        _connectionTimeout = connectionTimeout;
        _debugLog = debugLog;
        _host = host;
        _port = port;
        _semaphore = new SemaphoreSlim(maxPoolSize);

        // Pre-warm the pool with minimum connections
        Task.WhenAll(Enumerable.Range(0, _minPoolSize).Select(_ => CreateAndAddConnectionToPoolAsync())).Wait();

        // Start the periodic connection check task
        Task.Run(() => PeriodicallyCheckConnectionsAsync());
    }

    public async Task<TcpClient> GetConnectionAsync()
    {
        await _semaphore.WaitAsync();

        PooledConnection availableConnection = null;

        await _poolLock.WaitAsync();

        try
        {
            if (_connectionPool.TryDequeue(out availableConnection) && !availableConnection.InUse && IsConnectionHealthy(availableConnection))
            {
                availableConnection.InUse = true;
                LogDebug($"Retrieved connection from pool. Pool size: {_connectionPool.Count}");
                return availableConnection.TcpClient;
            }

            if (_currentPoolSize < _maxPoolSize)
            {
                LogDebug("No available connection in pool, creating a new one.");
                var newConnection = await CreateNewConnectionAsync();
                newConnection.InUse = true;
                return newConnection.TcpClient;
            }
        }
        finally
        {
            _poolLock.Release();
        }

        throw new InvalidOperationException("No available connections in the pool and max pool size reached.");
    }

    public async Task ReturnConnectionAsync(TcpClient connection)
    {
        await _poolLock.WaitAsync();

        try
        {
            if (_connectionPool.Any(pc => pc.TcpClient == connection))
            {
                var pooledConnection = _connectionPool.First(pc => pc.TcpClient == connection);
                pooledConnection.InUse = false;
                pooledConnection.LastUsed = DateTime.UtcNow;

                if (_currentPoolSize > _minPoolSize && !IsConnectionAlive(pooledConnection.TcpClient))
                {
                    _connectionPool.TryDequeue(out _);
                    Interlocked.Decrement(ref _currentPoolSize);
                    connection.Dispose();
                    LogDebug("Connection disposed and removed from pool due to poor health.");
                }
                else
                {
                    _connectionPool.Enqueue(pooledConnection);
                    LogDebug($"Returned connection to pool. Pool size: {_connectionPool.Count}");
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
            var tcpClient = new TcpClient();

            try
            {
                await tcpClient.ConnectAsync(_host, _port).WaitAsync(cts.Token);
                LogDebug($"Created new connection to {_host}:{_port}");
                var pooledConnection = new PooledConnection(tcpClient)
                {
                    CreatedAt = DateTime.UtcNow,
                    LastUsed = DateTime.UtcNow
                };
                return pooledConnection;
            }
            catch (OperationCanceledException)
            {
                LogDebug("Connection attempt timed out.");
                throw new TimeoutException("The connection attempt timed out.");
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to create connection: {ex.Message}");
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
                        if (!IsConnectionAlive(pooledConnection.TcpClient) || !IsConnectionHealthy(pooledConnection))
                        {
                            LogDebug("Connection not healthy, reconnecting.");
                            _connectionPool.TryDequeue(out _);
                            Interlocked.Decrement(ref _currentPoolSize);
                            await CreateAndAddConnectionToPoolAsync();
                        }
                    }
                }

                if (_currentPoolSize < _minPoolSize)
                {
                    LogDebug("Scaling up the pool to meet minimum size.");
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

    private bool IsConnectionAlive(TcpClient connection)
    {
        try
        {
            return connection.Connected && !(connection.Client.Poll(1, SelectMode.SelectRead) && connection.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private bool IsConnectionHealthy(PooledConnection connection)
    {
        if (!IsConnectionAlive(connection.TcpClient))
        {
            LogDebug("Connection is not alive.");
            return AttemptConnectionRecovery(connection);
        }

        var latency = MeasureConnectionLatency(connection);
        if (latency > ConnectionLatencyThreshold)
        {
            LogDebug($"Connection latency too high: {latency.TotalMilliseconds} ms.");
            return AttemptConnectionRecovery(connection);
        }

        if (connection.ErrorCount > MaxErrorCount)
        {
            LogDebug($"Connection has encountered too many errors: {connection.ErrorCount}.");
            return AttemptConnectionRecovery(connection);
        }

        if (!PerformCustomHealthChecks(connection))
        {
            LogDebug("Custom health checks failed.");
            return AttemptConnectionRecovery(connection);
        }

        return true;
    }

    private TimeSpan MeasureConnectionLatency(PooledConnection connection)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var stream = connection.TcpClient.GetStream();
            byte[] buffer = new byte[1] { 0x00 };

            var writeTask = stream.WriteAsync(buffer, 0, buffer.Length);
            if (!writeTask.Wait(TimeSpan.FromMilliseconds(200)))
            {
                LogDebug("Latency measurement write operation timed out.");
                return TimeSpan.MaxValue;
            }

            var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
            if (!readTask.Wait(TimeSpan.FromMilliseconds(200)))
            {
                LogDebug("Latency measurement read operation timed out.");
                return TimeSpan.MaxValue;
            }

            stopwatch.Stop();
            return stopwatch.Elapsed;
        }
        catch (Exception ex)
        {
            LogDebug($"Latency measurement failed: {ex.Message}");
            return TimeSpan.MaxValue;
        }
    }

    private bool PerformCustomHealthChecks(PooledConnection connection)
    {
        try
        {
            var stream = connection.TcpClient.GetStream();
            var buffer = new byte[1] { CustomHealthCheckMessage };

            var writeTask = stream.WriteAsync(buffer, 0, buffer.Length);
            if (!writeTask.Wait(HealthCheckTimeout))
            {
                LogDebug("Custom health check write operation timed out.");
                return false;
            }

            var responseBuffer = new byte[1];
            var readTask = stream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
            if (!readTask.Wait(HealthCheckTimeout))
            {
                LogDebug("Custom health check read operation timed out.");
                return false;
            }

            if (responseBuffer[0] != CustomHealthCheckMessage)
            {
                LogDebug("Custom health check failed: unexpected response.");
                return false;
            }

            LogDebug("Custom health check passed.");
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"Custom health check failed: {ex.Message}");
            return false;
        }
    }

    private bool AttemptConnectionRecovery(PooledConnection oldConnection)
    {
        try
        {
            oldConnection.TcpClient.Close();
            var newConnection = new PooledConnection(new TcpClient());
            newConnection.TcpClient.Connect(_host, _port);

            lock (_poolLock)
            {
                _connectionPool.Enqueue(newConnection);
                Interlocked.Increment(ref _currentPoolSize);
            }

            LogDebug("Connection recovered and replaced.");
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to recover connection: {ex.Message}");
            return false;
        }
    }

    private void LogDebug(string message)
    {
        if (_debugLog)
        {
            Console.WriteLine($"DEBUG: {message}");
        }
    }
}

public class PooledConnection
{
    public TcpClient TcpClient { get; }
    public bool InUse { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
    public int ErrorCount { get; set; }

    public PooledConnection(TcpClient tcpClient)
    {
        TcpClient = tcpClient;
    }
}
