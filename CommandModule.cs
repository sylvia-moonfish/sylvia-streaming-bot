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

        private MMDevice? _device;
        private AudioSessionControl? _targetSession;
        private WasapiLoopbackCapture? _capture;

        private SocketVoiceChannel? _voiceChannel;
        private Discord.Audio.IAudioClient? _audioClient;
        private AudioOutStream? _audioStream;

        private Task _updateStatusTask;

        private const int DISCORD_SAMPLE_RATE = 48000;

        public CommandModule(DiscordSocketClient client)
        {
            _client = client;

            _updateStatusTask = Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            if (_voiceChannel != null)
                            {
                                Process[] processes = Process.GetProcessesByName("Spotify");

                                foreach (Process p in processes)
                                {
                                    string title = p.MainWindowTitle.Trim();

                                    if (
                                        !string.IsNullOrEmpty(title)
                                        && title != _voiceChannel.Status
                                    )
                                    {
                                        await _voiceChannel.SetStatusAsync(":microphone: " + title);
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
                new CancellationTokenSource().Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        [SlashCommand("시작", "스트리밍 시작")]
        public async Task StartStream()
        {
            await DeferAsync();

            if (_capture != null && _capture.CaptureState != CaptureState.Stopped)
            {
                _capture.RecordingStopped -= RecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }

            _targetSession = null;

            if (_device != null)
            {
                _device.Dispose();
                _device = null;
            }

            if (_audioStream != null)
            {
                await _audioStream.DisposeAsync();
                _audioStream = null;
            }

            if (_audioClient != null)
            {
                await _audioClient.StopAsync();
                _audioClient.Dispose();
                _audioClient = null;
            }

            if (_voiceChannel != null)
            {
                await _voiceChannel.DisconnectAsync();
                _voiceChannel = null;
            }

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

                _voiceChannel = user.VoiceChannel;

                await FollowupAsync(
                    "연결됨: " + user.DisplayName + ", 채널: " + _voiceChannel.Name
                );

                _audioClient = await _voiceChannel.ConnectAsync();
                await _audioClient.SetSpeakingAsync(true);

                _audioStream = _audioClient.CreatePCMStream(AudioApplication.Mixed);

                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                _device = deviceEnumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia
                );

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
                _capture.DataAvailable += DataAvailable;
                _capture.RecordingStopped += RecordingStopped;

                _capture.StartRecording();

                await FollowupAsync("시작되었습니다.");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.ToString());
                await FollowupAsync("작업 중 오류 발생!");
            }
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

        private async void DataAvailable(object? s, WaveInEventArgs waveArgs)
        {
            try
            {
                if (
                    _audioClient != null
                    && _audioStream != null
                    && _targetSession != null
                    && _capture != null
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

                if (_capture != null)
                {
                    _capture.StopRecording();
                }
            }
        }

        private async void RecordingStopped(object? s, StoppedEventArgs stopArgs)
        {
            if (_audioStream != null)
            {
                await _audioStream.DisposeAsync();
                _audioStream = null;
            }

            if (_audioClient != null)
            {
                await _audioClient.StopAsync();
                _audioClient.Dispose();
                _audioClient = null;
            }

            ulong voiceChannelId = 0;

            if (_voiceChannel != null)
            {
                voiceChannelId = _voiceChannel.Id;

                await _voiceChannel.DisconnectAsync();
                _voiceChannel = null;
            }

            if (voiceChannelId == 0)
                return;

            try
            {
                _voiceChannel = await _client.GetChannelAsync(voiceChannelId) as SocketVoiceChannel;

                if (_voiceChannel == null)
                    throw new Exception("Voice channel is null.");

                _audioClient = await _voiceChannel.ConnectAsync();
                await _audioClient.SetSpeakingAsync(true);

                _audioStream = _audioClient.CreatePCMStream(AudioApplication.Mixed);

                if (_capture != null)
                    _capture.StartRecording();
            }
            catch (Exception e)
            {
                Console.WriteLine("Error while restarting: " + e.ToString());
            }
        }
    }
}
