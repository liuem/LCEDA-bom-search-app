namespace lceda_bom_search_AndroidApp;

public enum SoundKind
{
    Hit,       // 上扬双音：命中
    LineDone,  // 三音琶音：该物料找齐
    AllDone,   // 四音琶音：全部找齐
    Miss,      // 单低音：不需要
    Duplicate, // 双连击：重复袋
    ParseFail, // 单中音：码无法识别
}

/// <summary>提示音定义：(频率 Hz, 时长 ms)；频率 0 表示静音间隔。所有实现必须非阻塞。</summary>
public interface IAlertSound
{
    void Play(params (int FreqHz, int DurMs)[] tones);
}

public static class AlertSound
{
    public static readonly (int, int)[] Hit = [(880, 90), (1175, 130)];
    public static readonly (int, int)[] LineDone = [(1318, 100), (1568, 100), (2093, 170)];
    public static readonly (int, int)[] AllDone = [(1047, 100), (1318, 100), (1568, 100), (2093, 260)];
    // 手机扬声器对 <300Hz 还原差；Miss 用明显下行双音（392→262）+ 振动双保险
    public static readonly (int, int)[] Miss = [(392, 150), (0, 50), (262, 220)];
    // 重复袋：同音三连击，与命中（上行双音）明显区分
    public static readonly (int, int)[] Duplicate = [(587, 80), (0, 60), (587, 80), (0, 60), (587, 80)];
    public static readonly (int, int)[] ParseFail = [(330, 160)];

    static readonly IAlertSound Impl =
#if ANDROID
        new AndroidAlertSound();
#elif WINDOWS
        new WindowsAlertSound();
#else
        new NullAlertSound();
#endif

    public static void Play(SoundKind kind) => Impl.Play(kind switch
    {
        SoundKind.Hit => Hit,
        SoundKind.LineDone => LineDone,
        SoundKind.AllDone => AllDone,
        SoundKind.Miss => Miss,
        SoundKind.Duplicate => Duplicate,
        _ => ParseFail,
    });
}

file sealed class NullAlertSound : IAlertSound
{
    public void Play(params (int FreqHz, int DurMs)[] tones) { }
}
