using Gvdasa.GVmodeloexemploapi.Infra.Services;
using Serilog;
using GVdasa.Permissoes.Options;
using GVdasa.Permissoes.DependencyInjection;
using Gvdasa.GVmodeloexemploapi.WebApi;
using GVdasa.Cac.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json.Converters;
using Gvdasa.GVmodeloexemploapi.WebApi.Extensions;
using Gvdasa.GVmodeloexemploapi.WebApi.Middlewares;
using Gvdasa.GVmodeloexemploapi.WebApi.Providers;
using System.Reflection;
using Gvdasa.GVmodeloexemploapi.Domain.DependencyInjection;
using Gvdasa.GVmodeloexemploapi.WebApi.Filters;
using FluentValidation;
using SharpGrip.FluentValidation.AutoValidation.Mvc.Extensions;
using Gvdasa.GVmodeloexemploapi.Infra.Extensions;


var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

string MyAllowSpecificOrigins = "_myAllowSpecificOrigins";
ConfigurationManager Configuration = builder.Configuration;

CriarLogger();

if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddDateOnlyTimeOnlyStringConverters();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetCallingAssembly()));
builder.Services.AddScoped<IJwtProvider, JwtProvider>();
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
builder.Services.AddValidatorsFromAssembly(Assembly.GetCallingAssembly());
builder.Services.AddFluentValidationAutoValidation(config =>
{
    config.DisableBuiltInModelValidation = true;
    config.EnableFormBindingSourceAutomaticValidation = true;
});

string urlPermissoes = builder.Configuration.GetRequiredSection("Permissoes:Url").Value!;
var sbusOptions = builder.Configuration.GetRequiredSection("Permissoes:ServiceBus").Get<ServiceBusOptions>();

builder.Services.UsarAutorizacaoPorPermissao(new PermissoesOptions()
{
    Url = urlPermissoes,
    ServiceBusOptions = sbusOptions
});

builder.Services.UsarIntegracaoComCac(Configuration.GetRequiredSection("Cac").Get<GVdasa.Cac.Options.CacOptions>()!);
builder.Services.AdicionarDominio(builder.Configuration);

// Registrar Cost Optimizer
builder.Services.AdicionarCostOptimizer(builder.Configuration);

builder.Services.AddControllers(opt =>
{
    opt.Filters.Add(typeof(TratamentoGlobalDeExcecao));
})
.AddNewtonsoftJson(options =>
{
    options.SerializerSettings.Converters.Add(new StringEnumConverter());
});

var origensCors = Configuration["OrigensCORS"]!.Split(";");
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: MyAllowSpecificOrigins,
        builder =>
        {
            builder.WithOrigins(origensCors)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();

StartupSwagger.ConfigureServices(builder);

var app = builder.Build();
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.UseCors(MyAllowSpecificOrigins);
app.ExecutarMigrations();

app.UseSerilogRequestLogging(options =>
{
    // Customize the message template
    options.MessageTemplate = "Handled {RequestPath}";

    // Attach additional properties to the request completion event
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
        diagnosticContext.Set("RemoteIp", httpContext.Connection.RemoteIpAddress?.ToString());
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.GetValueOrDefault("User-Agent"));
        diagnosticContext.Set("IdTenant", httpContext.Request.Headers.GetValueOrDefault("IdTenant"));
        diagnosticContext.Set("IdCorrelacao", httpContext.Request.Headers.GetValueOrDefault("IdCorrelacao"));
    };
});


app.UseMiddleware<RequestLoggerMiddleware>();
app.MapControllers();
StartupSwagger.ConfigureApp(app);

app.Run();

void CriarLogger()
{
    var configuration = new ConfigurationBuilder()
        .AddConfiguration(Configuration)
        .Build();

    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration)
        .CreateLogger();
}

[ExcludeFromCodeCoverage]
public partial class Program { }
