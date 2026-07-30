using System.Windows;
using System.Windows.Controls;
using ESAPI_RegistrationQA.ViewModels;

namespace ESAPI_RegistrationQA.UI
{
    public partial class MainControl : UserControl
    {
        private readonly MainViewModel _viewModel;
        private bool _started;

        public MainControl(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;

            // The measurement starts here rather than in the ViewModel's constructor. It used to
            // run there, and since the script creates the window afterwards, Eclipse sat with no
            // window at all for the several seconds the pass took — indistinguishable from a hang.
            // Waiting for Loaded means the window, and its progress panel, are on screen first.
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Loaded can fire more than once — re-parenting or a tab being revisited will do it —
            // and measuring twice would double the work and re-read the whole volume.
            if (_started) return;
            _started = true;

            if (_viewModel != null) _viewModel.StartMeasurement();
        }
    }
}
