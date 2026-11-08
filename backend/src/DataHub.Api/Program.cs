using DataHub.Api.Hosting;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ContentRootResolver.Resolve(Directory.GetCurrentDirectory(), AppContext.BaseDirectory),
});

// 冇指定 URL（ASPNETCORE_URLS / --urls / launchSettings）時預設用 5080，
// 令 dotnet run、直接執行 DataHub.Api.exe 同 Visual Studio F5 嘅行為一致。
if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls("http://localhost:5080");
}

builder.Services.AddDataHub(builder.Configuration);

var app = builder.Build();
app.UseDataHub();
app.Run();

// 供 WebApplicationFactory<Program> 做整合測試。
public partial class Program;
