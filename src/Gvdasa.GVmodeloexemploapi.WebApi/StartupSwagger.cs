using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Gvdasa.GVmodeloexemploapi.WebApi;

[ExcludeFromCodeCoverage]
public class StartupSwagger
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "GVmodeloexemploapi", Version = "v1" });
            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
            c.UseOneOfForPolymorphism();
            c.UseDateOnlyTimeOnlyStringConverters();
            c.SchemaFilter<EnumSchemaFilter>();
        });
    }

    public static void ConfigureApp(WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Gvdasa.GVmodeloexemploapi.WebApi v1"));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/")
            {
                context.Response.Redirect(context.Request.Path + "swagger", permanent: true);
                return;
            }
            await next();
        });
    }
}

[ExcludeFromCodeCoverage]
public class EnumSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema model, SchemaFilterContext context)
    {
        if (context.Type.IsEnum)
        {
            model.Enum.Clear();
            model.Type = "string";
            model.Format = null;

            foreach (var enumName in Enum.GetNames(context.Type))
            {
                var member = context.Type.GetMember(enumName).FirstOrDefault();
                var enumValue = (Enum)Enum.Parse(context.Type, enumName);
                var enumMemberAttribute = member?.GetCustomAttribute<EnumMemberAttribute>();

                var displayName = enumMemberAttribute?.Value ?? Enum.GetName(context.Type, enumValue);
                model.Enum.Add(new OpenApiString(displayName));
            }
        }
    }
}
