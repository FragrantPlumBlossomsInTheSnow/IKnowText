using System.IO;
using SnapActions.Config;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// 改名（SnapActions → IKnowText）后的一次性数据迁移：设置与脚本要跟着走，日志不搬，绝不覆盖已有文件。
/// </summary>
public class LegacyDataMigrationTests
{
    [Fact]
    public void MigratesSettingsBackupsAndScriptsOnceWithoutOverwriting()
    {
        var root = Directory.CreateTempSubdirectory("iknowtext-migrate-").FullName;
        try
        {
            var oldDir = Path.Combine(root, "SnapActions");
            var newDir = Path.Combine(root, "IKnowText");
            Directory.CreateDirectory(Path.Combine(oldDir, "scripts"));
            Directory.CreateDirectory(Path.Combine(oldDir, "logs"));
            File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{\"Theme\":\"dark\"}");
            File.WriteAllText(Path.Combine(oldDir, "settings.json.broken-20260101"), "{}");
            File.WriteAllText(Path.Combine(oldDir, "scripts", "my-action.js"), "function JSAction(t){return t;}");
            File.WriteAllText(Path.Combine(oldDir, "logs", "2026-01-01.log"), "log line");

            var copied = LegacyDataMigration.Migrate(oldDir, newDir);

            Assert.Equal(3, copied); // settings.json + 备份 + 脚本；日志不迁移
            Assert.Equal("{\"Theme\":\"dark\"}", File.ReadAllText(Path.Combine(newDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(newDir, "settings.json.broken-20260101")));
            Assert.Equal("function JSAction(t){return t;}",
                File.ReadAllText(Path.Combine(newDir, "scripts", "my-action.js")));
            Assert.False(Directory.Exists(Path.Combine(newDir, "logs")));

            // 已经迁移过（新目录有 settings.json）：不再动作，也不覆盖新目录里被改过的文件
            File.WriteAllText(Path.Combine(oldDir, "scripts", "my-action.js"), "changed later");
            Assert.Equal(0, LegacyDataMigration.Migrate(oldDir, newDir));
            Assert.Equal("function JSAction(t){return t;}",
                File.ReadAllText(Path.Combine(newDir, "scripts", "my-action.js")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SkipsWhenLegacyDirectoryIsMissing()
    {
        var root = Directory.CreateTempSubdirectory("iknowtext-migrate-").FullName;
        try
        {
            Assert.Equal(0, LegacyDataMigration.Migrate(Path.Combine(root, "missing"), Path.Combine(root, "new")));
            Assert.False(Directory.Exists(Path.Combine(root, "new")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PrefersTheNewestLegacyDirectoryAndFallsThroughWhenItHasNoSettings()
    {
        var root = Directory.CreateTempSubdirectory("iknowtext-migrate-").FullName;
        try
        {
            var misspelled = Path.Combine(root, "IKonwText");
            var original = Path.Combine(root, "SnapActions");
            var target = Path.Combine(root, "IKnowText");
            Directory.CreateDirectory(misspelled);
            Directory.CreateDirectory(original);
            File.WriteAllText(Path.Combine(misspelled, "settings.json"), "{\"From\":\"misspelled\"}");
            File.WriteAllText(Path.Combine(original, "settings.json"), "{\"From\":\"original\"}");

            // 靠前的目录有数据 → 只用它，不跨目录合并
            var source = LegacyDataMigration.MigrateFirstAvailable([misspelled, original], target);
            Assert.Equal(misspelled, source);
            Assert.Contains("misspelled", File.ReadAllText(Path.Combine(target, "settings.json")));

            // 靠前的目录只有日志（没有 settings.json）→ 回退到下一个候选
            var target2 = Path.Combine(root, "IKnowText2");
            var emptyNewer = Path.Combine(root, "newer-empty");
            Directory.CreateDirectory(Path.Combine(emptyNewer, "logs"));
            File.WriteAllText(Path.Combine(emptyNewer, "logs", "old.log"), "log");
            var source2 = LegacyDataMigration.MigrateFirstAvailable([emptyNewer, original], target2);
            Assert.Equal(original, source2);
            Assert.Contains("original", File.ReadAllText(Path.Combine(target2, "settings.json")));

            // 全都不存在 → null，且不创建目标目录
            var target3 = Path.Combine(root, "IKnowText3");
            Assert.Null(LegacyDataMigration.MigrateFirstAvailable(
                [Path.Combine(root, "nope1"), Path.Combine(root, "nope2")], target3));
            Assert.False(Directory.Exists(target3));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
