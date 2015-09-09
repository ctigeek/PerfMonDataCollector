using System.ServiceProcess;

namespace DataCollector
{
    public partial class WindowsService : ServiceBase
    {
        private readonly ActionManager actionManager;
        public WindowsService()
        {
            InitializeComponent();
            actionManager = new ActionManager();
        }

        protected override void OnStart(string[] args)
        {
            actionManager.Start();
        }

        protected override void OnStop()
        {
            actionManager.Stop();
            actionManager.Dispose();
        }
    }
}
