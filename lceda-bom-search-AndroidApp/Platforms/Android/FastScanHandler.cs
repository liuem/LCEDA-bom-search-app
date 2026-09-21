using Android.Hardware.Camera2;
using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Camera2.InterOp;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using Java.Nio;
using Java.Util.Concurrent;
using Microsoft.Maui.Handlers;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Readers;
using Size = Microsoft.Maui.Graphics.Size;

namespace lceda_bom_search_AndroidApp.Platforms.Android;

/// <summary>
/// 替换 ZXing.Net.Maui 默认的 Android 相机处理器（在 MauiProgram 里 AddHandler 覆盖注册）。
/// 两个它没做对的地方（对照 0.10.4 源码 CameraManager.android.cs）：
///  1. 预览与分析帧的分辨率被钉死在 640×480（DefaultResolution），画面糊、二维码要凑很近才够像素；
///     且公开的 CameraResolutionSelector 只对分析流生效，预览流不动 —— 这里直接把两路都提到 1080p 档。
///  2. Preview+ImageAnalysis 组合不设对焦模式，多数机型不会开连续自动对焦（Redth/ZXing.Net.Maui#315）
///     —— 这里用 Camera2Interop 显式设 CONTROL_AF_MODE_CONTINUOUS_PICTURE。
/// </summary>
public class FastScanHandler : ViewHandler<ICameraBarcodeReaderView, PreviewView>
{
    global::Android.Content.Context Ctx => MauiContext!.Context;
    public static readonly IPropertyMapper<ICameraBarcodeReaderView, FastScanHandler> FastScanMapper =
        new PropertyMapper<ICameraBarcodeReaderView, FastScanHandler>(ViewMapper)
        {
            [nameof(ICameraBarcodeReaderView.Options)] = (h, v) =>
            {
                if (h.reader is not null)
                    h.reader.Options = v.Options ?? new BarcodeReaderOptions();
            },
            [nameof(ICameraBarcodeReaderView.IsDetecting)] = (h, v) => h.detecting = v.IsDetecting,
            [nameof(ICameraBarcodeReaderView.IsTorchOn)] = (h, v) => h.camera?.CameraControl?.EnableTorch(v.IsTorchOn),
        };

    public static readonly CommandMapper<ICameraBarcodeReaderView, FastScanHandler> FastScanCommandMapper =
        new(ViewCommandMapper)
        {
            [nameof(ICameraBarcodeReaderView.AutoFocus)] = (h, v, _) => h.FocusCenter(),
        };

    public FastScanHandler() : base(FastScanMapper, FastScanCommandMapper)
    {
    }

    PreviewView? previewView;
    IExecutorService? executor;
    ProcessCameraProvider? provider;
    ICamera? camera;
    ZXing.Net.Maui.Readers.IBarcodeReader? reader;
    bool detecting = true;
    long lastTryMs;
    long lastEmitMs;

    // 解码统计（供 logcat 诊断识别忽快忽慢）：3 秒汇总一条
    int statTries;
    int statHits;
    long statSumMs;
    long statMaxMs;
    long statLastLogMs;

    // 变焦：SetZoomRatio 在逻辑后摄上自动优先光学镜头、不够再数码裁切放大。
    // 自动策略=解码成功时把二维码放大到约半幅宽（上限 3×，提高每码元像素数与识别距离）；
    // 连续 2.5s 无成功解码回 1×（方便广角找下一袋）；手动捏合后 6s 内自动让位；双击立即复位。
    readonly object zoomLock = new();
    float zoomRatio = 1f;
    float minZoomRatio = 1f;
    float maxZoomRatio = 8f;
    long lastDecodeOkMs;
    long manualUntilMs;

    void InitZoomRange()
    {
        try
        {
            if (camera?.CameraInfo?.ZoomState?.Value is AndroidX.Camera.Core.IZoomState zs)
            {
                minZoomRatio = Math.Max(1f, zs.MinZoomRatio);
                maxZoomRatio = Math.Max(minZoomRatio, Math.Min(zs.MaxZoomRatio, 8f));
            }
        }
        catch { }
        Console.WriteLine($"[FastScan] 变焦范围 {minZoomRatio:0.##}×–{maxZoomRatio:0.##}×");
    }

    public void SetZoomRatio(float ratio, bool manual)
    {
        var cam = camera;
        if (cam is null)
            return;
        ratio = Math.Clamp(ratio, minZoomRatio, maxZoomRatio);
        lock (zoomLock)
            zoomRatio = ratio;
        try { cam.CameraControl?.SetZoomRatio(ratio); }
        catch { }
        if (manual)
            manualUntilMs = Environment.TickCount64 + 6000;
    }

    protected override PreviewView CreatePlatformView()
    {
        reader = new FastQrReader();

        previewView = new PreviewView(Ctx);
        previewView.SetImplementationMode(PreviewView.ImplementationMode.Compatible);

        // 捏合=手动变焦，双击=复位 1×，单击=中心对焦（原生层接管，替代页面级 TapGesture）
        var scaleDetector = new global::Android.Views.ScaleGestureDetector(Ctx, new ZoomScaleListener(this));
        var tapDetector = new global::Android.Views.GestureDetector(Ctx, new ZoomTapListener(this));
        previewView.Touch += (_, e) =>
        {
            tapDetector.OnTouchEvent(e.Event);
            scaleDetector.OnTouchEvent(e.Event);
            e.Handled = true;
        };

        executor = Executors.NewSingleThreadExecutor();

        return previewView;
    }

    protected override void ConnectHandler(PreviewView nativeView)
    {
        base.ConnectHandler(nativeView);

        var future = ProcessCameraProvider.GetInstance(Ctx);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                provider = (ProcessCameraProvider)future.Get();

                var resolution = new ResolutionSelector.Builder()
                    .SetResolutionStrategy(new ResolutionStrategy(
                        new global::Android.Util.Size(1920, 1440),
                        ResolutionStrategy.FallbackRuleClosestHigherThenLower))
                    .Build();

                var previewBuilder = new Preview.Builder().SetResolutionSelector(resolution);
                new Camera2Interop.Extender(previewBuilder)
                    .SetCaptureRequestOption(CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)ControlAFMode.ContinuousPicture));
                var preview = previewBuilder.Build();
                preview.SetSurfaceProvider(executor, previewView!.SurfaceProvider);

                var analysis = new ImageAnalysis.Builder()
                    .SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888)
                    .SetOutputImageRotationEnabled(true)
                    .SetResolutionSelector(resolution)
                    .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
                    .Build();
                analysis.SetAnalyzer(executor, new RgbaAnalyzer(this));

                var lifecycleOwner = (Ctx as AndroidX.Lifecycle.ILifecycleOwner)
                    ?? (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as AndroidX.Lifecycle.ILifecycleOwner);
                camera = provider!.BindToLifecycle(lifecycleOwner, CameraSelector.DefaultBackCamera, preview, analysis);
                InitZoomRange();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FastScan] 相机初始化失败: {ex}");
            }
        }), ContextCompat.GetMainExecutor(Ctx));
    }

    protected override void DisconnectHandler(PreviewView nativeView)
    {
        try
        {
            provider?.UnbindAll();
            provider = null;
            camera = null;
            executor?.Shutdown();
            executor = null;
        }
        catch { }
        base.DisconnectHandler(nativeView);
    }

    /// <summary>中心对焦一次（点击画面/扫不出码时兜底调用）。平时由连续对焦接管。</summary>
    public void FocusCenter()
    {
        try
        {
            var pv = previewView;
            var cam = camera;
            if (pv is null || cam is null || pv.Width <= 0 || pv.Height <= 0)
                return;

            var point = pv.MeteringPointFactory.CreatePoint(pv.Width / 2f, pv.Height / 2f);
            var action = new FocusMeteringAction.Builder(point, FocusMeteringAction.FlagAf).Build();
            if (cam.CameraInfo.IsFocusMeteringSupported(action))
                cam.CameraControl.StartFocusAndMetering(action);
        }
        catch { }
    }

    /// <summary>解码成功→按二维码在画面中的占比自动放大到约半幅宽（只放大不缩小，迟滞防抖）。</summary>
    internal void AutoZoomOnHit(Microsoft.Maui.Graphics.PointF[]? points, int frameWidth, long now)
    {
        lastDecodeOkMs = now;
        if (points is not { Length: >= 3 } || now < manualUntilMs)
            return;

        double span = 0;
        for (var i = 0; i < points.Length; i++)
        {
            for (var j = i + 1; j < points.Length; j++)
            {
                var dx = points[i].X - points[j].X;
                var dy = points[i].Y - points[j].Y;
                span = Math.Max(span, Math.Sqrt((dx * dx) + (dy * dy)));
            }
        }

        var fraction = span / Math.Max(frameWidth, 1);
        var desired = zoomRatio * 0.5f / Math.Max((float)fraction, 0.02f);
        if (desired > zoomRatio + 0.2f && desired > 1.05f)
            SetZoomRatio(Math.Min(desired, 3.0f), manual: false);
    }

    /// <summary>连续 2.5s 无成功解码→回 1× 广角找下一个码（手动捏合窗口内不干预）。</summary>
    internal void MaybeResetZoom(long now)
    {
        if (zoomRatio > 1.01f && now - lastDecodeOkMs > 2500 && now > manualUntilMs)
            SetZoomRatio(1f, manual: false);
    }

    void OnDecoded(BarcodeResult[] results)
    {
        if (results is { Length: > 0 })
            VirtualView?.BarcodesDetected(new BarcodeDetectionEventArgs(results));
    }

    /// <summary>
    /// RGBA 帧分析器：把帧整备成连续缓冲后交 ZXing 解码。
    /// 解码节流 ~10fps、结果上报节流 250ms（页面自身还有 2.5s 同码去抖）。
    /// </summary>
    sealed class RgbaAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        readonly FastScanHandler owner;

        public RgbaAnalyzer(FastScanHandler owner) => this.owner = owner;

        public void Analyze(IImageProxy? image)
        {
            if (image is null)
                return;
            try
            {
                if (!owner.detecting)
                    return;

                var now = Environment.TickCount64;
                if (now - owner.lastTryMs < 100)
                    return;
                owner.lastTryMs = now;

                var plane = image.GetPlanes()[0];
                var buffer = PrepareRgbaBuffer(plane.Buffer, image.Width, image.Height, plane.RowStride, plane.PixelStride);

                var t0 = Environment.TickCount64;
                var results = owner.reader?.Decode(new PixelBufferHolder
                {
                    Data = buffer,
                    Size = new Size(image.Width, image.Height),
                });
                var dt = Environment.TickCount64 - t0;

                // 诊断汇总：识别忽快忽慢时用 logcat（tag DOTNET）看尝试数/命中率/耗时分布
                owner.statTries++;
                owner.statSumMs += dt;
                if (dt > owner.statMaxMs) owner.statMaxMs = dt;
                if (results is { Length: > 0 }) owner.statHits++;
                if (now - owner.statLastLogMs > 3000 && owner.statTries > 0)
                {
                    Console.WriteLine($"[FastScan] 3s 汇总: 尝试 {owner.statTries} 次 命中 {owner.statHits} 次 均 {owner.statSumMs / owner.statTries}ms 峰值 {owner.statMaxMs}ms");
                    owner.statTries = 0;
                    owner.statHits = 0;
                    owner.statSumMs = 0;
                    owner.statMaxMs = 0;
                    owner.statLastLogMs = now;
                }

                if (results is { Length: > 0 })
                {
                    owner.AutoZoomOnHit(results[0].PointsOfInterest, image.Width, now);
                }
                else
                {
                    owner.MaybeResetZoom(now);
                }

                if (results is { Length: > 0 } && now - owner.lastEmitMs > 250)
                {
                    owner.lastEmitMs = now;
                    owner.OnDecoded(results);
                }
            }
            catch { }
            finally
            {
                image.Close();
            }
        }

        /// <summary>
        /// ZXingBarcodeReader 要求 RGBA 缓冲 position=0 且恰好覆盖 w*h*4 字节；
        /// 行跨距不对齐时先逐行拷成连续（照搬 ZXing FrameAnalyzer/RgbaFrameBuffer 的算法）。
        /// </summary>
        static ByteBuffer PrepareRgbaBuffer(ByteBuffer source, int width, int height, int rowStride, int pixelStride)
        {
            const int bpp = 4;
            var contiguousLength = width * height * bpp;

            ByteBuffer Prepare(ByteBuffer src, int length)
            {
                var b = src.Duplicate();
                b.Position(0);
                b.Limit(length);
                return b;
            }

            if (pixelStride == bpp && rowStride == width * bpp)
                return Prepare(source, contiguousLength);

            var required = ((height - 1) * rowStride) + ((width - 1) * pixelStride) + bpp;
            var src = Prepare(source, required);
            var sourceBytes = new byte[required];
            src.Get(sourceBytes, 0, sourceBytes.Length);

            var contiguous = new byte[contiguousLength];
            for (var y = 0; y < height; y++)
            {
                var srcRow = y * rowStride;
                var dstRow = y * width * bpp;
                if (pixelStride == bpp)
                {
                    Array.Copy(sourceBytes, srcRow, contiguous, dstRow, width * bpp);
                    continue;
                }
                for (var x = 0; x < width; x++)
                {
                    contiguous[dstRow + (x * bpp)] = sourceBytes[srcRow + (x * pixelStride)];
                    contiguous[dstRow + (x * bpp) + 1] = sourceBytes[srcRow + (x * pixelStride) + 1];
                    contiguous[dstRow + (x * bpp) + 2] = sourceBytes[srcRow + (x * pixelStride) + 2];
                    contiguous[dstRow + (x * bpp) + 3] = sourceBytes[srcRow + (x * pixelStride) + 3];
                }
            }
            return ByteBuffer.Wrap(contiguous);
        }
    }

    /// <summary>捏合手动变焦（手动后 6s 内自动变焦让位）。</summary>
    sealed class ZoomScaleListener : global::Android.Views.ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        readonly FastScanHandler owner;

        public ZoomScaleListener(FastScanHandler owner) => this.owner = owner;

        public override bool OnScale(global::Android.Views.ScaleGestureDetector? detector)
        {
            if (detector is null)
                return false;
            float current;
            lock (owner.zoomLock)
                current = owner.zoomRatio;
            owner.SetZoomRatio(current * detector.ScaleFactor, manual: true);
            return true;
        }
    }

    /// <summary>双击复位 1×，单击中心对焦。</summary>
    sealed class ZoomTapListener : global::Android.Views.GestureDetector.SimpleOnGestureListener
    {
        readonly FastScanHandler owner;

        public ZoomTapListener(FastScanHandler owner) => this.owner = owner;

        public override bool OnDoubleTap(global::Android.Views.MotionEvent? e)
        {
            owner.manualUntilMs = 0;
            owner.SetZoomRatio(1f, manual: false);
            return true;
        }

        public override bool OnSingleTapConfirmed(global::Android.Views.MotionEvent? e)
        {
            owner.FocusCenter();
            return true;
        }
    }

    /// <summary>
    /// 专用 QR 解码器，绕开库默认 ZXingBarcodeReader 的两个性能坑：
    ///  1. AutoRotate=true 时每帧失败要把图再转 90/180/270 各重试一遍（最多 4 遍全图检测）——
    ///     CameraX 输出已转正、且 QR 解码本身与旋转无关（ finder 图案自定向），这里只试一遍；
    ///  2. 每帧 new 11MB RGBA + 2.7MB 亮度数组（~10fps 下 GC 压力造成卡顿）——这里两块缓冲跨帧复用，
    ///     LuminanceSource 直接引用复用的亮度数组，稳态零分配。
    /// </summary>
    sealed class FastQrReader : ZXing.Net.Maui.Readers.IBarcodeReader
    {
        readonly ZXing.QrCode.QRCodeReader qr = new();
        readonly Dictionary<ZXing.DecodeHintType, object> hints = new()
        {
            [ZXing.DecodeHintType.CHARACTER_SET] = "UTF-8",
        };

        byte[] rgba = [];
        byte[] luma = [];

        public BarcodeReaderOptions Options { get; set; } = new() { Formats = BarcodeFormat.QrCode };

        public BarcodeResult[]? Decode(PixelBufferHolder image)
        {
            var w = (int)image.Size.Width;
            var h = (int)image.Size.Height;
            var bytes = w * h * 4;
            if (rgba.Length < bytes)
                rgba = new byte[bytes];

            var buf = image.Data;
            buf.Position(0);
            buf.Get(rgba, 0, bytes);

            var n = w * h;
            if (luma.Length < n)
                luma = new byte[n];
            for (int i = 0, j = 0; j < n; i += 4, j++)
                luma[j] = (byte)(((66 * rgba[i] + 129 * rgba[i + 1] + 25 * rgba[i + 2]) >> 8) + 16);

            try
            {
                var r = qr.decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(new ReuseLumaSource(luma, w, h))), hints);
                if (r is null)
                    return null;
                return
                [
                    new BarcodeResult
                    {
                        Value = r.Text,
                        Raw = r.RawBytes ?? [],
                        Format = BarcodeFormat.QrCode,
                        PointsOfInterest = (r.ResultPoints ?? [])
                            .Select(p => new Microsoft.Maui.Graphics.PointF(p.X, p.Y))
                            .ToArray(),
                    },
                ];
            }
            catch (ZXing.ReaderException)
            {
                return null;
            }
        }

        /// <summary>引用外部复用亮度数组的 LuminanceSource（解码器只读，单分析线程下安全）。</summary>
        sealed class ReuseLumaSource : ZXing.LuminanceSource
        {
            readonly byte[] data;

            public ReuseLumaSource(byte[] data, int width, int height)
                : base(width, height)
                => this.data = data;

            public override byte[] Matrix => data;

            public override byte[] getRow(int y, byte[]? row)
            {
                row ??= new byte[Width];
                if (row.Length < Width)
                    row = new byte[Width];
                Array.Copy(data, y * Width, row, 0, Width);
                return row;
            }

            public override ZXing.LuminanceSource crop(int left, int top, int width, int height)
                => throw new NotSupportedException();

            public override ZXing.LuminanceSource rotateCounterClockwise()
                => throw new NotSupportedException();

            public override ZXing.LuminanceSource invert()
                => throw new NotSupportedException();
        }
    }
}
