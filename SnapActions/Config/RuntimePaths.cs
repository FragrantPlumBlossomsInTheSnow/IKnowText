using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SnapActions.Config;

/// <summary>A separate data directory also isolates every instance identifier used by test builds.</summary>
internal static class RuntimePaths
{
    private static readonly string DefaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IKnowText");

    /// <summary>
    ///     改名过程中出现过的旧数据目录，越新的越靠前：先搬写错过拼写的 %AppData%\IKonwText，
    ///     再搬改名前的 %AppData%\SnapActions。第一个能提供 settings.json 的目录生效，不跨目录合并。
    /// </summary>
    private static readonly string[] LegacyDirectories =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IKonwText"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapActions"),
    ];

    internal static string DataDirectory { get; } = ResolveDataDirectory();
    internal static bool IsIsolated { get; } = !DataDirectory.Equals(DefaultDirectory, StringComparison.OrdinalIgnoreCase);
    internal static string InstanceSuffix { get; } = IsIsolated
        ? "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..16]
        : "";

    private static string ResolveDataDirectory()
    {
        var path = Environment.GetEnvironmentVariable("IKONWTEXT_DATA_DIR");
        return string.IsNullOrWhiteSpace(path)
            ? DefaultDirectory
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    ///     一次性数据迁移：新目录里还没有 settings.json 时，从 <see cref="LegacyDirectories"/> 里第一个有数据的
    ///     旧目录复制 settings.json（含 .broken-* 备份）与所有 scripts\*.js（自定义 JS 动作、翻译引擎脚本）。
    ///     旧目录保留不删除，日志不搬；隔离实例（IKONWTEXT_DATA_DIR）不参与迁移。
    /// </summary>
    internal static void MigrateLegacyDataDirectory()
    {
        if (IsIsolated) return;
        try
        {
            var source = LegacyDataMigration.MigrateFirstAvailable(LegacyDirectories, DefaultDirectory);
            if (source != null)
                SnapActions.Helpers.Log.Info($"已从旧数据目录 {source} 迁移设置与脚本到 {DefaultDirectory}");
        }
        catch (Exception ex)
        {
            SnapActions.Helpers.Log.Warn($"迁移旧数据目录失败（将使用新目录的默认设置）：{ex.Message}");
        }
    }
}
