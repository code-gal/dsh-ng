using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshNgDesktop;

internal sealed class AppConfiguration
{
    public int Port { get; init; }
}

internal sealed class PackageManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfiguration))]
[JsonSerializable(typeof(PackageManifest))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}

internal static class Configuration
{
    private const string ApplicationDirectoryName = "dsh-ng-desktop";
    private const string ConfigurationFileName = "appsettings.json";

    public static AppConfiguration Load()
    {
        Directory.CreateDirectory(DataDirectory);

        var configuration = Read();
        if (configuration is not null && IsValidPort(configuration.Port))
        {
            return configuration;
        }

        configuration = new AppConfiguration { Port = FindAvailablePort() };
        Write(configuration);
        return configuration;
    }

    internal static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationDirectoryName);

    internal static string NodeModulesDirectory => Path.Combine(DataDirectory, "node_modules");

    internal static string InstallationStagingDirectory => Path.Combine(DataDirectory, "installation-staging");

    private static string ConfigurationPath => Path.Combine(DataDirectory, ConfigurationFileName);

    private static AppConfiguration? Read()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(ConfigurationPath);
            return JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.AppConfiguration);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write(AppConfiguration configuration)
    {
        var temporaryPath = ConfigurationPath + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, configuration, AppJsonSerializerContext.Default.AppConfiguration);
        }

        File.Move(temporaryPath, ConfigurationPath, true);
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsValidPort(int port) => port is > 0 and <= ushort.MaxValue;
}
