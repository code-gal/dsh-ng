using DshNgDesktop.Core;

namespace DshNgDesktop.Installer;

public enum InstallChangeKind
{
    Unknown,
    Upgrade,
    Repair,
    Downgrade,
    BuildFlavorChange
}

public sealed record InstallChangeClassification(
    InstallChangeKind Kind,
    string ActionText,
    string DetailText);

/// <summary>
/// Classifies an installer package against the locally persisted package
/// metadata. It never queries a remote release service.
/// </summary>
public static class InstallPackageClassifier
{
    public static InstallChangeClassification Classify(
        InstallPackageMetadata? installed,
        InstallPackageMetadata incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (installed is null || !installed.HasVersion || !incoming.HasVersion ||
            !TryParse(installed.ProductVersion, out var installedVersion) ||
            !TryParse(incoming.ProductVersion, out var incomingVersion))
        {
            return new(
                InstallChangeKind.Unknown,
                "替换安装（保留数据）",
                "无法从旧安装读取可靠版本信息，将替换客户端文件并保留 DSH 数据。不会联网检查更新。");
        }

        if (installed.HasBuildFlavor && incoming.HasBuildFlavor && installed.BuildFlavor != incoming.BuildFlavor)
        {
            return new(
                InstallChangeKind.BuildFlavorChange,
                "切换构建形态（保留数据）",
                $"将从{DescribeFlavor(installed.BuildFlavor)}切换为{DescribeFlavor(incoming.BuildFlavor)}，并保留 DSH 配置、会话、插件、缓存和日志。当前包版本为 {incoming.ProductVersion}。");
        }

        var comparison = Compare(installedVersion, incomingVersion);
        if (comparison < 0)
        {
            return new(
                InstallChangeKind.Upgrade,
                "升级安装（保留数据）",
                $"检测到本地版本 {DisplayVersion(installed.ProductVersion)}，当前安装包为 {incoming.ProductVersion}。将替换客户端文件并保留 DSH 数据。");
        }

        if (comparison > 0)
        {
            return new(
                InstallChangeKind.Downgrade,
                "降级安装（保留数据）",
                $"本地版本 {DisplayVersion(installed.ProductVersion)} 高于当前安装包 {incoming.ProductVersion}。请确认后再替换客户端文件；DSH 数据会保留。");
        }

        if (!installed.HasBuildFlavor && incoming.HasBuildFlavor)
        {
            return new(
                InstallChangeKind.Repair,
                "修复/切换构建形态（保留数据）",
                $"当前安装包与本地版本 {DisplayVersion(installed.ProductVersion)} 相同，但旧安装未记录构建形态；将重新部署客户端文件并保留 DSH 数据。");
        }

        return new(
            InstallChangeKind.Repair,
            "修复/重新安装（保留数据）",
            $"当前安装包与本地版本 {DisplayVersion(installed.ProductVersion)} 相同，将重新部署客户端文件并保留 DSH 数据。");
    }

    private static string DescribeFlavor(ClientBuildFlavor flavor) => flavor switch
    {
        ClientBuildFlavor.Aot => " Native AOT 包",
        ClientBuildFlavor.DotNet => " .NET 依赖包",
        _ => "未知构建形态"
    };

    private static int Compare(SemVersion left, SemVersion right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = left.Patch.CompareTo(right.Patch);
        if (result != 0) return result;

        if (left.PreRelease.Length == 0 && right.PreRelease.Length == 0) return 0;
        if (left.PreRelease.Length == 0) return 1;
        if (right.PreRelease.Length == 0) return -1;

        var count = Math.Min(left.PreRelease.Length, right.PreRelease.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = left.PreRelease[index];
            var rightPart = right.PreRelease[index];
            if (int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber))
            {
                var numericResult = leftNumber.CompareTo(rightNumber);
                if (numericResult != 0) return numericResult;
            }
            else if (int.TryParse(leftPart, out _))
            {
                return -1;
            }
            else if (int.TryParse(rightPart, out _))
            {
                return 1;
            }
            else
            {
                var textResult = string.CompareOrdinal(leftPart, rightPart);
                if (textResult != 0) return textResult;
            }
        }

        return left.PreRelease.Length.CompareTo(right.PreRelease.Length);
    }

    private static bool TryParse(string? value, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var candidate = DisplayVersion(value);
        var withoutBuild = candidate.Split('+', 2)[0];
        var versionParts = withoutBuild.Split('-', 2);
        var core = versionParts[0].Split('.');
        if (core.Length != 3 ||
            !int.TryParse(core[0], out var major) ||
            !int.TryParse(core[1], out var minor) ||
            !int.TryParse(core[2], out var patch) ||
            major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        var prerelease = versionParts.Length == 2
            ? versionParts[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (versionParts.Length == 2 && prerelease.Length == 0)
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, prerelease);
        return true;
    }

    private static string DisplayVersion(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        var separator = Math.Max(candidate.LastIndexOf('/'), candidate.LastIndexOf('\\'));
        return separator >= 0 && separator < candidate.Length - 1
            ? candidate[(separator + 1)..]
            : candidate;
    }

    private readonly record struct SemVersion(int Major, int Minor, int Patch, string[] PreRelease);
}
