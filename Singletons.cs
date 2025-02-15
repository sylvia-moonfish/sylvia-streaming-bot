using Discord.Audio;
using Discord.WebSocket;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace sylvia_streaming_bot
{
    public static class Singletons
    {
        public static readonly object Lock = new object();

        #region Discord Voice Connection
        public static volatile SocketVoiceChannel? VoiceChannel;
        public static volatile IAudioClient? AudioClient;
        public static volatile AudioStream? AudioStream;
        #endregion

        #region Spotify Capture
        public static volatile MMDevice? Device;
        public static volatile WasapiLoopbackCapture? Capture;
        #endregion

        #region Flags
        public static volatile bool IsPlayingSpotify = false;
        #endregion

        #region Status Task
        public static Task? UpdateStatusTask;
        #endregion
    }
}
