using Microsoft.Extensions.Logging;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace lceda_bom_search_AndroidApp
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseBarcodeReader()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if ANDROID
            // 用自研 FastScanHandler 覆盖库默认的 Android 相机处理器：
            // 默认处理器把预览/分析帧钉在 640×480 且不开连续对焦（详见 FastScanHandler 头注释）
            builder.ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<ZXing.Net.Maui.Controls.CameraBarcodeReaderView, Platforms.Android.FastScanHandler>());
#endif

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
