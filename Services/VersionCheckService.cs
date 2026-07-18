using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using VPetLLM.Core.Services;

namespace VPetLLM.Services
{
    public sealed class PluginVersionInfo
    {
        internal PluginVersionInfo(int numericValue, string rawValue)
        {
            NumericValue = numericValue;
            RawValue = rawValue;
            DisplayText = FormatDisplayText(rawValue);
        }

        public int NumericValue { get; }
        public string RawValue { get; }
        public string DisplayText { get; }

        private static string FormatDisplayText(string rawValue)
        {
            return rawValue.Length >= 4
                ? $"{rawValue[0]}.{rawValue.Substring(1, 2)}.{rawValue.Substring(3)}"
                : rawValue;
        }
    }

    public sealed class VersionCheckResult
    {
        private VersionCheckResult(
            bool succeeded,
            PluginVersionInfo? currentVersion,
            PluginVersionInfo? latestVersion,
            string? errorMessage)
        {
            Succeeded = succeeded;
            CurrentVersion = currentVersion;
            LatestVersion = latestVersion;
            ErrorMessage = errorMessage;
        }

        public bool Succeeded { get; }
        public PluginVersionInfo? CurrentVersion { get; }
        public PluginVersionInfo? LatestVersion { get; }
        public bool UpdateAvailable => Succeeded
            && CurrentVersion is not null
            && LatestVersion is not null
            && VersionCheckService.IsUpdateAvailable(CurrentVersion.NumericValue, LatestVersion.NumericValue);
        public string? ErrorMessage { get; }

        internal static VersionCheckResult Success(PluginVersionInfo current, PluginVersionInfo latest)
            => new(true, current, latest, null);

        internal static VersionCheckResult Failure(string message, PluginVersionInfo? current = null)
            => new(false, current, null, message);
    }

    public sealed class VersionCheckService
    {
        internal const string RemoteInfoUrl =
            "https://raw.githubusercontent.com/ycxom/VPetLLM/refs/heads/main/3000_VPetLLM/info.lps";

        private static readonly Regex VersionFieldPattern = new(
            @"(?:^|\|)ver#(?<version>\d+):(?:\||$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly Setting.PluginStoreSetting? _pluginStoreSettings;

        public VersionCheckService(Setting.PluginStoreSetting? pluginStoreSettings)
        {
            _pluginStoreSettings = pluginStoreSettings;
        }

        public async Task<VersionCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            PluginVersionInfo? currentVersion = null;
            try
            {
                var localInfoFile = FindLocalInfoFile();
                if (localInfoFile is null)
                {
                    return VersionCheckResult.Failure("Local info.lps was not found.");
                }

                currentVersion = ParseVersion(await File.ReadAllTextAsync(localInfoFile, cancellationToken));
                if (currentVersion is null)
                {
                    return VersionCheckResult.Failure("Local info.lps does not contain a valid ver field.");
                }

                var decision = ProxyRoutingPolicy.ResolvePluginStore(
                    _pluginStoreSettings?.UseProxy == true,
                    _pluginStoreSettings?.ProxyUrl);
                var requestUrl = ProxyRoutingPolicy.BuildPluginStoreUrl(decision, RemoteInfoUrl);

                using var handler = ProxyRoutingPolicy.CreatePluginStoreHandler(decision);
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("VPetLLM-VersionCheck/1.0");

                var remoteInfo = await client.GetStringAsync(requestUrl, cancellationToken);
                var latestVersion = ParseVersion(remoteInfo);
                if (latestVersion is null)
                {
                    return VersionCheckResult.Failure(
                        "Remote info.lps does not contain a valid ver field.",
                        currentVersion);
                }

                return VersionCheckResult.Success(currentVersion, latestVersion);
            }
            catch (Exception ex)
            {
                return VersionCheckResult.Failure(ex.GetBaseException().Message, currentVersion);
            }
        }

        internal static PluginVersionInfo? ParseVersion(string infoLpsContent)
        {
            if (string.IsNullOrWhiteSpace(infoLpsContent))
            {
                return null;
            }

            var match = VersionFieldPattern.Match(infoLpsContent);
            if (!match.Success)
            {
                return null;
            }

            var rawValue = match.Groups["version"].Value;
            return int.TryParse(rawValue, out var numericValue)
                ? new PluginVersionInfo(numericValue, rawValue)
                : null;
        }

        internal static bool IsUpdateAvailable(int currentVersion, int latestVersion)
        {
            return latestVersion > currentVersion;
        }

        internal static string? FindLocalInfoFile()
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(assemblyLocation))
            {
                return null;
            }

            var directory = new DirectoryInfo(Path.GetDirectoryName(assemblyLocation)!);
            for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
            {
                var adjacentCandidate = Path.Combine(directory.FullName, "info.lps");
                if (File.Exists(adjacentCandidate))
                {
                    return adjacentCandidate;
                }

                var repositoryCandidate = Path.Combine(directory.FullName, "3000_VPetLLM", "info.lps");
                if (File.Exists(repositoryCandidate))
                {
                    return repositoryCandidate;
                }
            }

            return null;
        }
    }
}
