using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace triclapclap.Audio
{
    public class MciAudioStack : IDisposable
    {
        private const int ChannelCount = 4;
        private readonly string[] playCommands = new string[ChannelCount];
        private bool isLoaded = false;

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

        public void Load(string audioPath)
        {
            CloseAll();
            isLoaded = false;

            if (!File.Exists(audioPath)) return;

            try
            {
                for (int i = 0; i < ChannelCount; i++)
                {
                    mciSendString($"open \"{audioPath}\" type waveaudio alias slapch{i}", null, 0, IntPtr.Zero);
                    playCommands[i] = $"play slapch{i} from 0";
                }
                isLoaded = true;
            }
            catch { }
        }

        public void Play(long totalHits)
        {
            if (!isLoaded) return;
            int channel = (int)(totalHits % ChannelCount);
            mciSendString(playCommands[channel], null, 0, IntPtr.Zero);
        }

        private void CloseAll()
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                mciSendString($"close slapch{i}", null, 0, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            CloseAll();
            GC.SuppressFinalize(this);
        }
    }
}