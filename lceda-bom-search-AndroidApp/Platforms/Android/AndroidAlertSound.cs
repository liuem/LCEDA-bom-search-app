using Android.Media;

namespace lceda_bom_search_AndroidApp;

/// <summary>Android 提示音：AudioTrack 播放代码合成的正弦波（跟媒体音量，按音量+即可调大）。</summary>
public sealed class AndroidAlertSound : IAlertSound
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public void Play(params (int FreqHz, int DurMs)[] tones)
    {
        if (tones is not { Length: > 0 }) return;
        Task.Run(async () =>
        {
            if (!await _gate.WaitAsync(120)) return;
            try { PlaySync(tones); }
            catch { /* 提示音失败不影响主流程 */ }
            finally { _gate.Release(); }
        });
    }

    static void PlaySync((int FreqHz, int DurMs)[] tones)
    {
        const int sampleRate = 44100;
        const int gapMs = 45;
        int gapSamples = gapMs * sampleRate / 1000;
        int totalSamples = tones.Sum(t => t.DurMs * sampleRate / 1000 + gapSamples);

        var pcm = new short[totalSamples];
        int pos = 0;
        foreach (var (freq, durMs) in tones)
        {
            int n = Math.Min(durMs * sampleRate / 1000, totalSamples - pos);
            if (freq > 0 && n > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    // 4ms 线性淡入淡出，消除爆音
                    double fade = Math.Min(1.0, Math.Min(i + 1, n - i) / (double)(sampleRate * 0.004));
                    pcm[pos + i] = (short)(Math.Sin(2 * Math.PI * freq * i / sampleRate) * 0.8 * 32767 * fade);
                }
            }
            pos += n + gapSamples;
            if (pos >= totalSamples) break;
        }

        int bytes = totalSamples * sizeof(short);
        var audio = new byte[bytes];
        Buffer.BlockCopy(pcm, 0, audio, 0, bytes);

        using var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                .SetContentType(AudioContentType.Music)
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)
                .SetSampleRate(sampleRate)
                .SetChannelMask(ChannelOut.Mono)
                .Build()!)
            .SetBufferSizeInBytes(bytes)
            .Build()!;

        track.Write(audio, 0, bytes);
        track.Play();
        Thread.Sleep(tones.Sum(t => t.DurMs) + (tones.Length + 1) * gapMs + 80);
    }
}
