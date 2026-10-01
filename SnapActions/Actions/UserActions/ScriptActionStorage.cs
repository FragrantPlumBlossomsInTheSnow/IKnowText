using System.IO;
using SnapActions.Config;

namespace SnapActions.Actions.UserActions;

/// <summary>
/// JS 脚本动作的代码存储：脚本保存为数据目录 scripts\{ScriptFile} 下的独立 .js 文件，
/// settings.json 只存文件名，避免多行代码/引号在 JSON 中转义出错。旧数据的内嵌 Code 仍兼容读取。
/// </summary>
internal static class ScriptActionStorage
{
    internal static string ScriptsDirectory => Path.Combine(RuntimePaths.DataDirectory, "scripts");

    /// <summary>脚本源码：ScriptFile 非空读文件（文件缺失返回 null），否则回退内嵌 Code（旧数据兼容）。</summary>
    internal static string? LoadCode(UserAction def)
    {
        if (!string.IsNullOrWhiteSpace(def.ScriptFile))
        {
            var path = PathFor(def.ScriptFile);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return def.Code;
    }

    /// <summary>把源码写入独立 .js 文件并返回文件名；失败返回 null。</summary>
    internal static string? SaveCode(UserAction def, string code)
    {
        try
        {
            Directory.CreateDirectory(ScriptsDirectory);
            var fileName = def.Id + ".js";
            File.WriteAllText(Path.Combine(ScriptsDirectory, fileName), code);
            return fileName;
        }
        catch { return null; }
    }

    /// <summary>删除动作的脚本文件（best effort，不抛异常）。</summary>
    internal static void Delete(UserAction def)
    {
        if (string.IsNullOrWhiteSpace(def.ScriptFile)) return;
        try { File.Delete(PathFor(def.ScriptFile)); } catch { /* best effort */ }
    }

    /// <summary>按文件名解析路径，防路径穿越（ScriptFile 来自配置文件）。</summary>
    private static string PathFor(string scriptFile)
        => Path.Combine(ScriptsDirectory, Path.GetFileName(scriptFile));
}
