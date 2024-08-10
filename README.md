# TcpConnectionPoolManager

`TcpConnectionPoolManager` is a .NET class designed to manage a pool of TCP connections efficiently. It handles connection creation, health checks, and dynamic scaling to optimize network communication performance.

## Features

- **Connection Pooling**: Reuses `TcpClient` instances to reduce connection overhead.
- **Health Checks**: Periodically verifies the health of connections with configurable latency and error thresholds.
- **Dynamic Scaling**: Adjusts the pool size based on connection health and demand.
- **Configurable Parameters**: Customize connection timeout, health check intervals, and more.

## Getting Started

### Installation

To use `TcpConnectionPoolManager`, you need to include it in your .NET project. You can copy the class code into your project or add it as a new file.

### Configuration

Configure the connection pool manager with the following parameters:

```csharp
var manager = new TcpConnectionPoolManager(
    host: "example.com",           // Server hostname or IP address
    port: 12345,                    // Port number
    maxPoolSize: 10,                // Maximum number of connections
    minPoolSize: 5,                 // Minimum number of connections
    checkInterval: TimeSpan.FromMinutes(5), // Interval for health checks
    connectionTimeout: TimeSpan.FromSeconds(30), // Connection timeout
    debugLog: true                  // Enable debug logging
);
