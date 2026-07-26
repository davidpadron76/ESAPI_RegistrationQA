using System;
using System.Windows;
using VMS.IRS.Scripting;

namespace VMS.IRS.Scripting
{
    public class Script
    {
        public Script() { }

        [STAThread]
        public void Execute(ScriptContext context)
        {
            if (context.Patient == null)
            {
                MessageBox.Show("No active patient found in Eclipse. Please open a patient before running this QA script.",
                                "Registration QA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var viewModel = new ESAPI_RegistrationQA.ViewModels.MainViewModel(context);
                var mainControl = new ESAPI_RegistrationQA.UI.MainControl(viewModel);

                Window window = new Window
                {
                    Title = "ESAPI Registration QA - Technical Audit",
                    Content = mainControl,
                    Width = 880,
                    Height = 650,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Runtime Error: {ex.Message}\n{ex.StackTrace}",
                                "ESAPI Registration QA Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}