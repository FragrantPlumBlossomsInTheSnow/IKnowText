using System.Diagnostics;

namespace SnapActions.Config;

/// <summary>
///     通过任务计划程序注册"以最高权限开机自启"。
///     应用本身以 requireAdministrator/highestAvailable 启动，schtasks 调用无需再次提权。
/// </summary>
internal static class AutoStartTask
{
    public const string TaskName = "SnapActions";

    public static bool IsRegistered()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/Query");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(TaskName);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        // 先删旧的，避免残留触发器
        Delete();

        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/Create");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(TaskName);
        psi.ArgumentList.Add("/TR");
        psi.ArgumentList.Add($"\"{exe}\"");
        psi.ArgumentList.Add("/SC");
        psi.ArgumentList.Add("ONLOGON");
        psi.ArgumentList.Add("/RL");
        psi.ArgumentList.Add("HIGHEST");
        psi.ArgumentList.Add("/F");

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            SnapActions.Helpers.Log.Warn($"AutoStartTask.Register 失败: {ex.Message}");
            return false;
        }
    }

    public static bool Delete()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/Delete");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(TaskName);
        psi.ArgumentList.Add("/F");

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}