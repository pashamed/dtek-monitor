using DtekBot.WebApp;
using DtekBot.WebApp.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The app lives under /app/, but the API is at the origin root.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/") });
builder.Services.AddScoped<TelegramService>();
builder.Services.AddScoped<ApiClient>();

await builder.Build().RunAsync();
