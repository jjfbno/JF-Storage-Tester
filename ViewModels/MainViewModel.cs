using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using JFStorageTester.Models;
using JFStorageTester.Services;

namespace JFStorageTester.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly DriveDetectionService _driveService;
    private readonly ThemeService _themeService;
    
    private DriveInfoModel? _selectedDrive;
    private int _selectedTabIndex = 0;  // Explicitly default to Surface Test tab
    private bool _isTestRunning;
    
    // Child ViewModels
    public SurfaceTestViewModel SurfaceTestViewModel { get; }
    public SpeedTestViewModel SpeedTestViewModel { get; }
    
    public MainViewModel()
    {
        _driveService = DriveDetectionService.Instance;
        _themeService = ThemeService.Instance;
        
        // Initialize child ViewModels
        SurfaceTestViewModel = new SurfaceTestViewModel();
        SpeedTestViewModel = new SpeedTestViewModel();
        
        Drives = _driveService.Drives;
        
        // Select first drive by default
        if (Drives.Count > 0)
        {
            SelectedDrive = Drives[0];
        }
        
        // Subscribe to drive changes
        _driveService.DrivesChanged += (s, e) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(Drives));
                
                // Reselect drive if it still exists, otherwise select first
                if (SelectedDrive != null)
                {
                    var existingDrive = Drives.FirstOrDefault(d => d.DriveLetter == SelectedDrive.DriveLetter);
                    if (existingDrive != null)
                    {
                        SelectedDrive = existingDrive;
                    }
                    else if (Drives.Count > 0)
                    {
                        SelectedDrive = Drives[0];
                    }
                }
                else if (Drives.Count > 0)
                {
                    SelectedDrive = Drives[0];
                }
            });
        };
        
        // Commands
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        OpenDiskManagementCommand = new RelayCommand(OpenDiskManagement);
        OpenDiskPartCommand = new RelayCommand(OpenDiskPart);
        
        // Subscribe to theme changes to update UI
        _themeService.ThemeChanged += (s, e) => OnPropertyChanged(nameof(CurrentTheme));
    }
    
    public ObservableCollection<DriveInfoModel> Drives { get; }
    
    public DriveInfoModel? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            var previousDrive = _selectedDrive;
            if (SetProperty(ref _selectedDrive, value))
            {
                OnPropertyChanged(nameof(CanUseSpeedTest));
                OnPropertyChanged(nameof(SpeedTestLockedMessage));
                OnPropertyChanged(nameof(IsDriveSelected));
                
                // Reset test results when drive changes
                if (previousDrive != null && value != null && previousDrive.DriveLetter != value.DriveLetter)
                {
                    SurfaceTestViewModel.ResetBlocks();
                    SpeedTestViewModel.ResetResults();
                }
            }
        }
    }
    
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }
    
    public bool IsTestRunning
    {
        get => _isTestRunning;
        set
        {
            if (SetProperty(ref _isTestRunning, value))
            {
                OnPropertyChanged(nameof(CanChangeDrive));
                OnPropertyChanged(nameof(CanChangeTab));
                OnPropertyChanged(nameof(CanRefreshDrives));
            }
        }
    }
    
    public bool IsDriveSelected => SelectedDrive != null;
    
    public bool CanChangeDrive => !IsTestRunning;
    
    public bool CanChangeTab => !IsTestRunning;
    
    public bool CanRefreshDrives => !IsTestRunning;
    
    public bool CanUseSpeedTest => SelectedDrive != null && !SelectedDrive.IsBitLockerLocked;
    
    public string SpeedTestLockedMessage => 
        SelectedDrive?.IsBitLockerLocked == true 
            ? "Speed test unavailable: Drive is BitLocker locked" 
            : "";
    
    public AppTheme CurrentTheme => _themeService.CurrentTheme;
    
    // Commands
    public ICommand OpenSettingsCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand OpenDiskManagementCommand { get; }
    public ICommand OpenDiskPartCommand { get; }
    
    private void OpenSettings()
    {
        var settingsWindow = new Views.SettingsWindow
        {
            Owner = Application.Current.MainWindow
        };
        settingsWindow.ShowDialog();
    }
    
    private void RefreshDrives()
    {
        _driveService.RefreshDrives();
    }
    
    private void OpenDiskManagement()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskmgmt.msc",
                UseShellExecute = true
            });
        }
        catch { }
    }
    
    private void OpenDiskPart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k diskpart",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
