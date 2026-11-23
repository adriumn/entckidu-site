using Microsoft.Extensions.Hosting;

// Starts the Azure Functions worker; the functions themselves are found by their [Function] attribute.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();

host.Run();
