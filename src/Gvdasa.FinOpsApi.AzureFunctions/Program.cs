using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Azure.Identity;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Configurar Application Insights apenas em produção
        string ambiente = Environment.GetEnvironmentVariable("ambiente") ?? "local";
        bool isProducao = !ambiente.Equals("local", StringComparison.OrdinalIgnoreCase);
        
        if (isProducao)
        {
            services.AddApplicationInsightsTelemetryWorkerService();
            services.ConfigureFunctionsApplicationInsights();
        }

        // Configuração de autenticação Azure
        services.AddSingleton<DefaultAzureCredential>();
        
        // Configurar HttpClient para APIs do Azure
        services.AddHttpClient();
        
        // Configurações específicas para local development
        if (!isProducao)
        {
            // Mock de serviços para desenvolvimento local
            Console.WriteLine("[INFO] Ambiente LOCAL - usando configurações de desenvolvimento");
        }
    })
    .Build();

host.Run();