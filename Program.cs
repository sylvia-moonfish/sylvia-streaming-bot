using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using sylvia_streaming_bot;

public class Program
{
    private static DiscordSocketClient? _client;
    private static InteractionService? _interactionService;
    private static IServiceProvider? _serviceProvider;
    private static IConfiguration? _config;

    public static async Task Main()
    {
        // Initialize configuration
        _config = Configuration.GetConfig();

        // Get token using different fallback methods
        string? token = GetDiscordToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "Discord token not found. Please set it in configuration file."
            );

        // Setup dependency injection
        _serviceProvider = ConfigureServices();

        if (_serviceProvider == null)
            throw new InvalidOperationException();

        _client = _serviceProvider.GetRequiredService<DiscordSocketClient>();
        _interactionService = _serviceProvider.GetRequiredService<InteractionService>();

        // Setup event handlers
        _client.Ready += Client_Ready;
        _client.Log += LogAsync;
        _interactionService.Log += LogAsync;

        // Register command handlers
        await _interactionService.AddModuleAsync<CommandModule>(_serviceProvider);

        // Start the client
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Handle slash commands
        _client.InteractionCreated += async (interaction) =>
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            await _interactionService.ExecuteCommandAsync(ctx, _serviceProvider);
        };

        await Task.Delay(-1);
    }

    private static string? GetDiscordToken()
    {
        if (_config == null)
            return null;

        return _config["Discord:BotToken"];
    }

    private static IServiceProvider? ConfigureServices()
    {
        if (_config == null)
            return null;

        return new ServiceCollection()
            .AddSingleton(_config)
            .AddSingleton(
                new DiscordSocketClient(
                    new DiscordSocketConfig
                    {
                        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
                    }
                )
            )
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()))
            .BuildServiceProvider();
    }

    private static async Task Client_Ready()
    {
        if (_config == null || _interactionService == null)
            return;

        // Register slash commands with Discord
        if (IsDebug())
        {
            // Register commands to a specific test guild in debug mode
            var testGuildId = _config.GetValue<ulong>("Discord:TestGuildId");
            await _interactionService.RegisterCommandsToGuildAsync(testGuildId);
        }
        else
        {
            // Register commands globally in production
            await _interactionService.RegisterCommandsGloballyAsync();
        }

        Console.WriteLine("Bot is ready!");
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log.ToString());

        return Task.CompletedTask;
    }
}
