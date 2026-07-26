using System;
using System.Windows;
using ESAPI_RegistrationQA.Services;

namespace VMS.IRS.Scripting
{
    public class Script
    {
        public Script() { }

        // Note: [STAThread] only has an effect on the Main of an executable. Eclipse already
        // invokes scripts from an STA thread, so the attribute was inert here.
        public void Execute(ScriptContext context)
        {
            if (context == null)
            {
                MessageBox.Show(
                    "Eclipse did not provide a script context.",
                    "Registration QA", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (context.Patient == null)
            {
                MessageBox.Show(
                    "No patient is open in Eclipse. Open a patient before running this QA script.",
                    "Registration QA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var viewModel = new ESAPI_RegistrationQA.ViewModels.MainViewModel(context);
                var mainControl = new ESAPI_RegistrationQA.UI.MainControl(viewModel);

                var window = new Window
                {
                    Title = "ESAPI Registration QA — Technical Audit v" + HtmlReportBuilder.AssemblyVersion(),
                    Content = mainControl,
                    Width = 1020,
                    Height = 700,
                    MinWidth = 820,
                    MinHeight = 560,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Runtime error:" + Environment.NewLine +
                    DiagnosticLog.Describe(ex) + Environment.NewLine + Environment.NewLine +
                    ex.StackTrace,
                    "ESAPI Registration QA", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
