namespace DataHub.Api.Hosting;

/// <summary>
/// 決定 content root：appsettings.json 同相對路徑嘅 checkpoint 目錄（預設 data/checkpoints）都跟住佢。
/// </summary>
internal static class ContentRootResolver
{
    private const string SettingsFile = "appsettings.json";

    /// <summary>
    /// 優先用工作目錄（dotnet run、Visual Studio F5 同 Docker 嘅 WORKDIR 都係咁，行為同 ASP.NET Core 預設一致）。
    /// 已發佈嘅 app 由其他目錄啟動（Task Scheduler、服務、捷徑）時，工作目錄入面冇 appsettings.json，
    /// 就改用 app 所在目錄，唔使要求用戶先 cd 入去，亦避免因為搵唔到設定而用錯目錄。
    /// </summary>
    public static string Resolve(string workingDirectory, string appBaseDirectory)
    {
        if (File.Exists(Path.Combine(workingDirectory, SettingsFile)))
        {
            return workingDirectory;
        }

        return File.Exists(Path.Combine(appBaseDirectory, SettingsFile))
            ? Path.TrimEndingDirectorySeparator(appBaseDirectory)
            : workingDirectory;
    }
}
