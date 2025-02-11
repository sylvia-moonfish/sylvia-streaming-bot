using Microsoft.Extensions.Configuration;

namespace sylvia_streaming_bot
{
    public class Configuration
    {
        private static IConfiguration? _config;
        private static readonly object _lock = new object();

        public static IConfiguration GetConfig()
        {
            if (_config == null)
            {
                lock (_lock)
                {
                    IConfigurationBuilder builder = new ConfigurationBuilder()
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

                    _config = builder.Build();
                }
            }

            return _config;
        }
    }
}
