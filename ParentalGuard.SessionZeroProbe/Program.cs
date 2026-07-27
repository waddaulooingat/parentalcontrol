using ParentalGuard.SessionZeroProbe;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ParentalGuardSessionZeroProbe");
builder.Services.AddHostedService<Probe>();

var host = builder.Build();
host.Run();
