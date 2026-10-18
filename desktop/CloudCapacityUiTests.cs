using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        private static async Task CloudCapacityAcceptance(DesktopContext context, PreferencesPage preferences, Func<Task> save, Action<bool, string> assert)
        {
            var field = Descendants(preferences).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Missions Cloud simultanées au total (1 à 8)");
            int initial = CloudCapacity.Read(context.Store).MaxParallel;
            assert(field.Text == initial.ToString(), "réglages affichent la capacité Cloud enregistrée séparément du relais local");
            field.Text = "3"; await save();
            assert(CloudCapacity.Read(context.Store).MaxParallel == 3 && preferences.CanLeave(), "équipe Cloud de trois développeurs enregistrée sans brouillon résiduel");
            field.Text = "9"; await save();
            assert(CloudCapacity.Read(context.Store).MaxParallel == 3 && Descendants(preferences).OfType<TextBlock>().Any(text => text.Text.Contains("missions Cloud simultanées doit être compris entre 1 et 8")), "capacité Cloud invalide expliquée sans remplacer le réglage valide");
            field.Text = initial.ToString(); await save();
            assert(CloudCapacity.Read(context.Store).MaxParallel == initial && preferences.CanLeave(), "restauration de la capacité Cloud initiale enregistre un formulaire cohérent");
        }
    }
}
