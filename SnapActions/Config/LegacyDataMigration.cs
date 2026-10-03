using System.IO;

namespace SnapActions.Config;

/// <summary>
///     改名（SnapActions → IKnowText）后的一次性数据迁移：把旧数据目录里的 settings.json（连同
///     settings.json.broken-* 备份）与所有 scripts\*.js（自定义 JS 动作、翻译引擎脚本）复制到新目录。
///     旧目录保留不删，日志不搬（重新生成即可）。
///     抽成纯函数是为了能对「已迁移过 / 旧目录不存在 / 目标已有同名文件」这些分支写单元测试。
/// </summary>
internal static class LegacyDataMigration
{
    private static readonly string[] Patterns = ["settings.json*", "*.js"];

    /// <summary>
    ///     按给定顺序尝试各个旧目录，返回第一个真正完成迁移的目录（越新的应排在越前）；都没有可迁移内容
    ///     （或目标已有 settings.json）时返回 null。
    /// </summary>
    internal static string? MigrateFirstAvailable(IReadOnlyList<string> legacyDirectories, string targetDirectory)
    {
        foreach (var legacy in legacyDirectories)
            if (Migrate(legacy, targetDirectory) > 0)
                return legacy;
        return null;
    }

    /// <summary>返回复制过去的文件数；目标目录已有 settings.json（迁移过或是新用户）时返回 0。</summary>
    internal static int Migrate(string legacyDirectory, string targetDirectory)
    {
        if (File.Exists(Path.Combine(targetDirectory, "settings.json"))) return 0;
        if (!Directory.Exists(legacyDirectory)) return 0;

        var copied = 0;
        foreach (var pattern in Patterns)
        foreach (var source in Directory.EnumerateFiles(legacyDirectory, pattern, SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetDirectory, Path.GetRelativePath(legacyDirectory, source));
            if (File.Exists(target)) continue; // 绝不覆盖新目录里已有的文件
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            copied++;
        }
        return copied;
    }
}
