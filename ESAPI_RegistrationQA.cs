using System;
using System.Windows;
using ESAPI_RegistrationQA.Services;

namespace VMS.IRS.Scripting
{
    public class Script
    {
        public Script() { }

        // Nota: [STAThread] sólo tiene efecto sobre el Main de un ejecutable. Eclipse ya
        // invoca los scripts desde un hilo STA, de modo que el atributo era inocuo aquí.
        public void Execute(ScriptContext context)
        {
            if (context == null)
            {
                MessageBox.Show(
                    "Eclipse no proporcionó contexto de script.",
                    "Registration QA", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (context.Patient == null)
            {
                MessageBox.Show(
                    "No hay ningún paciente abierto en Eclipse. Abra un paciente antes de ejecutar este script de QA.",
                    "Registration QA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var viewModel = new ESAPI_RegistrationQA.ViewModels.MainViewModel(context);
                var mainControl = new ESAPI_RegistrationQA.UI.MainControl(viewModel);

                var window = new Window
                {
                    Title = "ESAPI Registration QA — Auditoría técnica v" + HtmlReportBuilder.AssemblyVersion(),
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
                    "Error en tiempo de ejecución:" + Environment.NewLine +
                    DiagnosticLog.Describe(ex) + Environment.NewLine + Environment.NewLine +
                    ex.StackTrace,
                    "ESAPI Registration QA", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
