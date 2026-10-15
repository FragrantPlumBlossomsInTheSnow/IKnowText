using System.IO;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// ScriptActionStorage 是 internal（生产程序集已对测试程序集开 InternalsVisibleTo）：
/// 脚本源码写到数据目录的 scripts\{Id}.js，settings.json 只存文件名。
/// 测试统一用 unittest_ 前缀 + GUID 文件名，并在 Dispose 里清掉，避免污染真实脚本目录。
/// </summary>
public class ScriptActionStorageTests : IDisposable
{
    private const string Prefix = "unittest_";

    private static string ScriptsDir => ScriptActionStorage.ScriptsDirectory;

    private static string NewId() => Prefix + Guid.NewGuid().ToString("N");

    private static UserAction Def(string id, string? scriptFile = null, string? code = null) => new()
    {
        Id = id,
        Name = id,
        ScriptFile = scriptFile ?? "",
        Code = code ?? "",
    };

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(ScriptsDir)) return;
            foreach (var path in Directory.EnumerateFiles(ScriptsDir, Prefix + "*.js"))
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── 保存 / 读取 ───────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_RoundTripsAndCreatesDirectory()
    {
        var id = NewId();
        var def = Def(id);

        var fileName = ScriptActionStorage.SaveCode(def, "JSAction(text) { return text; }");

        Assert.Equal(id + ".js", fileName);                 // 文件名由 Id 决定
        Assert.True(Directory.Exists(ScriptsDir));          // SaveCode 按需创建 scripts 目录
        Assert.True(File.Exists(Path.Combine(ScriptsDir, fileName!)));

        def.ScriptFile = fileName!;
        Assert.Equal("JSAction(text) { return text; }", ScriptActionStorage.LoadCode(def));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        // 同名文件直接覆盖：改名/重写脚本不会产生重复文件。
        var def = Def(NewId());
        ScriptActionStorage.SaveCode(def, "first");
        var fileName = ScriptActionStorage.SaveCode(def, "second");

        def.ScriptFile = fileName!;
        Assert.Equal("second", ScriptActionStorage.LoadCode(def));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_NoScriptFile_FallsBackToEmbeddedCode(string? scriptFile)
    {
        // 旧数据兼容：ScriptFile 未设置（含纯空白）时读内嵌 Code，不碰文件系统。
        var def = Def(NewId(), scriptFile, "JSAction(text) { return text; }");
        Assert.Equal("JSAction(text) { return text; }", ScriptActionStorage.LoadCode(def));
    }

    [Fact]
    public void Load_ScriptFileMissing_ReturnsNullWithoutFallingBack()
    {
        // ScriptFile 非空即只认文件：缺失返回 null（上层据此报「脚本文件缺失」），
        // 不会静默回退到内嵌 Code。
        var def = Def(NewId(), NewId() + ".js", "embedded fallback");
        Assert.Null(ScriptActionStorage.LoadCode(def));
    }

    // ── 路径安全（ScriptFile 来自 settings.json，属不可信输入）─────

    [Fact]
    public void Load_TraversalScriptFile_IsStrippedToFileName()
    {
        // 在 scripts 目录之外（数据目录根）放一个同名文件：若实现直接拼接路径就会读到它。
        var probe = NewId() + ".js";
        var dataDir = Path.GetDirectoryName(ScriptsDir)!;
        var outside = Path.Combine(dataDir, probe);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(outside, "OUTSIDE");
        try
        {
            var def = Def(NewId(), @"..\" + probe);
            Assert.Null(ScriptActionStorage.LoadCode(def)); // 穿越被 Path.GetFileName 挡下
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Load_AbsoluteScriptFile_IsStrippedToFileName()
    {
        // 绝对路径同样只取文件名，不能借配置读任意系统文件。
        var def = Def(NewId(), @"C:\Windows\win.ini");
        Assert.Null(ScriptActionStorage.LoadCode(def));
    }

    // ── 删除 ─────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesScriptFileAndIsIdempotent()
    {
        var def = Def(NewId());
        var fileName = ScriptActionStorage.SaveCode(def, "code")!;
        def.ScriptFile = fileName;
        Assert.True(File.Exists(Path.Combine(ScriptsDir, fileName)));

        ScriptActionStorage.Delete(def);
        Assert.False(File.Exists(Path.Combine(ScriptsDir, fileName)));

        ScriptActionStorage.Delete(def); // 再次删除（文件已不存在）不抛异常
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Delete_NoScriptFile_IsNoOp(string? scriptFile)
    {
        ScriptActionStorage.Delete(Def(NewId(), scriptFile)); // 无文件可删，不抛异常
    }
}
