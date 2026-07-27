using ParentalGuard.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ParentalGuard");
builder.Services.AddHostedService<ParentalGuardService>();

var host = builder.Build();
host.Run();
