using System.Collections.ObjectModel;
using System.IO;
using System.Management;
using JFStorageTester.Models;

namespace JFStorageTester.Services;

public class DriveDetectionService : IDisposable
{
    private static DriveDetectionService? _instance;
    private static readonly object _lock = new();
    
    public static DriveDetectionService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new DriveDetectionService();
                }
            }
            return _instance;
        }
    }
    
    public ObservableCollection<DriveInfoModel> Drives { get; } = new();
    public event EventHandler? DrivesChanged;
    
    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;
    private bool _disposed;
    
    private DriveDetectionService()
    {
        // Quick initial load for responsive startup
        var quickDrives = GetAllDrivesQuick();
        foreach (var drive in quickDrives)
        {
            Drives.Add(drive);
        }
        
        // Start watching for drive changes
        StartWatching();
        
        // Load detailed drive info in background
        Task.Run(() => LoadDetailedDriveInfo());
    }
    
    private void LoadDetailedDriveInfo()
    {
        try
        {
            var detailedDrives = GetAllDrives();
            
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            
            dispatcher.BeginInvoke(() => UpdateDrivesList(detailedDrives));
        }
        catch
        {
            // Keep quick info if detailed fails
        }
    }
    
    public void RefreshDrives()
    {
        // Run on background thread to prevent UI hanging
        Task.Run(() =>
        {
            try
            {
                // Use full GetAllDrives to get partition scheme and other details
                var currentDrives = GetAllDrives();
                
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                
                dispatcher.BeginInvoke(() => UpdateDrivesList(currentDrives));
            }
            catch (Exception)
            {
                // Silently handle refresh errors
            }
        });
    }
    
    private void UpdateDrivesList(List<DriveInfoModel> currentDrives)
    {
        Drives.Clear();
        foreach (var drive in currentDrives)
        {
            Drives.Add(drive);
        }
        DrivesChanged?.Invoke(this, EventArgs.Empty);
    }
    
    // Quick version that skips slow WMI queries for responsive UI
    private List<DriveInfoModel> GetAllDrivesQuick()
    {
        var drives = new List<DriveInfoModel>();
        
        try
        {
            foreach (var driveInfo in DriveInfo.GetDrives())
            {
                if (!driveInfo.IsReady) continue;
                
                try
                {
                    var driveModel = new DriveInfoModel
                    {
                        DriveLetter = driveInfo.Name.TrimEnd('\\'),
                        VolumeName = string.IsNullOrEmpty(driveInfo.VolumeLabel) 
                            ? "Local Disk" 
                            : driveInfo.VolumeLabel,
                        TotalSize = driveInfo.TotalSize,
                        FreeSpace = driveInfo.TotalFreeSpace,
                        UsedSpace = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
                        FileSystem = driveInfo.DriveFormat,
                        DriveType = GetDriveTypeQuick(driveInfo),
                        IsSystemDrive = IsSystemDrive(driveInfo.Name),
                        BitLockerStatus = BitLockerStatus.NotEncrypted // Skip slow BitLocker check for refresh
                    };
                    
                    drives.Add(driveModel);
                }
                catch
                {
                    // Skip drives that can't be read
                }
            }
        }
        catch
        {
            // Return empty list if enumeration fails
        }
        
        return drives.OrderBy(d => d.DriveLetter).ToList();
    }
    
    private StorageType GetDriveTypeQuick(DriveInfo driveInfo)
    {
        return driveInfo.DriveType switch
        {
            System.IO.DriveType.Removable => StorageType.USB,
            System.IO.DriveType.CDRom => StorageType.Optical,
            System.IO.DriveType.Network => StorageType.Network,
            System.IO.DriveType.Fixed => StorageType.SSD, // Default to SSD, will be refined on full scan
            _ => StorageType.Unknown
        };
    }
    
    private List<DriveInfoModel> GetAllDrives()
    {
        var drives = new List<DriveInfoModel>();
        
        try
        {
            // Get physical disk information
            var physicalDisks = GetPhysicalDiskInfo();
            
            // Get logical drives (partitions)
            foreach (var driveInfo in DriveInfo.GetDrives())
            {
                if (!driveInfo.IsReady) continue;
                
                try
                {
                    var driveLetter = driveInfo.Name.TrimEnd('\\');
                    var partitionScheme = physicalDisks.TryGetValue(driveLetter, out var physicalInfo)
                        ? physicalInfo.PartitionScheme
                        : PartitionScheme.Unknown;
                    
                    var driveModel = new DriveInfoModel
                    {
                        DriveLetter = driveLetter,
                        VolumeName = string.IsNullOrEmpty(driveInfo.VolumeLabel) 
                            ? "Local Disk" 
                            : driveInfo.VolumeLabel,
                        TotalSize = driveInfo.TotalSize,
                        FreeSpace = driveInfo.TotalFreeSpace,
                        UsedSpace = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
                        FileSystem = driveInfo.DriveFormat,
                        DriveType = GetDriveType(driveInfo, physicalDisks),
                        IsSystemDrive = IsSystemDrive(driveInfo.Name),
                        BitLockerStatus = GetBitLockerStatus(driveInfo.Name),
                        PartitionScheme = partitionScheme
                    };
                    
                    drives.Add(driveModel);
                }
                catch
                {
                    // Skip drives that can't be read
                }
            }
        }
        catch
        {
            // Return empty list if enumeration fails
        }
        
        return drives.OrderBy(d => d.DriveLetter).ToList();
    }
    
    private Dictionary<string, PhysicalDiskInfo> GetPhysicalDiskInfo()
    {
        var diskInfo = new Dictionary<string, PhysicalDiskInfo>();
        
        // Get partition style for each disk using MSFT_Disk (more reliable)
        var diskPartitionStyles = new Dictionary<int, PartitionScheme>();
        try
        {
            using var msftDiskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Number, PartitionStyle FROM MSFT_Disk");
            
            foreach (ManagementObject msftDisk in msftDiskSearcher.Get())
            {
                var diskNumber = Convert.ToInt32(msftDisk["Number"]);
                var partitionStyle = Convert.ToInt32(msftDisk["PartitionStyle"]);
                // PartitionStyle: 0 = Unknown, 1 = MBR, 2 = GPT
                diskPartitionStyles[diskNumber] = partitionStyle switch
                {
                    1 => PartitionScheme.MBR,
                    2 => PartitionScheme.GPT,
                    _ => PartitionScheme.Unknown
                };
            }
        }
        catch
        {
            // MSFT_Disk query failed, will fall back to Unknown
        }
        
        try
        {
            // Map logical drives to physical disks
            using var diskDriveSearcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_DiskDrive");
            
            foreach (ManagementObject disk in diskDriveSearcher.Get())
            {
                var deviceId = disk["DeviceID"]?.ToString() ?? "";
                var mediaType = disk["MediaType"]?.ToString() ?? "";
                var interfaceType = disk["InterfaceType"]?.ToString() ?? "";
                var model = disk["Model"]?.ToString() ?? "";
                var index = Convert.ToInt32(disk["Index"]);
                
                // Get partition scheme for this disk
                var partitionScheme = diskPartitionStyles.TryGetValue(index, out var scheme) 
                    ? scheme 
                    : PartitionScheme.Unknown;
                
                // Get partitions for this disk
                using var partitionSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                
                foreach (ManagementObject partition in partitionSearcher.Get())
                {
                    using var logicalSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                    
                    foreach (ManagementObject logical in logicalSearcher.Get())
                    {
                        var driveLetter = logical["DeviceID"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(driveLetter))
                        {
                            diskInfo[driveLetter] = new PhysicalDiskInfo
                            {
                                MediaType = mediaType,
                                InterfaceType = interfaceType,
                                Model = model,
                                PartitionScheme = partitionScheme
                            };
                        }
                    }
                }
            }
        }
        catch
        {
            // WMI query failed
        }
        
        return diskInfo;
    }
    
    private StorageType GetDriveType(DriveInfo driveInfo, Dictionary<string, PhysicalDiskInfo> physicalDisks)
    {
        var driveLetter = driveInfo.Name.TrimEnd('\\');
        
        // Check if it's a removable drive
        if (driveInfo.DriveType == System.IO.DriveType.Removable)
        {
            return StorageType.USB;
        }
        
        if (driveInfo.DriveType == System.IO.DriveType.CDRom)
        {
            return StorageType.Optical;
        }
        
        if (driveInfo.DriveType == System.IO.DriveType.Network)
        {
            return StorageType.Network;
        }
        
        // Check physical disk info
        if (physicalDisks.TryGetValue(driveLetter, out var physicalInfo))
        {
            var mediaType = physicalInfo.MediaType?.ToLower() ?? "";
            var model = physicalInfo.Model?.ToLower() ?? "";
            var interfaceType = physicalInfo.InterfaceType?.ToLower() ?? "";
            
            // Check for SSD indicators
            if (mediaType.Contains("ssd") || 
                mediaType.Contains("solid") ||
                model.Contains("ssd") ||
                model.Contains("nvme"))
            {
                return StorageType.SSD;
            }
            
            // Check for NVMe
            if (interfaceType.Contains("nvme") || model.Contains("nvme"))
            {
                return StorageType.NVMe;
            }
            
            // Check for eMMC
            if (model.Contains("emmc") || mediaType.Contains("emmc"))
            {
                return StorageType.eMMC;
            }
            
            // Check for HDD indicators
            if (mediaType.Contains("hdd") || 
                mediaType.Contains("hard") ||
                mediaType.Contains("fixed"))
            {
                return StorageType.HDD;
            }
        }
        
        // Try to determine by checking if it's an SSD using Win32_PhysicalMedia
        if (IsSsdByMediaType(driveLetter))
        {
            return StorageType.SSD;
        }
        
        // Default to HDD for fixed drives
        return driveInfo.DriveType == System.IO.DriveType.Fixed 
            ? StorageType.HDD 
            : StorageType.Unknown;
    }
    
    private bool IsSsdByMediaType(string driveLetter)
    {
        try
        {
            // Use MSFT_PhysicalDisk for more accurate SSD detection on Windows 10+
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\microsoft\windows\storage",
                "SELECT MediaType FROM MSFT_PhysicalDisk");
            
            foreach (ManagementObject disk in searcher.Get())
            {
                var mediaType = Convert.ToInt32(disk["MediaType"]);
                // MediaType 4 = SSD, 3 = HDD
                if (mediaType == 4) return true;
            }
        }
        catch
        {
            // This WMI namespace might not be available
        }
        
        return false;
    }
    
    private bool IsSystemDrive(string driveLetter)
    {
        var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return systemDrive.StartsWith(driveLetter, StringComparison.OrdinalIgnoreCase);
    }
    
    private BitLockerStatus GetBitLockerStatus(string driveLetter)
    {
        try
        {
            var letter = driveLetter.TrimEnd('\\', ':') + ":";
            
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\cimv2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{letter}'");
            
            foreach (ManagementObject volume in searcher.Get())
            {
                var protectionStatus = Convert.ToInt32(volume["ProtectionStatus"]);
                var conversionStatus = Convert.ToInt32(volume["ConversionStatus"]);
                
                // ProtectionStatus: 0 = Off, 1 = On, 2 = Unknown
                // ConversionStatus: 0 = FullyDecrypted, 1 = FullyEncrypted, 2 = EncryptionInProgress, etc.
                
                if (conversionStatus == 0)
                {
                    return BitLockerStatus.NotEncrypted;
                }
                
                if (protectionStatus == 1)
                {
                    return BitLockerStatus.Locked;
                }
                
                if (conversionStatus == 1 && protectionStatus == 0)
                {
                    return BitLockerStatus.Unlocked;
                }
                
                return BitLockerStatus.Encrypted;
            }
        }
        catch
        {
            // BitLocker WMI not available or access denied
        }
        
        return BitLockerStatus.NotEncrypted;
    }
    
    private void StartWatching()
    {
        try
        {
            // Watch for drive insertion
            var insertQuery = new WqlEventQuery(
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_LogicalDisk'");
            _insertWatcher = new ManagementEventWatcher(insertQuery);
            _insertWatcher.EventArrived += (s, e) => RefreshDrives();
            _insertWatcher.Start();
            
            // Watch for drive removal
            var removeQuery = new WqlEventQuery(
                "SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_LogicalDisk'");
            _removeWatcher = new ManagementEventWatcher(removeQuery);
            _removeWatcher.EventArrived += (s, e) => RefreshDrives();
            _removeWatcher.Start();
        }
        catch
        {
            // WMI events not available
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _insertWatcher?.Stop();
        _insertWatcher?.Dispose();
        _removeWatcher?.Stop();
        _removeWatcher?.Dispose();
        
        _disposed = true;
    }
    
    private class PhysicalDiskInfo
    {
        public string? MediaType { get; set; }
        public string? InterfaceType { get; set; }
        public string? Model { get; set; }
        public PartitionScheme PartitionScheme { get; set; }
    }
}
