using DhDnsSync;

var host = Host.CreateDefaultBuilder(args)
               .ConfigureAppConfiguration(builder => builder.AddEnvironmentVariables())
               .ConfigureServices((hostContext, services) =>
                                  {
                                      var config = hostContext.Configuration;
                                      services.Configure<DnsConfig>(config.GetSection(nameof(DnsConfig)));

                                      services.AddSingleton<IDnsProvider, DreamHostDnsProvider>();

                                      services.AddHostedService<Worker>();
                                  })
               .Build();

await host.RunAsync();
