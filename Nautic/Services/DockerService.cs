using Docker.DotNet;
using Docker.DotNet.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nautic.Services;

public class DockerService
{
    private readonly DockerClient _client;
    private readonly ConcurrentDictionary<string, ContainerMetrics> _metrics = new();
    private readonly DockerMetricsCalculator _calc = new();
    private CancellationTokenSource _cts = new();
    public event Action? StatsUpdated;
    private DateTime _lastContainerRefresh = DateTime.MinValue;
    public IReadOnlyDictionary<string, ContainerMetrics> CurrentMetrics => _metrics;

    public DockerService()
    {
        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";


        _client = new DockerClientConfiguration(new Uri(dockerUri), defaultTimeout: TimeSpan.FromMinutes(5)).CreateClient();
    }

    public async Task<IList<ContainerListResponse>> GetAllContainersAsync()
    {
        return await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        }, _cts.Token);
    }

    public async Task<ContainerListResponse?> GetContainerAsync(string Id)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        }, _cts.Token);

        return containers.FirstOrDefault(e => e.ID == Id);
    }

    public async Task StartMonitoringAsync()
    {
        _cts = new CancellationTokenSource();

        // Start event monitor
        _ = Task.Run(MonitorDockerEventsAsync);

        // Start existing container monitors
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        foreach (var c in containers)
        {
            _metrics[c.ID] = new ContainerMetrics
            {
                Id = c.ID,
                Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? "Unknown",
                State = c.State
            };

            if (c.State == "running")
                _ = MonitorContainerAsync(c);
        }
    }

    private async Task MonitorContainerAsync(ContainerListResponse container)
    {
        await _client.Containers.GetContainerStatsAsync(
            container.ID,
            new ContainerStatsParameters { Stream = true },
            new Progress<ContainerStatsResponse>(async stats =>
            {
                //var currentContainer = await GetContainerAsync(container.ID);
                var cpu = _calc.CalculateCpuPercent(stats);
                var memPercent = _calc.CalculateMemoryUsage(stats);
                var memStats = 0ul;
                var cache = stats.MemoryStats.Stats?.TryGetValue("cache", out memStats) ?? false;
                var usedMb = (stats.MemoryStats.Usage - (cache ? memStats : 0UL)) / 1024d / 1024d;
                var totalMb = stats.MemoryStats.Limit / 1024d / 1024d;

                _metrics[container.ID] = new ContainerMetrics
                {
                    Id = container.ID,
                    Name = container?.Names.FirstOrDefault()?.Trim('/') ?? container.ID[..12],
                    State = container?.State ?? "Unknown",
                    CpuPercent = cpu,
                    MemoryPercent = memPercent,
                    MemoryUsedMb = usedMb,
                    MemoryTotalMb = totalMb
                };
                StatsUpdated?.Invoke();
            }),
            _cts.Token
        );
    }

    public async Task MonitorDockerEventsAsync()
    {
        var parameters = new ContainerEventsParameters();

        await _client.System.MonitorEventsAsync(
            new ContainerEventsParameters(),
            new Progress<Message>(async msg =>
            {
                if (msg.Type == "container")
                {
                    switch (msg.Action)
                    {
                        case "start":
                            Console.WriteLine($"Container started: {msg.Actor?.ID}");
                            await AddOrUpdateContainerAsync(msg.Actor?.ID, "Running");
                            break;

                        case "stop":
                        case "die":
                            Console.WriteLine($"Container stopped: {msg.Actor?.ID}");
                            UpdateContainerState(msg.Actor?.ID, "Stopped");
                            break;

                        case "pause":
                            UpdateContainerState(msg.Actor?.ID, "Paused");
                            break;

                        case "unpause":
                            UpdateContainerState(msg.Actor?.ID, "Running");
                            break;

                        case "destroy":
                            RemoveContainer(msg.Actor?.ID);
                            break;
                    }
                    StatsUpdated?.Invoke();
                }
            }),
            _cts.Token
        );
    }

    private async Task AddOrUpdateContainerAsync(string? id, string state)
    {
        if (string.IsNullOrEmpty(id)) return;

        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var container = containers.FirstOrDefault(c => c.ID == id);
        if (container == null) return;

        if (!_metrics.ContainsKey(id))
        {
            _metrics[id] = new ContainerMetrics
            {
                Id = container.ID,
                Name = container.Names.FirstOrDefault()?.TrimStart('/') ?? "Unknown",
                State = state
            };

            // Start monitoring its stats if running
            if (state == "Running")
                _ = MonitorContainerAsync(container);
        }
        else
        {
            _metrics[id].State = state;
        }
    }

    private void UpdateContainerState(string? id, string state)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_metrics.TryGetValue(id, out var m))
            m.State = state;
    }

    private void RemoveContainer(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _metrics.Remove(id, out _);
    }



    public void StopMonitoring()
    {
        _cts.Cancel();
    }

    public class ContainerMetrics
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string State { get; set; }
        public double CpuPercent { get; set; }
        public double MemoryPercent { get; set; }
        public double MemoryUsedMb { get; set; }
        public double MemoryTotalMb { get; set; }
    }

    //public async IAsyncEnumerable<(string ContainerId, ContainerStatsResponse Stats)> StreamStatsAsync(CancellationToken cancellationToken)
    //{
    //    var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters() { All = false });

    //    await foreach (var (id, stats) in StreamContainerStatsAsync(containers, cancellationToken))
    //    {
    //        yield return (id, stats);
    //    }
    //}


    //public async IAsyncEnumerable<(string containerId, ContainerStatsResponse stats)> StreamContainerStatsAsync(
    //IEnumerable<ContainerListResponse> containers,
    //[EnumeratorCancellation] CancellationToken cancellationToken = default)
    //{
    //    foreach (var container in containers)
    //    {
    //        var channel = Channel.CreateUnbounded<ContainerStatsResponse>();

    //        // Typed progress directly reports ContainerStatsResponse
    //        var progress = new Progress<ContainerStatsResponse>(stats =>
    //        {
    //            channel.Writer.TryWrite(stats);
    //        });

    //        var task = _client.Containers.GetContainerStatsAsync(
    //            container.ID,
    //            new ContainerStatsParameters { Stream = true },
    //            progress,
    //            cancellationToken
    //        );

    //        // Yield each ContainerStatsResponse as it arrives
    //        await foreach (var stats in channel.Reader.ReadAllAsync(cancellationToken))
    //        {
    //            yield return (container.ID, stats);
    //        }

    //        await task;
    //    }
    //}
}


public class DockerMetricsCalculator
{
    public double CalculateCpuPercent(ContainerStatsResponse stats)
    {
        if (stats.CPUStats == null || stats.PreCPUStats == null)
            return 0;

        var cpuDelta = stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
        var systemDelta = stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
        var onlineCpus = stats.CPUStats?.OnlineCPUs ?? 1;

        return systemDelta > 0 ? (cpuDelta * 1.0 / systemDelta * onlineCpus * 100.0) : 0;
    }

    public double CalculateMemoryUsage(ContainerStatsResponse stats)
    {
        var memStats = 0ul;
        var cache = stats.MemoryStats.Stats?.TryGetValue("cache", out memStats) ?? false;
        var used = stats.MemoryStats.Usage - (cache ? memStats : 0ul);
        var limit = stats.MemoryStats.Limit;
        return limit > 0 ? (used / limit) * 100.0 : 0.0;
    }
}