using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace Creezio.Switcher
{
    internal static class ProductUx
    {
        public static string Percent(double? value)
        {
            if(!value.HasValue)return "—";
            // Keep the exact threshold understandable: 1.4 must not look like 1.
            return value.Value.ToString(value.Value<10 ? "0.0" : "0",CultureInfo.CurrentCulture)+" %";
        }
        public static string JobState(RelayMessage m)
        {
            if(m.CancellationRequested&&m.State=="waiting")return "Arrêt à effectuer dans Codex";
            switch(m.BlockReason){
                case "permissions":return "Permissions à régler";
                case "approval":return "Approbation Codex";
                case "user-input":return "Réponse attendue";
                case "quota":return "Quota insuffisant ou inconnu";
                case "capacity-or-dependency":return "Place ou dépendance attendue";
                case "policy":return "Configuration à vérifier";
                case "offline":return "Instance à reconnecter";
            }
            return m.DispatchPhase=="preflight"&&m.State=="waiting"?"Préparation du chat":RelayForm.State(m.State);
        }
        public static string Resolution(RelayMessage m)
        {
            if(m.CancellationRequested)return "Ouvrez la conversation destinataire et utilisez Arrêter dans Codex. Le travail reste actif tant que son arrêt n'a pas été constaté.";
            switch(m.BlockReason){
                case "permissions":return "Ouvrez CE chat destinataire, choisissez le mode autorisé puis envoyez « Permissions confirmées, réponds sans outil ». Le relais relira son contexte.";
                case "approval":return "Ouvrez le chat destinataire pour traiter la demande native Codex.";
                case "user-input":return "Ouvrez le chat destinataire et répondez à sa question.";
                case "quota":return "Actualisez les limites du compte et consultez la politique de réinitialisation. Une ressource privée reste réservée à ses agents autorisés.";
                case "capacity-or-dependency":return m.Error;
                case "policy":return "Vérifiez le projet, ses règles et les agents autorisés, puis relancez la vérification.";
                case "offline":return "Ouvrez l'instance concernée. Les instances gérées sont reconnectées automatiquement après vérification de leur identité.";
            }
            if(m.State=="uncertain"||m.ReturnState=="uncertain")return "Vérifiez la réception dans Codex avant de réconcilier. Ne soumettez pas une deuxième fois cette action.";
            return m.Error??"Aucune intervention requise.";
        }
        public static void Accessible(Control parent)
        {
            var form=parent as Form;if(form!=null)form.AutoScaleMode=AutoScaleMode.Dpi;
            foreach(Control c in parent.Controls){
                if(String.IsNullOrEmpty(c.AccessibleName)&&!String.IsNullOrWhiteSpace(c.Text))c.AccessibleName=c.Text.Replace("&","");
                if(SystemInformation.HighContrast){c.ForeColor=System.Drawing.SystemColors.WindowText;c.BackColor=System.Drawing.SystemColors.Window;}
                Accessible(c);
            }
        }
    }
    internal static class SingleWindow
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
        public static readonly uint ActivateMessage=RegisterWindowMessage("Creezio.AccountSwitcher.Activate.v1");
        public static void ActivateExisting(){PostMessage(new IntPtr(0xffff),ActivateMessage,IntPtr.Zero,IntPtr.Zero);}
    }
}
