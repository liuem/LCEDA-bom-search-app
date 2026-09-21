namespace lceda_bom_search_AndroidApp;

/// <summary>Windows 提示音：控制台蜂鸣（电脑模拟调试用）。</summary>
public sealed class WindowsAlertSound : IAlertSound
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public void Play(params (int FreqHz, int DurMs)[] tones)
    {
        if (tones is not { Length: > 0 }) return;
        Task.Run(async () =>
        {
            if (!await _gate.WaitAsync(120)) return;
            try
            {
                foreach (var (freq, durMs) in tones)
                {
                    if (freq <= 0) { Thread.Sleep(durMs); continue; }
                    Console.Beep(freq, durMs);
                    Thread.Sleep(30);
                }
            }
            catch { }
            finally { _gate.Release(); }
        });
    }
}
