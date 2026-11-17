using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KeedParsecNative.Infrastructure.Configuration;

public static class AppConfigLoader
{
    /// <summary>
    /// Loads configuration from appsettings.json, appsettings.{Environment}.json,
    /// environment variables (prefix KEEPARSEC_), and user secrets in Development.
    /// TURN credentials should be supplied via environment variables
    /// (KEEDPARSEC_Turn__Servers__0__Username etc.), never committed to source.
    /// </summary>
    public static AppConfig Load(string basePath, string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables(prefix: "KEEDPARSEC_")
            .AddUserSecrets<AppConfig>(optional: true)
            .Build();

        var config = new AppConfig();
        configuration.GetSection(AppConfig.SectionName).Bind(config);
        return config;
    }

    public static IServiceCollection AddKeedParsecConfiguration(
        this IServiceCollection services,
        string basePath,
        string environmentName)
    {
        var config = Load(basePath, environmentName);
        services.AddSingleton(config);
        return services;
    }
}
