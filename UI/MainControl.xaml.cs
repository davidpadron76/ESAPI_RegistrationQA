using System.Windows.Controls;
using ESAPI_RegistrationQA.ViewModels;

namespace ESAPI_RegistrationQA.UI
{
    public partial class MainControl : UserControl
    {
        public MainControl(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}