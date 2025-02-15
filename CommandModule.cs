using System.Diagnostics;
using Discord;
using Discord.Audio;
using Discord.Interactions;
using Discord.WebSocket;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpotifyAPI.Web;

namespace sylvia_streaming_bot
{
    public class CommandModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DiscordSocketClient _discordClient;
        private readonly SpotifyClient _spotifyClient;

        public CommandModule(DiscordSocketClient discordClient, SpotifyClient spotifyClient)
        {
            _discordClient = discordClient;
            _spotifyClient = spotifyClient;

            lock (Singletons.Lock)
            {
                if (Singletons.UpdateStatusTask == null)
                {
                    Singletons.UpdateStatusTask = new Task(
                        async () =>
                        {
                            while (true)
                            {
                                try
                                {
                                    if (Singletons.VoiceChannel == null)
                                        continue;

                                    string currentStatus = Singletons.VoiceChannel.Status;

                                    if (Singletons.IsPlayingSpotify)
                                    {
                                        Process[] processes = Process.GetProcessesByName("Spotify");

                                        foreach (Process p in processes)
                                        {
                                            string title = p.MainWindowTitle.Trim();

                                            if (
                                                !string.IsNullOrWhiteSpace(title)
                                                && title != currentStatus
                                            )
                                            {
                                                await Singletons.VoiceChannel.SetStatusAsync(
                                                    ":microphone: " + title
                                                );
                                                break;
                                            }
                                        }
                                    }
                                }
                                catch { }
                                finally
                                {
                                    Thread.Sleep(5000);
                                }
                            }
                        },
                        TaskCreationOptions.LongRunning
                    );
                    Singletons.UpdateStatusTask.Start();
                }
            }
        }

        #region Discord Commands
        [SlashCommand("소환", "현재 채널에 소환.", false, RunMode.Async)]
        public async Task Summon()
        {
            await DeferAsync();

            StopSpotifyCapture();
            await PauseSpotify();
            DisconnectFromChannel();

            Thread.Sleep(1000);

            try
            {
                SocketGuildUser? user = Context.User as SocketGuildUser;
                if (user == null)
                    throw new Exception("User is null.");

                if (user.VoiceState == null || user.VoiceChannel == null)
                {
                    await FollowupAsync("음성 채널에 있어야 소환할 수 있습니다.");
                    return;
                }

                await FollowupAsync(
                    $"소환을 시도합니다. 소환자: {user.DisplayName}, 소환 채널: {user.VoiceChannel.Name}"
                );

                await ConnectToChannel(user.VoiceChannel);
                await PlaySpotify();
                StartSpotifyCapture();

                await FollowupAsync(
                    "실포티파이, 소환에 응해 이곳에 왔다. 묻겠다, 그대가 나의 마스터인가."
                );
            }
            catch (Exception e)
            {
                Console.WriteLine("Error during Summon: " + e.ToString());
                await FollowupAsync("소환 실패.");
            }
        }

        [SlashCommand("소환해제", "소환 해제.", false, RunMode.Async)]
        public async Task Unsummon()
        {
            await DeferAsync();

            StopSpotifyCapture();
            await PauseSpotify();
            DisconnectFromChannel();

            await FollowupAsync("소환 해제 완료.");
        }
        #endregion

        #region Spotify Capture Functions
        private void StartSpotifyCapture()
        {
            lock (Singletons.Lock)
            {
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                Singletons.Device = deviceEnumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia
                );
                Singletons.Capture = new WasapiLoopbackCapture(Singletons.Device)
                {
                    ShareMode = AudioClientShareMode.Shared,
                };
                Singletons.Capture.DataAvailable += DataAvailable;
                Singletons.Capture.StartRecording();
                Singletons.IsPlayingSpotify = true;
            }
        }

        private void StopSpotifyCapture()
        {
            lock (Singletons.Lock)
            {
                Singletons.IsPlayingSpotify = false;

                if (Singletons.Capture != null)
                {
                    Singletons.Capture.StopRecording();
                    Singletons.Capture.Dispose();
                    Singletons.Capture = null;
                }

                if (Singletons.Device != null)
                {
                    Singletons.Device.Dispose();
                    Singletons.Device = null;
                }
            }
        }

        private void DataAvailable(object? s, WaveInEventArgs args)
        {
            try
            {
                lock (Singletons.Lock)
                {
                    if (
                        Singletons.AudioClient == null
                        || Singletons.AudioStream == null
                        || Singletons.Capture == null
                    )
                        return;

                    byte[] pcm16 = ToPcm16(
                        args.Buffer,
                        args.BytesRecorded,
                        Singletons.Capture.WaveFormat
                    );
                    Singletons.AudioStream.Write(pcm16);
                }
            }
            catch { }
        }

        private byte[] ToPcm16(byte[] inputBuffer, int length, WaveFormat format)
        {
            if (length == 0)
                return Array.Empty<byte>();

            using MemoryStream memStream = new MemoryStream(inputBuffer, 0, length);
            using RawSourceWaveStream inputStream = new RawSourceWaveStream(memStream, format);
            SampleToWaveProvider16 waveProvider = new SampleToWaveProvider16(
                new WdlResamplingSampleProvider(new WaveToSampleProvider(inputStream), 48000)
            );

            byte[] outBuffer = new byte[length];
            using MemoryStream outStream = new MemoryStream();
            int read;

            while ((read = waveProvider.Read(outBuffer, 0, length)) > 0)
                outStream.Write(outBuffer, 0, read);

            return outStream.ToArray();
        }
        #endregion

        #region Discord Connection Functions
        private async Task ConnectToChannel(SocketVoiceChannel voiceChannel)
        {
            IAudioClient? audioClient = null;

            try
            {
                audioClient = await voiceChannel.ConnectAsync();
            }
            catch { }

            if (audioClient == null)
                return;

            lock (Singletons.Lock)
            {
                Singletons.VoiceChannel = voiceChannel;
                Singletons.AudioClient = audioClient;
                Singletons.AudioStream = audioClient.CreatePCMStream(AudioApplication.Music);
            }
        }

        private void DisconnectFromChannel()
        {
            lock (Singletons.Lock)
            {
                if (Singletons.AudioStream != null)
                {
                    Singletons.AudioStream.DisposeAsync();
                    Singletons.AudioStream = null;
                }

                if (Singletons.AudioClient != null)
                {
                    Singletons.AudioClient.Dispose();
                    Singletons.AudioClient = null;
                }

                if (Singletons.VoiceChannel != null)
                {
                    Singletons.VoiceChannel.DisconnectAsync();
                    Singletons.VoiceChannel = null;
                }
            }
        }
        #endregion

        #region Spotify Web API
        private async Task PlaySpotify()
        {
            if (_spotifyClient == null)
                return;

            try
            {
                await _spotifyClient.Player.ResumePlayback();
            }
            catch { }
        }

        private async Task PauseSpotify()
        {
            if (_spotifyClient == null)
                return;

            try
            {
                await _spotifyClient.Player.PausePlayback();
            }
            catch { }
        }
        #endregion
    }
}
