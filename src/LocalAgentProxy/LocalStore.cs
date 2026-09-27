using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LocalAgentProxy;

public sealed record ProxyOptions
{
    public string Url { get; init; } = "http://127.0.0.1:17432";
    public string ClaudePath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
    public int QueueCapacity { get; init; } = 32;
    public int MaxContinuations { get; init; } = 64;
    public int GenerationSeconds { get; init; } = 120;
    public int ContinuationSeconds { get; init; } = 600;
    public int MaxRequestBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxOutputCharacters { get; init; } = 16 * 1024 * 1024;
    public string[] Models { get; init; } = ["claude-opus-5-5", "sonnet", "opus", "haiku"];
    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1"
            || uri.AbsolutePath != "/" || Url.EndsWith('/') || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Url must be http://127.0.0.1:PORT with no trailing slash.");
        if (QueueCapacity is < 1 or > 1024 || MaxContinuations is < 1 or > 256 || GenerationSeconds is < 1 or > 1800
            || ContinuationSeconds is < 1 or > 1500 || MaxRequestBytes is < 1024 or > 64 * 1024 * 1024
            || MaxOutputCharacters is < 1024 or > 128 * 1024 * 1024)
            throw new InvalidOperationException("Configuration limits are out of range.");
        if (Models is null || Models.Length > 128 || Models.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 128))
            throw new InvalidOperationException("Invalid model discovery list.");
    }
}
public sealed record ClientKey(string Id, string Name, string Hash);

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root { get; }
    public LocalStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAgentProxy"));
        var directory = Directory.CreateDirectory(Root);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
    }
    public ProxyOptions Options()
    {
        var path = Path.Combine(Root, "config.json");
        using (Lock("config.lock")) { if (!File.Exists(path)) Write(path, new ProxyOptions()); }
        var options = JsonSerializer.Deserialize<ProxyOptions>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Invalid configuration.");
        options.Validate();
        return options;
    }
    public List<ClientKey> Clients()
    {
        var path = Path.Combine(Root, "clients.json");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<List<ClientKey>>(stream) ?? [];
    }
    public (string Id, string Key) AddClient(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new InvalidOperationException("Use an application name of 1..100 printable characters.");
        using var guard = Lock("clients.lock");
        var clients = Clients();
        var id = Guid.NewGuid().ToString("N");
        var key = "lap_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        clients.Add(new ClientKey(id, name, Hash(key)));
        Write(Path.Combine(Root, "clients.json"), clients);
        return (id, key);
    }
    public void RemoveClient(string id)
    {
        using var guard = Lock("clients.lock");
        var clients = Clients();
        if (clients.RemoveAll(c => c.Id == id) == 0) throw new InvalidOperationException("Unknown client ID.");
        Write(Path.Combine(Root, "clients.json"), clients);
    }
    public string? Authenticate(string? header)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 256) return null;
        var hash = Encoding.ASCII.GetBytes(Hash(header[7..]));
        return Clients().FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(c.Hash)))?.Id;
    }
    public FileStream Lock(string file) => new(Path.Combine(Root, file), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void CleanAbandonedRuns()
    {
        var runs = Path.GetFullPath(Path.Combine(Root, "runs"));
        if (!Directory.Exists(runs)) return;
        if (new DirectoryInfo(runs).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Owned runs directory must not be a link.");
        foreach (var dir in new DirectoryInfo(runs).EnumerateDirectories())
        {
            if (!Guid.TryParseExact(dir.Name, "N", out _) || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (!Path.GetFullPath(dir.FullName).StartsWith(runs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run directory escaped its owned root.");
            dir.Delete(recursive: true);
        }
    }
    public string ManagementKey()
    {
        var path = Path.Combine(Root, "management.key");
        using var guard = Lock("management.lock");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        return File.ReadAllText(path);
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Write<T>(string path, T value)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }
    public async Task<bool> SubscriptionReadyAsync(ProxyOptions options, CancellationToken ct)
    {
        if (!File.Exists(options.ClaudePath)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(options.ClaudePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
        start.ArgumentList.Add("auth"); start.ArgumentList.Add("status");
        CliRunner.PrepareEnvironment(start);
        using var process = Process.Start(start);
        if (process is null) return false;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            using var data = JsonDocument.Parse(await output);
            await error;
            return process.ExitCode == 0 && data.RootElement.GetProperty("loggedIn").GetBoolean()
                && data.RootElement.GetProperty("authMethod").GetString() == "claude.ai"
                && data.RootElement.GetProperty("apiProvider").GetString() == "firstParty";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or OperationCanceledException) { return false; }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
