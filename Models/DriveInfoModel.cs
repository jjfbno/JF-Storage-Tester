namespace JFStorageTester.Models;

public enum StorageType
{
    Unknown,
    HDD,
    SSD,
    NVMe,
    eMMC,
    USB,
    Optical,
    Network
}

public enum BitLockerStatus
{
    NotEncrypted,
    Encrypted,
    Locked,
    Unlocked
}

public enum PartitionScheme
{
    Unknown,
    MBR,
    GPT
}

public class DriveInfoModel
{
    public string DriveLetter { get; set; } = "";
    public string VolumeName { get; set; } = "";
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
    public long UsedSpace { get; set; }
    public string FileSystem { get; set; } = "";
    public StorageType DriveType { get; set; }
    public bool IsSystemDrive { get; set; }
    public BitLockerStatus BitLockerStatus { get; set; }
    public PartitionScheme PartitionScheme { get; set; }
    
    public string DisplayName => $"{DriveLetter} - {VolumeName}";
    
    public string DriveTypeDisplay => DriveType switch
    {
        StorageType.HDD => "HDD",
        StorageType.SSD => "SSD",
        StorageType.NVMe => "NVMe",
        StorageType.eMMC => "eMMC",
        StorageType.USB => "USB",
        StorageType.Optical => "Optical",
        StorageType.Network => "Network",
        _ => "Unknown"
    };
    
    public string BitLockerDisplay => BitLockerStatus switch
    {
        BitLockerStatus.Locked => "ðŸ”’ Locked",
        BitLockerStatus.Unlocked => "ðŸ”“ Unlocked",
        BitLockerStatus.Encrypted => "ðŸ” Encrypted",
        _ => ""
    };
        public string PartitionSchemeDisplay => PartitionScheme switch
    {
        PartitionScheme.MBR => "MBR",
        PartitionScheme.GPT => "GPT",
        _ => "Unknown"
    };
        public bool IsBitLockerProtected => BitLockerStatus != BitLockerStatus.NotEncrypted;
    public bool IsBitLockerLocked => BitLockerStatus == BitLockerStatus.Locked;
    
    public string TotalSizeDisplay => FormatBytes(TotalSize);
    public string FreeSpaceDisplay => FormatBytes(FreeSpace);
    public string UsedSpaceDisplay => FormatBytes(UsedSpace);
    
    public double UsedPercentage => TotalSize > 0 
        ? (double)UsedSpace / TotalSize * 100 
        : 0;

    public bool IsStorageCritical => UsedPercentage >= 90;
    
    public string FullDisplayText => 
        $"{DriveLetter} {VolumeName} ({DriveTypeDisplay}) - {UsedSpaceDisplay} / {TotalSizeDisplay}" +
        (IsBitLockerProtected ? $" {BitLockerDisplay}" : "");
    
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        
        return $"{size:0.##} {sizes[order]}";
    }
}

