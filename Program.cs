using Microsoft.Extensions.Hosting;

await Host.CreateDefaultBuilder(args).Build().RunAsync().ConfigureAwait(false);
