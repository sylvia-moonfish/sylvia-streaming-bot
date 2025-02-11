using System.Diagnostics;
using Discord.Audio;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace sylvia_streaming_bot
{
    public class CommandModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DiscordSocketClient _client;
        private MMDevice _device;
        private AudioSessionControl _targetSession;
        private WasapiLoopbackCapture _capture;
        private Discord.Audio.IAudioClient? _audioClient;
        private AudioOutStream? _audioStream;

        private const int DISCORD_SAMPLE_RATE = 48000;

        public CommandModule(DiscordSocketClient client)
        {
            _client = client;

            MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
            _device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            AudioSessionManager sessionManager = _device.AudioSessionManager;

            for (int i = 0; i < sessionManager.Sessions.Count; i++)
            {
                AudioSessionControl session = sessionManager.Sessions[i];

                try
                {
                    Process p = Process.GetProcessById((int)session.GetProcessID);

                    if (p.ProcessName == "Spotify")
                        _targetSession = session;
                }
                catch
                {
                    continue;
                }
            }

            if (_targetSession == null)
                throw new InvalidOperationException("Not able to capture Spotify.");

            _capture = new WasapiLoopbackCapture(_device);
            _capture.ShareMode = AudioClientShareMode.Shared;

            _capture.DataAvailable += async (s, waveArgs) =>
            {
                try
                {
                    if (
                        _audioClient != null
                        && _audioStream != null
                        && !_targetSession.IsSystemSoundsSession
                        && _targetSession.State == AudioSessionState.AudioSessionStateActive
                    )
                    {
                        byte[] pcm16 = ToPCM16(
                            waveArgs.Buffer,
                            waveArgs.BytesRecorded,
                            _capture.WaveFormat
                        );

                        await _audioStream.WriteAsync(pcm16);
                        await _audioStream.FlushAsync();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error streaming audio: " + e.ToString());
                }
            };
        }

        private byte[] ToPCM16(byte[] inputBuffer, int length, WaveFormat format)
        {
            if (length == 0)
                return Array.Empty<byte>();

            using MemoryStream memStream = new MemoryStream(inputBuffer, 0, length);
            using RawSourceWaveStream inputStream = new RawSourceWaveStream(memStream, format);

            SampleToWaveProvider16 waveProvider = new SampleToWaveProvider16(
                new WdlResamplingSampleProvider(
                    new WaveToSampleProvider(inputStream),
                    DISCORD_SAMPLE_RATE
                )
            );

            byte[] convertedBuffer = new byte[length];

            using MemoryStream outStream = new MemoryStream();
            int read;

            while ((read = waveProvider.Read(convertedBuffer, 0, length)) > 0)
            {
                outStream.Write(convertedBuffer, 0, read);
            }

            return outStream.ToArray();
        }

        [SlashCommand("시작", "스트리밍 시작")]
        public async Task StartStream()
        {
            await DeferAsync();

            try
            {
                SocketGuildUser? user = Context.User as SocketGuildUser;

                if (user == null)
                    throw new Exception("User is null.");

                IConfiguration? config = Configuration.GetConfig();

                if (config == null)
                    throw new Exception("No config.");

                string? adminId = config["Discord:AdminId"];

                if (adminId == null)
                    throw new Exception("No admin id.");

                if (user.Id.ToString() != adminId)
                {
                    await FollowupAsync("당신은 실비아가 아니네요!");
                    return;
                }

                if (user.VoiceState == null)
                {
                    await FollowupAsync("음성 채널에 있지 않네요!");
                    return;
                }

                SocketVoiceChannel voiceChannel = user.VoiceChannel;

                await FollowupAsync("연결됨: " + user.DisplayName + ", 채널: " + voiceChannel.Name);

                _audioClient = await voiceChannel.ConnectAsync();
                await _audioClient.SetSpeakingAsync(true);

                _audioStream = _audioClient.CreatePCMStream(AudioApplication.Mixed);

                _capture.StartRecording();

                await FollowupAsync("시작되었습니다.");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.ToString());
                await FollowupAsync("작업 중 오류 발생!");
            }
        }
    }
}
