namespace lceda_bom_search_AndroidApp
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(ScannerPage), typeof(ScannerPage));
        }
    }
}
