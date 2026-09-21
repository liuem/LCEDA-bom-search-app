using Microsoft.Extensions.DependencyInjection;

namespace lceda_bom_search_AndroidApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // 恢复上次会话的 BOM、套数与扫描进度，打开即可继续扫码
            try { BomState.Instance.TryRestore(); }
            catch { }
            // 联动服务：PC 插件经局域网下发 BOM / 接收扫描事件（主页可查看地址）
            LinkServer.Start();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}