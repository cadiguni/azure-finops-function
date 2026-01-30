using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();

Console.WriteLine("[INFO] NÍVEL 3 BASIC - Iniciando...");
host.Run();