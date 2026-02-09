using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace JFStorageTester.Services;

public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("body")]
    public string Body { get; set; } = "";
    
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
    
    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";
    
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public long DownloadSize { get; set; }
    public string? ErrorMessage { get; set; }
}

public class UpdateService
{
    private const string GitHubOwner = "jjfbno";
    private const string GitHubRepo = "JF-Storage-Tester";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    
    private readonly HttpClient _httpClient;
    
    public event EventHandler<double>? DownloadProgressChanged;
    
    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "JFStorageTester");
    }
    
    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
    }
    
    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = CurrentVersion
        };
        
        try
        {
            var response = await _httpClient.GetAsync(GitHubApiUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // No releases yet
                    result.LatestVersion = CurrentVersion;
                    result.UpdateAvailable = false;
                    return result;
                }
                result.ErrorMessage = $"GitHub API returned {response.StatusCode}";
                return result;
            }
            
            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>();
            
            if (release == null)
            {
                result.ErrorMessage = "Could not parse release information";
                return result;
            }
            
            // Parse version from tag (remove 'v' prefix if present)
            var latestVersion = release.TagName.TrimStart('v', 'V');
            result.LatestVersion = latestVersion;
            result.ReleaseNotes = release.Body;
            result.ReleaseUrl = release.HtmlUrl;
            
            // Find the exe asset
            var exeAsset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
            
            // If no portable exe, try setup exe
            exeAsset ??= release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            
            if (exeAsset != null)
            {
                result.DownloadUrl = exeAsset.DownloadUrl;
                result.DownloadSize = exeAsset.Size;
            }
            else
            {
                result.ErrorMessage = "Update available but no download file attached to release. Please download manually from GitHub.";
                return result;
            }
            
            // Compare versions
            result.UpdateAvailable = IsNewerVersion(latestVersion, CurrentVersion);
        }
        catch (HttpRequestException ex)
        {
            result.ErrorMessage = $"Network error: {ex.Message}";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Error checking for updates: {ex.Message}";
        }
        
        return result;
    }
    
    public async Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, CancellationToken ct = default)
    {
        try
        {
            // Get the current exe path
            var currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExePath))
            {
                throw new Exception("Could not determine current executable path");
            }
            
            var currentDir = Path.GetDirectoryName(currentExePath)!;
            var tempPath = Path.Combine(currentDir, "JFStorageTester_update.exe");
            var scriptPath = Path.Combine(currentDir, "update.ps1");
            var currentPid = Environment.ProcessId;
            
            // Download the new version
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var downloadedBytes = 0L;
            
            using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                int bytesRead;
                
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    downloadedBytes += bytesRead;
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)downloadedBytes / totalBytes * 100;
                        DownloadProgressChanged?.Invoke(this, progress);
                    }
                }
            }
            
            // Create PowerShell script that waits for this process to exit
            var scriptContent = @$"
# Update script for JF Storage Tester
$exePath = '{currentExePath.Replace("'", "''")}'
$newPath = '{tempPath.Replace("'", "''")}'
$procId = {currentPid}

# Wait for the app to fully exit
try {{
    $proc = Get-Process -Id $procId -ErrorAction Stop
    Write-Host 'Waiting for application to close...'
    $proc.WaitForExit(60000) | Out-Null
}} catch {{
    # Process already exited
}}

# Extra wait to ensure file handles are released
Start-Sleep -Seconds 3

# Try to delete and replace with retries
$maxRetries = 15
$success = $false

for ($i = 1; $i -le $maxRetries; $i++) {{
    Write-Host ""Attempt $i of $maxRetries...""
    try {{
        if (Test-Path $exePath) {{
            Remove-Item -Path $exePath -Force -ErrorAction Stop
        }}
        Move-Item -Path $newPath -Destination $exePath -Force -ErrorAction Stop
        $success = $true
        Write-Host 'Update successful!'
        break
    }} catch {{
        Write-Host ""Failed: $($_.Exception.Message)""
        Start-Sleep -Seconds 2
    }}
}}

if ($success) {{
    Start-Process -FilePath $exePath
}} else {{
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('Update failed - the application file is still locked. Please close any other instances and try again, or update manually.', 'Update Error', 'OK', 'Error')
}}

# Clean up
Start-Sleep -Seconds 1
Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
";
            
            await File.WriteAllTextAsync(scriptPath, scriptContent, ct);
            
            // Start the PowerShell updater script
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            
            Process.Start(psi);
            
            // Give the script a moment to start
            await Task.Delay(500, ct);
            
            // Exit the application
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    private static bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();
            var currentParts = current.Split('.').Select(int.Parse).ToArray();
            
            // Pad arrays to same length
            var maxLen = Math.Max(latestParts.Length, currentParts.Length);
            Array.Resize(ref latestParts, maxLen);
            Array.Resize(ref currentParts, maxLen);
            
            for (int i = 0; i < maxLen; i++)
            {
                if (latestParts[i] > currentParts[i]) return true;
                if (latestParts[i] < currentParts[i]) return false;
            }
            
            return false; // Versions are equal
        }
        catch
        {
            return false; // If parsing fails, assume no update
        }
    }
}
