using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace sylvia_streaming_bot
{
    public class Program
    {
        #region Global Static
        private static IConfiguration? _config;
        private static IServiceProvider? _serviceProvider;
        #endregion

        #region Discord Static
        private static string? _discordToken;
        #endregion

        #region Spotify Static
        private static string? _spotifyClientId;
        private static string? _spotifyClientSecret;
        private static EmbedIOAuthServer? _server;
        #endregion

        public static async Task Main()
        {
            _config = Configuration.GetConfig();
            if (_config == null)
                throw new Exception("Failed to initialize config.");

            _discordToken = _config["Discord:BotToken"];
            if (string.IsNullOrWhiteSpace(_discordToken))
                throw new Exception("Discord token is empty.");

            _spotifyClientId = _config["Spotify:ClientId"];
            if (string.IsNullOrWhiteSpace(_spotifyClientId))
                throw new Exception("Spotify client id is empty.");

            _spotifyClientSecret = _config["Spotify:ClientSecret"];
            if (string.IsNullOrWhiteSpace(_spotifyClientSecret))
                throw new Exception("Spotify client secret is empty.");

            _server = new EmbedIOAuthServer(new Uri("http://localhost:5543/callback"), 5543);
            await _server.Start();

            _server.AuthorizationCodeReceived += AuthorizationCodeReceived;
            _server.ErrorReceived += ErrorReceived;

            LoginRequest loginRequest = new LoginRequest(
                _server.BaseUri,
                _spotifyClientId,
                LoginRequest.ResponseType.Code
            )
            {
                Scope = [Scopes.UserModifyPlaybackState, Scopes.UserReadPlaybackState],
            };

            BrowserUtil.Open(loginRequest.ToUri());

            await Task.Delay(-1);
        }

        #region Spotify Event Handlers
        private static async Task AuthorizationCodeReceived(
            object sender,
            AuthorizationCodeResponse response
        )
        {
            if (
                _server == null
                || string.IsNullOrWhiteSpace(_spotifyClientId)
                || string.IsNullOrWhiteSpace(_spotifyClientSecret)
            )
                return;

            await _server.Stop();

            SpotifyClientConfig spotifyClientConfig = SpotifyClientConfig.CreateDefault();
            AuthorizationCodeTokenResponse tokenResponse = await new OAuthClient(
                spotifyClientConfig
            ).RequestToken(
                new AuthorizationCodeTokenRequest(
                    _spotifyClientId,
                    _spotifyClientSecret,
                    response.Code,
                    new Uri("http://localhost:5543/callback")
                )
            );

            await SetUpServices(new SpotifyClient(tokenResponse.AccessToken));
        }

        private static async Task ErrorReceived(object sender, string error, string? state)
        {
            Console.WriteLine("Spotify authorization error: " + error.ToString());

            if (_server != null)
                await _server.Stop();
        }
        #endregion

        #region Service Set Up
        private static async Task SetUpServices(SpotifyClient spotifyClient)
        {
            if (_config == null || string.IsNullOrWhiteSpace(_discordToken))
                return;

            _serviceProvider = new ServiceCollection()
                .AddSingleton(_config)
                .AddSingleton(spotifyClient)
                .AddSingleton(
                    new DiscordSocketClient(
                        new DiscordSocketConfig
                        {
                            GatewayIntents =
                                GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
                            UseInteractionSnowflakeDate = false,
                        }
                    )
                )
                .AddSingleton(sp => new InteractionService(
                    sp.GetRequiredService<DiscordSocketClient>()
                ))
                .BuildServiceProvider();

            DiscordSocketClient discordClient =
                _serviceProvider.GetRequiredService<DiscordSocketClient>();
            InteractionService interactionService =
                _serviceProvider.GetRequiredService<InteractionService>();

            discordClient.Ready += Ready;
            discordClient.InteractionCreated += InteractionCreated;
            discordClient.Log += Log;
            interactionService.Log += Log;

            await interactionService.AddModuleAsync<CommandModule>(_serviceProvider);

            await discordClient.LoginAsync(TokenType.Bot, _discordToken);
            await discordClient.StartAsync();
        }
        #endregion

        #region Discord Event Handlers
        private static async Task Ready()
        {
            if (_config == null || _serviceProvider == null)
                return;

            InteractionService interactionService =
                _serviceProvider.GetRequiredService<InteractionService>();

            if (IsDebug())
            {
                ulong testGuildId = _config.GetValue<ulong>("Discord:TestGuildId");
                await interactionService.RegisterCommandsToGuildAsync(testGuildId);
            }
            else
            {
                await interactionService.RegisterCommandsGloballyAsync();
            }

            Console.WriteLine("Commands registered.");
        }

        private static async Task InteractionCreated(SocketInteraction interaction)
        {
            if (_serviceProvider == null)
                return;

            DiscordSocketClient discordClient =
                _serviceProvider.GetRequiredService<DiscordSocketClient>();
            InteractionService interactionService =
                _serviceProvider.GetRequiredService<InteractionService>();

            SocketInteractionContext context = new SocketInteractionContext(
                discordClient,
                interaction
            );

            await interactionService.ExecuteCommandAsync(context, _serviceProvider);
        }

        private static Task Log(LogMessage log)
        {
            Console.WriteLine(log.ToString());

            return Task.CompletedTask;
        }

        private static bool IsDebug()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
        #endregion
    }
}
