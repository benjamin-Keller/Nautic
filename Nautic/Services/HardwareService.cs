
using Hardware.Info;

namespace Nautic.Services;

public class HardwareService : BackgroundService, IDisposable
{
    private readonly IHardwareInfo _hardware;
    private readonly ILogger<HardwareService> _logger;
    public SystemStats CurrentStats { get; private set; } = new();

    public HardwareService(ILogger<HardwareService> logger)
    {
        _logger = logger;
        _hardware = new HardwareInfo();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                GetHardwareInfo();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HW Error] Error updating hardware info");
            }

            await GetDelay(stoppingToken);
        }
    }

    public async Task GetDelay(CancellationToken stoppingToken = default) => await Task.Delay(TimeSpan.FromSeconds(0.5), stoppingToken);

    private void GetHardwareInfo()
    {
        _hardware.RefreshAll(); // refresh CPU, memory, drives, etc.

        var cpu = _hardware.CpuList.FirstOrDefault();
        var cpuUsage = cpu?.PercentProcessorTime ?? 0;

        var memTotal = _hardware.MemoryStatus.TotalPhysical / 1024f / 1024f;
        var memUsed = (memTotal - _hardware.MemoryStatus.AvailablePhysical / 1024f / 1024f);

        // Using DriveInfo for accurate space
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
        var totalDisk = drives.Sum(d => d.TotalSize) / 1024f / 1024f / 1024f;
        var freeDisk = drives.Sum(d => d.AvailableFreeSpace) / 1024f / 1024f / 1024f;
        var usedDisk = totalDisk - freeDisk;

        // Estimate network traffic (may reset after reboot)
        var network = _hardware.NetworkAdapterList.FirstOrDefault();
        var sent = network?.BytesSentPersec ?? 0;
        var received = network?.BytesReceivedPersec ?? 0;

        CurrentStats = new SystemStats
        {
            CpuUsagePercent = (float)cpuUsage,
            MemoryUsedMb = memUsed,
            MemoryTotalMb = memTotal,
            DiskUsedGb = usedDisk,
            DiskTotalGb = totalDisk,
            NetworkBytesSent = sent / 1024f / 1024f,
            NetworkBytesReceived = received / 1024f / 1024f,
            LastUpdated = DateTime.Now
        };

        _logger.LogInformation($"[HW Info] System stats updated: {CurrentStats.CpuUsagePercent:F1}% CPU, {CurrentStats.MemoryUsedMb:F0}/{CurrentStats.MemoryTotalMb:F0}MB RAM");
    }
}

public class SystemStats
{
    public float CpuUsagePercent { get; set; }
    public float MemoryUsedMb { get; set; }
    public float MemoryTotalMb { get; set; }
    public float DiskUsedGb { get; set; }
    public float DiskTotalGb { get; set; }
    public float NetworkBytesSent { get; set; }
    public float NetworkBytesReceived { get; set; }
    public DateTime LastUpdated { get; set; }
}